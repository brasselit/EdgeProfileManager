using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EdgeProfileManager.Core.Config;
using EdgeProfileManager.Core.Edge;
using EdgeProfileManager.Core.Management;
using EdgeProfileManager.Dialogs;

namespace EdgeProfileManager;

/// <summary>
/// Profile und Menü in einem Fenster: Board mit einer Spalte pro Menügruppe, rechts die Details
/// der gewählten Karte, getrennt in Edge-Profil (ändert Edge) und Menü-Eintrag (ändert config.json).
/// </summary>
public partial class ManagementWindow : Window
{
    private const string NewProfile = "Neues Profil";
    private const string NoDesktop = "— kein Desktop";
    private const string DragFormat = "EdgeProfileManager.BoardCard";
    private const double LaneWidth = 230;

    private static readonly Brush Ink = Hex("#1D2B30"), Muted = Hex("#4F6066"), Heading = Hex("#163F43"), Accent = Hex("#0F6C74"),
        Line = Hex("#DDE4E6"), LaneBackground = Hex("#E6ECEE"), DropBackground = Hex("#D3E8EA"), Neutral = Hex("#E3EAEC"),
        Running = Hex("#1E6B3A"), Warning = Hex("#A4262C");

    private static readonly string[] ModeLabels = ["Standard", "Zum Desktop wechseln", "Hierher holen", "Auf zugewiesenen Desktop verschieben"];

    private readonly App _app;
    private readonly List<(BoardCard Card, Border View)> _cardViews = [];
    private readonly Dictionary<string, (DateTime Modified, ImageSource Image)> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _runningTimer;
    private IReadOnlyList<BoardLane> _lanes = [];
    private IReadOnlyList<BoardGroup> _groups = [];
    private HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private EdgeChannel? _filter;
    private BoardCard? _selected;
    private bool _busy;
    private bool _loadingDetail;
    private Point? _dragStart;
    private BoardCard? _dragCard;
    private Border? _dropLane;
    private Border? _dropCard;

