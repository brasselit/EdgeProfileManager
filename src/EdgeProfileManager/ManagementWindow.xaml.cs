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
/// Profiles and menu in one window: a board with one column per menu group, on the right the details
/// of the selected card, split into Edge profile (changes Edge) and menu entry (changes config.json).
/// </summary>
public partial class ManagementWindow : Window
{
    private const string NewProfile = "New profile";
    private const string NoDesktop = "— no desktop";
    private const string DragFormat = "EdgeProfileManager.BoardCard";
    private const double LaneWidth = 230;

    private static readonly Brush Ink = Hex("#1D2B30"), Muted = Hex("#4F6066"), Heading = Hex("#163F43"), Accent = Hex("#0F6C74"),
        Line = Hex("#DDE4E6"), LaneBackground = Hex("#E6ECEE"), DropBackground = Hex("#D3E8EA"), Neutral = Hex("#E3EAEC"),
        Running = Hex("#1E6B3A"), Warning = Hex("#A4262C");

    private static readonly string[] ModeLabels = ["Default", "Switch to desktop", "Bring here", "Move to assigned desktop"];

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
            MessageBox.Show(this, "Please wait for the running action to finish.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
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

    /// <summary>Transfer options last used in a dialog; preselected the next time.</summary>
    private TransferOptions _options = new();

    /// <summary>Checkbox fields for the transfer options a dialog needs.</summary>
    private Field[] OptionFields(bool localOnly = true, bool allowRunning = true)
    {
        var fields = new List<Field>();
        if (localOnly) fields.Add(new Field("LocalOnly", "Local data only", FieldType.Check)
        {
            Value = _options.LocalOnly.ToString(),
            ToolTip = "Only Local Storage, IndexedDB etc. instead of the whole profile.",
        });
        fields.Add(new Field("IncludeCaches", "Include caches", FieldType.Check) { Value = _options.IncludeCaches.ToString() });
        if (allowRunning) fields.Add(new Field("AllowRunning", "Allow running source", FieldType.Check)
        {
            Value = _options.AllowRunningSource.ToString(),
            ToolTip = "Back up or copy while Edge is open; the copy may be inconsistent.",
        });
        return [.. fields];
    }

    /// <summary>Reads the option checkboxes of a dialog and remembers them.</summary>
    private TransferOptions ReadOptions(Dictionary<string, string> values)
    {
        bool Get(string key, bool current) => values.TryGetValue(key, out var v) ? v == bool.TrueString : current;
        _options = new TransferOptions
        {
            LocalOnly = Get("LocalOnly", _options.LocalOnly),
            IncludeCaches = Get("IncludeCaches", _options.IncludeCaches),
            AllowRunningSource = Get("AllowRunning", _options.AllowRunningSource),
        };
        return _options with
        {
            LocalOnly = values.ContainsKey("LocalOnly") && _options.LocalOnly,
            AllowRunningSource = values.ContainsKey("AllowRunning") && _options.AllowRunningSource,
        };
    }

    private void OnStateChanged()
    {
        // Do not rebuild during a drag; the board catches up after the drop.
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
        var all = new ToggleButton { Content = "All channels", Style = (Style)Resources["FilterChip"], IsChecked = true };
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
        // Keep the selection: first the same menu entry, otherwise the same profile (e.g. after reloading the file).
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
                LaneKind.Root => "Profiles directly in the tray menu, without a group (below the groups).",
                LaneKind.Unassigned => "Profiles not in the menu. Drag here = remove from menu.",
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
                Text = unassigned ? "All profiles are in the menu." : "Drag profiles here",
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
            ToolTip = "Edit group",
        };
        DockPanel.SetDock(button, Dock.Right);
        var menu = new ContextMenu();
        AddMenuItem(menu, "Rename …", () => RenameGroup(lane.Group!));
        AddMenuItem(menu, "Move left", () => _app.UpdateConfig(c => MenuBoard.MoveGroup(c, lane.Group!, -1)));
        AddMenuItem(menu, "Move right", () => _app.UpdateConfig(c => MenuBoard.MoveGroup(c, lane.Group!, +1)));
        AddMenuItem(menu, "Add separator", () => _app.UpdateConfig(c => MenuBoard.ChildrenOf(c, lane.Group).Add(new MenuEntry { Separator = true })));
        AddMenuItem(menu, "Add subgroup …", () => AddGroup(lane.Group));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "Delete group …", () => DeleteGroup(lane));
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
            Text = profile is null ? $"{card.Reference} – not found" : $"Edge {profile.Channel}",
            FontSize = 11,
            Foreground = Muted,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        top.Children.Add(titles);
        body.Children.Add(top);

