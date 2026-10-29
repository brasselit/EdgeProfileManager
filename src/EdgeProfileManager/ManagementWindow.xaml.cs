using System.Diagnostics;
using System.Globalization;
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
using EdgeProfileManager.Resources;

namespace EdgeProfileManager;

/// <summary>
/// Profiles and menu in one window: a board with one column per menu group, on the right the details
/// of the selected card, split into Edge profile (changes Edge) and menu entry (changes config.json).
/// </summary>
public partial class ManagementWindow : Window
{
    private static string NoDesktop => BoardStrings.Desktop_None;
    private const string DragFormat = "EdgeProfileManager.BoardCard";
    private const double LaneWidth = 230;

    private static readonly Brush Ink = Hex("#1D2B30"), Muted = Hex("#4F6066"), Heading = Hex("#163F43"), Accent = Hex("#0F6C74"),
        Line = Hex("#DDE4E6"), LaneBackground = Hex("#E6ECEE"), DropBackground = Hex("#D3E8EA"), Neutral = Hex("#E3EAEC"),
        Running = Hex("#1E6B3A"), Warning = Hex("#A4262C");

    /// <summary>Labels of <see cref="DesktopMode"/>, index = mode + 1; index 0 = default (no mode set).</summary>
    private static readonly string[] ModeLabels =
        [BoardStrings.Mode_Default, BoardStrings.Mode_SwitchToDesktop, BoardStrings.Mode_BringHere, BoardStrings.Mode_MoveToAssigned];

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
            MessageBox.Show(this, BoardStrings.Message_WaitForAction, Title, MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (localOnly) fields.Add(new Field("LocalOnly", BoardStrings.Option_LocalOnly, FieldType.Check)
        {
            Value = _options.LocalOnly.ToString(),
            ToolTip = BoardStrings.Option_LocalOnlyToolTip,
        });
        fields.Add(new Field("IncludeCaches", BoardStrings.Option_IncludeCaches, FieldType.Check) { Value = _options.IncludeCaches.ToString() });
        if (allowRunning) fields.Add(new Field("AllowRunning", BoardStrings.Option_AllowRunning, FieldType.Check)
        {
            Value = _options.AllowRunningSource.ToString(),
            ToolTip = BoardStrings.Option_AllowRunningToolTip,
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
        var all = new ToggleButton { Content = BoardStrings.Filter_AllChannels, Style = (Style)Resources["FilterChip"], IsChecked = true };
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
                LaneKind.Root => BoardStrings.Lane_RootToolTip,
                LaneKind.Unassigned => BoardStrings.Lane_UnassignedToolTip,
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
                Text = unassigned ? BoardStrings.Lane_AllAssigned : BoardStrings.Lane_DropHere,
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
            ToolTip = BoardStrings.Lane_EditGroup,
        };
        DockPanel.SetDock(button, Dock.Right);
        var menu = new ContextMenu();
        AddMenuItem(menu, BoardStrings.Lane_RenameGroup, () => RenameGroup(lane.Group!));
        AddMenuItem(menu, BoardStrings.Lane_MoveLeft, () => _app.UpdateConfig(c => MenuBoard.MoveGroup(c, lane.Group!, -1)));
        AddMenuItem(menu, BoardStrings.Lane_MoveRight, () => _app.UpdateConfig(c => MenuBoard.MoveGroup(c, lane.Group!, +1)));
        AddMenuItem(menu, BoardStrings.Lane_AddSeparator, () => _app.UpdateConfig(c => MenuBoard.ChildrenOf(c, lane.Group).Add(new MenuEntry { Separator = true })));
        AddMenuItem(menu, BoardStrings.Lane_AddSubgroup, () => AddGroup(lane.Group));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, BoardStrings.Lane_DeleteGroup, () => DeleteGroup(lane));
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
            Text = profile is null ? string.Format(CultureInfo.CurrentCulture, BoardStrings.Card_NotFound, card.Reference) : $"Edge {profile.Channel}",
            FontSize = 11,
            Foreground = Muted,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        top.Children.Add(titles);
        body.Children.Add(top);

        var tags = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (card.Entry?.Desktop is { } desktop) tags.Children.Add(CreateTag($"▣ {desktop}", Hex("#E1EEF0"), Hex("#0F4F55")));
        if (isRunning) tags.Children.Add(CreateTag(BoardStrings.Card_Running, Hex("#E3F1E7"), Running));
        if (card.Entry?.JumpList == false) tags.Children.Add(CreateTag(BoardStrings.Card_NoJumpList, Neutral, Hex("#2E4247")));
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
            ToolTip = profile is null ? null : $"{profile.Name}\n{profile.Key}{(profile.User is null ? "" : $"\n{profile.User}")}\n{BoardStrings.Card_DoubleClickHint}",
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
        var values = FieldsDialog.Show(this,
            parent is null ? BoardStrings.Dialog_NewGroupTitle : string.Format(CultureInfo.CurrentCulture, BoardStrings.Dialog_SubgroupTitle, parent.Group),
            new Field("Name", BoardStrings.Dialog_GroupName) { Required = true });
        if (values is null) return;
        _app.UpdateConfig(c =>
        {
            if (parent is null) MenuBoard.AddGroup(c, values["Name"]);
            else MenuBoard.ChildrenOf(c, parent).Add(new MenuEntry { Group = values["Name"], Children = [] });
        });
    }

