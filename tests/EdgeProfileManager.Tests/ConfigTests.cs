using EdgeProfileManager.Core.Config;
using EdgeProfileManager.Core.Edge;

namespace EdgeProfileManager.Tests;

internal sealed class ConfigTests
{
    private static readonly EdgeProfile[] Profiles =
    [
        new(EdgeChannel.Stable, "Default", "Personal", null, "", null),
        new(EdgeChannel.Stable, "Profile 3", "Customer A", null, "", null),
        new(EdgeChannel.Beta, "Profile 1", "Archive", null, "", null),
        new(EdgeChannel.Dev, "Profile 2", "Admin", null, "", null),
    ];

    private const string SampleJson =
        """
        {
          // Comments and trailing commas are allowed
          "defaultDesktopMode": "bringHere",
          "jumpListStyle": "tasks",
          "menu": [
            { "group": "Customers", "children": [
              { "profile": "stable/profile 3", "label": "Customer A", "desktop": 2, "jumpList": true },
              { "group": "Archive", "children": [ { "profile": "Beta/Profile 1", "desktop": "Personal", "desktopMode": "switchTo" } ] },
            ]},
            { "separator": true },
            { "profile": "Stable/Default", "jumpList": false },
            { "profile": "Canary/Profile 9" },
          ],
        }
        """;

    [Test]
    public void ParsesDesktopTargetsAndEnums()
    {
        var config = ConfigStore.Parse(SampleJson);

        Assert.Equal(DesktopMode.BringHere, config.DefaultDesktopMode);
        Assert.Equal(JumpListStyle.Tasks, config.JumpListStyle);
        var customers = config.Menu[0];
        Assert.Equal(new DesktopTarget(2, null), customers.Children![0].Desktop);
        Assert.Equal(new DesktopTarget(null, "Personal"), customers.Children[1].Children![0].Desktop);
        Assert.Equal(DesktopMode.SwitchTo, customers.Children[1].Children![0].DesktopMode);
        Assert.True(config.Menu[1].Separator == true);
    }

    [Test]
    public void ResolvesTreeSettingsMissingAndUnassignedProfiles()
    {
        var menu = MenuResolver.Resolve(ConfigStore.Parse(SampleJson), Profiles);

        var customers = Assert.IsType<GroupNode>(menu.Nodes[0]);
        var customerA = Assert.IsType<ProfileNode>(customers.Children[0]);
        Assert.Equal("Stable/Profile 3", customerA.Profile.Key);
        // desktop set without desktopMode → move to assigned desktop.
        Assert.Equal(DesktopMode.MoveToAssigned, customerA.Settings.Mode);
        var archive = Assert.IsType<GroupNode>(customers.Children[1]);
        Assert.Equal(DesktopMode.SwitchTo, Assert.IsType<ProfileNode>(archive.Children[0]).Settings.Mode);

        Assert.IsType<SeparatorNode>(menu.Nodes[1]);
        var personal = Assert.IsType<ProfileNode>(menu.Nodes[2]);
        Assert.Equal(DesktopMode.BringHere, personal.Settings.Mode);
        Assert.True(!personal.Settings.JumpList);
        Assert.IsType<MissingProfileNode>(menu.Nodes[3]);

        var unassigned = Assert.IsType<GroupNode>(menu.Nodes[4]);
        Assert.Equal(ResolvedMenu.UnassignedTitle, unassigned.Title);
        var admin = Assert.IsType<ProfileNode>(Assert.IsType<ProfileNode>(unassigned.Children.Single()));
        Assert.Equal("Admin (Dev)", admin.Title);
        Assert.True(!admin.Settings.JumpList, "unassigned profiles are not in the Jump List by default");
        Assert.True(menu.SettingsByKey.ContainsKey("Dev/Profile 2"));
    }

    [Test]
    public void FlattenReturnsGroupPath()
    {
        var menu = MenuResolver.Resolve(ConfigStore.Parse(SampleJson), Profiles);

        var flat = MenuResolver.Flatten(menu.Nodes).Select(e => $"{string.Join(" › ", e.Groups)}|{e.Node.Profile.Key}").ToList();

        Assert.SequenceEqual(
            ["Customers|Stable/Profile 3", "Customers › Archive|Beta/Profile 1", "|Stable/Default", "Unassigned|Dev/Profile 2"],
            flat);
    }

    [Test]
    public void DefaultConfigGroupsByChannelAndRoundTrips()
    {
        var store = new ConfigStore(Path.Combine(Path.GetTempPath(), $"epm_{Guid.NewGuid():N}", "config.json"));
        try
        {
            var created = store.LoadOrCreate(() => ConfigStore.CreateDefault(Profiles));
            var reloaded = store.LoadOrCreate(() => throw new InvalidOperationException("File should exist"));

            Assert.SequenceEqual(["Edge Stable", "Edge Beta", "Edge Dev"], reloaded.Menu.Select(m => m.Group));
            Assert.SequenceEqual(["Stable/Default", "Stable/Profile 3"], reloaded.Menu[0].Children!.Select(c => c.Profile));
            Assert.Equal(created.Menu.Count, reloaded.Menu.Count);
            Assert.True(!File.ReadAllText(store.Path).Contains("\"label\""), "null values are not written");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(store.Path)!, recursive: true);
        }
    }

    [Test]
    public void InvalidJsonThrows()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => ConfigStore.Parse("{ \"menu\": [ { \"profile\": \"x\", \"desktop\": true } ] }"));
    }

    [Test]
    public void NormalizesReferences()
    {
        Assert.Equal("Canary/Profile 2", MenuResolver.NormalizeReference("canary/Profile 2"));
        Assert.Equal("Unknown/Profile 2", MenuResolver.NormalizeReference("Unknown/Profile 2"));
    }
}
