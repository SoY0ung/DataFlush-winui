using System.Collections.ObjectModel;
using DataFlushWinUI.Models;
using DataFlushWinUI.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace DataFlushWinUI.Pages;

public sealed partial class HatPage : Page
{
    private readonly AppState _state = AppState.Current;
    private readonly Dictionary<GridView, ObservableCollection<ImageItem>> _items = [];
    private readonly Dictionary<GridView, ComboBox> _classes = [];
    private readonly Dictionary<GridView, TextBlock> _statuses = [];
    private readonly Dictionary<GridView, Slider> _zooms = [];
    private readonly Dictionary<GridView, Border> _selectionRects = [];
    private readonly Dictionary<GridView, HashSet<ImageItem>> _marqueeBaseSelection = [];
    private readonly Dictionary<GridView, HashSet<ImageItem>> _lastMarqueeSelection = [];
    private readonly Dictionary<GridView, ImageItem> _lastPreviewItems = [];
    private readonly List<MoveRecord> _undoRecords = [];
    private readonly DispatcherTimer _toastCloseTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private GridView? _activeList;
    private PreviewWindow? _previewWindow;
    private bool _marqueeSelecting;
    private bool _marqueeAdditive;
    private Point _marqueeStart;
    private Point _marqueeCurrent;
    private long _lastMarqueeTick;
    private bool _syncingClass;
    private bool _initializing;
    private bool _skipNextDataChangedRefresh;

    public HatPage()
    {
        InitializeComponent();
        _initializing = true;
        try
        {
            SyncPairCheck.IsChecked = true;
            ConfigurePane(LeftList, LeftClass, LeftStatus, LeftZoom, LeftSelectionRect, 0);
            ConfigurePane(RightList, RightClass, RightStatus, RightZoom, RightSelectionRect, 16);
        }
        finally
        {
            _initializing = false;
        }

        Loaded += (_, _) => RefreshState();
        _toastCloseTimer.Tick += ToastCloseTimer_Tick;
        Unloaded += (_, _) => TogglePreview(false);
        _state.DataChanged += AppState_DataChanged;
        Unloaded += (_, _) => _state.DataChanged -= AppState_DataChanged;
    }

    private void ConfigurePane(GridView list, ComboBox combo, TextBlock status, Slider zoom, Border selectionRect, int classIndex)
    {
        combo.ItemsSource = FaceClasses.HatPageClasses;
        combo.DisplayMemberPath = nameof(FaceClass.DisplayName);
        combo.SelectedItem = FaceClasses.HatPageClasses.First(c => c.Index == classIndex);
        var source = new ObservableCollection<ImageItem>();
        list.ItemsSource = source;
        _items[list] = source;
        _classes[list] = combo;
        _statuses[list] = status;
        _zooms[list] = zoom;
        _selectionRects[list] = selectionRect;
        _marqueeBaseSelection[list] = [];
        _lastMarqueeSelection[list] = [];
        list.PointerWheelChanged += PaneList_PointerWheelChanged;
        list.PointerPressed += PaneList_PointerPressed;
        list.PointerMoved += PaneList_PointerMoved;
        list.PointerReleased += PaneList_PointerReleased;
        list.PointerCanceled += PaneList_PointerCanceled;
        list.AddHandler(KeyDownEvent, new KeyEventHandler(PaneList_KeyDown), true);
        list.DragItemsStarting += PaneList_DragItemsStarting;
        list.DragOver += PaneList_DragOver;
        list.Drop += PaneList_Drop;
    }

    private void AppState_DataChanged(object? sender, EventArgs e)
    {
        if (_skipNextDataChangedRefresh)
        {
            _skipNextDataChangedRefresh = false;
            return;
        }

        DispatcherQueue.TryEnqueue(RefreshState);
    }

