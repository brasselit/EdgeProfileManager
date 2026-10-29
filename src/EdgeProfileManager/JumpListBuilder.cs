using System.Globalization;
using System.Windows.Shell;
using EdgeProfileManager.Core.Config;
using EdgeProfileManager.Core.Edge;
using EdgeProfileManager.Resources;

namespace EdgeProfileManager;

/// <summary>
/// Writes the profiles to the jump list of the pinned taskbar button.
/// Jump lists have no submenus; nested groups become "Group › Subgroup".
/// </summary>
public sealed class JumpListBuilder(EdgeEnvironment environment)
{
    private string? _signature;

    public void Update(ResolvedMenu menu, JumpListStyle style)
    {
        var tasks = MenuResolver.Flatten(menu.Nodes)
            .Where(entry => entry.Node.Settings.JumpList)
            .Select(entry => CreateTask(entry.Groups, entry.Node, style))
            // Windows shows custom categories in reverse order (most recently added on top);
            // so reverse the categories but keep the entries within a category in menu order.
            .GroupBy(task => task.CustomCategory)
            .Reverse()
            .SelectMany(group => group)
            .ToList();

        // Only rewrite the jump list if something has changed.
        var signature = style + "\n" + string.Join("\n", tasks.Select(t => $"{t.CustomCategory}|{t.Title}|{t.Arguments}|{t.IconResourcePath}"));
        if (signature == _signature) return;
        _signature = signature;

        var jumpList = new JumpList(tasks, false, false);
        jumpList.JumpItemsRejected += (_, e) => AppLog.Write($"Jump list: {e.RejectedItems.Count} entries rejected.");
        jumpList.JumpItemsRemovedByUser += (_, e) => AppLog.Write($"Jump list: {e.RemovedItems.Count} entries removed by the user.");
        JumpList.SetJumpList(System.Windows.Application.Current, jumpList);
        jumpList.Apply();
        AppLog.Write($"Jump list updated ({tasks.Count} profiles, style {style}).");
    }

    private JumpTask CreateTask(IReadOnlyList<string> groups, ProfileNode node, JumpListStyle style)
    {
        var profile = node.Profile;
        return new JumpTask
        {
            Title = node.Title,
            Description = string.Format(CultureInfo.CurrentCulture, Strings.JumpList_Description, profile.Channel, profile.Name, profile.Folder) +
                (profile.User is null ? "" : $"\n{profile.User}"),
            ApplicationPath = ShellIntegration.ExecutablePath,
            Arguments = $"--open \"{profile.Key}\"",
            IconResourcePath = profile.IconPath ?? environment.FindExecutable(profile.Channel) ?? ShellIntegration.ExecutablePath,
            IconResourceIndex = 0,
            CustomCategory = style == JumpListStyle.Tasks ? null : groups.Count == 0 ? Strings.JumpList_Profiles : string.Join(" › ", groups),
        };
    }
}
