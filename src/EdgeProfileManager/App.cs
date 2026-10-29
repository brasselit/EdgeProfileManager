using System.Diagnostics;
using System.Windows;
using EdgeProfileManager.Core.Config;
using EdgeProfileManager.Core.Desktops;
using EdgeProfileManager.Core.Edge;
using EdgeProfileManager.Core.Management;
using Application = System.Windows.Application;

namespace EdgeProfileManager;

/// <summary>Tray application: keeps configuration, profiles, tray menu and jump list up to date.</summary>
public sealed class App : Application
{
    private static readonly TimeSpan UpdateCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromDays(1);

    private readonly CommandLine _initialCommand;
    private readonly SingleInstance _instance;
    private readonly bool _firstRun;
    private readonly List<FileSystemWatcher> _profileWatchers = [];
    private ConfigStore _configStore = null!;
    private JumpListBuilder _jumpList = null!;
    private TrayIcon _tray = null!;
    private Timer? _profileDebounce;
    private Timer? _updateTimer;
    private string? _notifiedVersion;
    private bool _updating;
    private ManagementWindow? _managementWindow;

    public App(CommandLine initialCommand, SingleInstance instance, bool firstRun)
    {
        _initialCommand = initialCommand;
        _instance = instance;
        _firstRun = firstRun;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    public Updater Updater { get; } = new();

    public EdgeEnvironment Environment { get; } = EdgeEnvironment.Default;
    public ProfileDiscovery Discovery { get; private set; } = null!;
    public EdgeWindowFinder Finder { get; } = new();
    public IVirtualDesktopService Desktops { get; private set; } = new NullVirtualDesktopService();
    public ProfileLauncher Launcher { get; private set; } = null!;
    public IEdgeProcessGuard Guard { get; private set; } = null!;
    public ProfileManager Manager { get; private set; } = null!;
    public AppConfig Config { get; private set; } = new();
    public string ConfigPath => _configStore.Path;
    public IReadOnlyList<EdgeProfile> Profiles { get; private set; } = [];
    public ResolvedMenu Menu { get; private set; } = new([], new Dictionary<string, ProfileSettings>());

    /// <summary>Raised after every update of configuration or profiles (on the UI thread).</summary>
    public event Action? StateChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Write($"Start {Updater.Version} ({ShellIntegration.ExecutablePath})");
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write($"Unhandled error: {args.Exception}");
            _tray?.ShowError(args.Exception.Message);
            args.Handled = true;
        };

        Discovery = new ProfileDiscovery(Environment);
        Desktops = VdaVirtualDesktopService.Load(out var desktopError);
        if (desktopError is not null) AppLog.Write(desktopError);
        Launcher = new ProfileLauncher(Environment, Discovery, Finder, Desktops);
        Launcher.Log += AppLog.Write;
        Guard = new EdgeProcessGuard(Finder);
        Manager = new ProfileManager(Environment, Guard, new RobocopyProfileCopier());
        _jumpList = new JumpListBuilder(Environment);
        _tray = new TrayIcon(this);

        _configStore = new ConfigStore(ConfigStore.DefaultPath);
        LoadConfig(initial: true);
        _configStore.Changed += () => Dispatcher.BeginInvoke(() => LoadConfig(initial: false));
        _configStore.Watch();
        WatchProfiles();

        try { ShellIntegration.EnsureStartMenuShortcut(); }
        catch (Exception ex) { AppLog.Write($"Start menu shortcut failed: {ex.Message}"); }

        _instance.StartServer(args => Dispatcher.BeginInvoke(() => Execute(CommandLine.Parse(args))));
        Execute(_initialCommand);

        if (_firstRun)
        {
            _tray.ShowInfo("Edge Profile Manager is running in the tray. Use Settings › Pin to taskbar… to pin it.", 10000);
        }
        if (Updater.IsInstalled)
        {
            _updateTimer = new Timer(_ => Dispatcher.BeginInvoke(async () =>
            {
                if (Config.CheckForUpdates) await CheckForUpdatesAsync(interactive: false);
            }), null, UpdateCheckDelay, UpdateCheckInterval);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        foreach (var watcher in _profileWatchers) watcher.Dispose();
        _profileDebounce?.Dispose();
        _updateTimer?.Dispose();
        _configStore.Dispose();
        _tray.Dispose();
        AppLog.Write("Exited");
        base.OnExit(e);
    }

    public void Execute(CommandLine command)
    {
        switch (command.Kind)
        {
            case CommandKind.Open when command.ProfileKey is not null:
                Open(command.ProfileKey, command.NewWindow);
                break;
            case CommandKind.ShowMenu:
                _tray.ShowMenu();
                break;
            case CommandKind.Manage:
                ShowManagementWindow();
                break;
            case CommandKind.Reload:
                LoadConfig(initial: false);
                break;
            case CommandKind.Exit:
                Shutdown();
                break;
            case CommandKind.Invalid:
                _tray.ShowError(command.Error ?? "Invalid command line.");
                break;
        }
    }

    public void Open(string profileKey, bool newWindow)
    {
        var key = MenuResolver.NormalizeReference(profileKey);
        try
        {
            // Read fresh so that just-created profiles are found too.
            var profile = Discovery.GetProfiles().FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Profile '{key}' not found.");
            var settings = Menu.SettingsByKey.GetValueOrDefault(profile.Key) ?? MenuResolver.DefaultSettings(Config);
            Launcher.Open(profile, settings, newWindow);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Opening {key} failed: {ex}");
            _tray.ShowError(ex.Message);
        }
    }

