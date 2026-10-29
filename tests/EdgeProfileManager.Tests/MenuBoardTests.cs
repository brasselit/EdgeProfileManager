using EdgeProfileManager.Core.Config;
using EdgeProfileManager.Core.Edge;

namespace EdgeProfileManager.Tests;

internal sealed class MenuBoardTests
{
    private static readonly EdgeProfile[] Profiles =
    [
        new(EdgeChannel.Stable, "Default", "Personal", null, "", null),
        new(EdgeChannel.Stable, "Profile 3", "Customer A", null, "", null),
        new(EdgeChannel.Beta, "Profile 1", "Archive", null, "", null),
        new(EdgeChannel.Dev, "Profile 2", "Admin", null, "", null),
    ];

    private static AppConfig Sample() => ConfigStore.Parse(
        """
        {
          "menu": [
            { "group": "Customers", "children": [
              { "profile": "stable/profile 3", "label": "Customer A" },
              { "separator": true },
              { "group": "Archive", "children": [ { "profile": "Beta/Profile 1" } ] }
            ]},
            { "separator": true },
            { "group": "Empty", "children": [] },
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

        Assert.SequenceEqual(["Main menu", "Customers", "Empty", "Unassigned"], lanes.Select(l => l.Title));
        Assert.SequenceEqual(["Personal", "Canary/Profile 9?"], lanes[0].Items.Select(Describe));
        Assert.SequenceEqual(["Customer A", "---", "[Archive]", ".Archive"], lanes[1].Items.Select(Describe));
        Assert.Equal(0, lanes[2].Items.Count);
        var unassigned = Assert.IsType<BoardCard>(lanes[3].Items.Single());
        Assert.Equal("Dev/Profile 2", unassigned.Reference);
        Assert.True(unassigned.Entry is null);
    }

    [Test]
    public void MovesAddsAndRemovesEntries()
    {
        var config = Sample();
        var customers = config.Menu[0];
        var empty = config.Menu[2];
        var customerA = customers.Children![0];
        var personal = config.Menu[3];

        // Move to another group, then before an entry of a subgroup.
        MenuBoard.Place(config, customerA, MenuBoard.ChildrenOf(config, empty), null);
        Assert.SequenceEqual(["Customer A"], MenuBoard.Build(config, Profiles)[2].Items.Select(Describe));
        var archiveEntry = customers.Children[1].Children![0];
        MenuBoard.Place(config, personal, MenuBoard.FindParent(config, archiveEntry)!, archiveEntry);
        Assert.SequenceEqual(["---", "[Archive]", ".Personal", ".Archive"], MenuBoard.Build(config, Profiles)[1].Items.Select(Describe));
        Assert.Equal("Customers › Archive", MenuBoard.GetGroups(config).Single(g => ReferenceEquals(g.Group, MenuBoard.FindParentGroup(config, personal))).Path);

        // Add from "Unassigned" and remove again.
        var admin = MenuBoard.Add(MenuBoard.ChildrenOf(config, null), "Dev/Profile 2", null);
        Assert.Equal(0, MenuBoard.Build(config, Profiles)[^1].Items.Count);
        Assert.True(MenuBoard.Remove(config, admin));
        Assert.SequenceEqual(["Admin"], MenuBoard.Build(config, Profiles)[^1].Items.Select(Describe));
    }

    [Test]
    public void MovesGroupsAmongGroupsOnly()
    {
        var config = Sample();
        var empty = config.Menu[2];

        MenuBoard.MoveGroup(config, empty, -1);
        Assert.SequenceEqual(["Empty", null, "Customers"], config.Menu.Take(3).Select(e => e.Group));
        Assert.True(config.Menu[1].Separator == true, "separator keeps its position");
        MenuBoard.MoveGroup(config, empty, -1);
        Assert.Equal("Empty", config.Menu[0].Group);

        Assert.Throws<InvalidOperationException>(() =>
            MenuBoard.Place(config, config.Menu[2], MenuBoard.ChildrenOf(config, config.Menu[2].Children![2]), null));
    }
}
