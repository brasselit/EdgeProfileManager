using System.Diagnostics;
using EdgeProfileManager.Core.Config;
using EdgeProfileManager.Core.Desktops;
using EdgeProfileManager.Core.Interop;

namespace EdgeProfileManager.Core.Edge;

public enum LaunchOutcome { Activated, Started }

/// <summary>Startet ein Profil oder wechselt zu dessen Fenster (Activate-or-Launch).</summary>
public sealed class ProfileLauncher(EdgeEnvironment environment, ProfileDiscovery discovery, EdgeWindowFinder finder, IVirtualDesktopService desktops)
{
    private static readonly TimeSpan NewWindowTimeout = TimeSpan.FromSeconds(10);

    public event Action<string>? Log;

    public LaunchOutcome Open(EdgeProfile profile, ProfileSettings settings, bool forceNewWindow = false)
    {
        var channelProfiles = discovery.GetProfiles(profile.Channel);
        var windows = finder.GetWindows(profile, channelProfiles);
        if (!forceNewWindow && windows.Count > 0)
        {
            var window = PickWindow(windows);
            PlaceExistingWindow(window, settings);
            Activate(window.Handle);
            return LaunchOutcome.Activated;
        }

        var known = finder.GetWindows().Select(w => w.Handle).ToHashSet();
        Start(profile);
        var target = desktops.Resolve(settings.Desktop);
        if (target is not null)
        {
            // Edge öffnet das Fenster asynchron; danach auf den zugewiesenen Desktop verschieben.
            _ = Task.Run(() => MoveNewWindowAsync(profile, channelProfiles, known, target.Value));
        }
        return LaunchOutcome.Started;
    }

    public void Start(EdgeProfile profile)
    {
        var exe = environment.FindExecutable(profile.Channel)
            ?? throw new InvalidOperationException($"Edge {profile.Channel} Anwendung nicht gefunden.");
        Process.Start(new ProcessStartInfo(exe) { ArgumentList = { $"--profile-directory={profile.Folder}" }, UseShellExecute = false })?.Dispose();
        Log?.Invoke($"Gestartet: Edge {profile.Channel} – {profile.Name} ({profile.Folder})");
    }

    /// <summary>
    /// Zuletzt aktives Fenster des Profils. Ist es bereits im Vordergrund, wird zum nächsten
    /// Fenster desselben Profils gewechselt, damit wiederholte Klicks durch die Fenster blättern.
    /// </summary>
    private static EdgeWindow PickWindow(IReadOnlyList<EdgeWindow> windows)
    {
        var foreground = Native.GetForegroundWindow();
        if (windows.Count > 1 && windows[0].Handle == foreground) return windows[^1];
        return windows[0];
    }

    private void PlaceExistingWindow(EdgeWindow window, ProfileSettings settings)
    {
        if (!desktops.IsAvailable) return;
        var current = desktops.CurrentDesktop;
        var windowDesktop = desktops.GetWindowDesktop(window.Handle);
        switch (settings.Mode)
        {
            case DesktopMode.BringHere when windowDesktop is not null && windowDesktop != current:
                desktops.MoveWindowToDesktop(window.Handle, current);
                break;
            case DesktopMode.MoveToAssigned when desktops.Resolve(settings.Desktop) is { } assigned:
                if (windowDesktop != assigned) desktops.MoveWindowToDesktop(window.Handle, assigned);
                if (assigned != current) desktops.GoToDesktop(assigned);
                break;
            case DesktopMode.SwitchTo or DesktopMode.MoveToAssigned when windowDesktop is not null && windowDesktop != current:
                desktops.GoToDesktop(windowDesktop.Value);
                break;
        }
    }

    private async Task MoveNewWindowAsync(EdgeProfile profile, IReadOnlyList<EdgeProfile> channelProfiles, HashSet<nint> known, int desktop)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < NewWindowTimeout)
        {
            await Task.Delay(250).ConfigureAwait(false);
            var window = finder.GetWindows()
                .FirstOrDefault(w => !known.Contains(w.Handle) && ProfileWindowMatcher.Matches(w, profile, channelProfiles));
            if (window is null) continue;
            if (desktops.GetWindowDesktop(window.Handle) != desktop) desktops.MoveWindowToDesktop(window.Handle, desktop);
            if (desktops.CurrentDesktop != desktop) desktops.GoToDesktop(desktop);
            Activate(window.Handle);
            Log?.Invoke($"Fenster von {profile.Name} auf Desktop {desktop + 1} verschoben.");
            return;
        }
        Log?.Invoke($"Neues Fenster von {profile.Name} nicht innerhalb von {NewWindowTimeout.TotalSeconds:0} s gefunden.");
    }

    /// <summary>Holt ein fremdes Fenster zuverlässig in den Vordergrund.</summary>
    public static void Activate(nint hwnd)
    {
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        if (Native.SetForegroundWindow(hwnd)) return;

        // Fallback: an den Eingabe-Thread des Vordergrundfensters anhängen bzw. Alt-Taste simulieren,
        // damit Windows die Vordergrundsperre aufhebt.
        var foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        var currentThread = Native.GetCurrentThreadId();
        var attached = foregroundThread != currentThread && Native.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            const byte VK_MENU = 0x12;
            const uint KEYEVENTF_KEYUP = 0x2;
            Native.keybd_event(VK_MENU, 0, 0, 0);
            Native.keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }
}