        var tags = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (card.Entry?.Desktop is { } desktop) tags.Children.Add(CreateTag($"▣ {desktop}", Hex("#E1EEF0"), Hex("#0F4F55")));
        if (isRunning) tags.Children.Add(CreateTag("● running", Hex("#E3F1E7"), Running));
        if (card.Entry?.JumpList == false) tags.Children.Add(CreateTag("no Jump List", Neutral, Hex("#2E4247")));
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
            ToolTip = profile is null ? null : $"{profile.Name}\n{profile.Key}{(profile.User is null ? "" : $"\n{profile.User}")}\nDouble-click opens the profile.",
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

    // ---------- Groups ----------

    private void AddGroup(MenuEntry? parent)
    {
        var values = FieldsDialog.Show(this, parent is null ? "New group" : $"Subgroup in “{parent.Group}”",
            new Field("Name", "Group name") { Required = true });
        if (values is null) return;
        _app.UpdateConfig(c =>
        {
            if (parent is null) MenuBoard.AddGroup(c, values["Name"]);
            else MenuBoard.ChildrenOf(c, parent).Add(new MenuEntry { Group = values["Name"], Children = [] });
        });
    }

    private void RenameGroup(MenuEntry group)
    {
        var values = FieldsDialog.Show(this, "Rename group", new Field("Name", "Group name") { Value = group.Group, Required = true });
        if (values is not null) _app.UpdateConfig(_ => group.Group = values["Name"]);
    }

