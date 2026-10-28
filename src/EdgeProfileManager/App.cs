using System.Diagnostics;
using System.Windows;
using EdgeProfileManager.Core.Config;
using EdgeProfileManager.Core.Desktops;
using EdgeProfileManager.Core.Edge;
using Application = System.Windows.Application;

namespace EdgeProfileManager;

/// <summary>Tray-Anwendung: hält Konfiguration, Profile, Tray-Menü und Jump List aktuell.</summary>
public sealed class App : Application
{
    private readonly CommandLine _initialCommand;
    private readonly SingleInstance _instance;
    private readonly List<FileSystemWatcher> _profileWatchers = [];
    private ConfigStore _configStore = null!;
    private JumpListBuilder _jumpList = null!;
    private TrayIcon _tray = null!;
    private Timer? _profileDebounce;

    public App(CommandLine initialCommand, SingleInstance instance)
    {
        _initialCommand = initialCommand;
        _instance = instance;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    public EdgeEnvironment Environment { get; } = EdgeEnvironment.Default;
    public ProfileDiscovery Discovery { get; private set; } = null!;
    public EdgeWindowFinder Finder { get; } = new();
    public IVirtualDesktopService Desktops { get; private set; } = new NullVirtualDesktopService();
    public ProfileLauncher Launcher { get; private set; } = null!;
    public AppConfig Config { get; private set; } = new();
    public string ConfigPath => _configStore.Path;
    public IReadOnlyList<EdgeProfile> Profiles { get; private set; } = [];
    public ResolvedMenu Menu { get; private set; } = new([], new Dictionary<string, ProfileSettings>());

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Write($"Start ({ShellIntegration.ExecutablePath})");
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write($"Unbehandelter Fehler: {args.Exception}");
            _tray?.ShowError(args.Exception.Message);
            args.Handled = true;
        };

        Discovery = new ProfileDiscovery(Environment);
        Desktops = VdaVirtualDesktopService.Load(out var desktopError);
        if (desktopError is not null) AppLog.Write(desktopError);
        Launcher = new ProfileLauncher(Environment, Discovery, Finder, Desktops);
        Launcher.Log += AppLog.Write;
        _jumpList = new JumpListBuilder(Environment);
        _tray = new TrayIcon(this);

        _configStore = new ConfigStore(ConfigStore.DefaultPath);
        LoadConfig(initial: true);
        _configStore.Changed += () => Dispatcher.BeginInvoke(() => LoadConfig(initial: false));
        _configStore.Watch();
        WatchProfiles();

        try { ShellIntegration.EnsureStartMenuShortcut(); }
        catch (Exception ex) { AppLog.Write($"Startmenü-Verknüpfung fehlgeschlagen: {ex.Message}"); }

        _instance.StartServer(args => Dispatcher.BeginInvoke(() => Execute(CommandLine.Parse(args))));
        Execute(_initialCommand);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        foreach (var watcher in _profileWatchers) watcher.Dispose();
        _profileDebounce?.Dispose();
        _configStore.Dispose();
        _tray.Dispose();
        AppLog.Write("Beendet");
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
            case CommandKind.Reload:
                LoadConfig(initial: false);
                break;
            case CommandKind.Exit:
                Shutdown();
                break;
            case CommandKind.Invalid:
                _tray.ShowError(command.Error ?? "Ungültiger Aufruf.");
                break;
        }
    }

    public void Open(string profileKey, bool newWindow)
    {
        var key = MenuResolver.NormalizeReference(profileKey);
        try
        {
            // Frisch einlesen, damit auch eben erst angelegte Profile gefunden werden.
            var profile = Discovery.GetProfiles().FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Profil '{key}' nicht gefunden.");
            var settings = Menu.SettingsByKey.GetValueOrDefault(profile.Key) ?? MenuResolver.DefaultSettings(Config);
            Launcher.Open(profile, settings, newWindow);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Öffnen von {key} fehlgeschlagen: {ex}");
            _tray.ShowError(ex.Message);
        }
    }

    /// <summary>Schlüssel der Profile, die aktuell mindestens ein Fenster haben.</summary>
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
            if (!initial) _tray.ShowInfo("Konfiguration neu geladen.");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Konfiguration ungültig: {ex.Message}");
            _tray.ShowError($"Konfiguration ungültig, die letzte gültige bleibt aktiv:\n{ex.Message}");
        }
        RefreshProfiles();
    }

    public void RefreshProfiles()
    {
        Profiles = Discovery.GetProfiles();
        Menu = MenuResolver.Resolve(Config, Profiles);
        try { _jumpList.Update(Menu, Config.JumpListStyle); }
        catch (Exception ex) { AppLog.Write($"Jump List fehlgeschlagen: {ex.Message}"); }
    }

    public void OpenFile(string path)
    {
        ShellIntegration.AllowForegroundForLaunchedProcess();
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex)
        {
            AppLog.Write($"Öffnen von {path} fehlgeschlagen: {ex.Message}");
            _tray.ShowError(ex.Message);
        }
    }

    /// <summary>
    /// Öffnet eine Textdatei zum Bearbeiten. Ohne Dateizuordnung tut ShellExecute für .json
    /// unter Windows 11 kommentarlos nichts; deshalb wird der Editor explizit gewählt.
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
            AppLog.Write($"Editor '{editor}' konnte nicht gestartet werden: {ex.Message}");
            _tray.ShowError($"Editor '{editor}' konnte nicht gestartet werden: {ex.Message}");
        }
    }

    /// <summary>Beobachtet "Local State" aller Channels, damit neue oder umbenannte Profile sofort erscheinen.</summary>
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