    public void ShowManagementWindow()
    {
        if (_managementWindow is null)
        {
            _managementWindow = new ManagementWindow(this);
            _managementWindow.Closed += (_, _) => _managementWindow = null;
            _managementWindow.Show();
        }
        if (_managementWindow.WindowState == WindowState.Minimized) _managementWindow.WindowState = WindowState.Normal;
        _managementWindow.Activate();
    }

    /// <summary>
    /// Looks for a new release. In the background only a notification is shown (once per version);
    /// clicking it, or a manual check, asks whether to install and restart.
    /// </summary>
    public async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_updating) return;
        if (!Updater.IsInstalled)
        {
            if (interactive) MessageBox.Show("Updates are only available in the installed version (Setup.exe from the GitHub releases).",
                "Edge Profile Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _updating = true;
        try
        {
            var update = await Updater.CheckAsync();
            if (update is null)
            {
                if (interactive) _tray.ShowInfo($"Edge Profile Manager {Updater.Version} is up to date.");
                return;
            }
            var version = update.TargetFullRelease.Version.ToString();
            AppLog.Write($"Update available: {version}");
            if (!interactive)
            {
                if (_notifiedVersion == version) return;
                _notifiedVersion = version;
                _tray.ShowInfo($"Version {version} is available. Click here to install it.", 15000,
                    () => _ = CheckForUpdatesAsync(interactive: true));
                return;
            }
            if (MessageBox.Show($"Install Edge Profile Manager {version} now (currently {Updater.Version})?\n\nThe app restarts in the tray afterwards.",
                    "Edge Profile Manager", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _tray.ShowInfo($"Downloading version {version} …");
            AppLog.Write($"Installing update {version}");
            await Updater.InstallAsync(update);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Update check failed: {ex}");
            if (interactive) _tray.ShowError($"Update failed: {ex.Message}");
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Changes the configuration, saves it and updates menu and jump list.</summary>
    public void UpdateConfig(Action<AppConfig> change)
    {
        change(Config);
        _configStore.Save(Config);
        RefreshProfiles();
    }

    /// <summary>Rewrites references in the configuration, e.g. after moving a profile.</summary>
    public int ReplaceProfileReference(string oldKey, string newKey)
    {
        var count = 0;
        UpdateConfig(c => count = MenuResolver.ReplaceProfileReference(c, oldKey, newKey));
        return count;
    }

    /// <summary>Keys of the profiles that currently have at least one window.</summary>
    public HashSet<string> GetRunningProfileKeys() =>
        Finder.GetWindows()
            .Select(w => ProfileWindowMatcher.FindProfile(w, Profiles)?.Key)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void LoadConfig(bool initial)
    {
        try
        {
            Config = _configStore.LoadOrCreate(() => ConfigStore.CreateDefault(Discovery.GetProfiles()));
            if (!initial) _tray.ShowInfo("Configuration reloaded.");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Invalid configuration: {ex.Message}");
            _tray.ShowError($"Invalid configuration, the last valid one stays active:\n{ex.Message}");
        }
        RefreshProfiles();
    }

    public void RefreshProfiles()
    {
        Profiles = Discovery.GetProfiles();
        Menu = MenuResolver.Resolve(Config, Profiles);
        try { _jumpList.Update(Menu, Config.JumpListStyle); }
        catch (Exception ex) { AppLog.Write($"Jump list failed: {ex.Message}"); }
        StateChanged?.Invoke();
    }

    public void OpenFile(string path)
    {
        ShellIntegration.AllowForegroundForLaunchedProcess();
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex)
        {
            AppLog.Write($"Opening {path} failed: {ex.Message}");
            _tray.ShowError(ex.Message);
        }
    }

    /// <summary>
    /// Opens a text file for editing. Without a file association, ShellExecute silently does nothing
    /// for .json on Windows 11; that is why the editor is chosen explicitly.
    /// </summary>
    public void EditFile(string path)
    {
        var editor = string.IsNullOrWhiteSpace(Config.Editor) ? null : System.Environment.ExpandEnvironmentVariables(Config.Editor);
        if (editor is null && ShellIntegration.HasFileAssociation(Path.GetExtension(path)))
        {
            OpenFile(path);
            return;
        }
        editor ??= ShellIntegration.FindVsCode() ?? "notepad.exe";
        ShellIntegration.AllowForegroundForLaunchedProcess();
        try { Process.Start(new ProcessStartInfo(editor) { ArgumentList = { path }, UseShellExecute = false })?.Dispose(); }
        catch (Exception ex)
        {
            AppLog.Write($"Editor '{editor}' could not be started: {ex.Message}");
            _tray.ShowError($"Editor '{editor}' could not be started: {ex.Message}");
        }
    }

    /// <summary>Watches "Local State" of all channels so that new or renamed profiles show up immediately.</summary>
    private void WatchProfiles()
    {
        _profileDebounce = new Timer(_ => Dispatcher.BeginInvoke(RefreshProfiles));
        foreach (var channel in EdgeChannels.All)
        {
            var userData = Environment.UserDataDir(channel);
            if (!Directory.Exists(userData)) continue;
            var watcher = new FileSystemWatcher(userData, "Local State") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            FileSystemEventHandler handler = (_, _) => _profileDebounce.Change(1500, Timeout.Infinite);
            watcher.Changed += handler;
            watcher.Created += handler;
            watcher.Renamed += (_, _) => _profileDebounce.Change(1500, Timeout.Infinite);
            watcher.EnableRaisingEvents = true;
            _profileWatchers.Add(watcher);
        }
    }
}
