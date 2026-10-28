using EdgeProfileManager.Core.Interop;

namespace EdgeProfileManager.Core.Edge;

public sealed record EdgeWindow(nint Handle, uint ProcessId, EdgeChannel Channel, string Title, string? AppUserModelId, bool IsCloaked);

/// <summary>Findet die Browserfenster aller Edge-Channels und ordnet sie Profilen zu.</summary>
public sealed class EdgeWindowFinder
{
    /// <summary>Alle Edge-Browserfenster in Z-Reihenfolge (zuletzt aktives zuerst).</summary>
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
            // Installierte Web-Apps (PWAs) haben eine eigene AUMID mit "_crx_" und sind keine Browserfenster.
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
/// Zuordnung Fenster → Profil. Primär über die AppUserModelID, die Edge pro Profil setzt:
/// "MSEdge" bzw. "MSEdgeBeta" für Default, "MSEdge.UserData.Profile1" für "Profile 1".
/// Fallback über den Fenstertitel "... – {Profilname} – Microsoft Edge".
/// </summary>
public static class ProfileWindowMatcher
{
    public static bool Matches(EdgeWindow window, EdgeProfile profile, IReadOnlyList<EdgeProfile> channelProfiles) =>
        window.Channel == profile.Channel && FindProfile(window, channelProfiles)?.Folder == profile.Folder;

    /// <summary>Profil eines Fensters; <paramref name="profiles"/> darf Profile aller Channels enthalten.</summary>
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
        // Kein Profil-Suffix → Default-Profil, sofern vorhanden.
        if (index < 0) return channelProfiles.FirstOrDefault(p => p.Folder == "Default");
        var suffix = aumid[(index + marker.Length)..];
        return channelProfiles.FirstOrDefault(p => p.Folder.Replace(" ", string.Empty).Equals(suffix, StringComparison.OrdinalIgnoreCase));
    }

    internal static EdgeProfile? MatchByTitle(string title, IReadOnlyList<EdgeProfile> channelProfiles)
    {
        // Edge trennt mit " – " (Halbgeviertstrich) und schreibt "Microsoft​ Edge";
        // deshalb beide Trennzeichen zulassen und nur auf "Microsoft" prüfen.
        return channelProfiles
            .OrderByDescending(p => p.Name.Length)
            .FirstOrDefault(p =>
                title.Contains($" – {p.Name} – Microsoft", StringComparison.Ordinal) ||
                title.Contains($" - {p.Name} - Microsoft", StringComparison.Ordinal));
    }
}
