using System.Collections.Concurrent;
using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Rules;
using Xunit;

namespace codereview.tests;

public class ProcessRuleValidationTests
{
    internal static string Source(string body, string condition = "return true;", string extra = "", string ns = "EntityTypesAttributes._2_59_0") => $$"""
        namespace Cmf.Custom.Actions.ProcessRules.{{ns}};
        public class Sample : DeeDevBase
        {
            const string AttributeName = "IsShared";
            public override bool DeeTestCondition(Dictionary<string, object> Input) { {{condition}} }
            public override Dictionary<string, object> DeeActionCode(Dictionary<string, object> Input)
            {
                {{body}}
                return Input;
            }
            {{extra}}
        }
        """;

    private static List<RuleMatch> Review(string body, string condition = "return true;", string path = "EntityTypesAttributes/2.59.0/Sample.cs", string extra = "")
    {
        var source = Source(body, condition, extra);
        var context = RuleFileContext.Build(source.Split('\n'), "Cmf.Custom.Data/DEEs/ProcessRules/" + path,
            new MethodCallGraph(), new ConcurrentDictionary<string, LoopExposure?>(), TestEntities.Catalog);
        return new ProcessRuleValidationRule().FindMatches(context).ToList();
    }

    private const string AttributeSetup = """
        IEntityType entityType = service.LoadEntityType("Container");
        IEntityTypePropertyCollection properties = provider.GetService<IEntityTypePropertyCollection>();
        """;
    private const string AttributeCreate = "properties.Add(service.CreateAttribute(AttributeName, \"Is Shared\", scalarType));";
    private const string AttributeUpdate = "service.UpdateEntityTypeProperties(entityType, properties);";

    [Fact]
    public void ConditionOnlyGuard_ReportsSkippedConditionAndCriticalUnguardedMutation()
    {
        var matches = Review(AttributeSetup + AttributeCreate + AttributeUpdate,
            "var entityType = service.LoadEntityType(\"Container\"); return !entityType.HasProperty(AttributeName);");
        Assert.Contains(matches, m => m.RuleName == "ProcessRuleCondition" && m.Severity == Severity.High);
        Assert.Contains(matches, m => m.RuleName == "ProcessRuleUnguardedMutation" && m.Severity == Severity.Critical);
    }

    [Theory]
    [InlineData("if (!entityType.HasProperty(AttributeName)) {", "}")]
    [InlineData("if (entityType.HasProperty(AttributeName)) return Input;", "")]
    [InlineData("if (entityType.HasProperty(AttributeName)) {} else {", "}")]
    [InlineData("if (entityType.HasProperty(AttributeName) == false) {", "}")]
    [InlineData("if (!entityType.TryGetEntityTypeProperty(AttributeName, out var existing)) {", "}")]
    [InlineData("if (!entityType.HasProperty(\"IsShared\") && enabled) {", "}")]
    public void AttributeCreation_RecognizesTargetSpecificGuards(string before, string after)
    {
        Assert.Empty(Review(AttributeSetup + before + AttributeCreate + after + AttributeUpdate));
    }

    [Theory]
    [InlineData("if (!entityType.HasProperty(\"Other\")) {")]
    [InlineData("if (entityType.HasProperty(AttributeName)) {")]
    [InlineData("if (!entityType.HasProperty(AttributeName) || enabled) {")]
    [InlineData("if (!otherType.HasProperty(AttributeName)) {")]
    public void WrongPropertyReceiverOrBooleanBranch_DoesNotSatisfyGuard(string before)
    {
        var matches = Review(AttributeSetup + before + AttributeCreate + "}" + AttributeUpdate);
        Assert.Contains(matches, m => m.RuleName == "ProcessRuleUnguardedMutation");
    }

    [Fact]
    public void ConditionalEarlyReturn_DoesNotProveAttributeIsMissing()
    {
        var matches = Review(AttributeSetup + "if (entityType.HasProperty(AttributeName)) { if (enabled) return Input; }" + AttributeCreate + AttributeUpdate);
        Assert.Contains(matches, m => m.RuleName == "ProcessRuleUnguardedMutation");
    }

    [Fact]
    public void SchemaAfterConditionalReturn_DoesNotProveRegenerationOnEveryPath()
    {
        Assert.Contains(Review(AttributeSetup + RawUpdate + "if (enabled) return Input; entityType.GenerateSchema();"),
            m => m.RuleName == "ProcessRuleSchemaRegeneration");
    }

    [Fact]
    public void ChangedTargetName_InvalidatesAnEarlierExistenceGuard()
    {
        Assert.Contains(Review("IAction target = provider.GetService<IAction>(); if (!target.ObjectExists()) { target.Name = \"Other\"; target.Create(); }"),
            m => m.RuleName == "ProcessRuleUnguardedMutation");
    }

