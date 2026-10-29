using System.Runtime.InteropServices;
using System.Text;
using EdgeProfileManager.Core.Edge;
using Microsoft.Win32;

namespace EdgeProfileManager;

/// <summary>Start menu shortcut (for pinning to the taskbar), autostart and diagnostics.</summary>
public static class ShellIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "EdgeProfileManager";

    public static string ExecutablePath { get; } = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unknown.");

    public static string StartMenuShortcutPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Edge Profile Manager.lnk");

    /// <summary>
    /// Creates the Start menu shortcut or fixes its target, e.g. after the application was moved.
    /// The file is overwritten rather than deleted so that existing pins are kept.
    /// </summary>
    public static void EnsureStartMenuShortcut(bool force = false)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(StartMenuShortcutPath);
            if (!force && File.Exists(StartMenuShortcutPath) && string.Equals((string)link.TargetPath, ExecutablePath, StringComparison.OrdinalIgnoreCase)) return;
            link.TargetPath = ExecutablePath;
            link.WorkingDirectory = Path.GetDirectoryName(ExecutablePath);
            link.Description = "Launch Edge profiles or switch to their windows";
            link.IconLocation = $"{ExecutablePath},0";
            link.Save();
            AppLog.Write($"Start menu shortcut created: {StartMenuShortcutPath}");
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, StringBuilder? output, ref uint length);

    private const int ASSOCF_INIT_IGNOREUNKNOWN = 0x400;
    private const int ASSOCSTR_EXECUTABLE = 2;

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    private const int ASFW_ANY = -1;

    /// <summary>Is there a program to open files with this extension?</summary>
    public static bool HasFileAssociation(string extension)
    {
        uint length = 0;
        // Without the verb "open" the query fails even if an association exists.
        // S_FALSE (1) = length determined, association exists.
        return AssocQueryString(ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_EXECUTABLE, extension, "open", null, ref length) is 0 or 1 && length > 0;
    }

    /// <summary>
    /// Allows the program about to be launched to bring itself to the foreground. Without this, e.g.
    /// VS Code only opens the file as a tab in the existing window in the background.
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

    /// <summary>Opens Explorer with the file selected.</summary>
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

    /// <summary>Text for --dump-windows: all Edge windows with their detected profile.</summary>
    public static string DescribeWindows(ProfileDiscovery discovery, EdgeWindowFinder finder)
    {
        var builder = new StringBuilder();
        var profiles = discovery.GetProfiles();
        builder.AppendLine("Edge windows (most recently active first):");
        foreach (var window in finder.GetWindows())
        {
            var channelProfiles = profiles.Where(p => p.Channel == window.Channel).ToList();
            var profile = channelProfiles.FirstOrDefault(p => ProfileWindowMatcher.Matches(window, p, channelProfiles));
            builder.AppendLine($"  0x{window.Handle:X8}  {window.Channel,-7} {(profile?.Folder ?? "?"),-11} " +
                $"{(window.IsCloaked ? "other desktop" : "visible"),-15} AUMID={window.AppUserModelId}  \"{window.Title}\"");
        }
        builder.AppendLine();
        builder.AppendLine("Profiles:");
        foreach (var profile in profiles)
        {
            builder.AppendLine($"  {profile.Key,-20} {profile.Name}{(profile.User is null ? "" : $" <{profile.User}>")}");
        }
        return builder.ToString();
    }
}
