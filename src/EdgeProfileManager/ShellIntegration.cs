using System.Runtime.InteropServices;
using System.Text;
using EdgeProfileManager.Core.Edge;
using Microsoft.Win32;

namespace EdgeProfileManager;

/// <summary>Startmenü-Verknüpfung (zum Anheften an die Taskbar), Autostart und Diagnose.</summary>
public static class ShellIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "EdgeProfileManager";

    public static string ExecutablePath { get; } = Environment.ProcessPath ?? throw new InvalidOperationException("Prozesspfad unbekannt.");

    public static string StartMenuShortcutPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Edge Profile Manager.lnk");

    /// <summary>
    /// Legt die Startmenü-Verknüpfung an oder korrigiert ihr Ziel, z.B. nach einem Verschieben der Anwendung.
    /// Die Datei wird überschrieben und nicht gelöscht, damit bestehende Pins erhalten bleiben.
    /// </summary>
    public static void EnsureStartMenuShortcut(bool force = false)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell nicht verfügbar.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(StartMenuShortcutPath);
            if (!force && File.Exists(StartMenuShortcutPath) && string.Equals((string)link.TargetPath, ExecutablePath, StringComparison.OrdinalIgnoreCase)) return;
            link.TargetPath = ExecutablePath;
            link.WorkingDirectory = Path.GetDirectoryName(ExecutablePath);
            link.Description = "Edge-Profile starten oder zu deren Fenster wechseln";
            link.IconLocation = $"{ExecutablePath},0";
            link.Save();
            AppLog.Write($"Startmenü-Verknüpfung erstellt: {StartMenuShortcutPath}");
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, StringBuilder? output, ref uint length);

    private const int ASSOCF_INIT_IGNOREUNKNOWN = 0x400;
    private const int ASSOCSTR_EXECUTABLE = 2;

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    private const int ASFW_ANY = -1;

    /// <summary>Gibt es für die Erweiterung ein Programm zum Öffnen?</summary>
    public static bool HasFileAssociation(string extension)
    {
        uint length = 0;
        // Ohne Verb "open" schlägt die Abfrage auch bei vorhandener Zuordnung fehl.
        // S_FALSE (1) = Länge ermittelt, Zuordnung vorhanden.
        return AssocQueryString(ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_EXECUTABLE, extension, "open", null, ref length) is 0 or 1 && length > 0;
    }

    /// <summary>
    /// Erlaubt dem gleich gestarteten Programm, sich in den Vordergrund zu holen. Ohne das öffnet z.B.
    /// VS Code die Datei nur als Tab im bestehenden Fenster im Hintergrund.
    /// </summary>
    public static void AllowForegroundForLaunchedProcess() => AllowSetForegroundWindow(ASFW_ANY);

    public static string? FindVsCode()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Öffnet den Explorer mit markierter Datei.</summary>
    public static void RevealInExplorer(string path)
    {
        AllowForegroundForLaunchedProcess();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\""))?.Dispose();
    }

    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string value && value.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{ExecutablePath}\" --background");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    /// <summary>Text für --dump-windows: alle Edge-Fenster mit erkanntem Profil.</summary>
    public static string DescribeWindows(ProfileDiscovery discovery, EdgeWindowFinder finder)
    {
        var builder = new StringBuilder();
        var profiles = discovery.GetProfiles();
        builder.AppendLine("Edge-Fenster (zuletzt aktives zuerst):");
        foreach (var window in finder.GetWindows())
        {
            var channelProfiles = profiles.Where(p => p.Channel == window.Channel).ToList();
            var profile = channelProfiles.FirstOrDefault(p => ProfileWindowMatcher.Matches(window, p, channelProfiles));
            builder.AppendLine($"  0x{window.Handle:X8}  {window.Channel,-7} {(profile?.Folder ?? "?"),-11} " +
                $"{(window.IsCloaked ? "anderer Desktop" : "sichtbar"),-15} AUMID={window.AppUserModelId}  \"{window.Title}\"");
        }
        builder.AppendLine();
        builder.AppendLine("Profile:");
        foreach (var profile in profiles)
        {
            builder.AppendLine($"  {profile.Key,-20} {profile.Name}{(profile.User is null ? "" : $" <{profile.User}>")}");
        }
        return builder.ToString();
    }
}
