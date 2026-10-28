using System.Windows.Shell;
using EdgeProfileManager.Core.Config;
using EdgeProfileManager.Core.Edge;

namespace EdgeProfileManager;

/// <summary>
/// Schreibt die Profile in die Jump List der angehefteten Taskbar-Schaltfläche.
/// Jump Lists kennen keine Untermenüs; verschachtelte Gruppen werden zu "Gruppe › Untergruppe".
/// </summary>
public sealed class JumpListBuilder(EdgeEnvironment environment)
{
    private const string TopLevelCategory = "Profile";
    private string? _signature;

    public void Update(ResolvedMenu menu, JumpListStyle style)
    {
        var tasks = MenuResolver.Flatten(menu.Nodes)
            .Where(entry => entry.Node.Settings.JumpList)
            .Select(entry => CreateTask(entry.Groups, entry.Node, style))
            // Windows zeigt eigene Kategorien in umgekehrter Reihenfolge an (zuletzt angelegte oben);
            // deshalb Kategorien umkehren, Einträge innerhalb einer Kategorie aber in Menüreihenfolge lassen.
            .GroupBy(task => task.CustomCategory)
            .Reverse()
            .SelectMany(group => group)
            .ToList();

        // Jump List nur neu schreiben, wenn sich etwas geändert hat.
        var signature = style + "\n" + string.Join("\n", tasks.Select(t => $"{t.CustomCategory}|{t.Title}|{t.Arguments}|{t.IconResourcePath}"));
        if (signature == _signature) return;
        _signature = signature;

        var jumpList = new JumpList(tasks, false, false);
        jumpList.JumpItemsRejected += (_, e) => AppLog.Write($"Jump List: {e.RejectedItems.Count} Einträge abgelehnt.");
        jumpList.JumpItemsRemovedByUser += (_, e) => AppLog.Write($"Jump List: {e.RemovedItems.Count} Einträge vom Benutzer entfernt.");
        JumpList.SetJumpList(System.Windows.Application.Current, jumpList);
        jumpList.Apply();
        AppLog.Write($"Jump List aktualisiert ({tasks.Count} Profile, Stil {style}).");
    }

    private JumpTask CreateTask(IReadOnlyList<string> groups, ProfileNode node, JumpListStyle style)
    {
        var profile = node.Profile;
        return new JumpTask
        {
            Title = node.Title,
            Description = $"Edge {profile.Channel} – {profile.Name} ({profile.Folder}){(profile.User is null ? "" : $"\n{profile.User}")}",
            ApplicationPath = ShellIntegration.ExecutablePath,
            Arguments = $"--open \"{profile.Key}\"",
            IconResourcePath = profile.IconPath ?? environment.FindExecutable(profile.Channel) ?? ShellIntegration.ExecutablePath,
            IconResourceIndex = 0,
            CustomCategory = style == JumpListStyle.Tasks ? null : groups.Count == 0 ? TopLevelCategory : string.Join(" › ", groups),
        };
    }
}