    [Fact]
    public void CreationRepeatedUnderOneAbsenceGuard_IsNotIdempotent()
    {
        Assert.Contains(Review("IAction target = provider.GetService<IAction>(); if (!target.ObjectExists()) { target.Create(); target.Create(); }"),
            m => m.RuleName == "ProcessRuleUnguardedMutation");
    }

    [Fact]
    public void NontrivialConditionWithoutMatchingGuard_DoesNotEscalateMutationToCritical()
    {
        var matches = Review(AttributeSetup + AttributeCreate + AttributeUpdate, "LogInfo(); return true;");
        Assert.Contains(matches, m => m.RuleName == "ProcessRuleUnguardedMutation" && m.Severity == Severity.High);
    }

    [Fact]
    public void Termination_RequiresBothExistenceAndNotAlreadyTerminatedState()
    {
        Assert.Contains(Review("IRule target = provider.GetService<IRule>(); if (target.ObjectExists()) target.Terminate();"),
            m => m.RuleName == "ProcessRuleTerminationState");
        Assert.Empty(Review("IRule target = provider.GetService<IRule>(); if (!target.ObjectExists()) return Input; if (target.UniversalState == UniversalState.Terminated) return Input; target.Terminate();"));
    }

    [Fact]
    public void ExternalGuardHelper_EmitsReviewHintInsteadOfProvingSafety()
    {
        var matches = Review(AttributeSetup + "if (service.CanCreate(entityType, AttributeName)) {" + AttributeCreate + "}" + AttributeUpdate);
        Assert.Contains(matches, m => m.RuleName == "ProcessRuleUnresolvedGuard" && m.Severity == Severity.Medium);
        Assert.DoesNotContain(matches, m => m.RuleName == "ProcessRuleUnguardedMutation");
    }

    [Fact]
    public void ReachableLocalHelper_WithInternalGuard_IsAccepted()
    {
        Assert.Empty(Review(AttributeSetup + """
            void AddMissing()
            {
                if (!entityType.HasProperty(AttributeName))
                    properties.Add(service.CreateAttribute(AttributeName, "Is Shared", scalarType));
            }
            AddMissing();
            """ + AttributeUpdate));
    }

    [Fact]
    public void GuardAtLocalHelperCallSite_ProtectsItsMutation()
    {
        Assert.Empty(Review(AttributeSetup + """
            void AddMissing() { properties.Add(service.CreateAttribute(AttributeName, "Is Shared", scalarType)); }
            if (!entityType.HasProperty(AttributeName)) AddMissing();
            """ + AttributeUpdate));
    }

    [Fact]
    public void ParameterizedLocalHelper_UsesGuardsForActualCallerArguments()
    {
        Assert.Empty(Review(AttributeSetup + """
            void AddMissing(IEntityType target, string name)
            {
                properties.Add(service.CreateAttribute(name, "Is Shared", scalarType));
            }
            if (!entityType.HasProperty(AttributeName)) AddMissing(entityType, AttributeName);
            """ + AttributeUpdate));
    }

    [Fact]
    public void OneUnguardedLocalHelperCall_DoesNotBorrowOtherCallersGuard()
    {
        var matches = Review(AttributeSetup + """
            void AddMissing() { properties.Add(service.CreateAttribute(AttributeName, "Is Shared", scalarType)); }
            if (!entityType.HasProperty(AttributeName)) AddMissing();
            AddMissing();
            """ + AttributeUpdate);
        Assert.Contains(matches, m => m.RuleName == "ProcessRuleUnguardedMutation");
    }

    [Fact]
    public void UnusedLocalHelperAndUnrelatedMethods_AreNotDeploymentOperations()
    {
        Assert.Empty(Review(AttributeSetup + "void Unused() { " + AttributeCreate + " }",
            extra: "void Other() { entityType.Create(); }") );
    }

    [Fact]
    public void ExpressionBodiedTrueCondition_IsAccepted()
    {
        var source = Source("").Replace("{ return true; }", "=> true;");
        var context = RuleFileContext.Build(source.Split('\n'), "Cmf.Custom.Data/DEEs/ProcessRules/EntityTypesAttributes/2.59.0/Sample.cs",
            new MethodCallGraph(), new(), TestEntities.Catalog);
        Assert.Empty(new ProcessRuleValidationRule().FindMatches(context));
    }

