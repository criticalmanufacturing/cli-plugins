using Cmf.Cli.Plugin.CodeReview.Enums;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Cmf.Cli.Plugin.CodeReview.Rules.ProcessRuleSyntax;

namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>Deterministic deployment checks derived from Process Rule authoring conventions.</summary>
public sealed class ProcessRuleValidationRule : IReviewRule
{
    public IEnumerable<string> EscalatableRuleNames => Array.Empty<string>();

    public IEnumerable<RuleMatch> FindMatches(RuleFileContext context)
    {
        if (context.ProcessRule is not { } deployment) yield break;
        foreach (var @class in context.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(c => c.BaseList?.Types.Any(t => CodeReviewSyntaxTree.LastIdentifierSegment(t.Type.ToString()) == "DeeDevBase") == true))
        {
            var action = @class.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "DeeActionCode" && m.Modifiers.Any(SyntaxKind.OverrideKeyword));
            if (action is null) continue;
            var condition = @class.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "DeeTestCondition" && m.Modifiers.Any(SyntaxKind.OverrideKeyword));
            var conditionHasLogic = condition is not null && !ReturnsTrue(condition);
            if (deployment.PackagingProblem is { } packagingProblem)
                yield return Match("ProcessRulePlacement", @class, Severity.High, packagingProblem,
                    "Align the Process Rule content type and position in cmfpackage.json with its deployment phase.");
            foreach (var problem in context.ProcessRuleOrderProblems)
                yield return Match("ProcessRuleDependencyOrder", @class, Severity.High, problem,
                    "Run the entity-type creator before consumers; use the proper content phase or deterministic alphabetical prefixes for sibling rules.");
            if (conditionHasLogic)
                yield return Match("ProcessRuleCondition", condition!, Severity.High,
                    "Deployment calls DeeActionCode directly and does not evaluate DeeTestCondition. This condition contains logic that cannot protect installation.",
                    "Make DeeTestCondition simply return true; put each existence/state guard around its operation in DeeActionCode.");

