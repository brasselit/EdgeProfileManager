using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EdgeProfileManager.Core.Edge;
using EdgeProfileManager.Core.Management;
using EdgeProfileManager.Dialogs;

namespace EdgeProfileManager;

/// <summary>Anzeige eines Profils in den Spalten des Verwaltungsfensters.</summary>
public sealed class ProfileItem(EdgeProfile profile)
{
    public EdgeProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public string? User => Profile.User;
    public string Folder => Profile.Folder;
    public Brush ColorBrush => Profile.Color is { } rgb ? new SolidColorBrush(ProfileColorDialog.ToColor(rgb)) : Brushes.Transparent;
}

/// <summary>Profilverwaltung (Port der Script-GUI): Neu, Umbenennen, Avatar, Farbe, Backup, Restore, Kopieren/Verschieben, Löschen.</summary>
public partial class ManagementWindow : Window
{
    private const string NewProfile = "Neues Profil";
    private const string DragFormat = "EdgeProfileManager.Profile";

    private readonly App _app;
    private readonly Dictionary<EdgeChannel, ListBox> _lists = [];
    private readonly Dictionary<EdgeChannel, TextBlock> _headers = [];
    private EdgeProfile? _selection;
    private bool _busy;
    private Point? _dragStart;
    private EdgeProfile? _dragProfile;

