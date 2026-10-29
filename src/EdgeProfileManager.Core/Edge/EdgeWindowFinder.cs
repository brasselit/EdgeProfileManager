using EdgeProfileManager.Core.Interop;

namespace EdgeProfileManager.Core.Edge;

public sealed record EdgeWindow(nint Handle, uint ProcessId, EdgeChannel Channel, string Title, string? AppUserModelId, bool IsCloaked);

/// <summary>Finds the browser windows of all Edge channels and maps them to profiles.</summary>
public sealed class EdgeWindowFinder
{
    /// <summary>All Edge browser windows in Z order (most recently active first).</summary>
    public IReadOnlyList<EdgeWindow> GetWindows()
    {
        var pathCache = new Dictionary<uint, EdgeChannel?>();
        var windows = new List<EdgeWindow>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != 0) return true;
            if (Native.GetClassName(hwnd) != "Chrome_WidgetWin_1") return true;
            var title = Native.GetWindowText(hwnd);
            if (title.Length == 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (!pathCache.TryGetValue(pid, out var channel))
            {
                var path = Native.GetProcessPath(pid);
                channel = path is not null && path.EndsWith(@"\msedge.exe", StringComparison.OrdinalIgnoreCase)
                    ? EdgeChannels.FromExecutablePath(path)
                    : null;
                pathCache[pid] = channel;
            }
            if (channel is null) return true;
            var aumid = Native.GetAppUserModelId(hwnd);
            // Installed web apps (PWAs) have their own AUMID containing "_crx_" and are not browser windows.
            if (aumid?.Contains("_crx_", StringComparison.OrdinalIgnoreCase) == true) return true;
            windows.Add(new EdgeWindow(hwnd, pid, channel.Value, title, aumid, Native.IsCloaked(hwnd)));
            return true;
        }, 0);
        return windows;
    }

    public IReadOnlyList<EdgeWindow> GetWindows(EdgeProfile profile, IReadOnlyList<EdgeProfile> channelProfiles) =>
        GetWindows().Where(w => ProfileWindowMatcher.Matches(w, profile, channelProfiles)).ToList();
}

/// <summary>
/// Maps windows to profiles. Primarily via the AppUserModelID that Edge sets per profile:
/// "MSEdge" or "MSEdgeBeta" for Default, "MSEdge.UserData.Profile1" for "Profile 1".
/// Falls back to the window title "... – {profile name} – Microsoft Edge".
/// </summary>
public static class ProfileWindowMatcher
{
    public static bool Matches(EdgeWindow window, EdgeProfile profile, IReadOnlyList<EdgeProfile> channelProfiles) =>
        window.Channel == profile.Channel && FindProfile(window, channelProfiles)?.Folder == profile.Folder;

    /// <summary>Profile of a window; <paramref name="profiles"/> may contain profiles of all channels.</summary>
    public static EdgeProfile? FindProfile(EdgeWindow window, IReadOnlyList<EdgeProfile> profiles)
    {
        var channelProfiles = profiles.Where(p => p.Channel == window.Channel).ToList();
        return MatchByAppUserModelId(window.AppUserModelId, channelProfiles) ?? MatchByTitle(window.Title, channelProfiles);
    }

    internal static EdgeProfile? MatchByAppUserModelId(string? aumid, IReadOnlyList<EdgeProfile> channelProfiles)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        const string marker = ".UserData.";
        var index = aumid.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        // No profile suffix → Default profile, if present.
        if (index < 0) return channelProfiles.FirstOrDefault(p => p.Folder == "Default");
        var suffix = aumid[(index + marker.Length)..];
        return channelProfiles.FirstOrDefault(p => p.Folder.Replace(" ", string.Empty).Equals(suffix, StringComparison.OrdinalIgnoreCase));
    }

    internal static EdgeProfile? MatchByTitle(string title, IReadOnlyList<EdgeProfile> channelProfiles)
    {
        // Edge separates with " – " (en dash) and writes "Microsoft​ Edge" (with a zero-width space);
        // so accept both separators and only check for "Microsoft".
        return channelProfiles
            .OrderByDescending(p => p.Name.Length)
            .FirstOrDefault(p =>
                title.Contains($" – {p.Name} – Microsoft", StringComparison.Ordinal) ||
                title.Contains($" - {p.Name} - Microsoft", StringComparison.Ordinal));
    }
}