    private void RefreshState()
    {
        var ready = Directory.Exists(_state.DatasetPath) && Directory.Exists(_state.CsvPath) && _state.Store.IsLoaded;
        var thresholdActive = _state.IsThresholdFilterEnabled;
        var enabled = ready && !thresholdActive && _state.Store.IsHatExtensionEnabled;
        UnavailableTitle.Text = thresholdActive ? "帽子分类已禁用" : "帽子分类不可用";
        UnavailableMessage.Text = thresholdActive
            ? "当前已启用阈值过滤，请先到 AI辅助 > 阈值过滤 解锁后再使用帽子分类。"
            : "请先前往清洗分类选择数据集和 CSV。";
        UnavailablePane.Visibility = !ready || thresholdActive ? Visibility.Visible : Visibility.Collapsed;
        EnablePane.Visibility = ready && !thresholdActive && !enabled ? Visibility.Visible : Visibility.Collapsed;
        WorkPane.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        SyncPairCheck.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        PreviewToggle.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        DisableHatButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled)
        {
            RefreshAll();
        }
    }

    private async void EnableHat_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsThresholdFilterEnabled)
        {
            ShowThresholdDisabledTip();
            return;
        }

        var confirm = await MainWindow.AppWindowInstance.ShowConfirmAsync("开启帽子分类", "帽子分类会建立新的CSV文件存储细化类别，不会对已有CSV造成影响。要开启帽子分类吗？");
        if (!confirm) return;
        _state.Store.EnableHatExtension();
        _state.NotifyHatSettingsChanged();
        RefreshState();
        ShowToast("帽子分类已启用。", false);
    }

    private async void DisableHat_Click(object sender, RoutedEventArgs e)
    {
        var confirm = await MainWindow.AppWindowInstance.ShowConfirmAsync("停用帽子分类", "停用帽子分类会删除细化分类的CSV文件，已细化的数据会还原到对应的原始分类中。要停用帽子分类吗？");
        if (!confirm) return;
        _state.Store.DisableHatExtension();
        _state.SetShowHatClassesInCleanPage(false);
        _state.NotifyHatSettingsChanged();
        _undoRecords.Clear();
        TogglePreview(false);
        RefreshState();
        ShowToast("帽子分类已停用，细化数据已还原。", false);
    }

    private void GoClean_Click(object sender, RoutedEventArgs e)
        => Frame.Navigate(typeof(CleanPage));

    private void PaneClass_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        if (_syncingClass)
        {
            return;
        }

        if (ReferenceEquals(sender, LeftClass) && SyncPairCheck.IsChecked == true && CurrentComboClass(LeftClass) is { } left && FaceClasses.HatPairMap.TryGetValue(left, out var pair))
        {
            _syncingClass = true;
            SelectClass(RightClass, pair);
            _syncingClass = false;
        }

        RefreshAll();
    }

    private void SyncPairCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        if (SyncPairCheck.IsChecked == true && CurrentComboClass(LeftClass) is { } left && FaceClasses.HatPairMap.TryGetValue(left, out var pair))
        {
            SelectClass(RightClass, pair);
        }
    }

    private void RefreshAll()
    {
        foreach (var (list, source) in _items)
        {
            source.Clear();
            var classIndex = CurrentClassIndex(list);
            if (!_state.Store.IsLoaded || classIndex < 0 || classIndex >= _state.Store.Items.Length) continue;
            foreach (var name in _state.Store.Items[classIndex])
            {
                var item = new ImageItem(name, classIndex);
                item.SetThumbnailSize(_zooms[list].Value);
                source.Add(item);
            }

            _statuses[list].Text = $"总数：{source.Count} 已选：{list.SelectedItems.Count}";
        }

        UpdatePreview();
    }

    private void PaneSelection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not GridView list) return;
        _activeList = list;
        var added = e.AddedItems.OfType<ImageItem>().LastOrDefault();
        if (added is not null)
        {
            _lastPreviewItems[list] = added;
        }

        _statuses[list].Text = $"总数：{_items[list].Count} 已选：{list.SelectedItems.Count}";
        UpdatePreview();
    }

    private void Image_ItemClick(object sender, ItemClickEventArgs e)
    {
        _activeList = (GridView)sender;
        if (e.ClickedItem is ImageItem item)
        {
            _lastPreviewItems[_activeList] = item;
        }
        UpdatePreview();
    }

    private void Pane_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not GridView list) return;
        _activeList = list;
        var readOnly = _state.IsThresholdFilterEnabled;
        var selected = SelectedNames(list);
        var flyout = new MenuFlyout();
        if (selected.Count == 0)
        {
            flyout.Items.Add(MenuItem("刷新缩略图", (_, _) => RefreshAll()));
            flyout.Items.Add(MenuItem("显示预览", (_, _) => TogglePreview(true)));
        }
        else
        {
            var source = CurrentClassIndex(list);
            var primary = FaceClasses.OriginalHatSources.Contains(source)
                ? FaceClasses.HatTargets
                : FaceClasses.OriginalHatSources;
            var secondary = FaceClasses.OriginalHatSources.Contains(source)
                ? FaceClasses.OriginalHatSources
                : FaceClasses.HatTargets;

            foreach (var target in primary.Where(i => i != source))
            {
                var item = MenuItem($"移动到 {FaceClasses.ByIndex(target).Name}", (_, _) => MoveSelected(list, target));
                item.IsEnabled = !readOnly;
                flyout.Items.Add(item);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());
            var sub = new MenuFlyoutSubItem { Text = "移动到..." };
            foreach (var target in secondary.Where(i => i != source))
            {
                var cls = FaceClasses.ByIndex(target);
                var item = MenuItem(cls.DisplayName, (_, _) => MoveSelected(list, target));
                item.IsEnabled = !readOnly;
                sub.Items.Add(item);
            }

            sub.IsEnabled = !readOnly;
            flyout.Items.Add(sub);
        }

        flyout.ShowAt(list, e.GetPosition(list));
    }

    private MenuFlyoutItem MenuItem(string text, RoutedEventHandler handler)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += handler;
        return item;
    }

    private void ShowToast(string message, bool undo)
    {
        if (!_state.ShowOperationTips)
        {
            return;
        }

        _toastCloseTimer.Stop();
        HatToastBar.Title = message;
        HatToastBar.ActionButton = undo ? new Button { Content = "撤销" } : null;
        if (HatToastBar.ActionButton is Button button)
        {
            button.Click += Undo_Click;
        }

        HatToastBar.Visibility = Visibility.Visible;
        HatToastBar.IsOpen = true;
        _toastCloseTimer.Start();
    }

    private void ShowThresholdDisabledTip()
        => ShowToast("当前已启用阈值过滤，请先到 AI辅助 > 阈值过滤 解锁后再使用帽子分类。", false);

    private void ToastCloseTimer_Tick(object? sender, object e)
    {
        _toastCloseTimer.Stop();
        HatToastBar.IsOpen = false;
        HatToastBar.Visibility = Visibility.Collapsed;
    }

    private void HatToastBar_CloseButtonClick(InfoBar sender, object args)
    {
        _toastCloseTimer.Stop();
        HatToastBar.Visibility = Visibility.Collapsed;
    }

    private void MoveSelected(GridView list, int target)
    {
        if (_state.IsThresholdFilterEnabled)
        {
            ShowThresholdDisabledTip();
            return;
        }

        var selected = SelectedNames(list);
        var source = CurrentClassIndex(list);
        if (selected.Count == 0 || source == target) return;
        var order = _state.Store.Items[source].ToList();
        var record = _state.Store.Move(source, target, selected);
        if (record is null) return;
        _undoRecords.Add(record);
        RefreshAll();
        SelectNextAfterMove(list, order, record.FileNames);
        _skipNextDataChangedRefresh = true;
        _state.NotifyDataChanged();
        ShowToast($"已将 {record.FileNames.Count} 张图像从 {FaceClasses.ByIndex(source).Name} 移动到 {FaceClasses.ByIndex(target).Name}", true);
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsThresholdFilterEnabled)
        {
            ShowThresholdDisabledTip();
            return;
        }

        var record = _undoRecords.LastOrDefault();
        if (record is null) return;
        _state.Store.Undo(record);
        _undoRecords.Remove(record);
        RefreshAll();
        _skipNextDataChangedRefresh = true;
        _state.NotifyDataChanged();
        ShowToast("已撤销。", false);
    }

    private void PreviewToggle_Click(object sender, RoutedEventArgs e)
        => TogglePreview(PreviewToggle.IsChecked == true);

    private void TogglePreview(bool show)
    {
        if (!show)
        {
            _previewWindow?.Close();
            _previewWindow = null;
            PreviewToggle.IsChecked = false;
            return;
        }

        _previewWindow ??= new PreviewWindow();
        _previewWindow.Closed += (_, _) =>
        {
            _previewWindow = null;
            PreviewToggle.IsChecked = false;
        };
        _previewWindow.Activate();
        PreviewToggle.IsChecked = true;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_previewWindow is null) return;
        var item = PreviewItem();
        if (item is not null)
        {
            _previewWindow.ShowImage(item);
        }
    }

    private ImageItem? PreviewItem()
    {
        if (_activeList is not null && _lastPreviewItems.TryGetValue(_activeList, out var item) && _items[_activeList].Contains(item))
        {
            return item;
        }

        return _activeList?.SelectedItems.OfType<ImageItem>().LastOrDefault();
    }

    private void PaneList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not GridView list) return;
        _activeList = list;
        if (e.Key == VirtualKey.Space)
        {
            TogglePreview(_previewWindow is null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.A && IsCtrlDown())
        {
            list.SelectedItems.Clear();
            foreach (var item in _items[list])
            {
                list.SelectedItems.Add(item);
            }
            e.Handled = true;
        }
    }

    private void PaneList_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not GridView list || !IsCtrlDown()) return;
        e.Handled = true;
        var slider = _zooms[list];
        var delta = e.GetCurrentPoint(list).Properties.MouseWheelDelta > 0 ? 10 : -10;
        slider.Value = Math.Clamp(slider.Value + delta, slider.Minimum, slider.Maximum);
    }

    private void Zoom_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is not Slider slider) return;
        var list = _zooms.FirstOrDefault(x => x.Value == slider).Key;
        if (list is null) return;
        foreach (var item in _items[list])
        {
            item.SetThumbnailSize(e.NewValue);
        }
    }

    private void PaneList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not GridView list) return;
        _activeList = list;
        var point = e.GetCurrentPoint(list);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (FindAncestor<GridViewItem>(e.OriginalSource as DependencyObject) is not null) return;
        _marqueeSelecting = true;
        _marqueeAdditive = IsCtrlDown();
        _marqueeStart = ClampPointToElement(point.Position, list);
        _marqueeCurrent = _marqueeStart;
        _lastMarqueeTick = 0;
        _marqueeBaseSelection[list].Clear();
        _lastMarqueeSelection[list].Clear();
        foreach (var item in list.SelectedItems.OfType<ImageItem>())
        {
            _marqueeBaseSelection[list].Add(item);
        }
        if (!_marqueeAdditive)
        {
            list.SelectedItems.Clear();
        }
        list.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PaneList_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeSelecting || sender is not GridView list) return;
        var now = Environment.TickCount64;
        var current = ClampPointToElement(e.GetCurrentPoint(list).Position, list);
        _marqueeCurrent = current;
        var rect = RectFromPoints(_marqueeStart, current);
        var selectionRect = _selectionRects[list];
        Canvas.SetLeft(selectionRect, rect.X);
        Canvas.SetTop(selectionRect, rect.Y);
        selectionRect.Width = rect.Width;
        selectionRect.Height = rect.Height;
        selectionRect.Visibility = Visibility.Visible;
        if (now - _lastMarqueeTick > 32)
        {
            _lastMarqueeTick = now;
            SelectVisibleItemsInRect(list, rect);
        }
    }

    private void PaneList_PointerReleased(object sender, PointerRoutedEventArgs e)
        => FinishMarquee(sender as GridView, e.Pointer);

    private void PaneList_PointerCanceled(object sender, PointerRoutedEventArgs e)
        => FinishMarquee(sender as GridView, e.Pointer);

    private void FinishMarquee(GridView? list, Pointer pointer)
    {
        if (list is null) return;
        if (_marqueeSelecting)
        {
            SelectVisibleItemsInRect(list, RectFromPoints(_marqueeStart, _marqueeCurrent));
        }
        _marqueeSelecting = false;
        _selectionRects[list].Visibility = Visibility.Collapsed;
        list.ReleasePointerCapture(pointer);
    }

    private void SelectVisibleItemsInRect(GridView list, Rect selection)
    {
        var next = _marqueeAdditive ? new HashSet<ImageItem>(_marqueeBaseSelection[list]) : [];
        foreach (var container in FindVisualChildren<GridViewItem>(list))
        {
            if (container.Content is not ImageItem item) continue;
            var bounds = container.TransformToVisual(list).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (RectIntersects(selection, bounds))
            {
                next.Add(item);
            }
        }
        if (_lastMarqueeSelection[list].SetEquals(next)) return;
        list.SelectedItems.Clear();
        foreach (var item in next)
        {
            list.SelectedItems.Add(item);
        }
        _lastMarqueeSelection[list].Clear();
        foreach (var item in next)
        {
            _lastMarqueeSelection[list].Add(item);
        }

        if (next.Count > 0)
        {
            _lastPreviewItems[list] = next.Last();
        }
        else
        {
            _lastPreviewItems.Remove(list);
        }

        _statuses[list].Text = $"总数：{_items[list].Count} 已选：{list.SelectedItems.Count}";
        UpdatePreview();
    }

    private void PaneList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (_state.IsThresholdFilterEnabled)
        {
            e.Cancel = true;
            ShowThresholdDisabledTip();
            return;
        }

        if (sender is not GridView list) return;
        var names = e.Items.OfType<ImageItem>().Select(x => x.FileName).ToList();
        if (names.Count == 0)
        {
            e.Cancel = true;
            return;
        }
        e.Data.SetText($"{CurrentClassIndex(list)}\n{string.Join('\n', names)}");
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void PaneList_DragOver(object sender, DragEventArgs e)
    {
        if (_state.IsThresholdFilterEnabled)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
    }

    private async void PaneList_Drop(object sender, DragEventArgs e)
    {
        if (_state.IsThresholdFilterEnabled)
        {
            ShowThresholdDisabledTip();
            e.Handled = true;
            return;
        }

        if (sender is not GridView targetList || !e.DataView.Contains(StandardDataFormats.Text)) return;
        var text = await e.DataView.GetTextAsync();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || !int.TryParse(lines[0], out var source)) return;
        var target = CurrentClassIndex(targetList);
        var sourceList = _classes.FirstOrDefault(x => CurrentComboClass(x.Value) == source).Key;
        var order = source >= 0 && source < _state.Store.Items.Length ? _state.Store.Items[source].ToList() : [];
        var record = _state.Store.Move(source, target, lines.Skip(1).ToList());
        if (record is null) return;
        _undoRecords.Add(record);
        RefreshAll();
        if (sourceList is not null)
        {
            SelectNextAfterMove(sourceList, order, record.FileNames);
        }
        _skipNextDataChangedRefresh = true;
        _state.NotifyDataChanged();
        ShowToast($"已将 {record.FileNames.Count} 张图像从 {FaceClasses.ByIndex(source).Name} 移动到 {FaceClasses.ByIndex(target).Name}", true);
    }

    private void SelectNextAfterMove(GridView list, List<string> order, IReadOnlyList<string> moved)
    {
        var movedSet = moved.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maxIndex = order.Select((name, index) => (name, index)).Where(x => movedSet.Contains(x.name)).Select(x => x.index).DefaultIfEmpty(-1).Max();
        var next = order.Skip(maxIndex + 1).FirstOrDefault(name => !movedSet.Contains(name)) ?? order.LastOrDefault(name => !movedSet.Contains(name));
        if (next is null) return;
        var item = _items[list].FirstOrDefault(x => x.FileName.Equals(next, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        list.SelectedItems.Clear();
        list.SelectedItems.Add(item);
        _lastPreviewItems[list] = item;
        list.ScrollIntoView(item);
    }

    private void VerticalSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var total = LeftColumn.ActualWidth + RightColumn.ActualWidth;
        if (total <= 0) return;
        var left = Math.Clamp(LeftColumn.ActualWidth + e.HorizontalChange, LeftColumn.MinWidth, total - RightColumn.MinWidth);
        LeftColumn.Width = new GridLength(left);
        RightColumn.Width = new GridLength(Math.Max(RightColumn.MinWidth, total - left));
    }

    private int CurrentClassIndex(GridView list) => CurrentComboClass(_classes[list]) ?? -1;

    private static int? CurrentComboClass(ComboBox combo) => combo.SelectedItem is FaceClass cls ? cls.Index : null;

    private static void SelectClass(ComboBox combo, int classIndex)
        => combo.SelectedItem = FaceClasses.HatPageClasses.FirstOrDefault(c => c.Index == classIndex);

    private List<string> SelectedNames(GridView list)
        => list.SelectedItems.OfType<ImageItem>().Select(x => x.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool IsCtrlDown()
    {
        var left = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftControl);
        var right = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightControl);
        return left.HasFlag(CoreVirtualKeyStates.Down) || right.HasFlag(CoreVirtualKeyStates.Down);
    }

    private static Rect RectFromPoints(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static Point ClampPointToElement(Point point, FrameworkElement element)
        => new(Math.Clamp(point.X, 0, Math.Max(0, element.ActualWidth)), Math.Clamp(point.Y, 0, Math.Max(0, element.ActualHeight)));

    private static bool RectIntersects(Rect a, Rect b)
        => a.X < b.X + b.Width && a.X + a.Width > b.X && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T wanted) return wanted;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T wanted)
            {
                yield return wanted;
            }
            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }
}
