using EdgeProfileManager.Core.Edge;

namespace EdgeProfileManager.Core.Config;

public abstract record MenuNode;
public sealed record GroupNode(string Title, IReadOnlyList<MenuNode> Children) : MenuNode;
public sealed record ProfileNode(EdgeProfile Profile, string Title, ProfileSettings Settings) : MenuNode;
public sealed record MissingProfileNode(string Reference, string Title) : MenuNode;
public sealed record SeparatorNode : MenuNode;

public sealed record ProfileSettings(DesktopTarget? Desktop, DesktopMode Mode, bool JumpList);

public sealed record ResolvedMenu(IReadOnlyList<MenuNode> Nodes, IReadOnlyDictionary<string, ProfileSettings> SettingsByKey)
{
    public const string UnassignedTitle = "Nicht zugeordnet";
}

/// <summary>Verbindet den Menübaum der Konfiguration mit den gefundenen Profilen.</summary>
public static class MenuResolver
{
    public static ResolvedMenu Resolve(AppConfig config, IReadOnlyList<EdgeProfile> profiles)
    {
        var byKey = profiles.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        var settings = new Dictionary<string, ProfileSettings>(StringComparer.OrdinalIgnoreCase);
        var nodes = ResolveEntries(config.Menu, config, byKey, settings);

        if (config.ShowUnassigned)
        {
            var unassigned = profiles
                .Where(p => !settings.ContainsKey(p.Key))
                .Select(p => (MenuNode)new ProfileNode(p, $"{p.Name} ({p.Channel})", DefaultSettings(config, config.JumpListUnassigned)))
                .ToList();
            foreach (var node in unassigned.Cast<ProfileNode>()) settings[node.Profile.Key] = node.Settings;
            if (unassigned.Count > 0) nodes.Add(new GroupNode(ResolvedMenu.UnassignedTitle, unassigned));
        }
        return new ResolvedMenu(nodes, settings);
    }

    public static ProfileSettings DefaultSettings(AppConfig config, bool jumpList = true) => new(null, config.DefaultDesktopMode, jumpList);

    private static List<MenuNode> ResolveEntries(
        IEnumerable<MenuEntry>? entries, AppConfig config,
        IReadOnlyDictionary<string, EdgeProfile> byKey, Dictionary<string, ProfileSettings> settings)
    {
        var result = new List<MenuNode>();
        foreach (var entry in entries ?? [])
        {
            if (entry.Separator == true)
            {
                result.Add(new SeparatorNode());
            }
            else if (entry.Group is not null)
            {
                result.Add(new GroupNode(entry.Group, ResolveEntries(entry.Children, config, byKey, settings)));
            }
            else if (entry.Profile is not null)
            {
                var reference = NormalizeReference(entry.Profile);
                if (byKey.TryGetValue(reference, out var profile))
                {
                    var mode = entry.DesktopMode ?? (entry.Desktop is not null ? DesktopMode.MoveToAssigned : config.DefaultDesktopMode);
                    var profileSettings = new ProfileSettings(entry.Desktop, mode, entry.JumpList ?? true);
                    settings.TryAdd(profile.Key, profileSettings);
                    result.Add(new ProfileNode(profile, entry.Label ?? profile.Name, profileSettings));
                }
                else
                {
                    result.Add(new MissingProfileNode(entry.Profile, $"{entry.Label ?? entry.Profile} (nicht gefunden)"));
                }
            }
        }
        return result;
    }

    /// <summary>"stable/profile 1" → "Stable/Profile 1"; unbekannte Channels bleiben unverändert.</summary>
    public static string NormalizeReference(string reference)
    {
        var parts = reference.Split('/', 2);
        if (parts.Length != 2 || !EdgeChannels.TryParse(parts[0], out var channel)) return reference;
        return $"{channel}/{parts[1].Trim()}";
    }

    /// <summary>Alle Profilknoten in Menüreihenfolge, mit Pfad der übergeordneten Gruppen.</summary>
    public static IEnumerable<(IReadOnlyList<string> Groups, ProfileNode Node)> Flatten(IEnumerable<MenuNode> nodes) =>
        Flatten(nodes, []);

    private static IEnumerable<(IReadOnlyList<string>, ProfileNode)> Flatten(IEnumerable<MenuNode> nodes, IReadOnlyList<string> path)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case ProfileNode profile:
                    yield return (path, profile);
                    break;
                case GroupNode group:
                    foreach (var child in Flatten(group.Children, [.. path, group.Title])) yield return child;
                    break;
            }
        }
    }
}