    [Fact]
    public void RuntimeDee_DoesNotReceiveProcessRuleDiagnostics()
    {
        var source = Source(AttributeSetup + AttributeCreate + AttributeUpdate, "return false;");
        var context = RuleFileContext.Build(source.Split('\n'), "Cmf.Custom.Data/DEEs/Materials/Sample.cs", new MethodCallGraph(), new(), TestEntities.Catalog);
        Assert.Empty(new ProcessRuleValidationRule().FindMatches(context));
    }

    private const string RawUpdate = """
        orchestration.FullUpdateEntityType(new FullUpdateEntityTypeInput
        {
            EntityType = entityType,
            EntityTypePropertiesToAddOrUpdate = properties
        });
        """;

    [Theory]
    [InlineData("entityType.GenerateSchema();", false)]
    [InlineData("orchestration.GenerateEntityTypeDBSchema(new GenerateEntityTypeDBSchemaInput { EntityType = entityType });", false)]
    [InlineData("otherType.GenerateSchema();", true)]
    [InlineData("if (enabled) entityType.GenerateSchema();", true)]
    [InlineData("", true)]
    public void RawPropertyUpdate_RequiresSchemaForSameEntityOnSamePath(string after, bool expected)
    {
        var matches = Review(AttributeSetup + RawUpdate + after);
        Assert.Equal(expected, matches.Any(m => m.RuleName == "ProcessRuleSchemaRegeneration"));
    }

    [Fact]
    public void SchemaBeforeUpdate_DoesNotSatisfyRegeneration()
    {
        Assert.Contains(Review(AttributeSetup + "entityType.GenerateSchema();" + RawUpdate), m => m.RuleName == "ProcessRuleSchemaRegeneration");
    }

    [Fact]
    public void ManualPropertyCollectionCreate_RecognizesGuardAndSchema()
    {
        Assert.Empty(Review(AttributeSetup + """
            if (!entityType.HasProperty(AttributeName))
            {
                IEntityTypeProperty property = provider.GetService<IEntityTypeProperty>();
                property.Name = AttributeName;
                property.EntityTypeId = entityType.Id;
                properties.Add(property);
                properties.Create();
                entityType.GenerateSchema();
            }
            """));
    }

    private const string Removal = """
        if (entityType.TryGetEntityTypeProperty(AttributeName, out IEntityTypeProperty existing))
            properties.Add(existing);
        orchestration.FullUpdateEntityType(new FullUpdateEntityTypeInput
        {
            EntityType = entityType,
            EntityTypePropertiesToRemoveOrTerminate = properties
        });
        entityType.GenerateSchema();
        """;

    [Theory]
    [InlineData("materials.RemoveAttributes(new Collection<string> { AttributeName });", false)]
    [InlineData("materials.RemoveAttributes(new Collection<string> { \"Other\" });", true)]
    [InlineData("if (enabled) materials.RemoveAttributes(new Collection<string> { AttributeName });", true)]
    [InlineData("", true)]
    public void AttributeRemoval_RequiresMatchingCleanupBeforeRemoval(string cleanup, bool expected)
    {
        var matches = Review(AttributeSetup + cleanup + Removal);
        Assert.Equal(expected, matches.Any(m => m.RuleName == "ProcessRuleAttributeRemoval"));
        Assert.DoesNotContain(matches, m => m.RuleName == "ProcessRuleUnguardedMutation");
    }

    [Fact]
    public void GuardedRemoveAndRecreate_DoesNotRequireAnAdditionalAbsenceCheck()
    {
        Assert.Empty(Review(AttributeSetup + "materials.RemoveAttributes(new Collection<string> { AttributeName });" + Removal + AttributeCreate + AttributeUpdate));
    }

    [Fact]
    public void OptionalRemoval_DoesNotProtectUnconditionalRecreation()
    {
        var body = AttributeSetup + "if (enabled) {" + Removal + "}" + AttributeCreate + AttributeUpdate;
        Assert.Contains(Review(body), m => m.RuleName == "ProcessRuleUnguardedMutation");
    }

    [Fact]
    public void RepairCollectionCountGuard_IsAccepted()
    {
        var removal = Removal.Replace("orchestration.FullUpdateEntityType", "if (properties.Count > 0) orchestration.FullUpdateEntityType");
        Assert.DoesNotContain(Review(AttributeSetup + removal + AttributeCreate + AttributeUpdate), m => m.RuleName == "ProcessRuleUnguardedMutation");
    }

    [Fact]
    public void CleanupWithSwallowedFailure_IsNotProofOfClearedValues()
    {
        Assert.Contains(Review(AttributeSetup + "try { materials.RemoveAttributes(new Collection<string> { AttributeName }); } catch { }" + Removal),
            m => m.RuleName == "ProcessRuleAttributeRemoval");
    }