            var syntax = new ProcessRuleSyntax(@class, action);
            var calls = syntax.Calls;
            foreach (var call in calls)
            {
                var name = Name(call);
                if (name == "CreateAttribute" && call.ArgumentList.Arguments.Count > 0)
                {
                    var entity = AttributeEntity(syntax, call);
                    var property = syntax.Key(call.ArgumentList.Arguments[0].Expression);
                    if (entity is not null && !syntax.Guarded(call, entity, property, false) && !RemovedFirst(syntax, call, entity, property))
                        yield return MutationMatch(syntax, call, entity, property, false, conditionHasLogic);
                    else if (entity is null)
                        yield return Match("ProcessRuleUnresolvedGuard", call, Severity.Medium,
                            "CreateAttribute target entity type cannot be resolved by the supported single-file patterns.",
                            "Verify a matching attribute existence guard in DeeActionCode and trace which entity type receives this property.");
                }

                if (name == "FullUpdateEntityType")
                {
                    var entity = InputMember(call, "EntityType");
                    if (entity is null) continue;
                    var target = Tokens(entity);
                    var adds = InputMember(call, "EntityTypePropertiesToAddOrUpdate");
                    var removes = InputMember(call, "EntityTypePropertiesToRemoveOrTerminate");
                    if ((adds is not null || removes is not null) && !syntax.HasFollowingCall(call, c => IsSchemaCall(c, target)))
                        yield return SchemaMatch(call, target);
                    if (adds is not null)
                        foreach (var match in CheckManualProperties(syntax, call, adds, target, conditionHasLogic)) yield return match;
                    if (removes is not null)
                    {
                        var removedProperties = CollectionProperties(syntax, removes, call).ToList();
                        if (removedProperties.Count == 0)
                            yield return Match("ProcessRuleAttributeRemovalReview", call, Severity.Medium,
                                "Removed entity-type properties cannot be resolved from local TryGetEntityTypeProperty/collection-add patterns.",
                                "Verify existence guards, value preservation/cleanup before removal, and schema regeneration for every property supplied by the helper/collection.");
                        foreach (var item in removedProperties)
                        {
                            if (!syntax.Guarded(item.Source, item.Entity, item.Property, true))
                                yield return MutationMatch(syntax, item.Source, item.Entity, item.Property, true, conditionHasLogic);
                            if (!syntax.HasPriorCleanup(call, item.Property))
                                yield return Match("ProcessRuleAttributeRemoval", call,
                                    calls.Any(c => c.SpanStart > call.SpanStart && syntax.CleanupMatches(c, item.Property)) ? Severity.Critical : Severity.High,
                                    $"No matching RemoveAttributes cleanup is recognized before removing {item.Entity}.{item.Property}. Populated attributes can prevent deployment or lose data during a repair.",
                                    "Query all holders of the attribute, preserve values if required, clear them before removing the property, and regenerate schema. Review external cleanup helpers and suppress only after verification.");
                        }
                    }
                }

                if (name == "Create" && syntax.TypeOf(Receiver(call)) is "IEntityTypePropertyCollection" or "EntityTypePropertyCollection")
                {
                    var collection = Receiver(call)!;
                    var properties = ManualProperties(syntax, collection, call).ToList();
                    foreach (var item in properties)
                        if (!syntax.Guarded(item.Source, item.Entity, item.Property, false) && !RemovedFirst(syntax, call, item.Entity, item.Property))
                            yield return MutationMatch(syntax, item.Source, item.Entity, item.Property, false, conditionHasLogic);
                    var targets = properties.Select(p => p.Entity).Distinct().ToList();
                    if (targets.Count == 1 && !syntax.HasFollowingCall(call, c => IsSchemaCall(c, targets[0]))) yield return SchemaMatch(call, targets[0]);
                }

                var receiver = Receiver(call);
                var type = syntax.TypeOf(receiver);
                if (type is "ISmartTable" or "SmartTable" or "IGenericTable" or "GenericTable" &&
                    name is "LoadData" or "InsertOrUpdateRows" or "InsertRows" or "UpdateRows" or "DeleteRows")
                {
                    var tableName = TableName(syntax, receiver!, call);
                    var schemaSource = tableName is null ? null : context.ProcessRuleRepository?.GetSchemaChange(deployment, tableName);
                    if (schemaSource is not null)
                        yield return Match("ProcessRuleTableMigration", call, Severity.Critical,
                            $"Table '{tableName}' is created or structurally changed in package version {deployment.Version} ({schemaSource}); '{name}' uses it within the deployment transaction.",
                            "Move data access/migration to a documented post-install step after the transaction commits, or to a later independent package. After-phase placement alone does not make it safe.");
                    else if (name is "InsertOrUpdateRows" or "InsertRows" or "UpdateRows" or "DeleteRows" &&
                        (tableName is null || deployment.Version is null || deployment.PackageDirectory is null))
                        yield return Match("ProcessRuleTableMigrationReview", call, Severity.Medium,
                            "Table mutation has an unresolved table name or package version. Syntax analysis cannot establish whether it depends on schema changed in this deployment.",
                            "Verify the table structure is unchanged in this version and the action is safe on reruns; move same-version schema/data migrations to a post-install step.");
                }

                if (receiver is not null && type is "IAction" or "Action" or "IRule" or "Rule" or "IConfig" or "Config" or "IEntityType" or "EntityType" &&
                    name is "Create" or "Delete" or "Remove" or "Terminate" or "Save" or "RemoveActionGroups")
                {
                    var target = Tokens(receiver);
                    if (!syntax.Guarded(call, target, null, name != "Create"))
                        yield return MutationMatch(syntax, call, target, null, name != "Create", conditionHasLogic);
                    if (name == "Terminate" && !syntax.TerminationGuarded(call, target))
                        yield return Match("ProcessRuleTerminationState", call, Severity.High,
                            $"'{target}' is terminated without a recognized already-terminated state guard. Existence alone does not make termination safe on retries.",
                            "Check existence and UniversalState != Terminated before terminating the same rule/entity.");
                }
                if (name == "RemoveAction" && InputMember(call, "Action") is { } removedAction &&
                    !syntax.Guarded(call, Tokens(removedAction), null, true))
                    yield return MutationMatch(syntax, call, Tokens(removedAction), null, true, conditionHasLogic);
            }