    public ManagementWindow(App app)
    {
        _app = app;
        InitializeComponent();
        using (var icon = typeof(ManagementWindow).Assembly.GetManifestResourceStream("app.ico"))
        {
            if (icon is not null) Icon = BitmapFrame.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        BuildFilters();
        ModeCombo.ItemsSource = ModeLabels;
        WireToolbar();
        WireDetails();

        _runningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _runningTimer.Tick += (_, _) => RefreshRunning();
        _runningTimer.Start();

        _app.StateChanged += OnStateChanged;
        _app.Manager.Log += OnManagerLog;
        Closing += (_, e) =>
        {
            if (!_busy) return;
            e.Cancel = true;
            MessageBox.Show(this, "Bitte die laufende Aktion abwarten.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        };
        Closed += (_, _) =>
        {
            _runningTimer.Stop();
            _app.StateChanged -= OnStateChanged;
            _app.Manager.Log -= OnManagerLog;
        };
        _running = _app.GetRunningProfileKeys();
        RebuildBoard();
    }

    private TransferOptions Options => new()
    {
        LocalOnly = LocalOption.IsChecked == true,
        IncludeCaches = CacheOption.IsChecked == true,
        AllowRunningSource = RunningOption.IsChecked == true,
    };

    private void OnStateChanged()
    {
        // Während eines Drags nicht neu aufbauen; das Board folgt nach dem Drop.
        if (_dragCard is null) RebuildBoard();
    }

    private void RefreshRunning()
    {
        if (_dragCard is not null || !IsVisible) return;
        var running = _app.GetRunningProfileKeys();
        if (running.SetEquals(_running)) return;
        _running = running;
        RebuildBoard();
    }

    // ---------- Board ----------

    private void BuildFilters()
    {
        var all = new ToggleButton { Content = "Alle Instanzen", Style = (Style)Resources["FilterChip"], IsChecked = true };
        Filters.Children.Add(all);
        foreach (var channel in EdgeChannels.All)
            Filters.Children.Add(new ToggleButton { Content = channel.ToString(), Style = (Style)Resources["FilterChip"], Tag = channel });
        foreach (ToggleButton chip in Filters.Children)
        {
            chip.Click += (_, _) =>
            {
                _filter = chip.Tag as EdgeChannel?;
                foreach (ToggleButton other in Filters.Children) other.IsChecked = other == chip;
                RebuildBoard();
            };
        }
    }

    private void RebuildBoard()
    {
        _lanes = MenuBoard.Build(_app.Config, _app.Profiles);
        _groups = MenuBoard.GetGroups(_app.Config);
        var cards = _lanes.SelectMany(l => l.Items.OfType<BoardCard>()).ToList();
        // Auswahl behalten: zuerst derselbe Menüeintrag, sonst dasselbe Profil (z.B. nach dem Neuladen der Datei).
        _selected = _selected is null ? null
            : cards.FirstOrDefault(c => c.Entry is not null && ReferenceEquals(c.Entry, _selected.Entry))
              ?? cards.FirstOrDefault(c => string.Equals(c.Reference, _selected.Reference, StringComparison.OrdinalIgnoreCase));

        _cardViews.Clear();
        Lanes.Children.Clear();
        foreach (var lane in _lanes) Lanes.Children.Add(CreateLane(lane));
        UpdateSelectionVisuals();
        ShowDetails();
    }

    private bool IsVisibleCard(BoardCard card) => _filter is null || card.Profile is null || card.Profile.Channel == _filter;

    private Border CreateLane(BoardLane lane)
    {
        var unassigned = lane.Kind == LaneKind.Unassigned;
        var view = new Border
        {
            Width = LaneWidth,
            Margin = new Thickness(0, 0, 10, 0),
            CornerRadius = new CornerRadius(8),
            Background = unassigned ? Brushes.Transparent : LaneBackground,
            BorderBrush = unassigned ? Hex("#9AA8AD") : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            AllowDrop = true,
            Tag = lane,
        };
        var layout = new DockPanel();
        view.Child = layout;

        var header = new DockPanel { Margin = new Thickness(10, 8, 6, 6) };
        DockPanel.SetDock(header, Dock.Top);
        if (lane.Kind == LaneKind.Group) header.Children.Add(CreateLaneMenuButton(lane));
        var visibleCards = lane.Items.OfType<BoardCard>().Count(IsVisibleCard);
        header.Children.Add(new TextBlock
        {
            Text = lane.Title,
            FontWeight = FontWeights.SemiBold,
            Foreground = Heading,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = lane.Kind switch
            {
                LaneKind.Root => "Profile direkt im Tray-Menü, ohne Gruppe (unterhalb der Gruppen).",
                LaneKind.Unassigned => "Profile, die nicht im Menü stehen. Hierher ziehen = aus dem Menü entfernen.",
                _ => null,
            },
        });
        header.Children.Add(new TextBlock { Text = $"  {visibleCards}", Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        layout.Children.Add(header);

        var items = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };
        foreach (var item in lane.Items)
        {
            switch (item)
            {
                case BoardCard card when IsVisibleCard(card):
                    items.Children.Add(CreateCard(lane, card));
                    break;
                case BoardHeader subgroup:
                    items.Children.Add(new TextBlock
                    {
                        Text = $"› {subgroup.Group.Group}",
                        Foreground = Muted,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 12,
                        Margin = new Thickness(4 + subgroup.Depth * 12, 6, 0, 4),
                    });
                    break;
                case BoardSeparator separator:
                    items.Children.Add(new Border { Height = 1, Background = Hex("#B5C1C5"), Margin = new Thickness(8 + separator.Depth * 12, 4, 8, 6) });
                    break;
            }
        }
        if (visibleCards == 0)
        {
            items.Children.Add(new TextBlock
            {
                Text = unassigned ? "Alle Profile stehen im Menü." : "Profile hierher ziehen",
                Foreground = Muted,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 16),
            });
        }
        layout.Children.Add(new ScrollViewer { Content = items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        view.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetData(DragFormat) is BoardCard ? DragDropEffects.Move : DragDropEffects.None;
            SetDropTarget(view, null);
            e.Handled = true;
        };
        view.Drop += (_, e) =>
        {
            e.Handled = true;
            if (e.Data.GetData(DragFormat) is BoardCard dragged) Invoke(() => DropCard(dragged, lane, null));
        };
        return view;
    }

    private Button CreateLaneMenuButton(BoardLane lane)
    {
        var button = new Button
        {
            Style = (Style)Resources["EditButton"],
            Content = "",
            Width = 24,
            Height = 24,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            ToolTip = "Gruppe bearbeiten",
        };
        DockPanel.SetDock(button, Dock.Right);
        var menu = new ContextMenu();
        AddMenuItem(menu, "Umbenennen …", () => RenameGroup(lane.Group!));
        AddMenuItem(menu, "Nach links", () => _app.UpdateConfig(c => MenuBoard.MoveGroup(c, lane.Group!, -1)));
        AddMenuItem(menu, "Nach rechts", () => _app.UpdateConfig(c => MenuBoard.MoveGroup(c, lane.Group!, +1)));
        AddMenuItem(menu, "Trennlinie anhängen", () => _app.UpdateConfig(c => MenuBoard.ChildrenOf(c, lane.Group).Add(new MenuEntry { Separator = true })));
        AddMenuItem(menu, "Untergruppe anlegen …", () => AddGroup(lane.Group));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "Gruppe löschen …", () => DeleteGroup(lane));
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        return button;
    }

    private void AddMenuItem(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => Invoke(action);
        menu.Items.Add(item);
    }

    private Border CreateCard(BoardLane lane, BoardCard card)
    {
        var profile = card.Profile;
        var color = profile?.Color is { } rgb ? new SolidColorBrush(ProfileColorDialog.ToColor(rgb)) : null;
        var isRunning = profile is not null && _running.Contains(profile.Key);

        var body = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        var top = new DockPanel();
        top.Children.Add(CreateAvatar(card, 30));
        var titles = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = card.Title,
            FontWeight = isRunning ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = profile is null ? Warning : Ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        titles.Children.Add(new TextBlock
        {
            Text = profile is null ? $"{card.Reference} – nicht gefunden" : $"Edge {profile.Channel}",
            FontSize = 11,
            Foreground = Muted,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        top.Children.Add(titles);
        body.Children.Add(top);

        var tags = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (card.Entry?.Desktop is { } desktop) tags.Children.Add(CreateTag($"▣ {desktop}", Hex("#E1EEF0"), Hex("#0F4F55")));
        if (isRunning) tags.Children.Add(CreateTag("● läuft", Hex("#E3F1E7"), Running));
        if (card.Entry?.JumpList == false) tags.Children.Add(CreateTag("ohne Jump List", Neutral, Hex("#2E4247")));
        if (tags.Children.Count > 0) body.Children.Add(tags);

        var content = new DockPanel();
        var bar = new Border { Height = 4, Background = color ?? Brushes.Transparent, CornerRadius = new CornerRadius(5, 5, 0, 0) };
        DockPanel.SetDock(bar, Dock.Top);
        content.Children.Add(bar);
        content.Children.Add(body);

        var view = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(card.Depth * 12, 0, 0, 8),
            Child = content,
            Cursor = Cursors.Hand,
            AllowDrop = true,
            ToolTip = profile is null ? null : $"{profile.Name}\n{profile.Key}{(profile.User is null ? "" : $"\n{profile.User}")}\nDoppelklick öffnet das Profil.",
        };
        _cardViews.Add((card, view));

        view.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && profile is not null)
            {
                Invoke(() => _app.Open(profile.Key, newWindow: false));
                return;
            }
            Select(card);
        };
        view.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this);
            _dragCard = null;
        };
        view.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start) return;
            var point = e.GetPosition(this);
            if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragStart = null;
            _dragCard = card;
            view.Opacity = 0.5;
            try { DragDrop.DoDragDrop(view, new DataObject(DragFormat, card), DragDropEffects.Move); }
            finally
            {
                _dragCard = null;
                SetDropTarget(null, null);
                RebuildBoard();
            }
        };
        view.DragOver += (_, e) =>
        {
            e.Effects = lane.Kind != LaneKind.Unassigned && e.Data.GetData(DragFormat) is BoardCard dragged && dragged != card
                ? DragDropEffects.Move : DragDropEffects.None;
            if (e.Effects == DragDropEffects.Move) SetDropTarget(null, view);
            e.Handled = true;
        };
        view.Drop += (_, e) =>
        {
            e.Handled = true;
            if (lane.Kind == LaneKind.Unassigned || e.Data.GetData(DragFormat) is not BoardCard dragged || dragged == card) return;
            Invoke(() => DropCard(dragged, lane, card));
        };
        return view;
    }

    private FrameworkElement CreateAvatar(BoardCard card, double size)
    {
        var circle = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = card.Profile?.Color is { } rgb ? new SolidColorBrush(ProfileColorDialog.ToColor(rgb)) : Neutral,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (LoadImage(card.Profile?.IconPath) is { } image)
        {
            circle.Child = new Image { Source = image, Stretch = Stretch.UniformToFill, Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2) };
        }
        else
        {
            circle.Child = new TextBlock
            {
                Text = Initials(card.Profile?.Name ?? card.Title),
                FontSize = size * 0.36,
                FontWeight = FontWeights.Bold,
                Foreground = card.Profile?.Color is null ? Hex("#2E4247") : Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        return circle;
    }

    private static Border CreateTag(string text, Brush background, Brush foreground) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(6, 1, 6, 1),
        Margin = new Thickness(0, 0, 4, 2),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = foreground },
    };

    private void SetDropTarget(Border? lane, Border? card)
    {
        if (_dropLane is not null && _dropLane != lane)
            _dropLane.Background = ((BoardLane)_dropLane.Tag).Kind == LaneKind.Unassigned ? Brushes.Transparent : LaneBackground;
        if (_dropCard is not null && _dropCard != card)
        {
            _dropCard.BorderThickness = new Thickness(1);
            UpdateSelectionVisuals();
        }
        _dropLane = lane;
        _dropCard = card;
        if (lane is not null) lane.Background = DropBackground;
        if (card is not null)
        {
            card.BorderBrush = Accent;
            card.BorderThickness = new Thickness(1, 4, 1, 1);
        }
    }

    private void DropCard(BoardCard dragged, BoardLane lane, BoardCard? before)
    {
        _app.UpdateConfig(c =>
        {
            if (lane.Kind == LaneKind.Unassigned)
            {
                if (dragged.Entry is not null) MenuBoard.Remove(c, dragged.Entry);
                return;
            }
            var target = before?.Entry is { } anchor ? MenuBoard.FindParent(c, anchor)! : MenuBoard.ChildrenOf(c, lane.Group);
            if (dragged.Entry is not null) MenuBoard.Place(c, dragged.Entry, target, before?.Entry);
            else MenuBoard.Add(target, dragged.Reference, before?.Entry);
        });
        _selected = dragged;
        RebuildBoard();
    }

    private void Select(BoardCard card)
    {
        _selected = card;
        UpdateSelectionVisuals();
        ShowDetails();
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var (card, view) in _cardViews)
        {
            var selected = _selected is not null && card == _selected;
            view.BorderBrush = selected ? Accent : Line;
            view.BorderThickness = new Thickness(selected ? 2 : 1);
        }
    }

    // ---------- Gruppen ----------

    private void AddGroup(MenuEntry? parent)
    {
        var values = FieldsDialog.Show(this, parent is null ? "Neue Gruppe" : $"Untergruppe in „{parent.Group}“",
            new Field("Name", "Name der Gruppe") { Required = true });
        if (values is null) return;
        _app.UpdateConfig(c =>
        {
            if (parent is null) MenuBoard.AddGroup(c, values["Name"]);
            else MenuBoard.ChildrenOf(c, parent).Add(new MenuEntry { Group = values["Name"], Children = [] });
        });
    }

    private void RenameGroup(MenuEntry group)
    {
        var values = FieldsDialog.Show(this, "Gruppe umbenennen", new Field("Name", "Name der Gruppe") { Value = group.Group, Required = true });
        if (values is not null) _app.UpdateConfig(_ => group.Group = values["Name"]);
    }

    private void DeleteGroup(BoardLane lane)
    {
        var count = lane.Items.OfType<BoardCard>().Count();
        if (count > 0 && !Confirm($"Gruppe „{lane.Title}“ löschen? Ihre {count} Profil(e) werden aus dem Menü entfernt (die Edge-Profile bleiben erhalten).")) return;
        _app.UpdateConfig(c => MenuBoard.Remove(c, lane.Group!));
    }

    // ---------- Details ----------

    private void WireToolbar()
    {
        AddGroupButton.Click += (_, _) => Invoke(() => AddGroup(null));
        NewProfileButton.Click += (_, _) => Invoke(CreateAsync);
        RestoreButton.Click += (_, _) => Invoke(RestoreAsync);
        BackupButton.Click += (_, _) => Invoke(BackupAsync);
        SettingsButton.Click += (_, _) => Invoke(EditSettings);
        RefreshButton.Click += (_, _) => _app.RefreshProfiles();
        LogButton.Click += (_, _) => _app.EditFile(AppLog.FilePath);
    }

    private void WireDetails()
    {
        AvatarButton.Click += (_, _) => Invoke(AvatarAsync);
        RenameButton.Click += (_, _) => Invoke(RenameAsync);
        ColorButton.Click += (_, _) => Invoke(ColorAsync);
        TransferButton.Click += (_, _) => Invoke(TransferDialogAsync);
        OpenFolderButton.Click += (_, _) => Invoke(() => { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SelectedProfile.Path}\""))?.Dispose(); });
        OpenButton.Click += (_, _) => Invoke(() => _app.Open(SelectedProfile.Key, newWindow: false));
        NewWindowButton.Click += (_, _) => Invoke(() => _app.Open(SelectedProfile.Key, newWindow: true));
        BackupProfileButton.Click += (_, _) => Invoke(() => BackupAsync(singleOnly: true));
        DeleteButton.Click += (_, _) => Invoke(DeleteAsync);

        GroupCombo.SelectionChanged += (_, _) => EditEntry((c, entry) =>
            MenuBoard.Place(c, entry, MenuBoard.ChildrenOf(c, GroupAt(GroupCombo.SelectedIndex)), null));
        LabelBox.LostFocus += (_, _) => ApplyLabel();
        LabelBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyLabel(); };
        DesktopCombo.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(ApplyDesktop);
        DesktopCombo.LostFocus += (_, _) => ApplyDesktop();
        DesktopCombo.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyDesktop(); };
        ModeCombo.SelectionChanged += (_, _) => EditEntry((_, entry) =>
            entry.DesktopMode = ModeCombo.SelectedIndex <= 0 ? null : (DesktopMode)(ModeCombo.SelectedIndex - 1));
        JumpListCheck.Click += (_, _) => EditEntry((_, entry) => entry.JumpList = JumpListCheck.IsChecked == true ? null : false);
        RemoveFromMenuButton.Click += (_, _) => EditEntry((c, entry) => MenuBoard.Remove(c, entry));
        AddToMenuButton.Click += (_, _) => Invoke(() =>
        {
            var card = _selected ?? throw new InvalidOperationException("Bitte zuerst eine Karte auswählen.");
            _app.UpdateConfig(c => MenuBoard.Add(MenuBoard.ChildrenOf(c, GroupAt(AddToGroupCombo.SelectedIndex)), card.Reference, null));
        });
    }

    /// <summary>Index 0 = Hauptmenü, danach die Gruppen in Menüreihenfolge.</summary>
    private MenuEntry? GroupAt(int index) => index <= 0 || index > _groups.Count ? null : _groups[index - 1].Group;

    private List<string> GroupLabels() => [BoardLane.RootTitle, .. _groups.Select(g => g.Path)];

    private void ShowDetails()
    {
        _loadingDetail = true;
        try
        {
            var card = _selected;
            EmptyHint.Visibility = card is null ? Visibility.Visible : Visibility.Collapsed;
            ProfileSection.Visibility = MenuSection.Visibility = card is null ? Visibility.Collapsed : Visibility.Visible;
            if (card is null)
            {
                DetailSubtitle.Text = "";
                return;
            }
            DetailSubtitle.Text = card.Entry is null ? "nicht im Menü" : $"Karte „{card.Title}“";
            ShowProfile(card);

            InMenuPanel.Visibility = card.Entry is null ? Visibility.Collapsed : Visibility.Visible;
            NotInMenuPanel.Visibility = card.Entry is null ? Visibility.Visible : Visibility.Collapsed;
            if (card.Entry is { } entry)
            {
                GroupCombo.ItemsSource = GroupLabels();
                var parent = MenuBoard.FindParentGroup(_app.Config, entry);
                GroupCombo.SelectedIndex = parent is null ? 0 : _groups.ToList().FindIndex(g => ReferenceEquals(g.Group, parent)) + 1;
                LabelBox.Text = entry.Label ?? "";
                ShowDesktop(entry.Desktop);
                ModeCombo.SelectedIndex = entry.DesktopMode is { } mode ? (int)mode + 1 : 0;
                ModeCombo.ToolTip = $"Standard: {(entry.Desktop is not null ? ModeLabels[3] : ModeLabels[(int)_app.Config.DefaultDesktopMode + 1])}";
                JumpListCheck.IsChecked = entry.JumpList != false;
            }
            else
            {
                AddToGroupCombo.ItemsSource = GroupLabels();
                AddToGroupCombo.SelectedIndex = Math.Min(1, _groups.Count);
            }
        }
        finally
        {
            _loadingDetail = false;
        }
    }

    private void ShowProfile(BoardCard card)
    {
        var profile = card.Profile;
        ProfileDetails.IsEnabled = profile is not null;
        var color = profile?.Color is { } rgb ? new SolidColorBrush(ProfileColorDialog.ToColor(rgb)) : null;
        AvatarCircle.Background = color ?? Neutral;
        AvatarImage.Source = LoadImage(profile?.IconPath);
        AvatarInitials.Text = AvatarImage.Source is null ? Initials(profile?.Name ?? card.Title) : "";
        AvatarInitials.Foreground = color is null ? Hex("#2E4247") : Brushes.White;
        ProfileNameText.Text = profile?.Name ?? "Profil nicht gefunden";
        ProfileUserText.Text = profile?.User ?? (profile is null ? card.Reference : "");
        var running = profile is not null && _running.Contains(profile.Key);
        RunningText.Text = profile is null ? "" : running ? "● läuft" : "geschlossen";
        RunningText.Foreground = running ? Running : Muted;
        ColorSwatch.Fill = color ?? Brushes.Transparent;
        ColorText.Text = profile?.Color is { } value ? $"#{value:X6}" : "keine";
        ChannelText.Text = profile is null ? "–" : $"Edge {profile.Channel}";
        FolderText.Text = profile?.Folder ?? "";
        PathText.Text = profile?.Path ?? "";
        PathText.ToolTip = profile?.Path;
    }

    private void ShowDesktop(DesktopTarget? target)
    {
        var options = new List<string> { NoDesktop };
        for (var i = 0; i < _app.Desktops.DesktopCount; i++)
            options.Add(_app.Desktops.GetDesktopName(i) is { Length: > 0 } name ? $"{i + 1} · {name}" : $"{i + 1}");
        DesktopCombo.ItemsSource = options;
        DesktopCombo.Text = target switch
        {
            null => NoDesktop,
            { Number: { } n } => options.FirstOrDefault(o => o == $"{n}" || o.StartsWith($"{n} · ", StringComparison.Ordinal)) ?? $"{n}",
            _ => target.Name ?? NoDesktop,
        };
    }

    /// <summary>"2 · Kunden" oder "2" → Nummer 2; anderer Text → Name; leer → kein Desktop.</summary>
    private static DesktopTarget? ParseDesktop(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text == NoDesktop) return null;
        var number = Regex.Match(text, @"^(\d+)(\s*·.*)?$");
        return number.Success ? new DesktopTarget(int.Parse(number.Groups[1].Value), null) : new DesktopTarget(null, text);
    }

    /// <summary>Leer oder gleich dem Edge-Namen = kein eigenes Label.</summary>
    private void ApplyLabel()
    {
        var label = LabelBox.Text.Trim();
        var value = label.Length == 0 || label == _selected?.Profile?.Name ? null : label;
        EditEntry((_, entry) => entry.Label = value, onlyIfChanged: () => value != _selected?.Entry?.Label);
    }

    private void ApplyDesktop()
    {
        var target = ParseDesktop(DesktopCombo.Text);
        EditEntry((_, entry) => entry.Desktop = target, onlyIfChanged: () => target != _selected?.Entry?.Desktop);
    }

    /// <summary>Ändert den Menüeintrag der gewählten Karte und speichert die Konfiguration.</summary>
    private void EditEntry(Action<AppConfig, MenuEntry> change, Func<bool>? onlyIfChanged = null)
    {
        if (_loadingDetail || _selected?.Entry is not { } entry) return;
        if (onlyIfChanged is not null && !onlyIfChanged()) return;
        Invoke(() => _app.UpdateConfig(c => change(c, entry)));
    }

    private void EditSettings()
    {
        const string yes = "Ja", no = "Nein";
        var config = _app.Config;
        var values = FieldsDialog.Show(this, "Einstellungen",
            new Field("Mode", "Wenn ein Fenster schon offen ist (Standard)", FieldType.Combo) { Options = ModeLabels[1..], Value = ModeLabels[(int)config.DefaultDesktopMode + 1] },
            new Field("Unassigned", "Nicht zugeordnete Profile im Tray-Menü zeigen", FieldType.Combo) { Options = [yes, no], Value = config.ShowUnassigned ? yes : no },
            new Field("JumpUnassigned", "… und in der Jump List", FieldType.Combo) { Options = [yes, no], Value = config.JumpListUnassigned ? yes : no },
            new Field("JumpStyle", "Jump List", FieldType.Combo)
            {
                Options = ["Kategorien pro Gruppe (max. ca. 10 Einträge)", "Aufgaben (alle Profile, ungruppiert)"],
                Value = config.JumpListStyle == JumpListStyle.Categories ? "Kategorien pro Gruppe (max. ca. 10 Einträge)" : "Aufgaben (alle Profile, ungruppiert)",
            });
        if (values is null) return;
        _app.UpdateConfig(c =>
        {
            c.DefaultDesktopMode = (DesktopMode)(Array.IndexOf(ModeLabels, values["Mode"]) - 1);
            c.ShowUnassigned = values["Unassigned"] == yes;
            c.JumpListUnassigned = values["JumpUnassigned"] == yes;
            c.JumpListStyle = values["JumpStyle"].StartsWith("Kategorien", StringComparison.Ordinal) ? JumpListStyle.Categories : JumpListStyle.Tasks;
        });
    }

    private static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    private ImageSource? LoadImage(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        var modified = File.GetLastWriteTimeUtc(path);
        if (_images.TryGetValue(path, out var cached) && cached.Modified == modified) return cached.Image;
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad)
                .Frames.OrderByDescending(f => f.PixelWidth).First();
            frame.Freeze();
            _images[path] = (modified, frame);
            return frame;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static SolidColorBrush Hex(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    private async void Invoke(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            Report($"Fehler: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Invoke(Action action) => Invoke(() => { action(); return Task.CompletedTask; });

    // ---------- Edge-Aktionen ----------

    private EdgeProfile SelectedProfile => _selected?.Profile ?? throw new InvalidOperationException("Bitte zuerst ein vorhandenes Profil auswählen.");

    private EdgeChannel ActiveChannel => _selected?.Profile?.Channel ?? _filter ?? EdgeChannels.All.FirstOrDefault(IsInstalled);

    private bool IsInstalled(EdgeChannel channel) => _app.Environment.FindExecutable(channel) is not null;

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
        if (created is not null) SelectProfile(created.Key);
    }

    private async Task RenameAsync()
    {
        var selected = SelectedProfile;
        var values = FieldsDialog.Show(this, "Profil umbenennen", new Field("Name", "Neuer Name in Edge") { Value = selected.Name, Required = true });
        if (values is not null) await RunAsync("Profil umbenennen", m => m.Rename(selected.Channel, selected.Folder, values["Name"]));
    }

    private async Task AvatarAsync()
    {
        var selected = SelectedProfile;
        var values = FieldsDialog.Show(this, "Profilavatar",
            new Field("Avatar", "PNG-Datei", FieldType.File) { Filter = "PNG-Bild|*.png", Required = true });
        if (values is not null) await RunAsync("Avatar setzen", m => m.SetAvatar(selected.Channel, selected.Folder, values["Avatar"]));
    }

    private async Task ColorAsync()
    {
        var selected = SelectedProfile;
        if (ProfileColorDialog.Show(this, selected.Name, selected.Color) is { } choice)
            await RunAsync(choice.Rgb is null ? "Farbe entfernen" : "Farbe setzen", m => m.SetColor(selected.Channel, selected.Folder, choice.Rgb));
    }

    private async Task DeleteAsync()
    {
        var selected = SelectedProfile;
        if (!Confirm($"Profil '{selected.Name}' ({selected.Channel} / {selected.Folder}) samt allen lokalen Daten dauerhaft löschen?")) return;
        await RunAsync("Profil löschen", m => m.Delete(selected.Channel, selected.Folder));
    }

    private async Task BackupAsync() => await BackupAsync(singleOnly: false);

    private async Task BackupAsync(bool singleOnly)
    {
        const string single = "Ausgewähltes Profil", instance = "Instanz des ausgewählten Profils", all = "Alle Instanzen";
        var fields = new List<Field>();
        if (!singleOnly) fields.Add(new Field("Scope", "Umfang", FieldType.Combo) { Options = _selected?.Profile is null ? [all] : [all, instance, single] });
        fields.Add(new Field("Path", "Backup-Ordner", FieldType.Folder) { Value = Environment.ExpandEnvironmentVariables(_app.Config.BackupPath), Required = true });
        var values = FieldsDialog.Show(this, singleOnly ? $"Backup von „{SelectedProfile.Name}“" : "Backup erstellen", [.. fields]);
        if (values is null) return;

        var path = values["Path"];
        var scope = singleOnly ? single : values["Scope"];
        IReadOnlyList<EdgeProfile> targets = scope switch
        {
            single => [SelectedProfile],
            instance => _app.Discovery.GetProfiles(SelectedProfile.Channel),
            _ => _app.Discovery.GetProfiles(),
        };
        if (targets.Count == 0) throw new InvalidOperationException("Keine Profile für das Backup gefunden.");
        var options = Options;
        var perChannel = scope != single;
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
        if (folder is not null) SelectProfile($"{channel}/{folder}");
    }

    private async Task TransferDialogAsync()
    {
        var selected = SelectedProfile;
        var values = FieldsDialog.Show(this, $"„{selected.Name}“ übertragen",
            new Field("Channel", "Zielinstanz", FieldType.Combo) { Options = EdgeChannels.All.Where(c => c != selected.Channel).Select(c => c.ToString()).ToList() },
            new Field("Mode", "Aktion", FieldType.Combo) { Options = ["Kopieren", "Verschieben"] });
        if (values is null) return;
        var channel = Enum.Parse<EdgeChannel>(values["Channel"]);
        var options = Options;
        if (values["Mode"] == "Verschieben")
        {
            if (options.LocalOnly || options.AllowRunningSource)
                throw new InvalidOperationException("Zum Verschieben bitte \"Nur lokale Daten\" und \"Quelle darf laufen\" ausschalten.");
            if (!Confirm($"Profil '{selected.Name}' von {selected.Channel} nach {channel} verschieben?\n\nDie Quelle wird erst nach erfolgreicher SHA256-Prüfung gelöscht. Caches werden nur mit der entsprechenden Option übernommen. Menüeinträge werden auf das neue Profil umgestellt.")) return;
            var folder = await RunAsync("Profil verschieben", m => m.MoveVerified(selected.Channel, channel, selected.Folder, options));
            if (folder is null) return;
            var newKey = $"{channel}/{folder}";
            var updated = _app.ReplaceProfileReference(selected.Key, newKey);
            if (updated > 0) Report($"Konfiguration: {updated} Verweis(e) von {selected.Key} auf {newKey} umgestellt.");
            SelectProfile(newKey);
            return;
        }

        if (SelectTargetFolder(channel, options, "Profil kopieren") is not { } target) return;
        var copied = await RunAsync("Profil kopieren", m =>
            m.Copy(selected.Channel, channel, selected.Folder, target.Folder, options with { ReplaceExisting = target.Folder is not null }));
        if (copied is not null) SelectProfile($"{channel}/{copied}");
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

    private void SelectProfile(string key)
    {
        _app.RefreshProfiles();
        var card = _lanes.SelectMany(l => l.Items.OfType<BoardCard>())
            .FirstOrDefault(c => string.Equals(c.Reference, key, StringComparison.OrdinalIgnoreCase));
        if (card is not null) Select(card);
    }

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
        WorkArea.IsEnabled = DetailContent.IsEnabled = false;
        Report($"{label} …");
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
                    Status.Text = $"{label}: abgeschlossen.";
                    return result;
                }
                if (attempt >= 3) throw running;
                if (!Confirm($"Edge {running.Channel} läuft noch.\n\nEdge {running.Channel} jetzt beenden und fortfahren? Nicht gespeicherte Eingaben können verloren gehen.")) throw running;
                Report($"Beende Edge {running.Channel} …");
                await Task.Run(() => _app.Guard.Close(running.Channel));
            }
        }
        catch (Exception ex)
        {
            Report($"Fehler: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally
        {
            _busy = false;
            WorkArea.IsEnabled = DetailContent.IsEnabled = true;
            _app.RefreshProfiles();
        }
    }

    private static List<string> ChannelsStartingWith(EdgeChannel first) =>
        [first.ToString(), .. EdgeChannels.All.Where(c => c != first).Select(c => c.ToString())];

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void OnManagerLog(string message) => Dispatcher.BeginInvoke(() => Report(message));

    private void Report(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Status.Text = text;
        AppLog.Write(text);
    }
}