    [Fact]
    public void ParameterizedHelperInternalGuard_MustMatchCollectionUpdateTarget()
    {
        Assert.Empty(Review(AttributeSetup + """
            void AddMissing(IEntityType target, string name)
            {
                if (!target.HasProperty(name)) properties.Add(service.CreateAttribute(name, "Is Shared", scalarType));
            }
            AddMissing(entityType, AttributeName);
            """ + AttributeUpdate));
        Assert.Contains(Review(AttributeSetup + """
            void AddMissing(IEntityType target, string name)
            {
                if (!target.HasProperty(name)) properties.Add(service.CreateAttribute(name, "Is Shared", scalarType));
            }
            AddMissing(otherType, AttributeName);
            """ + AttributeUpdate), m => m.RuleName == "ProcessRuleUnguardedMutation");
    }

    [Fact]
    public void CleanupAfterRemoval_IsCriticalOrderingViolation()
    {
        Assert.Contains(Review(AttributeSetup + Removal + "materials.RemoveAttributes(new Collection<string> { AttributeName });"),
            m => m.RuleName == "ProcessRuleAttributeRemoval" && m.Severity == Severity.Critical);
    }

    [Fact]
    public void CleanupUsingNamedAttributeList_IsRecognized()
    {
        Assert.DoesNotContain(Review(AttributeSetup + "var attributes = new Collection<string> { AttributeName }; materials.RemoveAttributes(attributes);" + Removal),
            m => m.RuleName == "ProcessRuleAttributeRemoval");
    }

    [Theory]
    [InlineData("IAction", "Delete", "if (target.ObjectExists()) {", "}", false)]
    [InlineData("IAction", "Create", "if (!target.ObjectExists()) {", "}", false)]
    [InlineData("IConfig", "Delete", "if (!target.TryLoad(path)) return Input;", "", false)]
    [InlineData("IRule", "Terminate", "if (target.ObjectExists() && target.UniversalState != UniversalState.Terminated) {", "}", false)]
    [InlineData("IRule", "Terminate", "if (target.UniversalState == UniversalState.Terminated) return Input;", "", true)]
    [InlineData("IAction", "Delete", "if (other.ObjectExists()) {", "}", true)]
    [InlineData("IConfig", "Save", "", "", true)]
    public void StateMutations_RequireApplicableGuard(string type, string method, string before, string after, bool expected)
    {
        var matches = Review($"{type} target = provider.GetService<{type}>(); {before} target.{method}(); {after}");
        Assert.Equal(expected, matches.Any(m => m.RuleName == "ProcessRuleUnguardedMutation"));
    }

    [Fact]
    public void RemoveActionInput_MatchesItsActionGuard()
    {
        var body = "IAction target = provider.GetService<IAction>(); if (target.ObjectExists()) orchestration.RemoveAction(new RemoveActionInput { Action = target });";
        Assert.Empty(Review(body));
        Assert.Contains(Review(body.Replace("if (target.ObjectExists())", "")), m => m.RuleName == "ProcessRuleUnguardedMutation");
    }

    [Fact]
    public void AttributesUnderGeneralPhase_AreFlagged()
    {
        Assert.Contains(Review(AttributeSetup + "if (!entityType.HasProperty(AttributeName)) {" + AttributeCreate + "}" + AttributeUpdate,
            path: "2.59.0/After/Sample.cs"), m => m.RuleName == "ProcessRulePlacement");
    }

    [Fact]
    public void MigrationUnderServices_ReportsRecurringExecution()
    {
        Assert.Contains(Review(AttributeSetup + "if (!entityType.HasProperty(AttributeName)) {" + AttributeCreate + "}" + AttributeUpdate,
            path: "Services/Sample.cs"), m => m.RuleName == "ProcessRuleRecurringMigration" && m.Severity == Severity.Medium);
    }

    [Theory]
    [InlineData("EntityTypesAttributes/2.59.0/Sample.cs", "EntityTypesAttributes", "2.59.0")]
    [InlineData("EntityTypes/2.59.0/Sample.cs", "EntityTypes", "2.59.0")]
    [InlineData("2.59.0/Before/Sample.cs", "Before", "2.59.0")]
    [InlineData("2.59.0/After/Sample.cs", "After", "2.59.0")]
    [InlineData("Services/Sample.cs", "Services", null)]
    [InlineData("Sample.cs", "Unknown", null)]
    public void PathClassification_CoversAllDeploymentCategories(string tail, string phase, string? version)
    {
        var result = ProcessRuleRepositoryContext.ClassifyPath("Cmf.Semi.Data\\DEEs\\ProcessRules\\" + tail.Replace('/', '\\'));
        Assert.NotNull(result);
        Assert.Equal(phase, result.Phase);
        Assert.Equal(version, result.Version);
    }
}