    private void DeleteGroup(BoardLane lane)
    {
        var count = lane.Items.OfType<BoardCard>().Count();
        if (count > 0 && !Confirm($"Delete group “{lane.Title}”? Its {count} profile(s) will be removed from the menu (the Edge profiles are kept).")) return;
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
        LabelBox.TextChanged += (_, _) => UpdateLabelStyle();
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
            var card = _selected ?? throw new InvalidOperationException("Please select a card first.");
            _app.UpdateConfig(c => MenuBoard.Add(MenuBoard.ChildrenOf(c, GroupAt(AddToGroupCombo.SelectedIndex)), card.Reference, null));
        });
    }

    /// <summary>Index 0 = main menu, followed by the groups in menu order.</summary>
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
            DetailSubtitle.Text = card.Entry is null ? "not in menu" : $"Card “{card.Title}”";
            ShowProfile(card);

            InMenuPanel.Visibility = card.Entry is null ? Visibility.Collapsed : Visibility.Visible;
            NotInMenuPanel.Visibility = card.Entry is null ? Visibility.Visible : Visibility.Collapsed;
            if (card.Entry is { } entry)
            {
                GroupCombo.ItemsSource = GroupLabels();
                var parent = MenuBoard.FindParentGroup(_app.Config, entry);
                GroupCombo.SelectedIndex = parent is null ? 0 : _groups.ToList().FindIndex(g => ReferenceEquals(g.Group, parent)) + 1;
                LabelBox.Text = entry.Label ?? DefaultLabel;
                ShowDesktop(entry.Desktop);
                ModeCombo.SelectedIndex = entry.DesktopMode is { } mode ? (int)mode + 1 : 0;
                ModeCombo.ToolTip = $"Default: {(entry.Desktop is not null ? ModeLabels[3] : ModeLabels[(int)_app.Config.DefaultDesktopMode + 1])}";
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
        ProfileNameText.Text = profile?.Name ?? "Profile not found";
        ProfileUserText.Text = profile?.User ?? (profile is null ? card.Reference : "");
        var running = profile is not null && _running.Contains(profile.Key);
        RunningText.Text = profile is null ? "" : running ? "● running" : "closed";
        RunningText.Foreground = running ? Running : Muted;
        ColorSwatch.Fill = color ?? Brushes.Transparent;
        ColorText.Text = profile?.Color is { } value ? $"#{value:X6}" : "none";
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

    /// <summary>"2 · Customers" or "2" → number 2; other text → name; empty → no desktop.</summary>
    private static DesktopTarget? ParseDesktop(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text == NoDesktop) return null;
        var number = Regex.Match(text, @"^(\d+)(\s*·.*)?$");
        return number.Success ? new DesktopTarget(int.Parse(number.Groups[1].Value), null) : new DesktopTarget(null, text);
    }

    /// <summary>Label shown when none is configured: the Edge profile name (not saved).</summary>
    private string DefaultLabel => _selected?.Profile?.Name ?? "";

    /// <summary>Empty or equal to the Edge name = no custom label; the field then shows the default again.</summary>
    private void ApplyLabel()
    {
        var label = LabelBox.Text.Trim();
        var value = label.Length == 0 || label == DefaultLabel ? null : label;
        if (value is null && !_loadingDetail) LabelBox.Text = DefaultLabel;
        EditEntry((_, entry) => entry.Label = value, onlyIfChanged: () => value != _selected?.Entry?.Label);
    }

    /// <summary>Shows the default label greyed out, a custom one in normal color.</summary>
    private void UpdateLabelStyle()
    {
        if (LabelBox.Text.Trim() == DefaultLabel) LabelBox.Foreground = Muted;
        else LabelBox.ClearValue(ForegroundProperty);
    }

    private void ApplyDesktop()
    {
        var target = ParseDesktop(DesktopCombo.Text);
        EditEntry((_, entry) => entry.Desktop = target, onlyIfChanged: () => target != _selected?.Entry?.Desktop);
    }

    /// <summary>Changes the menu entry of the selected card and saves the configuration.</summary>
    private void EditEntry(Action<AppConfig, MenuEntry> change, Func<bool>? onlyIfChanged = null)
    {
        if (_loadingDetail || _selected?.Entry is not { } entry) return;
        if (onlyIfChanged is not null && !onlyIfChanged()) return;
        Invoke(() => _app.UpdateConfig(c => change(c, entry)));
    }

    private void EditSettings()
    {
        const string yes = "Yes", no = "No", systemLanguage = "Windows display language";
        var config = _app.Config;
        string[] languages = [systemLanguage, .. Localization.Languages.Select(l => l.Name)];
        var currentLanguage = Localization.Languages.FirstOrDefault(l => string.Equals(l.Code, config.Language, StringComparison.OrdinalIgnoreCase)).Name ?? systemLanguage;
        var values = FieldsDialog.Show(this, "Settings",
            new Field("Language", "Language (applies after restarting the tool)", FieldType.Combo) { Options = languages, Value = currentLanguage },
            new Field("Mode", "If a window is already open (default)", FieldType.Combo) { Options = ModeLabels[1..], Value = ModeLabels[(int)config.DefaultDesktopMode + 1] },
            new Field("Unassigned", "Show unassigned profiles in tray menu", FieldType.Combo) { Options = [yes, no], Value = config.ShowUnassigned ? yes : no },
            new Field("JumpUnassigned", "… and in the Jump List", FieldType.Combo) { Options = [yes, no], Value = config.JumpListUnassigned ? yes : no },
            new Field("JumpStyle", "Jump List", FieldType.Combo)
            {
                Options = ["Categories per group (max. ~10 entries)", "Tasks (all profiles, ungrouped)"],
                Value = config.JumpListStyle == JumpListStyle.Categories ? "Categories per group (max. ~10 entries)" : "Tasks (all profiles, ungrouped)",
            });
        if (values is null) return;
        var languageIndex = Array.IndexOf(languages, values["Language"]);
        _app.UpdateConfig(c =>
        {
            c.Language = languageIndex > 0 ? Localization.Languages[languageIndex - 1].Code : null;
            c.DefaultDesktopMode = (DesktopMode)(Array.IndexOf(ModeLabels, values["Mode"]) - 1);
            c.ShowUnassigned = values["Unassigned"] == yes;
            c.JumpListUnassigned = values["JumpUnassigned"] == yes;
            c.JumpListStyle = values["JumpStyle"].StartsWith("Categories", StringComparison.Ordinal) ? JumpListStyle.Categories : JumpListStyle.Tasks;
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
            Report($"Error: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Invoke(Action action) => Invoke(() => { action(); return Task.CompletedTask; });

    // ---------- Edge actions ----------

    private EdgeProfile SelectedProfile => _selected?.Profile ?? throw new InvalidOperationException("Please select an existing profile first.");

    private EdgeChannel ActiveChannel => _selected?.Profile?.Channel ?? _filter ?? EdgeChannels.All.FirstOrDefault(IsInstalled);

    private bool IsInstalled(EdgeChannel channel) => _app.Environment.FindExecutable(channel) is not null;

    private async Task CreateAsync()
    {
        var channels = ChannelsStartingWith(ActiveChannel);
        var values = FieldsDialog.Show(this, "New profile",
            new Field("Channel", "Channel", FieldType.Combo) { Options = channels },
            new Field("Name", "Profile name") { Required = true },
            new Field("User", "Email") { Required = true },
            new Field("Avatar", "Avatar (optional, PNG)", FieldType.File) { Filter = "PNG image|*.png" });
        if (values is null) return;
        var channel = Enum.Parse<EdgeChannel>(values["Channel"]);
        var created = await RunAsync("Create profile", m => m.Create(channel, values["Name"], values["User"], values["Avatar"]));
        if (created is not null) SelectProfile(created.Key);
    }

    private async Task RenameAsync()
    {
        var selected = SelectedProfile;
        var values = FieldsDialog.Show(this, "Rename profile", new Field("Name", "New name in Edge") { Value = selected.Name, Required = true });
        if (values is not null) await RunAsync("Rename profile", m => m.Rename(selected.Channel, selected.Folder, values["Name"]));
    }

    private async Task AvatarAsync()
    {
        var selected = SelectedProfile;
        var values = FieldsDialog.Show(this, "Profile avatar",
            new Field("Avatar", "PNG file", FieldType.File) { Filter = "PNG image|*.png", Required = true });
        if (values is not null) await RunAsync("Set avatar", m => m.SetAvatar(selected.Channel, selected.Folder, values["Avatar"]));
    }

    private async Task ColorAsync()
    {
        var selected = SelectedProfile;
        if (ProfileColorDialog.Show(this, selected.Name, selected.Color) is { } choice)
            await RunAsync(choice.Rgb is null ? "Remove color" : "Set color", m => m.SetColor(selected.Channel, selected.Folder, choice.Rgb));
    }

    private async Task DeleteAsync()
    {
        var selected = SelectedProfile;
        if (!Confirm($"Permanently delete profile '{selected.Name}' ({selected.Channel} / {selected.Folder}) including all local data?")) return;
        await RunAsync("Delete profile", m => m.Delete(selected.Channel, selected.Folder));
    }

    private async Task BackupAsync() => await BackupAsync(singleOnly: false);

    private async Task BackupAsync(bool singleOnly)
    {
        const string single = "Selected profile", instance = "Channel of selected profile", all = "All channels";
        var fields = new List<Field>();
        if (!singleOnly) fields.Add(new Field("Scope", "Scope", FieldType.Combo) { Options = _selected?.Profile is null ? [all] : [all, instance, single] });
        fields.Add(new Field("Path", "Backup folder", FieldType.Folder) { Value = Environment.ExpandEnvironmentVariables(_app.Config.BackupPath), Required = true });
        fields.AddRange(OptionFields());
        var values = FieldsDialog.Show(this, singleOnly ? $"Backup of “{SelectedProfile.Name}”" : "Create backup", [.. fields]);
        if (values is null) return;

        var path = values["Path"];
        var scope = singleOnly ? single : values["Scope"];
        IReadOnlyList<EdgeProfile> targets = scope switch
        {
            single => [SelectedProfile],
            instance => _app.Discovery.GetProfiles(SelectedProfile.Channel),
            _ => _app.Discovery.GetProfiles(),
        };
        if (targets.Count == 0) throw new InvalidOperationException("No profiles found to back up.");
        var options = ReadOptions(values);
        var perChannel = scope != single;
        _app.UpdateConfig(c => c.BackupPath = path);
        await RunAsync("Create backup", m =>
        {
            foreach (var p in targets) m.Backup(p.Channel, p.Folder, perChannel ? Path.Combine(path, p.Channel.ToString()) : path, options);
        });
    }

    private async Task RestoreAsync()
    {
        var values = FieldsDialog.Show(this, "Restore backup",
        [
            new Field("Zip", "Backup ZIP", FieldType.File) { Filter = "Edge backup|*.zip", Required = true },
            new Field("Channel", "Target channel", FieldType.Combo) { Options = ChannelsStartingWith(ActiveChannel) },
            .. OptionFields(allowRunning: false),
        ]);
        if (values is null) return;
        var channel = Enum.Parse<EdgeChannel>(values["Channel"]);
        var options = ReadOptions(values);
        if (SelectTargetFolder(channel, options, "Restore backup") is not { } target) return;
        var folder = await RunAsync("Restore backup", m =>
            m.Restore(channel, values["Zip"], target.Folder, options with { ReplaceExisting = target.Folder is not null }));
        if (folder is not null) SelectProfile($"{channel}/{folder}");
    }

    private async Task TransferDialogAsync()
    {
        var selected = SelectedProfile;
        var values = FieldsDialog.Show(this, $"Transfer “{selected.Name}”",
        [
            new Field("Channel", "Target channel", FieldType.Combo) { Options = EdgeChannels.All.Where(c => c != selected.Channel).Select(c => c.ToString()).ToList() },
            new Field("Mode", "Action", FieldType.Combo) { Options = ["Copy", "Verified move"] },
            .. OptionFields(),
        ]);
        if (values is null) return;
        var channel = Enum.Parse<EdgeChannel>(values["Channel"]);
        var options = ReadOptions(values);
        if (values["Mode"] == "Verified move")
        {
            if (options.LocalOnly || options.AllowRunningSource)
                throw new InvalidOperationException("A verified move always transfers the whole, closed profile. Turn off \"Local data only\" and \"Allow running source\".");
            if (!Confirm($"Move profile '{selected.Name}' from {selected.Channel} to {channel}?\n\nThe source is deleted only after a successful SHA256 check. Caches are only carried over with the corresponding option. Menu entries are switched to the new profile.")) return;
            var folder = await RunAsync("Move profile", m => m.MoveVerified(selected.Channel, channel, selected.Folder, options));
            if (folder is null) return;
            var newKey = $"{channel}/{folder}";
            var updated = _app.ReplaceProfileReference(selected.Key, newKey);
            if (updated > 0) Report($"Configuration: {updated} reference(s) switched from {selected.Key} to {newKey}.");
            SelectProfile(newKey);
            return;
        }

        if (SelectTargetFolder(channel, options, "Copy profile") is not { } target) return;
        var copied = await RunAsync("Copy profile", m =>
            m.Copy(selected.Channel, channel, selected.Folder, target.Folder, options with { ReplaceExisting = target.Folder is not null }));
        if (copied is not null) SelectProfile($"{channel}/{copied}");
    }

    /// <summary>Asks for the target profile: a new one, or replace an existing one (or merge local data into it).</summary>
    private TargetChoice? SelectTargetFolder(EdgeChannel channel, TransferOptions options, string title)
    {
        var profiles = _app.Discovery.GetProfiles(channel);
        var labels = new List<string> { NewProfile };
        labels.AddRange(profiles.Select(p => $"{p.Folder} | {p.Name}"));
        var values = FieldsDialog.Show(this, "Target profile", new Field("Target", "Target profile", FieldType.Combo) { Options = labels });
        if (values is null) return null;
        if (values["Target"] == NewProfile) return new TargetChoice(null);

        var target = profiles[labels.IndexOf(values["Target"]) - 1];
        var effect = options.LocalOnly
            ? $"Merge local data into '{target.Name}'? Existing files may be replaced."
            : $"The existing profile '{target.Name}' ({channel} / {target.Folder}) will be completely replaced.";
        return Confirm($"{title}: {effect}\n\nContinue?") ? new TargetChoice(target.Folder) : null;
    }

    /// <summary>Chosen target profile; <see cref="Folder"/> null = create a new profile.</summary>
    private sealed record TargetChoice(string? Folder);

    private void SelectProfile(string key)
    {
        _app.RefreshProfiles();
        var card = _lanes.SelectMany(l => l.Items.OfType<BoardCard>())
            .FirstOrDefault(c => string.Equals(c.Reference, key, StringComparison.OrdinalIgnoreCase));
        if (card is not null) Select(card);
    }

    // ---------- Execution ----------

    private Task RunAsync(string label, Action<ProfileManager> work) =>
        RunAsync<object>(label, m => { work(m); return new object(); });

    /// <summary>
    /// Runs a management action in the background. If Edge is still running, asks whether to close
    /// that channel and then retries the action.
    /// </summary>
    private async Task<T?> RunAsync<T>(string label, Func<ProfileManager, T> work) where T : class
    {
        if (_busy) throw new InvalidOperationException("An action is already running.");
        _busy = true;
        WorkArea.IsEnabled = DetailContent.IsEnabled = false;
        Report($"{label} …");
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                // Catch EdgeRunningException in the background already: if it left Task.Run, the debugger
                // (Just My Code) would treat it as unhandled even though we prompt for it here.
                var (result, running) = await Task.Run<(T?, EdgeRunningException?)>(() =>
                {
                    try { return (work(_app.Manager), null); }
                    catch (EdgeRunningException ex) { return (null, ex); }
                });
                if (running is null)
                {
                    Status.Text = $"{label}: done.";
                    return result;
                }
                if (attempt >= 3) throw running;
                if (!Confirm($"Edge {running.Channel} is still running.\n\nClose Edge {running.Channel} now and continue? Unsaved input may be lost.")) throw running;
                Report($"Closing Edge {running.Channel} …");
                await Task.Run(() => _app.Guard.Close(running.Channel));
            }
        }
        catch (Exception ex)
        {
            Report($"Error: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
