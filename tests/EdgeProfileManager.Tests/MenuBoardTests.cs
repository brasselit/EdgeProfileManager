using EdgeProfileManager.Core.Config;
using EdgeProfileManager.Core.Edge;

namespace EdgeProfileManager.Tests;

internal sealed class MenuBoardTests
{
    private static readonly EdgeProfile[] Profiles =
    [
        new(EdgeChannel.Stable, "Default", "Privat", null, "", null),
        new(EdgeChannel.Stable, "Profile 3", "Kunde A", null, "", null),
        new(EdgeChannel.Beta, "Profile 1", "Archiv", null, "", null),
        new(EdgeChannel.Dev, "Profile 2", "Admin", null, "", null),
    ];

    private static AppConfig Sample() => ConfigStore.Parse(
        """
        {
          "menu": [
            { "group": "Kunden", "children": [
              { "profile": "stable/profile 3", "label": "Kunde A" },
              { "separator": true },
              { "group": "Archiv", "children": [ { "profile": "Beta/Profile 1" } ] }
            ]},
            { "separator": true },
            { "group": "Leer", "children": [] },
            { "profile": "Stable/Default" },
            { "profile": "Canary/Profile 9" }
          ]
        }
        """);

    private static string Describe(BoardItem item) => item switch
    {
        BoardCard c => $"{new string('.', c.Depth)}{c.Title}{(c.Profile is null ? "?" : "")}",
        BoardHeader h => $"{new string('.', h.Depth)}[{h.Group.Group}]",
        BoardSeparator s => $"{new string('.', s.Depth)}---",
        _ => "",
    };

    [Test]
    public void BuildsRootGroupAndUnassignedLanes()
    {
        var lanes = MenuBoard.Build(Sample(), Profiles);

        Assert.SequenceEqual(["Hauptmenü", "Kunden", "Leer", "Nicht zugeordnet"], lanes.Select(l => l.Title));
        Assert.SequenceEqual(["Privat", "Canary/Profile 9?"], lanes[0].Items.Select(Describe));
        Assert.SequenceEqual(["Kunde A", "---", "[Archiv]", ".Archiv"], lanes[1].Items.Select(Describe));
        Assert.Equal(0, lanes[2].Items.Count);
        var unassigned = Assert.IsType<BoardCard>(lanes[3].Items.Single());
        Assert.Equal("Dev/Profile 2", unassigned.Reference);
        Assert.True(unassigned.Entry is null);
    }

    [Test]
    public void MovesAddsAndRemovesEntries()
    {
        var config = Sample();
        var kunden = config.Menu[0];
        var leer = config.Menu[2];
        var kundeA = kunden.Children![0];
        var privat = config.Menu[3];

        // In andere Gruppe verschieben, dann vor einen Eintrag einer Untergruppe.
        MenuBoard.Place(config, kundeA, MenuBoard.ChildrenOf(config, leer), null);
        Assert.SequenceEqual(["Kunde A"], MenuBoard.Build(config, Profiles)[2].Items.Select(Describe));
        var archivEntry = kunden.Children[1].Children![0];
        MenuBoard.Place(config, privat, MenuBoard.FindParent(config, archivEntry)!, archivEntry);
        Assert.SequenceEqual(["---", "[Archiv]", ".Privat", ".Archiv"], MenuBoard.Build(config, Profiles)[1].Items.Select(Describe));
        Assert.Equal("Kunden › Archiv", MenuBoard.GetGroups(config).Single(g => ReferenceEquals(g.Group, MenuBoard.FindParentGroup(config, privat))).Path);

        // Aufnehmen aus "Nicht zugeordnet" und wieder entfernen.
        var admin = MenuBoard.Add(MenuBoard.ChildrenOf(config, null), "Dev/Profile 2", null);
        Assert.Equal(0, MenuBoard.Build(config, Profiles)[^1].Items.Count);
        Assert.True(MenuBoard.Remove(config, admin));
        Assert.SequenceEqual(["Admin"], MenuBoard.Build(config, Profiles)[^1].Items.Select(Describe));
    }

    [Test]
    public void MovesGroupsAmongGroupsOnly()
    {
        var config = Sample();
        var leer = config.Menu[2];

        MenuBoard.MoveGroup(config, leer, -1);
        Assert.SequenceEqual(["Leer", null, "Kunden"], config.Menu.Take(3).Select(e => e.Group));
        Assert.True(config.Menu[1].Separator == true, "Trennlinie bleibt an ihrer Position");
        MenuBoard.MoveGroup(config, leer, -1);
        Assert.Equal("Leer", config.Menu[0].Group);

        Assert.Throws<InvalidOperationException>(() =>
            MenuBoard.Place(config, config.Menu[2], MenuBoard.ChildrenOf(config, config.Menu[2].Children![2]), null));
    }
}