    public ManagementWindow(App app)
    {
        _app = app;
        InitializeComponent();
        using (var icon = typeof(ManagementWindow).Assembly.GetManifestResourceStream("app.ico"))
        {
            if (icon is not null) Icon = BitmapFrame.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        DropMode.ItemsSource = new[] { "Kopieren", "Verschieben" };
        DropMode.SelectedIndex = 0;
        BuildColumns();
        BuildActions();
        RefreshButton.Click += (_, _) => RefreshProfiles();
        _app.Manager.Log += OnManagerLog;
        Closing += (_, e) =>
        {
            if (!_busy) return;
            e.Cancel = true;
            MessageBox.Show(this, "Bitte die laufende Aktion abwarten.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        };
        Closed += (_, _) => _app.Manager.Log -= OnManagerLog;
        RefreshProfiles();
    }

    private TransferOptions Options => new()
    {
        LocalOnly = LocalOption.IsChecked == true,
        IncludeCaches = CacheOption.IsChecked == true,
        AllowRunningSource = RunningOption.IsChecked == true,
    };

    private EdgeChannel ActiveChannel => _selection?.Channel ?? EdgeChannels.All.FirstOrDefault(IsInstalled);

    public void RefreshProfiles()
    {
        var previous = _selection;
        foreach (var channel in EdgeChannels.All)
        {
            try
            {
                var profiles = _app.Discovery.GetProfiles(channel);
                var items = profiles.Select(p => new ProfileItem(p)).ToList();
                _lists[channel].ItemsSource = items;
                var state = !IsInstalled(channel) ? "Nicht installiert" : _app.Guard.IsRunning(channel) ? "Läuft" : "Geschlossen";
                _headers[channel].Text = $"{channel}  |  {profiles.Count} Profile\n{state}";
                if (previous?.Channel == channel)
                    _lists[channel].SelectedItem = items.FirstOrDefault(i => i.Folder == previous.Folder);
            }
            catch (Exception ex)
            {
                AddLog($"Fehler beim Laden von {channel}: {ex.Message}");
            }
        }
    }

    private bool IsInstalled(EdgeChannel channel) => _app.Environment.FindExecutable(channel) is not null;

    // ---------- Aufbau ----------

    private void BuildColumns()
    {
        var index = 0;
        foreach (var channel in EdgeChannels.All)
        {
            var column = new Grid { Margin = new Thickness(0, 0, index < 3 ? 10 : 0, 0) };
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            column.RowDefinitions.Add(new RowDefinition());
            var header = new TextBlock { Margin = new Thickness(0, 0, 0, 10), FontWeight = FontWeights.SemiBold, Foreground = (Brush)new BrushConverter().ConvertFromString("#163F43")! };
            var list = new ListBox
            {
                Tag = channel,
                AllowDrop = true,
                BorderBrush = (Brush)new BrushConverter().ConvertFromString("#C4CDD0")!,
                ItemTemplate = (DataTemplate)Resources["ProfileTemplate"],
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            Grid.SetRow(list, 1);
            column.Children.Add(header);
            column.Children.Add(list);
            Grid.SetColumn(column, index++);
            ProfileColumns.Children.Add(column);
            _lists[channel] = list;
            _headers[channel] = header;

            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is not ProfileItem item) return;
                _selection = item.Profile;
                foreach (var other in _lists.Values.Where(l => l != list)) other.SelectedItem = null;
                Status.Text = $"{item.Profile.Channel} / {item.Name} / {item.Folder}";
            };
            list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is ProfileItem) Invoke(OpenSelected); };
            list.PreviewMouseLeftButtonDown += (_, e) =>
            {
                _dragStart = e.GetPosition(list);
                _dragProfile = (ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) as ListBoxItem)?.DataContext is ProfileItem item ? item.Profile : null;
            };
            list.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || _dragProfile is null) return;
                var point = e.GetPosition(list);
                if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _dragStart = null;
                DragDrop.DoDragDrop(list, new DataObject(DragFormat, _dragProfile), DragDropEffects.Copy | DragDropEffects.Move);
            };
            list.DragOver += (_, e) =>
            {
                e.Effects = DragDropEffects.None;
                if (e.Data.GetData(DragFormat) is EdgeProfile dragged && dragged.Channel != channel)
                    e.Effects = IsCopyDrop(e.KeyStates) ? DragDropEffects.Copy : DragDropEffects.Move;
                e.Handled = true;
            };
            list.Drop += (_, e) =>
            {
                e.Handled = true;
                if (e.Data.GetData(DragFormat) is not EdgeProfile dragged || dragged.Channel == channel) return;
                var move = !IsCopyDrop(e.KeyStates);
                Invoke(() => TransferAsync(dragged, channel, move, null));
            };
        }
    }

    /// <summary>Kopieren gemäss Auswahl; Strg erzwingt Kopieren.</summary>
    private bool IsCopyDrop(DragDropKeyStates keys) => DropMode.SelectedIndex == 0 || keys.HasFlag(DragDropKeyStates.ControlKey);

    private void BuildActions()
    {
        AddAction("Öffnen", '', OpenSelected, showLabel: true);
        AddAction("Neues Profil", '', CreateAsync, showLabel: true);
        AddAction("Umbenennen", '', RenameAsync);
        AddAction("Avatar", '', AvatarAsync);
        AddAction("Farbe", '', ColorAsync, showLabel: true);
        AddAction("Übertragen", '', TransferDialogAsync, showLabel: true);
        AddAction("Backup", '', BackupAsync, showLabel: true);
        AddAction("Wiederherstellen", '', RestoreAsync, showLabel: true);
        AddAction("Profilordner öffnen", '', OpenFolder);
        AddAction("Profil löschen", '', DeleteAsync);
    }

    private void AddAction(string label, char glyph, Action action, bool showLabel = false) =>
        AddAction(label, glyph, () => { action(); return Task.CompletedTask; }, showLabel);

    private void AddAction(string label, char glyph, Func<Task> action, bool showLabel = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = glyph.ToString(), VerticalAlignment = VerticalAlignment.Center });
        if (showLabel) content.Children.Add(new TextBlock { Text = label, Margin = new Thickness(8, 0, 0, 0) });
        var button = new Button { Content = content, ToolTip = label };
        button.Click += (_, _) => Invoke(action);
        Actions.Children.Add(button);
    }

    private async void Invoke(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            AddLog($"Fehler: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Invoke(Action action) => Invoke(() => { action(); return Task.CompletedTask; });

    // ---------- Aktionen ----------

    private EdgeProfile Selected => _selection ?? throw new InvalidOperationException("Bitte zuerst ein Profil auswählen.");

    private void OpenSelected() => _app.Open(Selected.Key, newWindow: false);

    private void OpenFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Selected.Path}\""))?.Dispose();

    private async Task CreateAsync()
    {
        var channels = ChannelsStartingWith(ActiveChannel);
        var values = FieldsDialog.Show(this, "Neues Profil",
            new Field("Channel", "Instanz", FieldType.Combo) { Options = channels },
            new Field("Name", "Profilname") { Required = true },
            new Field("User", "E-Mail") { Required = true },
            new Field("Avatar", "Avatar (optional, PNG)", FieldType.File) { Filter = "PNG-Bild|*.png" });
        if (values is null) return;
        var channel = Enum.Parse<EdgeChannel>(values["Channel"]);
        var created = await RunAsync("Profil erstellen", m => m.Create(channel, values["Name"], values["User"], values["Avatar"]));
        if (created is not null) Select(created.Channel, created.Folder);
    }

    private async Task RenameAsync()
    {
        var selected = Selected;
        var values = FieldsDialog.Show(this, "Profil umbenennen", new Field("Name", "Neuer Name") { Value = selected.Name, Required = true });
        if (values is not null) await RunAsync("Profil umbenennen", m => m.Rename(selected.Channel, selected.Folder, values["Name"]));
    }

    private async Task AvatarAsync()
    {
        var selected = Selected;
        var values = FieldsDialog.Show(this, "Profilavatar",
            new Field("Avatar", "PNG-Datei", FieldType.File) { Filter = "PNG-Bild|*.png", Required = true });
        if (values is not null) await RunAsync("Avatar setzen", m => m.SetAvatar(selected.Channel, selected.Folder, values["Avatar"]));
    }

    private async Task ColorAsync()
    {
        var selected = Selected;
        if (ProfileColorDialog.Show(this, selected.Name, selected.Color) is { } rgb)
            await RunAsync("Farbe setzen", m => m.SetColor(selected.Channel, selected.Folder, rgb));
    }

    private async Task DeleteAsync()
    {
        var selected = Selected;
        if (!Confirm($"Profil '{selected.Name}' ({selected.Channel} / {selected.Folder}) samt allen lokalen Daten dauerhaft löschen?")) return;
        await RunAsync("Profil löschen", m => m.Delete(selected.Channel, selected.Folder));
    }

    private async Task BackupAsync()
    {
        const string single = "Ausgewähltes Profil", instance = "Aktive Instanz", all = "Alle Instanzen";
        var values = FieldsDialog.Show(this, "Backup erstellen",
            new Field("Scope", "Umfang", FieldType.Combo) { Options = [single, instance, all] },
            new Field("Path", "Backup-Ordner", FieldType.Folder) { Value = Environment.ExpandEnvironmentVariables(_app.Config.BackupPath), Required = true });
        if (values is null) return;

        var path = values["Path"];
        IReadOnlyList<EdgeProfile> targets = values["Scope"] switch
        {
            single => [Selected],
            instance => _app.Discovery.GetProfiles(ActiveChannel),
            _ => _app.Discovery.GetProfiles(),
        };
        if (targets.Count == 0) throw new InvalidOperationException("Keine Profile für das Backup gefunden.");
        var options = Options;
        var perChannel = values["Scope"] != single;
        _app.UpdateConfig(c => c.BackupPath = path);
        await RunAsync("Backup erstellen", m =>
        {
            foreach (var p in targets) m.Backup(p.Channel, p.Folder, perChannel ? Path.Combine(path, p.Channel.ToString()) : path, options);
        });
    }

    private async Task RestoreAsync()
    {
        var values = FieldsDialog.Show(this, "Backup wiederherstellen",
            new Field("Zip", "Backup-ZIP", FieldType.File) { Filter = "Edge-Backup|*.zip", Required = true },
            new Field("Channel", "Zielinstanz", FieldType.Combo) { Options = ChannelsStartingWith(ActiveChannel) });
        if (values is null) return;
        var channel = Enum.Parse<EdgeChannel>(values["Channel"]);
        var options = Options;
        if (SelectTargetFolder(channel, options, "Backup wiederherstellen") is not { } target) return;
        var folder = await RunAsync("Backup wiederherstellen", m =>
            m.Restore(channel, values["Zip"], target.Folder, options with { ReplaceExisting = target.Folder is not null }));
        if (folder is not null) Select(channel, folder);
    }

    private async Task TransferDialogAsync()
    {
        var selected = Selected;
        var values = FieldsDialog.Show(this, "Profil übertragen",
            new Field("Channel", "Zielinstanz", FieldType.Combo) { Options = EdgeChannels.All.Where(c => c != selected.Channel).Select(c => c.ToString()).ToList() },
            new Field("Mode", "Aktion", FieldType.Combo) { Options = ["Kopieren", "Verschieben"] });
        if (values is null) return;
        var channel = Enum.Parse<EdgeChannel>(values["Channel"]);
        var move = values["Mode"] == "Verschieben";
        string? folder = null;
        if (!move)
        {
            if (SelectTargetFolder(channel, Options, "Profil kopieren") is not { } target) return;
            folder = target.Folder;
        }
        await TransferAsync(selected, channel, move, folder, targetConfirmed: folder is not null);
    }

    private async Task TransferAsync(EdgeProfile source, EdgeChannel destination, bool move, string? targetFolder, bool targetConfirmed = false)
    {
        var options = Options;
        if (move)
        {
            if (options.LocalOnly || options.AllowRunningSource)
                throw new InvalidOperationException("Zum Verschieben bitte \"Nur lokale Daten\" und \"Quelle darf laufen\" ausschalten.");
            if (!Confirm($"Profil '{source.Name}' von {source.Channel} nach {destination} verschieben?\n\nDie Quelle wird erst nach erfolgreicher SHA256-Prüfung gelöscht. Caches werden nur mit der entsprechenden Option übernommen.")) return;
            var folder = await RunAsync("Profil verschieben", m => m.MoveVerified(source.Channel, destination, source.Folder, options));
            if (folder is null) return;
            var newKey = $"{destination}/{folder}";
            var updated = _app.ReplaceProfileReference(source.Key, newKey);
            if (updated > 0) AddLog($"Konfiguration: {updated} Verweis(e) von {source.Key} auf {newKey} umgestellt.");
            Select(destination, folder);
            return;
        }

        if (!targetConfirmed && !Confirm($"Profil '{source.Name}' von {source.Channel} nach {destination} kopieren?\n\nEin neues Zielprofil wird angelegt. Verschlüsselte Cookies und Passwörter sind eventuell nicht übertragbar.")) return;
        var copied = await RunAsync("Profil kopieren", m =>
            m.Copy(source.Channel, destination, source.Folder, targetFolder, options with { ReplaceExisting = targetFolder is not null }));
        if (copied is not null) Select(destination, copied);
    }

    /// <summary>Fragt nach dem Zielprofil: neu oder vorhandenes ersetzen bzw. lokale Daten zusammenführen.</summary>
    private TargetChoice? SelectTargetFolder(EdgeChannel channel, TransferOptions options, string title)
    {
        var profiles = _app.Discovery.GetProfiles(channel);
        var labels = new List<string> { NewProfile };
        labels.AddRange(profiles.Select(p => $"{p.Folder} | {p.Name}"));
        var values = FieldsDialog.Show(this, "Zielprofil", new Field("Target", "Zielprofil", FieldType.Combo) { Options = labels });
        if (values is null) return null;
        if (values["Target"] == NewProfile) return new TargetChoice(null);

        var target = profiles[labels.IndexOf(values["Target"]) - 1];
        var effect = options.LocalOnly
            ? $"Lokale Daten in '{target.Name}' zusammenführen? Vorhandene Dateien können ersetzt werden."
            : $"Das vorhandene Profil '{target.Name}' ({channel} / {target.Folder}) wird vollständig ersetzt.";
        return Confirm($"{title}: {effect}\n\nFortfahren?") ? new TargetChoice(target.Folder) : null;
    }

    /// <summary>Gewähltes Zielprofil; <see cref="Folder"/> null = neues Profil anlegen.</summary>
    private sealed record TargetChoice(string? Folder);

    // ---------- Ausführung ----------

    private Task RunAsync(string label, Action<ProfileManager> work) =>
        RunAsync<object>(label, m => { work(m); return new object(); });

    /// <summary>
    /// Führt eine Verwaltungsaktion im Hintergrund aus. Läuft Edge noch, wird nachgefragt, ob die
    /// Instanz beendet werden soll, und die Aktion danach wiederholt.
    /// </summary>
    private async Task<T?> RunAsync<T>(string label, Func<ProfileManager, T> work) where T : class
    {
        if (_busy) throw new InvalidOperationException("Es läuft bereits eine Aktion.");
        _busy = true;
        WorkArea.IsEnabled = false;
        Status.Text = $"{label} …";
        AddLog(label);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                // EdgeRunningException schon im Hintergrund abfangen: Würde sie Task.Run verlassen,
                // hielte der Debugger (Just My Code) sie für unbehandelt, obwohl hier nachgefragt wird.
                var (result, running) = await Task.Run<(T?, EdgeRunningException?)>(() =>
                {
                    try { return (work(_app.Manager), null); }
                    catch (EdgeRunningException ex) { return (null, ex); }
                });
                if (running is null)
                {
                    Status.Text = "Aktion abgeschlossen.";
                    return result;
                }
                if (attempt >= 3) throw running;
                if (!Confirm($"Edge {running.Channel} läuft noch.\n\nEdge {running.Channel} jetzt beenden und fortfahren? Nicht gespeicherte Eingaben können verloren gehen.")) throw running;
                AddLog($"Beende Edge {running.Channel} …");
                await Task.Run(() => _app.Guard.Close(running.Channel));
            }
        }
        catch (Exception ex)
        {
            AddLog($"Fehler: {ex.Message}");
            Status.Text = "Aktion fehlgeschlagen. Details im Protokoll.";
            MessageBox.Show(this, ex.Message, "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally
        {
            _busy = false;
            WorkArea.IsEnabled = true;
            _app.RefreshProfiles();
            RefreshProfiles();
        }
    }

    private void Select(EdgeChannel channel, string folder)
    {
        RefreshProfiles();
        var list = _lists[channel];
        list.SelectedItem = list.Items.Cast<ProfileItem>().FirstOrDefault(i => i.Folder == folder);
        list.ScrollIntoView(list.SelectedItem);
    }

    private static List<string> ChannelsStartingWith(EdgeChannel first) =>
        [first.ToString(), .. EdgeChannels.All.Where(c => c != first).Select(c => c.ToString())];

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void OnManagerLog(string message) => Dispatcher.BeginInvoke(() => AddLog(message));

    private void AddLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Log.AppendText($"{DateTime.Now:HH:mm:ss}  {text}\r\n");
        Log.ScrollToEnd();
        AppLog.Write(text);
    }
}
