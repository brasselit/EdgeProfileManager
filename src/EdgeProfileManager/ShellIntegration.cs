using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using EdgeProfileManager.Core.Edge;
using EdgeProfileManager.Resources;
using Microsoft.Win32;

namespace EdgeProfileManager;

/// <summary>Start menu shortcut (for pinning to the taskbar), autostart and diagnostics.</summary>
public static class ShellIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "EdgeProfileManager";

    /// <summary>
    /// Fixed AppUserModelID for process and Start menu shortcut. The taskbar uses it to match the
    /// running app with the pinned shortcut and its Jump List, regardless of the install location.
    /// </summary>
    public const string AppUserModelId = "BrasselIT.EdgeProfileManager";

    public static string ExecutablePath { get; } = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unknown.");

    public static string StartMenuShortcutPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Edge Profile Manager.lnk");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>Must be called before the first window or Jump List is created.</summary>
    public static void SetProcessAppUserModelId() => SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

    /// <summary>
    /// Creates the Start menu shortcut or fixes its target and AppUserModelID, e.g. after the application
    /// was moved. The file is overwritten rather than deleted so that existing pins are kept.
    /// </summary>
    public static void EnsureStartMenuShortcut(bool force = false)
    {
        if (!force && IsShortcutCurrent()) return;
        var link = (IShellLinkW)new ShellLink();
        try
        {
            var file = (IPersistFile)link;
            var properties = (IPropertyStore)link;
            link.SetPath(ExecutablePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(ExecutablePath)!);
            link.SetDescription(Strings.Shortcut_Description);
            link.SetIconLocation(ExecutablePath, 0);
            SetAppUserModelId(properties, AppUserModelId);
            file.Save(StartMenuShortcutPath, true);
            AppLog.Write($"Start menu shortcut created: {StartMenuShortcutPath}");
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    /// <summary>Does the shortcut exist with the right target and AppUserModelID?</summary>
    private static bool IsShortcutCurrent()
    {
        if (!File.Exists(StartMenuShortcutPath)) return false;
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(StartMenuShortcutPath, 0);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            return string.Equals(target.ToString(), ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                GetAppUserModelId((IPropertyStore)link) == AppUserModelId;
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    /// <summary>Removes shortcut and autostart entry; called by the uninstaller.</summary>
    public static void RemoveIntegration()
    {
        try { File.Delete(StartMenuShortcutPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (IsAutostartEnabled()) SetAutostart(false);
    }

    private static readonly PropertyKey AppUserModelIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private const ushort VT_LPWSTR = 31;

    private static string? GetAppUserModelId(IPropertyStore properties)
    {
        var key = AppUserModelIdKey;
        properties.GetValue(ref key, out var value);
        try { return value.Type == VT_LPWSTR ? Marshal.PtrToStringUni(value.Pointer) : null; }
        finally { PropVariantClear(ref value); }
    }

    private static void SetAppUserModelId(IPropertyStore properties, string appId)
    {
        var key = AppUserModelIdKey;
        var value = new PropVariant { Type = VT_LPWSTR, Pointer = Marshal.StringToCoTaskMemUni(appId) };
        try
        {
            properties.SetValue(ref key, ref value);
            properties.Commit();
        }
        finally { PropVariantClear(ref value); }
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PropertyKey(Guid FormatId, uint PropertyId);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        private ushort _reserved1, _reserved2, _reserved3;
        public IntPtr Pointer;
        private IntPtr _padding;
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
