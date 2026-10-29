using EdgeProfileManager.Core.Edge;

namespace EdgeProfileManager.Core.Config;

public enum LaneKind { Root, Group, Unassigned }

/// <summary>A column in the menu board: main menu, a top-level group or "Unassigned".</summary>
public sealed record BoardLane(LaneKind Kind, string Title, MenuEntry? Group, IReadOnlyList<BoardItem> Items)
{
    public const string RootTitle = "Main menu";
}

public abstract record BoardItem(int Depth);

/// <summary>Profile card. <see cref="Entry"/> null = unassigned, <see cref="Profile"/> null = dangling reference.</summary>
public sealed record BoardCard(MenuEntry? Entry, EdgeProfile? Profile, string Reference, int Depth) : BoardItem(Depth)
{
    public string Title => Entry?.Label ?? Profile?.Name ?? Reference;
}

/// <summary>Subgroup within a column; its entries follow with greater depth.</summary>
public sealed record BoardHeader(MenuEntry Group, int Depth) : BoardItem(Depth);

public sealed record BoardSeparator(MenuEntry Entry, int Depth) : BoardItem(Depth);

/// <summary>Group with path for selection lists, e.g. "Customers › Archive".</summary>
public sealed record BoardGroup(MenuEntry Group, string Path);

/// <summary>
/// View of the menu tree as a board (column = top-level group) and editing operations on it.
/// All changes apply directly to the given configuration.
/// </summary>
public static class MenuBoard
{
    public static IReadOnlyList<BoardLane> Build(AppConfig config, IReadOnlyList<EdgeProfile> profiles)
    {
        var byKey = profiles.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        var lanes = new List<BoardLane>
        {
            new(LaneKind.Root, BoardLane.RootTitle, null,
                config.Menu.Where(e => e.Group is null && e.Profile is not null).Select(e => Card(e, 0)).ToList()),
        };
        foreach (var group in config.Menu.Where(e => e.Group is not null))
        {
            var items = new List<BoardItem>();
            AddItems(items, group.Children, 0);
            lanes.Add(new BoardLane(LaneKind.Group, group.Group!, group, items));
        }

        var referenced = ProfileEntries(config.Menu).Select(e => MenuResolver.NormalizeReference(e.Profile!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lanes.Add(new BoardLane(LaneKind.Unassigned, ResolvedMenu.UnassignedTitle, null,
            profiles.Where(p => !referenced.Contains(p.Key)).Select(p => new BoardCard(null, p, p.Key, 0)).ToList()));
        return lanes;

        void AddItems(List<BoardItem> items, IEnumerable<MenuEntry>? entries, int depth)
        {
            foreach (var entry in entries ?? [])
            {
                if (entry.Separator == true) items.Add(new BoardSeparator(entry, depth));
                else if (entry.Group is not null)
                {
                    items.Add(new BoardHeader(entry, depth));
                    AddItems(items, entry.Children, depth + 1);
                }
                else if (entry.Profile is not null) items.Add(Card(entry, depth));
            }
        }

        BoardCard Card(MenuEntry entry, int depth)
        {
            var reference = MenuResolver.NormalizeReference(entry.Profile!);
            return new BoardCard(entry, byKey.GetValueOrDefault(reference), reference, depth);
        }
    }

    /// <summary>All groups in menu order, nested ones with their path.</summary>
    public static IReadOnlyList<BoardGroup> GetGroups(AppConfig config)
    {
        var result = new List<BoardGroup>();
        Collect(config.Menu, null);
        return result;

        void Collect(IEnumerable<MenuEntry>? entries, string? path)
        {
            foreach (var entry in entries ?? [])
            {
                if (entry.Group is null) continue;
                var current = path is null ? entry.Group : $"{path} › {entry.Group}";
                result.Add(new BoardGroup(entry, current));
                Collect(entry.Children, current);
            }
        }
    }

    /// <summary>List that holds the entries of the group; null = main menu.</summary>
    public static List<MenuEntry> ChildrenOf(AppConfig config, MenuEntry? group) => group is null ? config.Menu : group.Children ??= [];

    /// <summary>List that contains the entry (compared by reference).</summary>
    public static List<MenuEntry>? FindParent(AppConfig config, MenuEntry entry)
    {
        return Find(config.Menu);

        List<MenuEntry>? Find(List<MenuEntry>? entries)
        {
            if (entries is null) return null;
            if (entries.Any(e => ReferenceEquals(e, entry))) return entries;
            return entries.Select(e => Find(e.Children)).FirstOrDefault(found => found is not null);
        }
    }

    /// <summary>Group that directly contains the entry; null = main menu or not found.</summary>
    public static MenuEntry? FindParentGroup(AppConfig config, MenuEntry entry)
    {
        var parent = FindParent(config, entry);
        return GetGroups(config).FirstOrDefault(g => ReferenceEquals(g.Group.Children, parent))?.Group;
    }

    /// <summary>Moves an entry into <paramref name="target"/> before <paramref name="before"/> (null = to the end).</summary>
    public static void Place(AppConfig config, MenuEntry entry, List<MenuEntry> target, MenuEntry? before)
    {
        if (ReferenceEquals(entry, before)) return;
        if (entry.Group is not null && Contains(entry, target)) throw new InvalidOperationException("A group cannot be moved into itself.");
        FindParent(config, entry)?.Remove(entry);
        Insert(target, entry, before);
    }

    /// <summary>Adds a profile to the menu.</summary>
    public static MenuEntry Add(List<MenuEntry> target, string profileKey, MenuEntry? before)
    {
        var entry = new MenuEntry { Profile = profileKey };
        Insert(target, entry, before);
        return entry;
    }

    public static bool Remove(AppConfig config, MenuEntry entry) => FindParent(config, entry)?.Remove(entry) == true;

    public static MenuEntry AddGroup(AppConfig config, string title)
    {
        var group = new MenuEntry { Group = title, Children = [] };
        config.Menu.Add(group);
        return group;
    }

    /// <summary>Moves a top-level group one position among the groups to the left (-1) or right (+1).</summary>
    public static void MoveGroup(AppConfig config, MenuEntry group, int direction)
    {
        var groups = config.Menu.Where(e => e.Group is not null).ToList();
        var index = groups.IndexOf(group);
        var neighbour = index + Math.Sign(direction);
        if (index < 0 || neighbour < 0 || neighbour >= groups.Count) return;
        var a = config.Menu.IndexOf(group);
        var b = config.Menu.IndexOf(groups[neighbour]);
        (config.Menu[a], config.Menu[b]) = (config.Menu[b], config.Menu[a]);
    }

    private static void Insert(List<MenuEntry> target, MenuEntry entry, MenuEntry? before)
    {
        var index = before is null ? -1 : target.FindIndex(e => ReferenceEquals(e, before));
        target.Insert(index < 0 ? target.Count : index, entry);
    }

    private static bool Contains(MenuEntry group, List<MenuEntry> list) =>
        ReferenceEquals(group.Children, list) || (group.Children ?? []).Any(c => c.Group is not null && Contains(c, list));

    private static IEnumerable<MenuEntry> ProfileEntries(IEnumerable<MenuEntry>? entries) =>
        (entries ?? []).SelectMany(e => e.Profile is not null ? [e] : ProfileEntries(e.Children));
}