            var propertyChange = calls.FirstOrDefault(c => Name(c) is "CreateAttribute" or "FullUpdateEntityType" ||
                Name(c) == "Create" && syntax.TypeOf(Receiver(c)) is "IEntityTypePropertyCollection" or "EntityTypePropertyCollection");
            if (deployment.Phase is "Before" or "After" && propertyChange is not null)
                yield return Match("ProcessRulePlacement", propertyChange, Severity.High,
                    "Entity-type property changes are in a general Before/After folder rather than the dedicated EntityTypesAttributes deployment content.",
                    "Use EntityTypesAttributes/<version> for attribute-only changes, or EntityTypes/<version> for creation of a complete entity type. Verify the package content ordering.");
            if (deployment.Phase == "Unknown")
                yield return Match("ProcessRulePlacement", @class, Severity.High,
                    "Process Rule path does not match a versioned Before/After, EntityTypes, EntityTypesAttributes, or recurring Services location.",
                    "Place the file in an appropriate deployment folder and include it in the package manifest.");
            if (deployment.Version is { } version)
            {
                var namespaceName = string.Join(".", @class.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
                var segments = namespaceName.Split('.');
                if (!segments.Contains("_" + version.Replace('.', '_')))
                    yield return Match("ProcessRuleVersionNamespace", @class, Severity.Medium,
                        $"Namespace does not contain the version segment _{version.Replace('.', '_')} matching deployment folder {version}.",
                        "Use dotted versions in folders and underscore-separated versions in the Process Rule namespace.");
                if (deployment.Phase is "Before" or "After" && segments.LastOrDefault() != deployment.Phase)
                    yield return Match("ProcessRulePlacement", @class, Severity.High,
                        $"Namespace phase does not match the {deployment.Phase} deployment folder.",
                        "Align the namespace phase with the folder and package content type.");
            }
            if (deployment.Phase == "Services" && calls.FirstOrDefault(c => Name(c) is "CreateAttribute" or "FullUpdateEntityType" or
                "RemoveAction" or "InsertOrUpdateRows" or "InsertRows" or "DeleteRows" ||
                Name(c) is "Create" or "Delete" or "Remove" or "Terminate" && syntax.TypeOf(Receiver(c)) is "IAction" or "IRule" or "IConfig" or "IEntityType") is { } recurringMutation)
                yield return Match("ProcessRuleRecurringMigration", recurringMutation, Severity.Medium,
                    "This migration-like mutation is under unversioned Services and will be repackaged and run on every deployment.",
                    "Use a versioned Process Rule for a one-time migration; keep this here only if recurring execution is intentional and idempotent.");
        }
    }

    private static bool ReturnsTrue(MethodDeclarationSyntax method) =>
        method.ExpressionBody is { } arrow && Unwrap(arrow.Expression).IsKind(SyntaxKind.TrueLiteralExpression) ||
        method.Body?.Statements is { Count: 1 } statements && statements[0] is ReturnStatementSyntax { Expression: { } value } && Unwrap(value).IsKind(SyntaxKind.TrueLiteralExpression);

    private static RuleMatch Match(string id, SyntaxNode node, Severity severity, string description, string fix) =>
        new(id, CodeReviewSyntaxTree.StartLine(node), severity, description, fix);

    private static RuleMatch MutationMatch(ProcessRuleSyntax syntax, InvocationExpressionSyntax call, string target, string? property, bool exists, bool conditionHasLogic)
    {
        var unknownGuard = ControlGuard(call, (e, _) => e is InvocationExpressionSyntax c && Name(c) is not ("HasProperty" or "TryGetEntityTypeProperty" or "ObjectExists" or "TryLoad"));
        var skippedGuard = conditionHasLogic && syntax.HasSkippedGuard(target, property, exists);
        var severity = unknownGuard ? Severity.Medium : skippedGuard ? Severity.Critical : Severity.High;
        return Match(unknownGuard ? "ProcessRuleUnresolvedGuard" : "ProcessRuleUnguardedMutation", call, severity,
            $"'{Name(call)}' has no recognized target-specific {(exists ? "existence/state" : "absence")} guard for {target}{(property is null ? "" : "." + property)} in DeeActionCode." +
                (unknownGuard ? " The enclosing helper condition needs review." : skippedGuard ? " The matching DeeTestCondition guard is skipped during deployment." : " Re-execution can fail when the target state already exists."),
            "Guard the operation in DeeActionCode using the same target/property (HasProperty, TryGetEntityTypeProperty, ObjectExists, TryLoad, or termination-state checks). Verify helper behavior and both initial/already-applied cases.");
    }

    private static RuleMatch SchemaMatch(SyntaxNode node, string target) => Match("ProcessRuleSchemaRegeneration", node, Severity.High,
        $"Raw entity-type property update for '{target}' has no recognized subsequent schema regeneration on the same execution path.",
        "Call GenerateEntityTypeDBSchema for the same entity type or its GenerateSchema method after the update. IMicronEntityTypeService.UpdateEntityTypeProperties already handles regeneration.");

    private static bool IsSchemaCall(InvocationExpressionSyntax call, string target) =>
        Name(call) == "GenerateSchema" && Tokens(Receiver(call)) == target ||
        Name(call) == "GenerateEntityTypeDBSchema" && Tokens(InputMember(call, "EntityType")) == target;

    private static ExpressionSyntax? InputMember(InvocationExpressionSyntax call, string name) => call.ArgumentList.Arguments
        .Select(a => a.Expression).OfType<BaseObjectCreationExpressionSyntax>()
        .SelectMany(c => c.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>() ?? [])
        .FirstOrDefault(a => Tokens(a.Left) == name)?.Right;

    private static string? AttributeEntity(ProcessRuleSyntax syntax, InvocationExpressionSyntax creation)
    {
        var local = creation.Ancestors().OfType<LocalFunctionStatementSyntax>().FirstOrDefault();
        if (creation.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault(c => Name(c) == "Add") is { } add)
        {
            var collection = Tokens(Receiver(add));
            var entities = syntax.Calls.Where(c => Name(c) == "UpdateEntityTypeProperties" && c.ArgumentList.Arguments.Count >= 2 &&
                    Tokens(c.ArgumentList.Arguments[1].Expression) == collection)
                .Select(c => Tokens(c.ArgumentList.Arguments[0].Expression))
                .Concat(syntax.Calls.Where(c => Name(c) == "FullUpdateEntityType" && Tokens(InputMember(c, "EntityTypePropertiesToAddOrUpdate")) == collection)
                    .Select(c => Tokens(InputMember(c, "EntityType"))))
                .Where(s => s.Length != 0).Distinct().ToList();
            if (entities.Count == 1) return local is null ? entities[0] : syntax.ParameterAlias(local, entities[0]) ?? entities[0];
        }
        if (local is not null)
        {
            var entityParameters = local.ParameterList.Parameters.Where(p => p.Type?.ToString() == "IEntityType").ToList();
            if (entityParameters.Count == 1) return entityParameters[0].Identifier.ValueText;
        }
        var entityVariables = creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Parent is VariableDeclarationSyntax d && d.Type.ToString() == "IEntityType")
            .Select(v => v.Identifier.ValueText).Distinct().ToList();
        return entityVariables?.Count == 1 ? entityVariables[0] : null;
    }

    private record AttributeProperty(string Entity, string PropertyName, InvocationExpressionSyntax Source)
    {
        public string Property => PropertyName;
    }

    private static IEnumerable<AttributeProperty> CollectionProperties(ProcessRuleSyntax syntax, ExpressionSyntax collection, InvocationExpressionSyntax operation)
    {
        foreach (var add in syntax.Calls.Where(c => Name(c) == "Add" && Tokens(Receiver(c)) == Tokens(collection) && c.SpanStart < operation.SpanStart && c.ArgumentList.Arguments.Count == 1))
        {
            var propertyVariable = Tokens(add.ArgumentList.Arguments[0].Expression);
            var checks = syntax.Calls.Where(c => Name(c) == "TryGetEntityTypeProperty" && c.ArgumentList.Arguments.Count > 1 &&
                (Tokens(c.ArgumentList.Arguments[1].Expression) == propertyVariable ||
                 c.ArgumentList.Arguments[1].Expression is DeclarationExpressionSyntax declaration && Tokens(declaration.Designation) == propertyVariable)).ToList();
            if (checks.Count == 1)
                yield return new(Tokens(Receiver(checks[0])), syntax.Key(checks[0].ArgumentList.Arguments[0].Expression), add);
        }
    }

    private static IEnumerable<AttributeProperty> ManualProperties(ProcessRuleSyntax syntax, ExpressionSyntax collection, InvocationExpressionSyntax operation)
    {
        foreach (var add in syntax.Calls.Where(c => Name(c) == "Add" && Tokens(Receiver(c)) == Tokens(collection) && c.SpanStart < operation.SpanStart && c.ArgumentList.Arguments.Count == 1))
        {
            var variable = Tokens(add.ArgumentList.Arguments[0].Expression);
            if (syntax.Calls.Any(c => Name(c) == "TryGetEntityTypeProperty" && c.ArgumentList.Arguments.Count > 1 &&
                (Tokens(c.ArgumentList.Arguments[1].Expression) == variable || c.ArgumentList.Arguments[1].Expression is DeclarationExpressionSyntax d && Tokens(d.Designation) == variable))) continue;
            var name = syntax.AssignedValue(variable, "Name", add);
            var entityId = syntax.AssignedValue(variable, "EntityTypeId", add) as MemberAccessExpressionSyntax;
            if (name is not null && entityId?.Name.Identifier.ValueText == "Id")
                yield return new(Tokens(entityId.Expression), syntax.Key(name), add);
        }
    }

    private static IEnumerable<RuleMatch> CheckManualProperties(ProcessRuleSyntax syntax, InvocationExpressionSyntax operation, ExpressionSyntax collection, string target, bool conditionHasLogic)
    {
        foreach (var item in ManualProperties(syntax, collection, operation))
            if (!syntax.Guarded(item.Source, target, item.Property, false) && !RemovedFirst(syntax, operation, target, item.Property))
                yield return MutationMatch(syntax, item.Source, target, item.Property, false, conditionHasLogic);
    }

    private static bool RemovedFirst(ProcessRuleSyntax syntax, InvocationExpressionSyntax operation, string target, string property) =>
        syntax.Calls.Where(c => Name(c) == "FullUpdateEntityType" && c.SpanStart < operation.SpanStart && Tokens(InputMember(c, "EntityType")) == target)
            .Any(c => InputMember(c, "EntityTypePropertiesToRemoveOrTerminate") is { } removed &&
                syntax.RepairConditions(c, target, property, Tokens(removed)) &&
                CollectionProperties(syntax, removed, c).Any(p => p.Entity == target && p.Property == property &&
                    syntax.Guarded(p.Source, target, property, true) && syntax.RepairConditions(p.Source, target, property)));

    private static string? TableName(ProcessRuleSyntax syntax, ExpressionSyntax receiver, InvocationExpressionSyntax before)
    {
        if (syntax.AssignedValue(Tokens(receiver), "Name", before) is { } assigned) return syntax.StringValue(assigned);
        var loads = syntax.Calls.Where(c => c.SpanStart < before.SpanStart && Name(c) == "Load" && Tokens(Receiver(c)) == Tokens(receiver) && c.ArgumentList.Arguments.Count > 0).ToList();
        return loads.Count == 1 ? syntax.StringValue(loads[0].ArgumentList.Arguments[0].Expression) : null;
    }
}
