using Cmf.Cli.Plugin.CodeReview;
using Xunit;

namespace codereview.tests;

public class DeeActionGroupRemovalScannerTests
{
    [Theory]
    [InlineData("action.Name = \"Dee\"; api.RemoveAction(new RemoveActionInput { Action = action, IsToRemoveAllVersions = true });", 0)]
    [InlineData("const string name = \"Dee\"; action.Name = name; action.RemoveActionGroups(action.ActionGroupActions);", 0)]
    [InlineData("action.Name = \"Dee\"; var selected = action.ActionGroupActions.Where(x => x.ActionGroup.Name.Equals(\"A\")); action.RemoveActionGroups(selected);", 1)]
    [InlineData("action.Name = \"Dee\"; var selected = service.CreateCollection(); selected.AddRange(action.ActionGroupActions.Where(x => x.ActionGroup.Name.Equals(\"A\"))); action.RemoveActionGroups(selected);", 1)]
    [InlineData("action.Name = \"Dee\"; var selected = action.ActionGroupActions.Where(x => x.ActionGroup.Name.Equals(\"A\") || x.ActionGroup.Name.Equals(\"B\")); action.RemoveActionGroups(selected);", 2)]
    [InlineData("first.Name = \"Dee\"; second.Name = \"Other\"; first.RemoveActionGroups(first.ActionGroupActions);", 2)]
    [InlineData("action.Name = GetName(); action.RemoveActionGroups(action.ActionGroupActions);", 2)]
    [InlineData("action.Name = \"Dee\"; api.RemoveAction(new RemoveActionInput { Action = action, IsToRemoveAllVersions = false });", 2)]
    public void RecognizedShapes_RemoveOnlySupportedWiring(string script, int remaining)
    {
        using var fixture = new ActionGroupFixture();
        var root = fixture.AddPackage();
        fixture.Write($"{root}/9.0/orders.json", ActionGroupFixture.Orders(("Dee", "A"), ("Dee", "B")));
        // Even an older removal remains authoritative over a later JSON re-add.
        fixture.Write("Cmf.Sample.Data/DEEs/ProcessRules/1.0/Before/Remove.cs", "class Migration { void Run() { " + script + " } }");
        var map = MasterDataActionGroupResolver.GetActionGroupMap(fixture.Root);
        Assert.Equal(remaining, map.GetValueOrDefault("Dee")?.Count ?? 0);
        if (remaining == 1) Assert.Equal("B", Assert.Single(map["Dee"]).ActionGroup);
    }

    [Fact]
    public void LastPairRemoval_DropsDeeAndRegularDeesDoNotRemoveWiring()
    {
        using var fixture = new ActionGroupFixture();
        var root = fixture.AddPackage();
        fixture.Write($"{root}/1.0/orders.json", ActionGroupFixture.Orders(("Dee", "A")));
        const string script = "class Migration { void Run() { action.Name = \"Dee\"; action.RemoveActionGroups(action.ActionGroupActions); } }";
        fixture.Write("Cmf.Sample.Data/DEEs/Regular.cs", script);
        Assert.Single(MasterDataActionGroupResolver.GetActionGroupMap(fixture.Root));
        fixture.Write("Cmf.Sample.Data/DEEs/ProcessRules/1.0/After/Remove.cs", script);
        Assert.Empty(MasterDataActionGroupResolver.GetActionGroupMap(fixture.Root));
    }

    [Fact]
    public void UnrelatedFilterCannotSelectTheRemovedGroup()
    {
        using var fixture = new ActionGroupFixture();
        var root = fixture.AddPackage();
        fixture.Write($"{root}/1.0/orders.json", ActionGroupFixture.Orders(("Dee", "A"), ("Dee", "B")));
        fixture.Write("Cmf.Sample.Data/DEEs/ProcessRules/1.0/Before/Remove.cs", """
            class Migration { void Run() {
                action.Name = "Dee";
                var unrelated = action.ActionGroupActions.Where(x => x.ActionGroup.Name.Equals("A"));
                var selected = action.ActionGroupActions.Where(x => x.ActionGroup.Name.Equals("B"));
                action.RemoveActionGroups(selected);
            } }
            """);
        Assert.Equal("A", Assert.Single(MasterDataActionGroupResolver.GetActionGroupMap(fixture.Root)["Dee"]).ActionGroup);
    }
}
