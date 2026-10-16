using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.Cli.Plugin.CodeReview.Rules;
using Cmf.CLI.Utilities;
using Xunit;

namespace codereview.tests;

public class CmFrameworkScannerTests : IDisposable
{
    private readonly string _tempRoot;

    public CmFrameworkScannerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "codereview-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private string WriteOrchestrationFile(string fileName, string content)
    {
        var businessDir = Path.Combine(_tempRoot, "Cmf.Custom.Business");
        var manifestPath = Path.Combine(businessDir, "cmfpackage.json");
        if (!File.Exists(manifestPath))
        {
            Directory.CreateDirectory(businessDir);
            File.WriteAllText(manifestPath, """{ "packageType": "Business" }""");
        }

        var dir = Path.Combine(businessDir, "Cmf.Custom.Orchestration");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void GetFindings_LoadInsideLoop_IsDetected()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        entity.Load();
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "LoadInsideLoop" && f.LineNumber == 7);
    }

    [Fact]
    public void GetFindings_NestedLoop_EscalatesLoadInsideLoopToCritical()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        foreach (var item in items)
                        {
                            entity.Load();
                        }
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "LoadInsideLoop");
        Assert.Equal(Severity.Critical, finding.Severity);
    }

    [Fact]
    public void GetFindings_ResolveInsideLoop_IsDetected()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        smartTable.Resolve();
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "ResolveInsideLoop" && f.LineNumber == 7);
    }

    [Fact]
    public void GetFindings_QueryExecuteInsideLoop_IsDetected()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    while (condition)
                    {
                        myquery.Execute();
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "QueryExecuteInsideLoop" && f.LineNumber == 7);
    }

    [Fact]
    public void GetFindings_SaveAttributesInsideLoop_IsDetected()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var material in materials)
                    {
                        material.SaveAttributes(attributes);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "SaveInsideLoop" && f.LineNumber == 7);
    }

    [Fact]
    public void GetFindings_CollectionSaveInsideLoop_DoesNotTriggerSaveInsideLoop()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var batch in batches)
                    {
                        materialCollection.Save();
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "SaveInsideLoop");
    }

    [Fact]
    public void GetFindings_CreateInstanceInsideLoop_IsDetected()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var name in names)
                    {
                        material.CreateInstance(name);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "CreateInstanceInsideLoop" && f.LineNumber == 7);
    }

    [Fact]
    public void GetFindings_CreateInstancesBulkCall_DoesNotTriggerCreateInstanceInsideLoop()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var batch in batches)
                    {
                        material.CreateInstances(names, attributes);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "CreateInstanceInsideLoop");
    }

    [Fact]
    public void GetFindings_EntityDeleteInsideLoop_IsDetected()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var material in materials)
                    {
                        material.Delete();
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "DeleteRemoveInsideLoop" && f.LineNumber == 7);
    }

    [Fact]
    public void GetFindings_ListRemoveInsideLoop_DoesNotTriggerDeleteRemoveInsideLoop()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        list.Remove(item);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "DeleteRemoveInsideLoop");
    }

    [Fact]
    public void GetFindings_CreateInstanceOnRelationEntity_SuggestsParentAddRelationsInsteadOfCreateInstances()
    {
        // SubContainer is a real EntityRelation in the CM Framework platform -- it has no
        // CreateInstances; bulk creation goes through the parent's AddXRelations-style helper.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var name in names)
                    {
                        subContainer.CreateInstance(name);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "CreateInstanceInsideLoop");
        Assert.Contains("relation entity", finding.SuggestedFix);
        Assert.Contains("AddXRelations", finding.SuggestedFix);
    }

    [Fact]
    public void GetFindings_CreateInstanceOnCreateDisabledEntity_SuggestsBulkCreateIsDisabled()
    {
        // MaterialDeviation explicitly overrides CreateInstances to throw InvalidOperationCmfException.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var name in names)
                    {
                        materialDeviation.CreateInstance(name);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "CreateInstanceInsideLoop");
        Assert.Contains("explicitly disabled", finding.SuggestedFix);
    }

    [Fact]
    public void GetFindings_SaveOnNoBulkEntity_DoesNotClaimBulkAlternativeExists()
    {
        // SmartTable has no bulk Save at all -- the fix text must not claim one exists.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        smartTable.Save();
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "SaveInsideLoop");
        Assert.Contains("No bulk API exists", finding.SuggestedFix);
        Assert.DoesNotContain("Batch entities into a collection", finding.SuggestedFix);
    }

    [Fact]
    public void GetFindings_DeleteOnNoBulkEntity_DoesNotClaimBulkAlternativeExists()
    {
        // GenericTable has no bulk Delete/Remove at all -- only a plain per-item Remove(T).
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        genericTable.Remove(item);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "DeleteRemoveInsideLoop");
        Assert.Contains("No bulk API exists", finding.SuggestedFix);
    }

    [Fact]
    public void GetFindings_SaveOnUnknownReceiver_FallsBackToGenericMessage()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        someTotallyUnrelatedThing.Save();
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "SaveInsideLoop");
        Assert.Contains("Batch entities into a collection", finding.SuggestedFix);
    }

    [Fact]
    public void GetFindings_SaveAttributesGuardedByContinueThenReturn_DoesNotTriggerSaveInsideLoop()
    {
        // Regression test for the reported false positive: every path either `continue`s past the
        // call or falls straight into an unconditional `return` right after it, so the call runs at
        // most once per method call despite sitting inside a `foreach`.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public object DoWork()
                {
                    foreach (var overlay in overlays)
                    {
                        if (overlay.BinCodeMap is null)
                        {
                            continue;
                        }

                        material.SaveAttributes(attributes);

                        return Input;
                    }
                    return null;
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "SaveInsideLoop");
    }

    [Fact]
    public void GetFindings_SaveAttributesFallsThroughAfterCall_StillTriggersSaveInsideLoop()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var material in materials)
                    {
                        material.SaveAttributes(attributes);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "SaveInsideLoop" && f.LineNumber == 7);
    }

    [Fact]
    public void GetFindings_SaveAttributesFollowedByContinue_StillTriggersSaveInsideLoop()
    {
        // The trap case: `continue` also makes the fall-through endpoint unreachable, but it
        // explicitly loops back -- unlike return/throw/break, it must not be treated as a safe exit.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var material in materials)
                    {
                        material.SaveAttributes(attributes);
                        continue;
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "SaveInsideLoop" && f.LineNumber == 7);
    }

    [Fact]
    public void GetFindings_SaveAttributesOneBranchFallsThrough_StillTriggersSaveInsideLoop()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public object DoWork()
                {
                    foreach (var material in materials)
                    {
                        material.SaveAttributes(attributes);
                        if (material.IsDone)
                        {
                            return material;
                        }
                    }
                    return null;
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "SaveInsideLoop" && f.LineNumber == 7);
    }

    [Fact]
    public void GetFindings_NestedLoopSaveAttributesFollowedByReturn_DoesNotTriggerSaveInsideLoop()
    {
        // Regression test for the real reported shape, which turned out to be nested two loops deep:
        // an unconditional `return` unwinds the whole method regardless of how many loops enclose it,
        // so the call still runs at most once even though it's inside a nested loop.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public object DoWork()
                {
                    foreach (var batch in batches)
                    {
                        foreach (var material in batch.Materials)
                        {
                            material.SaveAttributes(attributes);
                            return material;
                        }
                    }
                    return null;
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "SaveInsideLoop");
    }

    [Fact]
    public void GetFindings_NestedLoopSaveAttributesFollowedByBreak_StillTriggersSaveInsideLoop()
    {
        // Unlike return/throw, a `break` only exits its own (inner) loop -- the outer loop can still
        // re-enter it and re-run the call, so this must still be flagged.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var batch in batches)
                    {
                        foreach (var material in batch.Materials)
                        {
                            material.SaveAttributes(attributes);
                            break;
                        }
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "SaveInsideLoop" && f.LineNumber == 9);
    }

    [Fact]
    public void GetFindings_LoadInsideIfWithBreak_DoesNotTriggerLoadInsideLoop()
    {
        // Find-first-match-then-stop: whenever the load runs, the loop exits right after via the
        // break inside the same if block -- so the load runs at most once despite the loop.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        if (item.Name == wanted)
                        {
                            item.Load();
                            break;
                        }
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "LoadInsideLoop");
    }

    [Fact]
    public void GetFindings_LoadInsideIfWithoutExit_StillTriggersLoadInsideLoop()
    {
        // The if block falls through back into the loop -- the load can run on every iteration.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        if (item.IsActive)
                        {
                            item.Load();
                        }
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "LoadInsideLoop" && f.LineNumber == 9);
    }

    [Fact]
    public void GetFindings_LoadInsideTryWithBreak_StillTriggersLoadInsideLoop()
    {
        // A catch in the same loop can swallow the exception and keep iterating, so a
        // guaranteed-exit tail inside a try body is not trusted.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        try
                        {
                            item.Load();
                            break;
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "LoadInsideLoop" && f.LineNumber == 9);
    }

    [Fact]
    public void GetFindings_EntityTypeMetadataAccess_DoesNotTriggerLazyLoadRules()
    {
        // .EntityType is in-memory metadata populated on every loaded entity -- dereferencing it
        // never lazy-loads, so neither ExcessiveLazyLoading nor LoadReferenceForFkOnly applies.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        var id = item.EntityType.Id;
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "ExcessiveLazyLoading");
        Assert.DoesNotContain(findings, f => f.Rule == "LoadReferenceForFkOnly");
    }

    [Fact]
    public void GetFindings_NewQueryObject_DoesNotTriggerDirectEntityInstantiation()
    {
        // new QueryObject() is the canonical CM pattern for building ad-hoc queries.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    var query = new QueryObject();
                    var material = new Material();
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "DirectEntityInstantiation" && f.LineNumber == 5);
        Assert.Contains(findings, f => f.Rule == "DirectEntityInstantiation" && f.LineNumber == 6);
    }

    [Fact]
    public void GetFindings_LoopInvariantReceiver_DoesNotTriggerExcessiveLazyLoading()
    {
        // `input` never changes across iterations: input.Material lazy-loads once (cached on the
        // instance), so the loop doesn't multiply the cost.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork(SomeInput input)
                {
                    foreach (var item in items)
                    {
                        Log(input.Material.Name);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "ExcessiveLazyLoading");
    }

    [Fact]
    public void GetFindings_ReceiverReassignedInsideLoop_StillTriggersExcessiveLazyLoading()
    {
        // `current` is reassigned every iteration, so current.Material is a different instance
        // each time -- the lazy load genuinely repeats.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork(Thing current)
                {
                    foreach (var item in items)
                    {
                        current = item.Parent;
                        Log(current.Material.Name);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "ExcessiveLazyLoading" && f.LineNumber == 8);
    }

    [Fact]
    public void GetFindings_LambdaParameterReceiver_StillTriggersExcessiveLazyLoading()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    var names = items.Select(m => m.Material.Name).ToList();
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "ExcessiveLazyLoading" && f.LineNumber == 5);
    }

    [Fact]
    public void GetFindings_ExcessiveLazyLoadingGuardedByReturn_DoesNotTrigger()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public object DoWork()
                {
                    foreach (var item in items)
                    {
                        var x = item.Material.Description;
                        return x;
                    }
                    return null;
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "ExcessiveLazyLoading");
    }

    [Fact]
    public void GetFindings_ExcessiveLazyLoadingInsideLoop_IsDetected()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        var x = item.Material.Description;
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "ExcessiveLazyLoading" && f.LineNumber == 7);
    }

    [Fact]
    public void GetFindings_LinqForEachCall_DoesNotTriggerLoadInsideLoopOrExcessiveLazyLoading()
    {
        // Regression test for the CmLoopAnalyzer case-sensitivity fix: `.ForEach(` is a LINQ
        // method call, not a `foreach` loop statement, so calls inside it must not be treated
        // as "inside a loop" for LoadInsideLoop/ExcessiveLazyLoading purposes.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    list.ForEach(x => x.DoThing());
                    entity.Load();
                    var y = entity.Material.Description;
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "LoadInsideLoop");
        Assert.DoesNotContain(findings, f => f.Rule == "ExcessiveLazyLoading" && f.LineNumber == 7);
    }

    [Fact]
    public void GetFindings_LinqForEachLambdaBody_StillFlagsExcessiveLazyLoadingViaLambdaArrow()
    {
        // Even though `.ForEach(` itself is not a loop, the lambda body line contains `=>`,
        // which independently triggers the ExcessiveLazyLoading rule (loop OR lambda arrow).
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    list.ForEach(x => x.Material.Load());
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "ExcessiveLazyLoading" && f.LineNumber == 5);
        Assert.DoesNotContain(findings, f => f.Rule == "LoadInsideLoop");
    }

    [Fact]
    public void GetFindings_LoadStylePatternInsideTrailingInlineComment_IsNotMisdetected()
    {
        // Regression test: the previous per-line regex scan had no comment-stripping at all beyond
        // skipping lines that are ENTIRELY a comment, so a call-shaped pattern inside a trailing
        // inline comment on an otherwise-real code line could be misdetected as a real call.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        DoSomethingElse(); // don't use entity.Load() here, it's slow
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "LoadInsideLoop");
    }

    [Fact]
    public void GetFindings_LoadStylePatternInsideMultiLineBlockComment_IsNotMisdetected()
    {
        // Regression test: a /* ... */ block comment spanning multiple lines was never handled at
        // all by the previous per-line "//" comment stripping, so a call-shaped pattern inside one
        // could be misdetected as a real call on whichever physical line it happened to land on.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        /* old code used to do
                           entity.Load();
                           here, but not anymore */
                        DoSomethingElse();
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "LoadInsideLoop");
    }

    [Fact]
    public void GetFindings_LoadCallWrappedAcrossMultipleLinesInsideLoop_IsStillDetected()
    {
        // The deferred fix this restructure delivers: a loop-sensitive rule now matches against
        // syntax nodes, not physical lines, so a call whose arguments wrap onto following lines
        // (a real formatting style for long argument lists) is no longer invisible to it.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        entity.Load(
                            someArgument,
                            anotherArgument);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "LoadInsideLoop" && f.LineNumber == 7);
    }

    [Fact]
    public void GetFindings_NewEntityCallWrappedAcrossMultipleLines_IsStillDetected()
    {
        // Same fix, for a non-loop-sensitive rule: DirectEntityInstantiation now matches
        // ObjectCreationExpressionSyntax nodes directly, so a constructor call whose arguments
        // wrap across lines is detected too.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    var m = new Material(
                        "arg1",
                        "arg2");
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "DirectEntityInstantiation" && f.LineNumber == 5);
    }

    [Fact]
    public void GetFindings_NullConditionalLoadCallInsideLoop_IsStillDetected()
    {
        // Roslyn parses `entity?.Load()` with the receiver on a MemberBindingExpressionSyntax,
        // not a MemberAccessExpressionSyntax -- a rule matching only the latter silently skips
        // every null-conditional call site. This is a real, common pattern in production DEEs.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        entity?.Load();
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "LoadInsideLoop" && f.LineNumber == 7);
    }

    [Fact]
    public void GetFindings_NullConditionalEntityPropertyAccess_IsStillDetectedAsLazyLoading()
    {
        // Same gap for a property-access rule: `a.Material?.Description` puts "Description" on
        // a MemberBindingExpressionSyntax (the segment right after `?.`), invisible to a rule
        // walking MemberAccessExpressionSyntax only. The receiver is the loop variable so the
        // access genuinely varies per iteration (an invariant receiver would now be correctly
        // suppressed as a one-time cached lazy load).
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var a in items)
                    {
                        var name = a.Material?.Description;
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "ExcessiveLazyLoading" && f.LineNumber == 7);
    }

    [Fact]
    public void IReviewRule_CustomThirdPartyRule_CanBeImplementedAndRunDirectlyAgainstRuleFileContext()
    {
        // Demonstrates the extension point itself: a brand new rule needs nothing from
        // CmFrameworkScanner -- it's a class implementing IReviewRule, run against a
        // RuleFileContext built the same way the scanner builds one internally.
        var path = WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    Console.WriteLine("hello");
                }
            }
            """);

        var lines = File.ReadAllLines(path);
        var callGraph = CmCallGraphBuilder.BuildCallGraph(_tempRoot);
        var loopExposureCache = new System.Collections.Concurrent.ConcurrentDictionary<string, LoopExposure?>();
        var context = RuleFileContext.Build(lines, path, callGraph, loopExposureCache, TestEntities.Catalog);

        var customRule = new ConsoleWriteLineRule();
        var matches = customRule.FindMatches(context).ToList();

        Assert.Single(matches);
        Assert.Equal("NoConsoleWriteLine", matches[0].RuleName);
        Assert.Equal(5, matches[0].Line);
    }

    private class ConsoleWriteLineRule : IReviewRule
    {
        public IEnumerable<string> EscalatableRuleNames => Array.Empty<string>();

        public IEnumerable<RuleMatch> FindMatches(RuleFileContext context)
        {
            foreach (var invocation in context.Root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
            {
                if (invocation.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax
                    {
                        Expression: Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax { Identifier.Text: "Console" },
                        Name.Identifier.Text: "WriteLine"
                    })
                {
                    yield return new RuleMatch("NoConsoleWriteLine", CodeReviewSyntaxTree.StartLine(invocation), Severity.Medium,
                        "Direct Console.WriteLine call.", "Use a proper logger instead.");
                }
            }
        }
    }

    [Fact]
    public void GetFindings_ResolvedRootsHaveNoFiles_ReturnsEmptyList()
    {
        // A recognizable repository (real manifests) whose scan roots simply have no .cs files in
        // them yet -- distinct from an unrecognizable repository, which throws instead (see
        // GetFindings_NoManifestsResolveToBusinessOrData_ThrowsCliException).
        var businessDir = Path.Combine(_tempRoot, "Cmf.Custom.Business");
        Directory.CreateDirectory(businessDir);
        File.WriteAllText(Path.Combine(businessDir, "cmfpackage.json"), """{ "packageType": "Business" }""");
        Directory.CreateDirectory(Path.Combine(businessDir, "Cmf.Custom.Orchestration"));

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void GetFindings_DirectEntityInstantiation_IsDetected()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    var m = new Material();
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "DirectEntityInstantiation" && f.LineNumber == 5);
    }

    [Fact]
    public void GetEscalatableRules_ContainsTheThreeLoopRules()
    {
        var rules = CmFrameworkScanner.GetEscalatableRules();

        Assert.Contains("LoadInsideLoop", rules);
        Assert.Contains("ResolveInsideLoop", rules);
        Assert.Contains("QueryExecuteInsideLoop", rules);
        Assert.Contains("SaveInsideLoop", rules);
        Assert.Contains("CreateInstanceInsideLoop", rules);
        Assert.Contains("DeleteRemoveInsideLoop", rules);
    }

    [Fact]
    public void GetFindings_ChangedFilesFilter_OnlyReportsFindingsOnChangedFiles()
    {
        var fooPath = WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    var m = new Material();
                }
            }
            """);
        WriteOrchestrationFile("Bar.cs", """
            public class Bar
            {
                public void DoWork()
                {
                    var m = new Material();
                }
            }
            """);

        var relativeFoo = CodeReviewPaths.GetRelativePath(_tempRoot, fooPath);
        var findings = CmFrameworkScanner.GetFindings(_tempRoot, changedFiles: new[] { relativeFoo }, entityCatalog: TestEntities.Catalog);

        Assert.All(findings, f => Assert.Equal("Foo.cs", Path.GetFileName(f.RelativePath)));
        Assert.Contains(findings, f => f.Rule == "DirectEntityInstantiation" && f.RelativePath.EndsWith("Foo.cs"));
    }

    [Fact]
    public void GetFindings_ChangedFilesFilter_StillUsesWholeRepoForLoopExposureContext()
    {
        // Bar.cs calls Helper() from inside a loop; Helper() itself (in Foo.cs) has no loop.
        // Even when only Foo.cs is "changed", the loop context from the excluded Bar.cs must
        // still be used to correctly flag Foo.cs's Load() as LoopInducedByCaller.
        var fooPath = WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void Helper()
                {
                    entity.Load();
                }
            }
            """);
        WriteOrchestrationFile("Bar.cs", """
            public class Bar
            {
                public void Caller()
                {
                    foreach (var item in items)
                    {
                        this.Helper();
                    }
                }
            }
            """);

        var relativeFoo = CodeReviewPaths.GetRelativePath(_tempRoot, fooPath);
        var findings = CmFrameworkScanner.GetFindings(_tempRoot, changedFiles: new[] { relativeFoo }, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "LoopInducedByCaller" && f.RelativePath.EndsWith("Foo.cs"));
        Assert.All(findings, f => Assert.EndsWith("Foo.cs", f.RelativePath));
    }

    [Fact]
    public void AnyAtOrAboveSeverity_ReturnsTrueOnlyWhenAMatchingOrHigherSeverityExists()
    {
        var findings = new List<Finding>
        {
            new() { FilePath = "a.cs", RelativePath = "a.cs", LineNumber = 1, Severity = Severity.Medium, Rule = "R", Description = "d", Snippet = "s", SuggestedFix = "f" },
            new() { FilePath = "b.cs", RelativePath = "b.cs", LineNumber = 2, Severity = Severity.High, Rule = "R", Description = "d", Snippet = "s", SuggestedFix = "f" }
        };

        Assert.True(CmFrameworkScanner.AnyAtOrAboveSeverity(findings, Severity.Medium));
        Assert.True(CmFrameworkScanner.AnyAtOrAboveSeverity(findings, Severity.High));
        Assert.False(CmFrameworkScanner.AnyAtOrAboveSeverity(findings, Severity.Critical));
    }

    [Fact]
    public void Serialize_WritesSeverityAsStringName_AndStillReadsOldNumericForm()
    {
        var findings = new List<Finding>
        {
            new() { FilePath = "a.cs", RelativePath = "a.cs", LineNumber = 1, Severity = Severity.Critical, Rule = "R", Description = "d", Snippet = "s", SuggestedFix = "f" }
        };

        var json = FindingsIo.Serialize(findings);
        Assert.Contains("\"Severity\": \"Critical\"", json);

        // Scan files written before the string switch carried numbers (2 == Critical) -- report must still load them.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<List<Finding>>(
            """[{"FilePath":"a.cs","RelativePath":"a.cs","LineNumber":1,"Severity":2,"Rule":"R","Description":"d","Snippet":"s","SuggestedFix":"f"}]""");
        Assert.Equal(Severity.Critical, Assert.Single(legacy!).Severity);
    }

    [Fact]
    public void GetFindings_NestedLoop_PopulatesStructuredEscalation()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        foreach (var item in items)
                        {
                            entity.Load();
                        }
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "LoadInsideLoop");
        Assert.NotNull(finding.Escalation);
        Assert.Equal(Severity.High, finding.Escalation!.BaseSeverity);
        Assert.Contains(Escalation.NestedLoopReason, finding.Escalation.Reasons);
        Assert.Equal(2, finding.Escalation.NestedLoopDepth);
    }

    [Fact]
    public void GetFindings_SingleLoop_HasNoEscalation()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        entity.Load();
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "LoadInsideLoop");
        Assert.Null(finding.Escalation);
    }

    [Theory]
    [InlineData("TrackInMaterial", "TrackInMaterials")]
    [InlineData("CreateContainer", "CreateContainers")]
    [InlineData("AdjustResourceState", "AdjustResourcesState")]
    public void GetFindings_SingularOrchestrationCallInsideLoop_IsFlaggedWithThePluralSiblingNamed(string singular, string plural)
    {
        WriteOrchestrationFile("Foo.cs", $$"""
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        _materialOrchestration.{{singular}}(item);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "OrchestrationSingularInsideLoop");
        Assert.Equal(7, finding.LineNumber);
        Assert.Contains(plural, finding.SuggestedFix);
    }

    [Fact]
    public void GetFindings_BulkOrchestrationCallInsideLoop_IsNotFlagged()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var batch in batches)
                    {
                        _materialOrchestration.TrackInMaterials(batch);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "OrchestrationSingularInsideLoop");
    }

    [Fact]
    public void GetFindings_SingularOrchestrationCallOnNonOrchestrationReceiver_IsNotFlagged()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        _myHelper.TrackInMaterial(item);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "OrchestrationSingularInsideLoop");
    }

    [Theory]
    [InlineData("EmptyContainer")]
    [InlineData("MoveMaterialToStep")]
    [InlineData("TerminateContainer")]
    public void GetFindings_NoBulkOrchestrationCallInsideLoop_IsFlaggedWithoutClaimingABulkAlternative(string method)
    {
        WriteOrchestrationFile("Foo.cs", $$"""
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        _containerOrchestration.{{method}}(item);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "OrchestrationNoBulkInsideLoop");
        Assert.Equal(7, finding.LineNumber);
        Assert.Contains("No bulk alternative exists", finding.SuggestedFix);
    }

    [Fact]
    public void GetFindings_NoBulkOrchestrationCallOutsideLoop_IsNotFlagged()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    _containerOrchestration.EmptyContainer(input);
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "OrchestrationNoBulkInsideLoop");
    }

    [Fact]
    public void GetFindings_LoadInLocalFunctionCalledInsideLoop_IsFlagged()
    {
        // Scenario: the load isn't textually in a loop -- it's in a local function -- but the local
        // function is called by bare name inside a loop, so it runs once per iteration.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    void Helper(IMaterial material)
                    {
                        material.Load();
                    }

                    foreach (var lot in lots)
                    {
                        Helper(lot);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "LoadInsideLoop");
        Assert.Equal(7, finding.LineNumber);
        Assert.Equal(Severity.High, finding.Severity);
    }

    [Fact]
    public void GetFindings_LoadInLocalFunctionNeverCalledInLoop_IsNotFlagged()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    void Helper(IMaterial material)
                    {
                        material.Load();
                    }

                    Helper(oneLot);
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "LoadInsideLoop");
    }

    [Fact]
    public void GetFindings_LoadInLocalFunctionWithInnerLoopCalledInsideLoop_EscalatesToCritical()
    {
        // Call-in-a-loop (depth 1) compounds with the load's own inner loop (depth 1) -> nested (2) -> Critical.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    void Helper(IMaterialCollection materials)
                    {
                        foreach (var material in materials)
                        {
                            material.Load();
                        }
                    }

                    foreach (var batch in batches)
                    {
                        Helper(batch);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "LoadInsideLoop");
        Assert.Equal(Severity.Critical, finding.Severity);
    }

    [Fact]
    public void GetFindings_LoadInLocalFunctionCalledByAnotherLocalFunctionThatIsCalledInLoop_IsFlaggedTransitively()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    void Inner(IMaterial material)
                    {
                        material.Load();
                    }

                    void Outer(IMaterial material)
                    {
                        Inner(material);
                    }

                    foreach (var lot in lots)
                    {
                        Outer(lot);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "LoadInsideLoop");
    }

    [Fact]
    public void GetFindings_LoadInSelfRecursiveLocalFunctionNotCalledFromAnOuterLoop_IsNotFlagged()
    {
        // A recursive tree-walker calls itself inside a foreach, but the entry call is not in a
        // loop. Recursion is not the "load in a loop" pattern (no batch fix applies), so the load
        // guarding the recursion must not be flagged just because the self-call sits in a foreach.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    void Walk(IContainer container)
                    {
                        container.LoadRelations(SubContainer);
                        foreach (var child in container.SubContainers)
                        {
                            Walk(child);
                        }
                    }

                    Walk(root);
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "LoadInsideLoop" && f.Snippet.Contains("LoadRelations"));
    }

    [Fact]
    public void GetFindings_UnreadableFile_IsSkippedWithoutAbortingTheScan()
    {
        WriteOrchestrationFile("Good.cs", """
            public class Good
            {
                public void DoWork()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        entity.Load();
                    }
                }
            }
            """);
        var lockedPath = WriteOrchestrationFile("Locked.cs", "public class Locked { }");

        using var exclusiveLock = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "LoadInsideLoop" && f.RelativePath.EndsWith("Good.cs"));
        Assert.DoesNotContain(findings, f => f.RelativePath.EndsWith("Locked.cs"));
    }

    [Fact]
    public void GetFindings_NonCustomProjectPrefix_StillDetectsFindings()
    {
        // Reproduces the SemiconductorTemplate case: a customization project named "Semi" rather than
        // "Custom", with the flatter Cmf.<Project>.Data/DEEs shape (no nested *.Data.Packages folder).
        // Before CodeReviewPaths generalized root discovery, none of the hardcoded Cmf.Custom.* roots
        // existed on disk, so the scan silently resolved to zero files and reported zero findings.
        var businessDir = Path.Combine(_tempRoot, "Cmf.Semi.Business");
        Directory.CreateDirectory(businessDir);
        File.WriteAllText(Path.Combine(businessDir, "cmfpackage.json"), """{ "packageType": "Business" }""");

        var orchestrationDir = Path.Combine(businessDir, "Cmf.Semi.Orchestration");
        Directory.CreateDirectory(orchestrationDir);
        File.WriteAllText(Path.Combine(orchestrationDir, "Foo.cs"), """
            public class Foo
            {
                public void DoWork()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        entity.Load();
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "LoadInsideLoop" && f.RelativePath.EndsWith("Foo.cs"));
    }

    [Fact]
    public void GetFindings_NoManifestsResolveToBusinessOrData_ThrowsCliException()
    {
        // cmfpackage.json is the only supported project marker -- a repository with no manifest
        // resolving to a Business or Data package isn't one this tool can scan, so it fails loudly.
        Assert.Throws<CliException>(() => CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog));
    }

    [Fact]
    public void GetFindings_DiscoveredRootsContainNoCsFiles_WarnsOnStderrInsteadOfSilentlyReturningNothing()
    {
        // Roots can resolve correctly (real manifests, real folders) and still contain zero .cs files --
        // an empty DEEs folder, say. That's a different situation from an unrecognized repository layout
        // (which throws instead), but it's just as silent a "zero findings" outcome without this warning.
        var businessDir = Path.Combine(_tempRoot, "Cmf.Custom.Business");
        Directory.CreateDirectory(businessDir);
        File.WriteAllText(Path.Combine(businessDir, "cmfpackage.json"), """{ "packageType": "Business" }""");
        Directory.CreateDirectory(Path.Combine(businessDir, "Cmf.Custom.Orchestration"));

        var originalError = Console.Error;
        try
        {
            using var capturedError = new StringWriter();
            Console.SetError(capturedError);

            var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

            Assert.Empty(findings);
            Assert.Contains("no .cs files found", capturedError.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