    private void RenameGroup(MenuEntry group)
    {
        var values = FieldsDialog.Show(this, BoardStrings.Dialog_RenameGroupTitle, new Field("Name", BoardStrings.Dialog_GroupName) { Value = group.Group, Required = true });
        if (values is not null) _app.UpdateConfig(_ => group.Group = values["Name"]);
    }

    private void DeleteGroup(BoardLane lane)
    {
        var count = lane.Items.OfType<BoardCard>().Count();
        if (count > 0 && !Confirm(string.Format(CultureInfo.CurrentCulture, BoardStrings.Confirm_DeleteGroup, lane.Title, count))) return;
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
            var card = _selected ?? throw new InvalidOperationException(BoardStrings.Error_SelectCard);
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
            DetailSubtitle.Text = card.Entry is null ? BoardStrings.Detail_SubtitleNotInMenu : string.Format(CultureInfo.CurrentCulture, BoardStrings.Detail_SubtitleCard, card.Title);
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
                ModeCombo.ToolTip = string.Format(CultureInfo.CurrentCulture, BoardStrings.Detail_ModeDefault,
                    entry.Desktop is not null ? ModeLabels[3] : ModeLabels[(int)_app.Config.DefaultDesktopMode + 1]);
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
        ProfileNameText.Text = profile?.Name ?? BoardStrings.Detail_ProfileNotFound;
        ProfileUserText.Text = profile?.User ?? (profile is null ? card.Reference : "");
        var running = profile is not null && _running.Contains(profile.Key);
        RunningText.Text = profile is null ? "" : running ? BoardStrings.Card_Running : BoardStrings.Detail_Closed;
        RunningText.Foreground = running ? Running : Muted;
        ColorSwatch.Fill = color ?? Brushes.Transparent;
        ColorText.Text = profile?.Color is { } value ? $"#{value:X6}" : BoardStrings.Detail_NoColor;
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
        // Combo options are compared by index, never by their (localized) text.
        string[] yesNo = [BoardStrings.Settings_Yes, BoardStrings.Settings_No];
        string[] jumpStyles = [BoardStrings.Settings_JumpListCategories, BoardStrings.Settings_JumpListTasks];
        string[] modes = ModeLabels[1..];
        var config = _app.Config;
        string[] languages = [BoardStrings.Settings_SystemLanguage, .. Localization.Languages.Select(l => l.Name)];
        var currentLanguage = Localization.Languages.FirstOrDefault(l => string.Equals(l.Code, config.Language, StringComparison.OrdinalIgnoreCase)).Name
            ?? BoardStrings.Settings_SystemLanguage;
        var values = FieldsDialog.Show(this, BoardStrings.Settings_Title,
            new Field("Language", BoardStrings.Settings_Language, FieldType.Combo) { Options = languages, Value = currentLanguage },
            new Field("Mode", BoardStrings.Settings_Mode, FieldType.Combo) { Options = modes, Value = modes[(int)config.DefaultDesktopMode] },
            new Field("Unassigned", BoardStrings.Settings_ShowUnassigned, FieldType.Combo) { Options = yesNo, Value = yesNo[config.ShowUnassigned ? 0 : 1] },
            new Field("JumpUnassigned", BoardStrings.Settings_JumpListUnassigned, FieldType.Combo) { Options = yesNo, Value = yesNo[config.JumpListUnassigned ? 0 : 1] },
            new Field("JumpStyle", BoardStrings.Settings_JumpListStyle, FieldType.Combo)
            {
                Options = jumpStyles,
                Value = jumpStyles[config.JumpListStyle == JumpListStyle.Categories ? 0 : 1],
            });
        if (values is null) return;
        var languageIndex = Array.IndexOf(languages, values["Language"]);
        var modeIndex = Array.IndexOf(modes, values["Mode"]);
        _app.UpdateConfig(c =>
        {
            c.Language = languageIndex > 0 ? Localization.Languages[languageIndex - 1].Code : null;
            if (modeIndex >= 0) c.DefaultDesktopMode = (DesktopMode)modeIndex;
            c.ShowUnassigned = Array.IndexOf(yesNo, values["Unassigned"]) == 0;
            c.JumpListUnassigned = Array.IndexOf(yesNo, values["JumpUnassigned"]) == 0;
            c.JumpListStyle = Array.IndexOf(jumpStyles, values["JumpStyle"]) == 0 ? JumpListStyle.Categories : JumpListStyle.Tasks;
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
            Report(string.Format(CultureInfo.CurrentCulture, BoardStrings.Status_Error, ex.Message));
            MessageBox.Show(this, ex.Message, BoardStrings.Message_ErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Invoke(Action action) => Invoke(() => { action(); return Task.CompletedTask; });

    // ---------- Edge actions ----------

    private EdgeProfile SelectedProfile => _selected?.Profile ?? throw new InvalidOperationException(BoardStrings.Error_SelectProfile);

    private EdgeChannel ActiveChannel => _selected?.Profile?.Channel ?? _filter ?? EdgeChannels.All.FirstOrDefault(IsInstalled);

    private bool IsInstalled(EdgeChannel channel) => _app.Environment.FindExecutable(channel) is not null;

    /// <summary>File dialog filters: localized description, fixed pattern.</summary>
    private static string PngFilter => BoardStrings.Dialog_PngFilter + "|*.png";
    private static string BackupFilter => BoardStrings.Dialog_BackupFilter + "|*.zip";

    private async Task CreateAsync()
    {
        var channels = ChannelsStartingWith(ActiveChannel);
        var values = FieldsDialog.Show(this, BoardStrings.Dialog_NewProfileTitle,
            new Field("Channel", BoardStrings.Dialog_Channel, FieldType.Combo) { Options = channels },
            new Field("Name", BoardStrings.Dialog_ProfileName) { Required = true },
            new Field("User", BoardStrings.Dialog_Email) { Required = true },
            new Field("Avatar", BoardStrings.Dialog_AvatarOptional, FieldType.File) { Filter = PngFilter });
        if (values is null) return;
        var channel = Enum.Parse<EdgeChannel>(values["Channel"]);
        var created = await RunAsync(BoardStrings.Action_CreateProfile, m => m.Create(channel, values["Name"], values["User"], values["Avatar"]));
        if (created is not null) SelectProfile(created.Key);
    }

    private async Task RenameAsync()
    {
        var selected = SelectedProfile;
        var values = FieldsDialog.Show(this, BoardStrings.Dialog_RenameProfileTitle, new Field("Name", BoardStrings.Dialog_NewName) { Value = selected.Name, Required = true });
        if (values is not null) await RunAsync(BoardStrings.Action_RenameProfile, m => m.Rename(selected.Channel, selected.Folder, values["Name"]));
    }

    private async Task AvatarAsync()
    {
        var selected = SelectedProfile;
        var values = FieldsDialog.Show(this, BoardStrings.Dialog_AvatarTitle,
            new Field("Avatar", BoardStrings.Dialog_PngFile, FieldType.File) { Filter = PngFilter, Required = true });
        if (values is not null) await RunAsync(BoardStrings.Action_SetAvatar, m => m.SetAvatar(selected.Channel, selected.Folder, values["Avatar"]));
    }

    private async Task ColorAsync()
    {
        var selected = SelectedProfile;
        if (ProfileColorDialog.Show(this, selected.Name, selected.Color) is { } choice)
            await RunAsync(choice.Rgb is null ? BoardStrings.Action_RemoveColor : BoardStrings.Action_SetColor, m => m.SetColor(selected.Channel, selected.Folder, choice.Rgb));
    }

    private async Task DeleteAsync()
    {
        var selected = SelectedProfile;
        if (!Confirm(string.Format(CultureInfo.CurrentCulture, BoardStrings.Confirm_DeleteProfile, selected.Name, selected.Channel, selected.Folder))) return;
        await RunAsync(BoardStrings.Action_DeleteProfile, m => m.Delete(selected.Channel, selected.Folder));
    }

    private async Task BackupAsync() => await BackupAsync(singleOnly: false);

    private async Task BackupAsync(bool singleOnly)
    {
        // Scope options by index: 0 = all channels, 1 = channel of selected profile, 2 = selected profile.
        const int all = 0, instance = 1, single = 2;
        string[] scopes = [BoardStrings.Dialog_BackupScopeAll, BoardStrings.Dialog_BackupScopeChannel, BoardStrings.Dialog_BackupScopeSingle];
        var fields = new List<Field>();
        if (!singleOnly) fields.Add(new Field("Scope", BoardStrings.Dialog_BackupScope, FieldType.Combo) { Options = _selected?.Profile is null ? [scopes[all]] : scopes });
        fields.Add(new Field("Path", BoardStrings.Dialog_BackupFolder, FieldType.Folder) { Value = Environment.ExpandEnvironmentVariables(_app.Config.BackupPath), Required = true });
        fields.AddRange(OptionFields());
        var values = FieldsDialog.Show(this,
            singleOnly ? string.Format(CultureInfo.CurrentCulture, BoardStrings.Dialog_BackupOfTitle, SelectedProfile.Name) : BoardStrings.Action_CreateBackup,
            [.. fields]);
        if (values is null) return;

        var path = values["Path"];
        var scope = singleOnly ? single : Array.IndexOf(scopes, values["Scope"]);
        IReadOnlyList<EdgeProfile> targets = scope switch
        {
            single => [SelectedProfile],
            instance => _app.Discovery.GetProfiles(SelectedProfile.Channel),
            _ => _app.Discovery.GetProfiles(),
        };
        if (targets.Count == 0) throw new InvalidOperationException(BoardStrings.Error_NoProfilesToBackUp);
        var options = ReadOptions(values);
        var perChannel = scope != single;
        _app.UpdateConfig(c => c.BackupPath = path);
        await RunAsync(BoardStrings.Action_CreateBackup, m =>
        {
            foreach (var p in targets) m.Backup(p.Channel, p.Folder, perChannel ? Path.Combine(path, p.Channel.ToString()) : path, options);
        });
    }

    private async Task RestoreAsync()
    {
        var values = FieldsDialog.Show(this, BoardStrings.Action_RestoreBackup,
        [
            new Field("Zip", BoardStrings.Dialog_BackupZip, FieldType.File) { Filter = BackupFilter, Required = true },
            new Field("Channel", BoardStrings.Dialog_TargetChannel, FieldType.Combo) { Options = ChannelsStartingWith(ActiveChannel) },
            .. OptionFields(allowRunning: false),
        ]);
        if (values is null) return;
        var channel = Enum.Parse<EdgeChannel>(values["Channel"]);
        var options = ReadOptions(values);
        if (SelectTargetFolder(channel, options, BoardStrings.Action_RestoreBackup) is not { } target) return;
        var folder = await RunAsync(BoardStrings.Action_RestoreBackup, m =>
            m.Restore(channel, values["Zip"], target.Folder, options with { ReplaceExisting = target.Folder is not null }));
        if (folder is not null) SelectProfile($"{channel}/{folder}");
    }

    private async Task TransferDialogAsync()
    {
        var selected = SelectedProfile;
        // Action options by index: 0 = copy, 1 = verified move.
        string[] actions = [BoardStrings.Dialog_TransferCopy, BoardStrings.Dialog_TransferVerifiedMove];
        var values = FieldsDialog.Show(this, string.Format(CultureInfo.CurrentCulture, BoardStrings.Dialog_TransferTitle, selected.Name),
        [
            new Field("Channel", BoardStrings.Dialog_TargetChannel, FieldType.Combo) { Options = EdgeChannels.All.Where(c => c != selected.Channel).Select(c => c.ToString()).ToList() },
            new Field("Mode", BoardStrings.Dialog_TransferAction, FieldType.Combo) { Options = actions },
            .. OptionFields(),
        ]);
        if (values is null) return;
        var channel = Enum.Parse<EdgeChannel>(values["Channel"]);
        var options = ReadOptions(values);
        if (Array.IndexOf(actions, values["Mode"]) == 1)
        {
            if (options.LocalOnly || options.AllowRunningSource)
                throw new InvalidOperationException(BoardStrings.Error_VerifiedMoveOptions);
            if (!Confirm(string.Format(CultureInfo.CurrentCulture, BoardStrings.Confirm_MoveProfile, selected.Name, selected.Channel, channel))) return;
            var folder = await RunAsync(BoardStrings.Action_MoveProfile, m => m.MoveVerified(selected.Channel, channel, selected.Folder, options));
            if (folder is null) return;
            var newKey = $"{channel}/{folder}";
            var updated = _app.ReplaceProfileReference(selected.Key, newKey);
            if (updated > 0) Report(string.Format(CultureInfo.CurrentCulture, BoardStrings.Status_ReferencesSwitched, updated, selected.Key, newKey));
            SelectProfile(newKey);
            return;
        }

        if (SelectTargetFolder(channel, options, BoardStrings.Action_CopyProfile) is not { } target) return;
        var copied = await RunAsync(BoardStrings.Action_CopyProfile, m =>
            m.Copy(selected.Channel, channel, selected.Folder, target.Folder, options with { ReplaceExisting = target.Folder is not null }));
        if (copied is not null) SelectProfile($"{channel}/{copied}");
    }

    /// <summary>Asks for the target profile: a new one, or replace an existing one (or merge local data into it).</summary>
    private TargetChoice? SelectTargetFolder(EdgeChannel channel, TransferOptions options, string title)
    {
        var profiles = _app.Discovery.GetProfiles(channel);
        // Index 0 = new profile, followed by the existing profiles.
        var labels = new List<string> { BoardStrings.Dialog_TargetNewProfile };
        labels.AddRange(profiles.Select(p => $"{p.Folder} | {p.Name}"));
        var values = FieldsDialog.Show(this, BoardStrings.Dialog_TargetProfile,
            new Field("Target", BoardStrings.Dialog_TargetProfile, FieldType.Combo) { Options = labels });
        if (values is null) return null;
        var index = labels.IndexOf(values["Target"]);
        if (index == 0) return new TargetChoice(null);

        var target = profiles[index - 1];
        var effect = options.LocalOnly
            ? string.Format(CultureInfo.CurrentCulture, BoardStrings.Confirm_MergeLocal, target.Name)
            : string.Format(CultureInfo.CurrentCulture, BoardStrings.Confirm_ReplaceProfile, target.Name, channel, target.Folder);
        return Confirm(string.Format(CultureInfo.CurrentCulture, BoardStrings.Confirm_TargetContinue, title, effect)) ? new TargetChoice(target.Folder) : null;
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
        if (_busy) throw new InvalidOperationException(BoardStrings.Error_ActionRunning);
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
                    Status.Text = string.Format(CultureInfo.CurrentCulture, BoardStrings.Status_Done, label);
                    return result;
                }
                if (attempt >= 3) throw running;
                if (!Confirm(string.Format(CultureInfo.CurrentCulture, BoardStrings.Confirm_CloseEdge, running.Channel))) throw running;
                Report(string.Format(CultureInfo.CurrentCulture, BoardStrings.Status_Closing, running.Channel));
                await Task.Run(() => _app.Guard.Close(running.Channel));
            }
        }
        catch (Exception ex)
        {
            Report(string.Format(CultureInfo.CurrentCulture, BoardStrings.Status_Error, ex.Message));
            MessageBox.Show(this, ex.Message, BoardStrings.Message_ErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
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
