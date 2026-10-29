using System.Drawing;
using System.Windows.Forms;
using System.Reflection;
using EdgeProfileManager.Core.Config;

namespace EdgeProfileManager;

/// <summary>Tray icon with the profile menu as configured.</summary>
public sealed class TrayIcon : IDisposable
{
    // NotifyIcon shows the context menu internally in a way that reliably closes it when clicking elsewhere.
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
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            action?.Invoke();
        };
        _notifyIcon.BalloonTipClosed += (_, _) => _balloonAction = null;
    }

    /// <summary>Action for a click on the currently shown notification.</summary>
    private Action? _balloonAction;

    public void ShowMenu()
    {
        if (ShowContextMenuMethod is not null) ShowContextMenuMethod.Invoke(_notifyIcon, null);
        else _menu.Show(Cursor.Position);
    }

    public void ShowError(string message) => ShowBalloon(message, ToolTipIcon.Error, 5000, null);
    public void ShowInfo(string message, int timeout = 2000, Action? onClick = null) => ShowBalloon(message, ToolTipIcon.Info, timeout, onClick);

    private void ShowBalloon(string message, ToolTipIcon icon, int timeout, Action? onClick)
    {
        _balloonAction = onClick;
        _notifyIcon.ShowBalloonTip(timeout, "Edge Profile Manager", message, icon);
    }

    private void Build()
    {
        _menu.SuspendLayout();
        _menu.Items.Clear();
        var running = _app.GetRunningProfileKeys();
        AddNodes(_menu.Items, _app.Menu.Nodes, running);
        if (_app.Menu.Nodes.Count == 0) _menu.Items.Add(new ToolStripMenuItem("No Edge profiles found") { Enabled = false });

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Manage profiles and menu…", null, (_, _) => _app.ShowManagementWindow());
        _menu.Items.Add(BuildSettingsMenu());
        _menu.Items.Add("Exit", null, (_, _) => _app.Shutdown());
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
        var item = new ToolStripMenuItem(node.Title, GetIcon(profile.IconPath, profile.Color))
        {
            ToolTipText = $"Edge {profile.Channel} – {profile.Folder}" +
                (profile.User is null ? "" : $"\n{profile.User}") +
                (node.Settings.Desktop is null ? "" : $"\nDesktop: {node.Settings.Desktop}") +
                (isRunning ? "\nRunning – click switches to the window, Shift+click opens a new one." : ""),
        };
        if (isRunning) item.Font = _runningFont;
        item.Click += (_, _) => _app.Open(profile.Key, newWindow: (Control.ModifierKeys & Keys.Shift) != 0);
        return item;
    }

    private ToolStripMenuItem BuildSettingsMenu()
    {
        var settings = new ToolStripMenuItem("Settings");
        settings.DropDownItems.Add("Edit configuration", null, (_, _) => _app.EditFile(_app.ConfigPath));
        settings.DropDownItems.Add("Open configuration folder", null, (_, _) => _app.OpenFile(Path.GetDirectoryName(_app.ConfigPath)!));
        settings.DropDownItems.Add("Reload", null, (_, _) => _app.LoadConfig(initial: false));
        settings.DropDownItems.Add(new ToolStripSeparator());

        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = ShellIntegration.IsAutostartEnabled() };
        autostart.Click += (_, _) => ShellIntegration.SetAutostart(!autostart.Checked);
        settings.DropDownItems.Add(autostart);
        // Windows does not let programs pin themselves (access denied); so select the shortcut in
        // Explorer and show the required right-click command.
        settings.DropDownItems.Add("Pin to Start…", null, (_, _) =>
            RevealShortcut("Right-click \"Edge Profile Manager\" → \"Pin to Start\"."));
        settings.DropDownItems.Add("Pin to taskbar…", null, (_, _) =>
            RevealShortcut("Right-click \"Edge Profile Manager\" → \"Show more options\" → \"Pin to taskbar\"."));
        settings.DropDownItems.Add(new ToolStripSeparator());

        settings.DropDownItems.Add("Window diagnostics", null, (_, _) =>
        {
            var file = Path.Combine(AppLog.Directory, "windows.txt");
            Directory.CreateDirectory(AppLog.Directory);
            File.WriteAllText(file, ShellIntegration.DescribeWindows(_app.Discovery, _app.Finder));
            _app.EditFile(file);
        });
        settings.DropDownItems.Add("Open log", null, (_, _) => _app.EditFile(AppLog.FilePath));
        var desktopState = _app.Desktops.IsAvailable
            ? $"Virtual desktops: {_app.Desktops.DesktopCount} (current {_app.Desktops.CurrentDesktop + 1})"
            : "Virtual desktops: not available";
        settings.DropDownItems.Add(new ToolStripMenuItem(desktopState) { Enabled = false });
        settings.DropDownItems.Add(new ToolStripSeparator());

        settings.DropDownItems.Add("Check for updates…", null, (_, _) => _ = _app.CheckForUpdatesAsync(interactive: true));
        var autoUpdate = new ToolStripMenuItem("Check for updates automatically") { Checked = _app.Config.CheckForUpdates };
        autoUpdate.Click += (_, _) => _app.UpdateConfig(c => c.CheckForUpdates = !autoUpdate.Checked);
        settings.DropDownItems.Add(autoUpdate);
        settings.DropDownItems.Add(new ToolStripMenuItem($"Version {_app.Updater.Version}{(_app.Updater.IsInstalled ? "" : " (not installed)")}") { Enabled = false });
        return settings;
    }

    private void RevealShortcut(string instruction)
    {
        try
        {
            ShellIntegration.EnsureStartMenuShortcut(force: true);
            ShellIntegration.RevealInExplorer(ShellIntegration.StartMenuShortcutPath);
            _notifyIcon.ShowBalloonTip(15000, "Pin", instruction, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Showing shortcut failed: {ex.Message}");
            ShowError(ex.Message);
        }
    }

    /// <summary>Edge's profile icon; with a profile color set, also a color dot at the bottom right.</summary>
    private Image? GetIcon(string? path, int? color)
    {
        var hasIcon = path is not null && File.Exists(path);
        if (!hasIcon && color is null) return null;
        var key = $"{path}|{color}";
        var modified = hasIcon ? File.GetLastWriteTimeUtc(path!) : DateTime.MinValue;
        if (_icons.TryGetValue(key, out var cached) && cached.Modified == modified) return cached.Image;
        try
        {
            var size = SystemInformation.SmallIconSize;
            var image = new Bitmap(size.Width, size.Height);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (hasIcon)
                {
                    using var icon = new Icon(path!, size);
                    graphics.DrawIcon(icon, new Rectangle(Point.Empty, size));
                }
                if (color is { } rgb)
                {
                    var dot = hasIcon ? size.Width * 0.55f : size.Width - 2f;
                    var rect = new RectangleF(size.Width - dot - 0.5f, size.Height - dot - 0.5f, dot, dot);
                    using var fill = new SolidBrush(Color.FromArgb(rgb | unchecked((int)0xFF000000)));
                    using var outline = new Pen(Color.White, 1.5f);
                    graphics.FillEllipse(fill, rect);
                    graphics.DrawEllipse(outline, rect);
                }
            }
            if (_icons.Remove(key, out var old)) old.Image.Dispose();
            _icons[key] = (modified, image);
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
