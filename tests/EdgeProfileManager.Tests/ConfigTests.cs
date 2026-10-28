using EdgeProfileManager.Core.Config;
using EdgeProfileManager.Core.Edge;

namespace EdgeProfileManager.Tests;

internal sealed class ConfigTests
{
    private static readonly EdgeProfile[] Profiles =
    [
        new(EdgeChannel.Stable, "Default", "Privat", null, "", null),
        new(EdgeChannel.Stable, "Profile 3", "Kunde A", null, "", null),
        new(EdgeChannel.Beta, "Profile 1", "Archiv", null, "", null),
        new(EdgeChannel.Dev, "Profile 2", "Admin", null, "", null),
    ];

    private const string SampleJson =
        """
        {
          // Kommentare und nachgestellte Kommas sind erlaubt
          "defaultDesktopMode": "bringHere",
          "jumpListStyle": "tasks",
          "menu": [
            { "group": "Kunden", "children": [
              { "profile": "stable/profile 3", "label": "Kunde A", "desktop": 2, "jumpList": true },
              { "group": "Archiv", "children": [ { "profile": "Beta/Profile 1", "desktop": "Privat", "desktopMode": "switchTo" } ] },
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
        var kunden = config.Menu[0];
        Assert.Equal(new DesktopTarget(2, null), kunden.Children![0].Desktop);
        Assert.Equal(new DesktopTarget(null, "Privat"), kunden.Children[1].Children![0].Desktop);
        Assert.Equal(DesktopMode.SwitchTo, kunden.Children[1].Children![0].DesktopMode);
        Assert.True(config.Menu[1].Separator == true);
    }

    [Test]
    public void ResolvesTreeSettingsMissingAndUnassignedProfiles()
    {
        var menu = MenuResolver.Resolve(ConfigStore.Parse(SampleJson), Profiles);

        var kunden = Assert.IsType<GroupNode>(menu.Nodes[0]);
        var kundeA = Assert.IsType<ProfileNode>(kunden.Children[0]);
        Assert.Equal("Stable/Profile 3", kundeA.Profile.Key);
        // desktop gesetzt ohne desktopMode → auf zugewiesenen Desktop verschieben.
        Assert.Equal(DesktopMode.MoveToAssigned, kundeA.Settings.Mode);
        var archiv = Assert.IsType<GroupNode>(kunden.Children[1]);
        Assert.Equal(DesktopMode.SwitchTo, Assert.IsType<ProfileNode>(archiv.Children[0]).Settings.Mode);

        Assert.IsType<SeparatorNode>(menu.Nodes[1]);
        var privat = Assert.IsType<ProfileNode>(menu.Nodes[2]);
        Assert.Equal(DesktopMode.BringHere, privat.Settings.Mode);
        Assert.True(!privat.Settings.JumpList);
        Assert.IsType<MissingProfileNode>(menu.Nodes[3]);

        var unassigned = Assert.IsType<GroupNode>(menu.Nodes[4]);
        Assert.Equal(ResolvedMenu.UnassignedTitle, unassigned.Title);
        var admin = Assert.IsType<ProfileNode>(Assert.IsType<ProfileNode>(unassigned.Children.Single()));
        Assert.Equal("Admin (Dev)", admin.Title);
        Assert.True(!admin.Settings.JumpList, "Nicht zugeordnete Profile standardmässig nicht in der Jump List");
        Assert.True(menu.SettingsByKey.ContainsKey("Dev/Profile 2"));
    }

    [Test]
    public void FlattenReturnsGroupPath()
    {
        var menu = MenuResolver.Resolve(ConfigStore.Parse(SampleJson), Profiles);

        var flat = MenuResolver.Flatten(menu.Nodes).Select(e => $"{string.Join(" › ", e.Groups)}|{e.Node.Profile.Key}").ToList();

        Assert.SequenceEqual(
            ["Kunden|Stable/Profile 3", "Kunden › Archiv|Beta/Profile 1", "|Stable/Default", "Nicht zugeordnet|Dev/Profile 2"],
            flat);
    }

    [Test]
    public void DefaultConfigGroupsByChannelAndRoundTrips()
    {
        var store = new ConfigStore(Path.Combine(Path.GetTempPath(), $"epm_{Guid.NewGuid():N}", "config.json"));
        try
        {
            var created = store.LoadOrCreate(() => ConfigStore.CreateDefault(Profiles));
            var reloaded = store.LoadOrCreate(() => throw new InvalidOperationException("Datei sollte existieren"));

            Assert.SequenceEqual(["Edge Stable", "Edge Beta", "Edge Dev"], reloaded.Menu.Select(m => m.Group));
            Assert.SequenceEqual(["Stable/Default", "Stable/Profile 3"], reloaded.Menu[0].Children!.Select(c => c.Profile));
            Assert.Equal(created.Menu.Count, reloaded.Menu.Count);
            Assert.True(!File.ReadAllText(store.Path).Contains("\"label\""), "null-Werte werden nicht geschrieben");
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
        Assert.Equal("Unbekannt/Profile 2", MenuResolver.NormalizeReference("Unbekannt/Profile 2"));
    }
}
