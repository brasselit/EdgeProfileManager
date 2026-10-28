using System.Reflection;
using EdgeProfileManager.Core.Config;

namespace EdgeProfileManager;

/// <summary>Tray-Symbol mit dem Profilmenü gemäss Konfiguration.</summary>
public sealed class TrayIcon : IDisposable
{
    // NotifyIcon zeigt das Kontextmenü intern so an, dass es sich beim Wegklicken zuverlässig schliesst.
    private static readonly MethodInfo? ShowContextMenuMethod =
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly App _app;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu = new();
    private readonly Dictionary<string, (DateTime Modified, Image Image)> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Font _runningFont;

    public TrayIcon(App app)
    {
        _app = app;
        _runningFont = new Font(_menu.Font, FontStyle.Bold);
        _menu.Opening += (_, e) =>
        {
            Build();
            e.Cancel = false;
        };
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Edge Profile Manager",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMenu();
        };
    }

    public void ShowMenu()
    {
        if (ShowContextMenuMethod is not null) ShowContextMenuMethod.Invoke(_notifyIcon, null);
        else _menu.Show(Cursor.Position);
    }

    public void ShowError(string message) => _notifyIcon.ShowBalloonTip(5000, "Edge Profile Manager", message, ToolTipIcon.Error);
    public void ShowInfo(string message) => _notifyIcon.ShowBalloonTip(2000, "Edge Profile Manager", message, ToolTipIcon.Info);

    private void Build()
    {
        _menu.SuspendLayout();
        _menu.Items.Clear();
        var running = _app.GetRunningProfileKeys();
        AddNodes(_menu.Items, _app.Menu.Nodes, running);
        if (_app.Menu.Nodes.Count == 0) _menu.Items.Add(new ToolStripMenuItem("Keine Edge-Profile gefunden") { Enabled = false });

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(BuildSettingsMenu());
        _menu.Items.Add("Beenden", null, (_, _) => _app.Shutdown());
        _menu.ResumeLayout();
    }

    private void AddNodes(ToolStripItemCollection items, IEnumerable<MenuNode> nodes, HashSet<string> running)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case SeparatorNode:
                    items.Add(new ToolStripSeparator());
                    break;
                case GroupNode group:
                    var groupItem = new ToolStripMenuItem(group.Title);
                    AddNodes(groupItem.DropDownItems, group.Children, running);
                    if (groupItem.DropDownItems.Count == 0) groupItem.Enabled = false;
                    items.Add(groupItem);
                    break;
                case MissingProfileNode missing:
                    items.Add(new ToolStripMenuItem(missing.Title) { Enabled = false, ToolTipText = missing.Reference });
                    break;
                case ProfileNode profileNode:
                    items.Add(CreateProfileItem(profileNode, running.Contains(profileNode.Profile.Key)));
                    break;
            }
        }
    }

    private ToolStripMenuItem CreateProfileItem(ProfileNode node, bool isRunning)
    {
        var profile = node.Profile;
        var item = new ToolStripMenuItem(node.Title, GetIcon(profile.IconPath))
        {
            ToolTipText = $"Edge {profile.Channel} – {profile.Folder}" +
                (profile.User is null ? "" : $"\n{profile.User}") +
                (node.Settings.Desktop is null ? "" : $"\nDesktop: {node.Settings.Desktop}") +
                (isRunning ? "\nGeöffnet – Klick wechselt zum Fenster, Shift+Klick öffnet ein neues." : ""),
        };
        if (isRunning) item.Font = _runningFont;
        item.Click += (_, _) => _app.Open(profile.Key, newWindow: (Control.ModifierKeys & Keys.Shift) != 0);
        return item;
    }

    private ToolStripMenuItem BuildSettingsMenu()
    {
        var settings = new ToolStripMenuItem("Einstellungen");
        settings.DropDownItems.Add("Konfiguration bearbeiten", null, (_, _) => _app.EditFile(_app.ConfigPath));
        settings.DropDownItems.Add("Konfigurationsordner öffnen", null, (_, _) => _app.OpenFile(Path.GetDirectoryName(_app.ConfigPath)!));
        settings.DropDownItems.Add("Neu laden", null, (_, _) => _app.LoadConfig(initial: false));
        settings.DropDownItems.Add(new ToolStripSeparator());

        var autostart = new ToolStripMenuItem("Mit Windows starten") { Checked = ShellIntegration.IsAutostartEnabled() };
        autostart.Click += (_, _) => ShellIntegration.SetAutostart(!autostart.Checked);
        settings.DropDownItems.Add(autostart);
        // Windows erlaubt Programmen kein Anheften (Zugriff verweigert); deshalb die Verknüpfung im
        // Explorer markieren und den nötigen Rechtsklick-Befehl anzeigen.
        settings.DropDownItems.Add("An Start anheften…", null, (_, _) =>
            RevealShortcut("Rechtsklick auf „Edge Profile Manager“ → „An Start anheften“."));
        settings.DropDownItems.Add("An Taskleiste anheften…", null, (_, _) =>
            RevealShortcut("Rechtsklick auf „Edge Profile Manager“ → „Weitere Optionen anzeigen“ → „An Taskleiste anheften“."));
        settings.DropDownItems.Add(new ToolStripSeparator());

        settings.DropDownItems.Add("Fenster-Diagnose", null, (_, _) =>
        {
            var file = Path.Combine(AppLog.Directory, "windows.txt");
            Directory.CreateDirectory(AppLog.Directory);
            File.WriteAllText(file, ShellIntegration.DescribeWindows(_app.Discovery, _app.Finder));
            _app.EditFile(file);
        });
        settings.DropDownItems.Add("Protokoll öffnen", null, (_, _) => _app.EditFile(AppLog.FilePath));
        var desktopState = _app.Desktops.IsAvailable
            ? $"Virtuelle Desktops: {_app.Desktops.DesktopCount} (aktuell {_app.Desktops.CurrentDesktop + 1})"
            : "Virtuelle Desktops: nicht verfügbar";
        settings.DropDownItems.Add(new ToolStripMenuItem(desktopState) { Enabled = false });
        return settings;
    }

    private void RevealShortcut(string instruction)
    {
        try
        {
            ShellIntegration.EnsureStartMenuShortcut(force: true);
            ShellIntegration.RevealInExplorer(ShellIntegration.StartMenuShortcutPath);
            _notifyIcon.ShowBalloonTip(15000, "Anheften", instruction, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Verknüpfung anzeigen fehlgeschlagen: {ex.Message}");
            ShowError(ex.Message);
        }
    }

    private Image? GetIcon(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        var modified = File.GetLastWriteTimeUtc(path);
        if (_icons.TryGetValue(path, out var cached) && cached.Modified == modified) return cached.Image;
        try
        {
            using var icon = new Icon(path, SystemInformation.SmallIconSize);
            var image = icon.ToBitmap();
            _icons[path] = (modified, image);
            return image;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    }

    private static Icon LoadAppIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _runningFont.Dispose();
        foreach (var (_, image) in _icons.Values) image.Dispose();
    }
}
