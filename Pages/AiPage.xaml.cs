using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using DataFlushWinUI.Models;
using DataFlushWinUI.Services;
using DataFlushWinUI.Services.Ai;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Documents;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace DataFlushWinUI.Pages;

public sealed partial class AiPage : Page
{
    private readonly AppState _state = AppState.Current;
    private readonly ObservableCollection<ImageItem> _items = [];
    private readonly ObservableCollection<ModelClassRow> _modelRows = [];
    private readonly ThresholdFilterStore _thresholdStore = new();
    private CancellationTokenSource? _aiCts;
    private FaceQualityOnnxClassifier? _classifier;
    private string _classifierModelPath = "";
    private bool _isRefreshing;
    private bool _skipNextDataChangedRefresh;
    private bool _paused;
    private bool _controlsReady;
    private bool _marqueeSelecting;
    private bool _marqueeAdditive;
    private Point _marqueeStart;
    private long _lastMarqueeTick;
    private readonly HashSet<ImageItem> _marqueeBaseSelection = [];
    private readonly HashSet<ImageItem> _lastMarqueeSelection = [];
    private readonly DispatcherTimer _toastCloseTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private ImageItem? _lastPreviewItem;
    private double _thumbnailSize = 132;
    private double _browseLeftRatio = 2d / 3d;
    private bool _adjustingBrowseColumns;
    private bool _thresholdMode;
    private double _thresholdPercent = 85;
    private int _thresholdDatasetClassIndex = -1;
    private int _thresholdOnnxClassId = -1;
    private bool _thresholdLocked;

    public AiPage()
    {
        InitializeComponent();
        _thresholdPercent = _state.AiProbabilityThresholdPercent;
        ThresholdValueBox.Value = _thresholdPercent;
        AiGrid.ItemsSource = _items;
        ModelClassList.ItemsSource = _modelRows;

        AiClassFilter.Items.Add("全部");
        foreach (var cls in OnnxClasses.All)
        {
            AiClassFilter.Items.Add(cls.DisplayName);
        }

        foreach (var cls in FaceClasses.Classifiable)
        {
            ThresholdDatasetClassBox.Items.Add(cls.DisplayName);
        }

        AiThresholdFilter.Items.Add("未处理");
        AiThresholdFilter.Items.Add("train列表");
        AiThresholdFilter.Items.Add("valid列表");
        AiClassFilter.SelectedIndex = 0;
        AiThresholdFilter.SelectedIndex = 0;
        ThresholdDatasetClassBox.SelectedIndex = 0;
        RestoreThresholdLockState();
        UpdateThresholdModeState();
        BuildMoveMenu();
        _toastCloseTimer.Tick += AiToastCloseTimer_Tick;
        AiGrid.AddHandler(KeyDownEvent, new KeyEventHandler(AiGrid_KeyDown), true);
        AiGrid.PointerWheelChanged += AiGrid_PointerWheelChanged;
        _controlsReady = true;
        Loaded += async (_, _) => await RefreshAiPagesAsync();
        Loaded += (_, _) => SubscribeLocalOnnxService();
        Unloaded += (_, _) => UnsubscribeLocalOnnxService();
        _state.DataChanged += AppState_DataChanged;
        UpdateTopCommandState();
    }

    private async void AppState_DataChanged(object? sender, EventArgs e)
    {
        if (_skipNextDataChangedRefresh)
        {
            _skipNextDataChangedRefresh = false;
            return;
        }

        await RefreshAiPagesAsync();
    }

    private async Task RefreshAiPagesAsync()
    {
        if (!_controlsReady || _isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        SetLoading(true);
        try
        {
            AiProcessedText.Text = "正在加载本地模型缓存...";
            var results = await Task.Run(_state.ReadAiResultsFromDisk);
            _state.AiResults.Clear();
            foreach (var (fileName, payload) in results)
            {
                _state.AiResults[fileName] = payload;
            }

            RestoreThresholdLockState();
            PopulateModelClasses();
            PopulateBrowse();
            AiProcessedText.Text = $"已加载：{_state.AiResults.Count}";
        }
        catch (Exception ex)
        {
            AiErrorLog.Text += $"AI页面刷新失败：{ex.Message}\r\n";
        }
        finally
        {
            SetLoading(false);
            _isRefreshing = false;
            UpdateTopCommandState();
        }
    }

    private async void RefreshClassStatus_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAiPagesAsync();
    }

    private void OpenCsvFolder_Click(object sender, RoutedEventArgs e)
        => MainWindow.OpenInExplorer(_state.CsvPath);

    private void UpdateTopCommandState()
        => OpenCsvFolderButton.IsEnabled = Directory.Exists(_state.CsvPath);

    private void SetLoading(bool loading)
    {
        if (AiLoadingOverlay is not null)
        {
            AiLoadingOverlay.Visibility = loading && IsLoadingPaneSelected() ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private bool IsLoadingPaneSelected()
        => ReferenceEquals(AiSelector?.SelectedItem, ModelSelectorItem) ||
           ReferenceEquals(AiSelector?.SelectedItem, BrowseSelectorItem) ||
           ReferenceEquals(AiSelector?.SelectedItem, ThresholdSelectorItem);

    private void PopulateModelClasses()
    {
        _modelRows.Clear();
        var results = _state.AiResults.Values.ToArray();
        var counts = results
            .Select(x => OnnxClasses.IdFromResult(x.Result))
            .Where(x => x is not null)
            .GroupBy(x => x!.Value)
            .ToDictionary(x => x.Key, x => x.Count());
        var unprocessed = _state.Store.IsLoaded
            ? _state.Store.Items[15].ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var pendingCounts = results
            .Where(x => unprocessed.Contains(x.FileName))
            .Select(x => OnnxClasses.IdFromResult(x.Result))
            .Where(x => x is not null)
            .GroupBy(x => x!.Value)
            .ToDictionary(x => x.Key, x => x.Count());

        foreach (var cls in OnnxClasses.All)
        {
            var count = counts.GetValueOrDefault(cls.Id);
            var pending = pendingCounts.GetValueOrDefault(cls.Id);
            _modelRows.Add(new ModelClassRow(cls.Id, GlyphFor(cls.IconGlyphName), cls.DisplayName, $"{cls.Id} {cls.DetailName} - 共{count}张", $"{pending} 待处理"));
        }
    }

    private void PopulateBrowse()
    {
        if (!_controlsReady)
        {
            return;
        }

        _items.Clear();
        if (!_state.Store.IsLoaded)
        {
            UpdateSelectionStatus();
            return;
        }

        if (_thresholdMode)
        {
            PopulateThresholdBrowse();
            return;
        }

        var unprocessed = _state.Store.Items[15].ToHashSet(StringComparer.OrdinalIgnoreCase);
        int? filter = !_thresholdMode && AiClassFilter.SelectedIndex > 0 ? AiClassFilter.SelectedIndex - 1 : null;
        var results = _state.AiResults.Values.ToArray();
        foreach (var payload in results.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase))
        {
            if (HideClassified.IsChecked == true && !unprocessed.Contains(payload.FileName))
            {
                continue;
            }

            var onnxId = OnnxClasses.IdFromResult(payload.Result);
            if (filter is not null && onnxId != filter)
            {
                continue;
            }

            var item = new ImageItem(payload.FileName, 15);
            item.SetThumbnailSize(_thumbnailSize);
            _items.Add(item);
        }

        if (_lastPreviewItem is not null && !_items.Contains(_lastPreviewItem))
        {
            _lastPreviewItem = null;
            AiPreviewImage.Source = null;
        }

        UpdateSelectionStatus();
    }

    private void PopulateThresholdBrowse()
    {
        if (!_thresholdLocked || _thresholdDatasetClassIndex < 0 || _thresholdOnnxClassId < 0)
        {
            UpdateSelectionStatus();
            return;
        }

        _thresholdStore.Load(_state.CsvPath, _thresholdOnnxClassId, _thresholdDatasetClassIndex);
        var source = ThresholdSourceItems().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in source.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!_state.AiResults.TryGetValue(fileName, out var payload) ||
                (DisableThresholdChoiceCheck.IsChecked != true && !PassesThresholdCase(payload)))
            {
                continue;
            }

            var item = new ImageItem(fileName, 15);
            item.SetThumbnailSize(_thumbnailSize);
            _items.Add(item);
        }

        if (_lastPreviewItem is not null && !_items.Contains(_lastPreviewItem))
        {
            _lastPreviewItem = null;
            AiPreviewImage.Source = null;
        }

        UpdateSelectionStatus();
    }

    private void AiGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var added = e.AddedItems.OfType<ImageItem>().LastOrDefault();
        if (added is not null)
        {
            _lastPreviewItem = added;
        }
        else if (_lastPreviewItem is not null && !AiGrid.SelectedItems.Contains(_lastPreviewItem))
        {
            _lastPreviewItem = null;
        }

        UpdateSelectionStatus();
        UpdateSummaryAndActions();
        UpdatePreview();
    }

    private void AiGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ImageItem item)
        {
            _lastPreviewItem = item;
        }

        UpdatePreview();
    }

    private void AiGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!IsCtrlDown())
        {
            return;
        }

        e.Handled = true;
        var delta = e.GetCurrentPoint(AiGrid).Properties.MouseWheelDelta > 0 ? 10 : -10;
        AiZoom.Value = Math.Clamp(AiZoom.Value + delta, AiZoom.Minimum, AiZoom.Maximum);
    }

    private void AiGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.A || !IsCtrlDown())
        {
            return;
        }

        AiGrid.SelectedItems.Clear();
        foreach (var item in _items)
        {
            AiGrid.SelectedItems.Add(item);
        }

        e.Handled = true;
    }

    private void AiGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsInsideGridViewItem((DependencyObject)e.OriginalSource))
        {
            return;
        }

        var point = e.GetCurrentPoint(AiGrid);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _marqueeSelecting = true;
        _marqueeAdditive = IsCtrlDown();
        _marqueeStart = ClampPointToElement(point.Position, AiGrid);
        _lastMarqueeTick = 0;
        _lastMarqueeSelection.Clear();
        _marqueeBaseSelection.Clear();
        foreach (var item in AiGrid.SelectedItems.OfType<ImageItem>())
        {
            _marqueeBaseSelection.Add(item);
        }

        if (!_marqueeAdditive)
        {
            AiGrid.SelectedItems.Clear();
        }

        AiGrid.CapturePointer(e.Pointer);
        UpdateSelectionRect(_marqueeStart, _marqueeStart);
        e.Handled = true;
    }

    private void AiGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeSelecting)
        {
            return;
        }

        var current = ClampPointToElement(e.GetCurrentPoint(AiGrid).Position, AiGrid);
        UpdateSelectionRect(_marqueeStart, current);

        var now = Environment.TickCount64;
        if (now - _lastMarqueeTick < 16)
        {
            e.Handled = true;
            return;
        }

        _lastMarqueeTick = now;
        SelectVisibleItemsInRect(MakeRect(_marqueeStart, current));
        e.Handled = true;
    }

    private void AiGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndMarquee(e);
    }

    private void AiGrid_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndMarquee(e);
    }

    private void EndMarquee(PointerRoutedEventArgs e)
    {
        if (!_marqueeSelecting)
        {
            return;
        }

        _marqueeSelecting = false;
        AiGrid.ReleasePointerCapture(e.Pointer);
        AiSelectionRect.Visibility = Visibility.Collapsed;
        _marqueeBaseSelection.Clear();
        _lastMarqueeSelection.Clear();
        _lastMarqueeTick = 0;
    }

    private void UpdateSelectionStatus()
    {
        AiSelectionStatus.Text = $"总数：{_items.Count}，已选：{AiGrid.SelectedItems.Count}";
    }

    private void UpdateSelectionRect(Point start, Point current)
    {
        var rect = MakeRect(start, current);
        Canvas.SetLeft(AiSelectionRect, rect.X);
        Canvas.SetTop(AiSelectionRect, rect.Y);
        AiSelectionRect.Width = rect.Width;
        AiSelectionRect.Height = rect.Height;
        AiSelectionRect.Visibility = Visibility.Visible;
    }

    private void SelectVisibleItemsInRect(Rect selection)
    {
        var next = _marqueeAdditive ? new HashSet<ImageItem>(_marqueeBaseSelection) : [];
        foreach (var container in FindVisualChildren<GridViewItem>(AiGrid))
        {
            if (container.Content is not ImageItem item)
            {
                continue;
            }

            var transform = container.TransformToVisual(AiGrid);
            var bounds = transform.TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (RectIntersects(selection, bounds))
            {
                next.Add(item);
            }
        }

        if (_lastMarqueeSelection.SetEquals(next))
        {
            return;
        }

        _lastMarqueeSelection.Clear();
        foreach (var item in next)
        {
            _lastMarqueeSelection.Add(item);
        }

        AiGrid.SelectedItems.Clear();
        foreach (var item in next)
        {
            AiGrid.SelectedItems.Add(item);
        }
    }

    private static Rect MakeRect(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static Point ClampPointToElement(Point point, FrameworkElement element)
        => new(
            Math.Clamp(point.X, 0, Math.Max(0, element.ActualWidth)),
            Math.Clamp(point.Y, 0, Math.Max(0, element.ActualHeight)));

    private static bool RectIntersects(Rect a, Rect b)
        => a.X < b.X + b.Width && a.X + a.Width > b.X && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;

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

    private static bool IsCtrlDown()
    {
        var left = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftControl);
        var right = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.RightControl);
        return left.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) || right.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private bool IsInsideGridViewItem(DependencyObject source)
    {
        var current = source;
        while (current is not null && current != AiGrid)
        {
            if (current is GridViewItem)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void UpdateSummaryAndActions()
    {
        if (_thresholdMode)
        {
            UpdateThresholdSummaryAndActions();
            return;
        }

        var selected = AiGrid.SelectedItems.OfType<ImageItem>().ToList();
        var canQuickMove = selected.Count > 0 && HideClassified.IsChecked == true && !IsAiBrowseReadOnly();
        MoveAiDropDown.IsEnabled = canQuickMove;
        MappedMoveButtons.Children.Clear();

        if (selected.Count == 0)
        {
            AiSummaryTitle.Text = "选择图片";
            AiSummarySub.Text = "";
            AiSummarySub.Foreground = null;
            return;
        }

        var majority = selected
            .Select(x => _state.AiResults.TryGetValue(x.FileName, out var payload) ? OnnxClasses.IdFromResult(payload.Result) : null)
            .Where(x => x is not null)
            .GroupBy(x => x!.Value)
            .OrderByDescending(x => x.Count())
            .FirstOrDefault();

        if (majority is null)
        {
            AiSummaryTitle.Text = "未知分类";
            AiSummarySub.Text = selected.Count == 1 ? "无模型结果" : $"0/{selected.Count}";
            return;
        }

        var onnxClass = OnnxClasses.ById(majority.Key);
        AiSummaryTitle.Text = onnxClass.DisplayName;

        if (selected.Count == 1 && selected[0].AiPayload?.Result is { } result)
        {
            AiSummarySub.Text = result.Probability is { } probability
                ? $"{result.Certainty} - {probability * 100:0.0}%"
                : result.Certainty;
            AiSummarySub.Foreground = new SolidColorBrush(CertaintyColor(result.Certainty));
        }
        else
        {
            AiSummarySub.Text = $"（{majority.Count()}/{selected.Count}）";
            AiSummarySub.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }

        if (_state.OnnxMapping.TryGetValue(onnxClass.Id, out var targets))
        {
            AddMappedMoveButtons(targets, canQuickMove);
        }
    }

    private void AddMappedMoveButtons(IReadOnlyList<int> targets, bool isEnabled)
    {
        StackPanel? row = null;
        for (var i = 0; i < targets.Count; i++)
        {
            if (i % 3 == 0)
            {
                row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                MappedMoveButtons.Children.Add(row);
            }

            var cls = FaceClasses.ByIndex(targets[i]);
            var button = new Button { Content = $"移动到 {cls.Code} {cls.Name}", MinHeight = 32, IsEnabled = isEnabled };
            button.Click += (_, _) => MoveAiItems(cls.Index);
            row!.Children.Add(button);
        }
    }

    private void UpdateThresholdSummaryAndActions()
    {
        var selected = AiGrid.SelectedItems.OfType<ImageItem>().ToList();
        MappedMoveButtons.Children.Clear();
        MoveAiDropDown.Visibility = Visibility.Collapsed;
        ThresholdLockButton.Visibility = Visibility.Visible;
        UpdateThresholdActionHint();

        var currentList = CurrentThresholdListKind();
        var canMoveToTrainOrValid = _thresholdLocked && selected.Count > 0 && currentList == ThresholdListKind.Unprocessed;
        var canMoveToUnprocessed = _thresholdLocked && selected.Count > 0 && currentList is ThresholdListKind.Train or ThresholdListKind.Valid;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        MappedMoveButtons.Children.Add(row);
        row.Children.Add(ThresholdMoveButton("移动到 train", ThresholdListKind.Train, canMoveToTrainOrValid));
        row.Children.Add(ThresholdMoveButton("移动到 valid", ThresholdListKind.Valid, canMoveToTrainOrValid));
        row.Children.Add(ThresholdMoveButton("移动到未处理", ThresholdListKind.Unprocessed, canMoveToUnprocessed));

        if (selected.Count == 0)
        {
            AiSummaryTitle.Text = _thresholdLocked ? "选择图片" : "未锁定类别";
            AiSummarySub.Text = "";
            AiSummarySub.Foreground = null;
            return;
        }

        var majority = selected
            .Select(x => _state.AiResults.TryGetValue(x.FileName, out var payload) ? OnnxClasses.IdFromResult(payload.Result) : null)
            .Where(x => x is not null)
            .GroupBy(x => x!.Value)
            .OrderByDescending(x => x.Count())
            .FirstOrDefault();

        if (majority is null)
        {
            AiSummaryTitle.Text = "未知分类";
            AiSummarySub.Text = selected.Count == 1 ? "无模型结果" : $"0/{selected.Count}";
            return;
        }

        var onnxClass = OnnxClasses.ById(majority.Key);
        AiSummaryTitle.Text = onnxClass.DisplayName;
        if (selected.Count == 1 && selected[0].AiPayload?.Result is { } result)
        {
            AiSummarySub.Text = result.Probability is { } probability
                ? $"{result.Certainty} - {probability * 100:0.0}%"
                : result.Certainty;
            AiSummarySub.Foreground = new SolidColorBrush(CertaintyColor(result.Certainty));
        }
        else
        {
            AiSummarySub.Text = $"（{majority.Count()}/{selected.Count}）";
            AiSummarySub.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }
    }

    private void UpdateThresholdActionHint()
    {
        if (!_thresholdLocked)
        {
            AiActionHint.Text = "选择并锁定数据集类别来处理数据。";
            return;
        }

        if (DisableThresholdChoiceCheck.IsChecked == true)
        {
            AiActionHint.Text = $"阈值与预测选择已禁用。当前显示所有展示项目。未处理总数：{ThresholdRemainingCount()}";
            return;
        }

        var confidenceText = ThresholdHighRadio.IsChecked == true ? "高/等于阈值" : "低于阈值";
        var correctnessText = ThresholdCorrectRadio.IsChecked == true ? "预测正确" : "预测错误";
        var suggested = ThresholdHighRadio.IsChecked == true && ThresholdCorrectRadio.IsChecked == true ? "valid" : "train";
        AiActionHint.Text = $"当前：{confidenceText} & {correctnessText}；建议移动到 {suggested}。未处理总数：{ThresholdRemainingCount()}";
    }

    private int ThresholdRemainingCount()
        => ThresholdUnprocessedItems().Count(name =>
            _state.AiResults.TryGetValue(name, out var payload) &&
            payload.Result.Probability is not null &&
            OnnxClasses.IdFromResult(payload.Result) is not null);

    private Button ThresholdMoveButton(string text, ThresholdListKind target, bool enabled)
    {
        var button = new Button { Content = text, MinHeight = 32, IsEnabled = enabled };
        button.Click += (_, _) => MoveThresholdItems(target);
        return button;
    }

    private static Windows.UI.Color CertaintyColor(string certainty) => certainty switch
    {
        "高" => Windows.UI.Color.FromArgb(255, 46, 160, 67),
        "中" => Windows.UI.Color.FromArgb(255, 201, 120, 32),
        "低" => Windows.UI.Color.FromArgb(255, 218, 54, 51),
        _ => Windows.UI.Color.FromArgb(255, 140, 140, 140)
    };

    private void UpdatePreview()
    {
        var item = _lastPreviewItem is not null && AiGrid.SelectedItems.Contains(_lastPreviewItem)
            ? _lastPreviewItem
            : AiGrid.SelectedItems.OfType<ImageItem>().LastOrDefault() ?? AiGrid.SelectedItem as ImageItem;
        AiPreviewImage.Source = item?.ImageUri is null ? null : new BitmapImage(item.ImageUri);
    }

    private void AiGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_thresholdMode)
        {
            ShowThresholdContextMenu(e);
            return;
        }

        var selected = AiGrid.SelectedItems.OfType<ImageItem>().Select(x => x.FileName).ToList();
        if (selected.Count == 0) return;
        var canMove = HideClassified.IsChecked == true && !IsAiBrowseReadOnly();

        var flyout = new MenuFlyout();
        var majority = selected
            .Select(x => _state.AiResults.TryGetValue(x, out var payload) ? OnnxClasses.IdFromResult(payload.Result) : null)
            .Where(x => x is not null)
            .GroupBy(x => x!.Value)
            .OrderByDescending(x => x.Count())
            .FirstOrDefault();

        if (majority is not null && _state.OnnxMapping.TryGetValue(majority.Key, out var targets))
        {
            foreach (var target in targets)
            {
                var cls = FaceClasses.ByIndex(target);
                var item = MenuItem($"移动到 {cls.Code} {cls.Name}", (_, _) => MoveAiItems(target));
                item.IsEnabled = canMove;
                flyout.Items.Add(item);
            }
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        var sub = new MenuFlyoutSubItem { Text = "移动到..." };
        foreach (var cls in FaceClasses.Classifiable)
        {
            var item = MenuItem(cls.DisplayName, (_, _) => MoveAiItems(cls.Index));
            item.IsEnabled = canMove;
            sub.Items.Add(item);
        }
        sub.IsEnabled = canMove;

        flyout.Items.Add(sub);
        flyout.ShowAt(AiGrid, e.GetPosition(AiGrid));
    }

    private void ShowThresholdContextMenu(RightTappedRoutedEventArgs e)
    {
        var selected = AiGrid.SelectedItems.OfType<ImageItem>().Select(x => x.FileName).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var currentList = CurrentThresholdListKind();
        var canMoveToTrainOrValid = _thresholdLocked && currentList == ThresholdListKind.Unprocessed;
        var canMoveToUnprocessed = _thresholdLocked && currentList is ThresholdListKind.Train or ThresholdListKind.Valid;
        var flyout = new MenuFlyout();
        var train = MenuItem("移动到 train", (_, _) => MoveThresholdItems(ThresholdListKind.Train));
        train.IsEnabled = canMoveToTrainOrValid;
        flyout.Items.Add(train);
        var valid = MenuItem("移动到 valid", (_, _) => MoveThresholdItems(ThresholdListKind.Valid));
        valid.IsEnabled = canMoveToTrainOrValid;
        flyout.Items.Add(valid);
        var unprocessed = MenuItem("移动到 未处理", (_, _) => MoveThresholdItems(ThresholdListKind.Unprocessed));
        unprocessed.IsEnabled = canMoveToUnprocessed;
        flyout.Items.Add(unprocessed);
        flyout.ShowAt(AiGrid, e.GetPosition(AiGrid));
    }

    private MenuFlyoutItem MenuItem(string text, RoutedEventHandler handler)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += handler;
        return item;
    }

    private void MoveAiItems(int target)
    {
        if (_thresholdMode)
        {
            return;
        }

        if (IsAiBrowseReadOnly())
        {
            ShowToast("当前已启用阈值过滤，请先到 AI辅助 > 阈值过滤 解锁后再使用 AI 图片浏览移动功能。");
            return;
        }

        if (!_state.Store.IsLoaded) return;
        var selected = AiGrid.SelectedItems.OfType<ImageItem>().Select(x => x.FileName).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var currentOrder = _items.Select(x => x.FileName).ToList();
        var sourceMap = BuildSourceClassMap(selected);
        var records = new List<MoveRecord>();
        foreach (var group in selected.GroupBy(name => sourceMap.GetValueOrDefault(name, 15)))
        {
            if (group.Key == target) continue;
            var record = _state.Store.Move(group.Key, target, group.ToList());
            if (record is not null) records.Add(record);
        }

        if (records.Count > 0)
        {
            _state.UndoRecord = records.LastOrDefault();
            ApplyMoveLocally(selected, currentOrder);
            ShowToast($"已移动 {selected.Count} 张图像到 {FaceClasses.ByIndex(target).Name}");
            DispatcherQueue.TryEnqueue(() =>
            {
                _skipNextDataChangedRefresh = true;
                _state.NotifyDataChanged();
            });
        }
    }

    private void MoveThresholdItems(ThresholdListKind target)
    {
        if (!_state.Store.IsLoaded || !_thresholdLocked || _thresholdDatasetClassIndex < 0 || _thresholdOnnxClassId < 0)
        {
            return;
        }

        var selected = AiGrid.SelectedItems.OfType<ImageItem>().Select(x => x.FileName).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var currentList = CurrentThresholdListKind();
        if (currentList == ThresholdListKind.Unprocessed && target == ThresholdListKind.Unprocessed)
        {
            return;
        }

        if (currentList != ThresholdListKind.Unprocessed && target != ThresholdListKind.Unprocessed)
        {
            return;
        }

        var previousOrder = _items.Select(x => x.FileName).ToList();
        var allowed = ThresholdSourceItems().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!_thresholdStore.Move(selected, target, allowed))
        {
            return;
        }

        if (target == ThresholdListKind.Unprocessed)
        {
            _state.Store.AddToUnprocessed(selected);
        }
        else
        {
            _state.Store.RemoveFromUnprocessed(selected);
        }

        _thresholdStore.Save(_state.CsvPath, _thresholdOnnxClassId, _thresholdDatasetClassIndex);
        ApplyThresholdMoveLocally(selected, previousOrder);
        ShowToast($"已移动 {selected.Count} 张图像到 {ThresholdListName(target)}");
    }

    private Dictionary<string, int> BuildSourceClassMap(IReadOnlyList<string> fileNames)
    {
        var wanted = fileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var classIndex = 0; classIndex < FaceClasses.All.Count && wanted.Count > 0; classIndex++)
        {
            foreach (var fileName in _state.Store.Items[classIndex])
            {
                if (!wanted.Remove(fileName))
                {
                    continue;
                }

                result[fileName] = classIndex;
                if (wanted.Count == 0)
                {
                    break;
                }
            }
        }

        return result;
    }

    private void ApplyMoveLocally(IReadOnlyList<string> movedNames, IReadOnlyList<string> previousOrder)
    {
        if (HideClassified.IsChecked == true)
        {
            var moved = movedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = _items.Count - 1; i >= 0; i--)
            {
                if (moved.Contains(_items[i].FileName))
                {
                    _items.RemoveAt(i);
                }
            }

            if (_lastPreviewItem is not null && moved.Contains(_lastPreviewItem.FileName))
            {
                _lastPreviewItem = null;
                AiPreviewImage.Source = null;
            }

            SelectNextAfterMove(previousOrder, movedNames);
        }

        UpdateSelectionStatus();
        UpdateSummaryAndActions();
        UpdatePreview();
    }

    private void ApplyThresholdMoveLocally(IReadOnlyList<string> movedNames, IReadOnlyList<string> previousOrder)
    {
        var moved = movedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (moved.Contains(_items[i].FileName))
            {
                _items.RemoveAt(i);
            }
        }

        if (_lastPreviewItem is not null && moved.Contains(_lastPreviewItem.FileName))
        {
            _lastPreviewItem = null;
            AiPreviewImage.Source = null;
        }

        SelectNextAfterMove(previousOrder, movedNames);
        UpdateSelectionStatus();
        UpdateSummaryAndActions();
        UpdatePreview();
    }

    private void SelectNextAfterMove(IReadOnlyList<string> previousOrder, IReadOnlyList<string> movedNames)
    {
        var moved = movedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maxIndex = previousOrder
            .Select((name, index) => (name, index))
            .Where(x => moved.Contains(x.name))
            .Select(x => x.index)
            .DefaultIfEmpty(-1)
            .Max();
        var next = previousOrder.Skip(maxIndex + 1).FirstOrDefault(name => !moved.Contains(name))
            ?? previousOrder.LastOrDefault(name => !moved.Contains(name));
        if (next is null)
        {
            return;
        }

        var item = _items.FirstOrDefault(x => x.FileName.Equals(next, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        AiGrid.SelectedItems.Clear();
        AiGrid.SelectedItems.Add(item);
        _lastPreviewItem = item;
        AiGrid.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(async () =>
        {
            await BringAiItemIntoViewAsync(item);
        });
    }

    private async Task BringAiItemIntoViewAsync(ImageItem item)
    {
        if (!_items.Contains(item))
        {
            return;
        }

        AiGrid.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
        await Task.Delay(50);
        AiGrid.UpdateLayout();

        if (AiGrid.ContainerFromItem(item) is FrameworkElement container)
        {
            container.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = false,
                VerticalAlignmentRatio = 0.15
            });
        }

        await Task.Delay(50);
        AiGrid.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
    }

    private int SourceClassOf(string filename)
    {
        for (var i = 0; i < FaceClasses.All.Count; i++)
        {
            if (_state.Store.Items[i].Contains(filename, StringComparer.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 15;
    }

    private IEnumerable<string> ThresholdSourceItems()
    {
        if (_thresholdDatasetClassIndex < 0 || _state.Store.Items.Length <= 15)
        {
            return [];
        }

        return CurrentThresholdListKind() switch
        {
            ThresholdListKind.Train => _thresholdStore.Train,
            ThresholdListKind.Valid => _thresholdStore.Valid,
            _ => ThresholdUnprocessedItems()
        };
    }

    private IEnumerable<string> ThresholdUnprocessedItems()
    {
        var assigned = _thresholdStore.Train
            .Concat(_thresholdStore.Valid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _state.Store.Items[15].Where(name => !assigned.Contains(name));
    }

    private bool PassesThresholdCase(AiPayload payload)
    {
        var onnxId = OnnxClasses.IdFromResult(payload.Result);
        if (onnxId is null || payload.Result.Probability is null)
        {
            return false;
        }

        var isHigh = payload.Result.Probability.Value * 100 >= _thresholdPercent;
        var isCorrect = onnxId.Value == _thresholdOnnxClassId;
        var wantsHigh = ThresholdHighRadio.IsChecked == true;
        var wantsCorrect = ThresholdCorrectRadio.IsChecked == true;
        return isHigh == wantsHigh && isCorrect == wantsCorrect;
    }

    private ThresholdListKind CurrentThresholdListKind()
        => AiThresholdFilter.SelectedIndex switch
        {
            1 => ThresholdListKind.Train,
            2 => ThresholdListKind.Valid,
            _ => ThresholdListKind.Unprocessed
        };

    private static string ThresholdListName(ThresholdListKind kind)
        => kind switch
        {
            ThresholdListKind.Train => "train列表",
            ThresholdListKind.Valid => "valid列表",
            _ => "未处理"
        };

    private void ModelClassList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ModelClassRow row) return;
        AiSelector.SelectedItem = BrowseSelectorItem;
        SetBrowseMode(false);
        ShowPivotPane(BrowsePane);
        AiClassFilter.SelectedIndex = row.OnnxId + 1;
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        var service = LocalOnnxBatchInferenceService.Current;
        if (service.IsRunning)
        {
            var paused = service.TogglePause();
            PauseButton.Content = paused ? "继续" : "暂停";
            return;
        }

        if (!service.CanStart(_state))
        {
            AiErrorLog.Text += "本地模型运行检查未通过。请检查数据集、CSV目录是否正确，设置页本地ONNX模型是否设置\r\n";
            return;
        }

        var started = await service.StartAsync(_state);
        if (started)
        {
            PauseButton.Content = "暂停";
            ShowToast("本地 ONNX 后台推理已启动。");
        }
    }

    private void SubscribeLocalOnnxService()
    {
        var service = LocalOnnxBatchInferenceService.Current;
        service.Message -= LocalOnnxService_Message;
        service.ProgressChanged -= LocalOnnxService_ProgressChanged;
        service.PauseChanged -= LocalOnnxService_PauseChanged;
        service.Message += LocalOnnxService_Message;
        service.ProgressChanged += LocalOnnxService_ProgressChanged;
        service.PauseChanged += LocalOnnxService_PauseChanged;
        PauseButton.Content = service.IsRunning ? service.IsPaused ? "继续" : "暂停" : "启动本地推理";
    }

    private void UnsubscribeLocalOnnxService()
    {
        var service = LocalOnnxBatchInferenceService.Current;
        service.Message -= LocalOnnxService_Message;
        service.ProgressChanged -= LocalOnnxService_ProgressChanged;
        service.PauseChanged -= LocalOnnxService_PauseChanged;
    }

    private void LocalOnnxService_Message(object? sender, string message)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            AiErrorLog.Text += $"{message}\r\n";
            if (message.Contains("完成", StringComparison.Ordinal))
            {
                PauseButton.Content = "启动本地推理";
                ShowToast(message);
                await RefreshAiPagesAsync();
            }
            else if (message.Contains("失败", StringComparison.Ordinal) || message.Contains("取消", StringComparison.Ordinal))
            {
                PauseButton.Content = "启动本地推理";
            }
        });
    }

    private void LocalOnnxService_ProgressChanged(object? sender, LocalOnnxProgress progress)
    {
        DispatcherQueue.TryEnqueue(() => UpdateTaskProgress(progress.Done, progress.Total));
    }

    private void LocalOnnxService_PauseChanged(object? sender, bool paused)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            PauseButton.Content = LocalOnnxBatchInferenceService.Current.IsRunning
                ? paused ? "继续" : "暂停"
                : "启动本地推理";
        });
    }

    private async Task StartLocalOnnxTaskAsync()
    {
        if (!_state.Store.IsLoaded)
        {
            AiErrorLog.Text += "请先加载数据集和 CSV。\r\n";
            return;
        }

        if (string.IsNullOrWhiteSpace(_state.LocalModelPath) || !File.Exists(_state.LocalModelPath))
        {
            AiErrorLog.Text += "请先在设置页选择有效的本地 ONNX 模型。\r\n";
            return;
        }

        _aiCts = new CancellationTokenSource();
        _paused = false;
        PauseButton.Content = "暂停";
        AiErrorLog.Text += "开始本地 ONNX 批量推理。\r\n";

        try
        {
            var classifier = GetClassifier();
            AiErrorLog.Text += $"模型已加载：输入 {classifier.InputName} [{string.Join(", ", classifier.InputShape)}]，输出 {classifier.OutputName} [{string.Join(", ", classifier.OutputShape)}]\r\n";

            var all = BuildInferenceQueue();
            var total = all.Count;
            var done = all.Count(x => File.Exists(DifyService.AiJsonPath(_state.CsvPath, x)));
            UpdateTaskProgress(done, total);

            foreach (var filename in all)
            {
                var token = _aiCts.Token;
                token.ThrowIfCancellationRequested();
                while (_paused)
                {
                    await Task.Delay(250, token);
                }

                var outputPath = DifyService.AiJsonPath(_state.CsvPath, filename);
                if (File.Exists(outputPath))
                {
                    continue;
                }

                try
                {
                    var imagePath = Path.Combine(_state.DatasetPath, filename);
                    var result = await classifier.PredictAsync(imagePath, token);
                    var payload = new AiPayload
                    {
                        FileName = filename,
                        Result = result.ToAiResult(),
                        Raw = JsonSerializer.Deserialize<object>(result.ToJson())
                    };

                    await WriteAiPayloadAsync(outputPath, payload, token);
                    _state.AiResults[filename] = payload;
                }
                catch (Exception ex)
                {
                    AiErrorLog.Text += $"{filename}: {ex.Message}\r\n";
                }

                done++;
                UpdateTaskProgress(done, total);
                if (done % 25 == 0)
                {
                    PopulateModelClasses();
                }
            }

            AiErrorLog.Text += "本地 ONNX 批量推理完成。\r\n";
            ShowToast("本地 ONNX 批量推理完成。");
        }
        catch (OperationCanceledException)
        {
            AiErrorLog.Text += "本地 ONNX 批量推理已取消。\r\n";
        }
        catch (Exception ex)
        {
            AiErrorLog.Text += $"本地 ONNX 批量推理失败：{ex.Message}\r\n";
        }
        finally
        {
            _aiCts?.Dispose();
            _aiCts = null;
            _paused = false;
            PauseButton.Content = "启动本地推理";
            await RefreshAiPagesAsync();
        }
    }

    private FaceQualityOnnxClassifier GetClassifier()
    {
        if (_classifier is not null && string.Equals(_classifierModelPath, _state.LocalModelPath, StringComparison.OrdinalIgnoreCase))
        {
            return _classifier;
        }

        DisposeClassifier();
        _classifier = new FaceQualityOnnxClassifier(_state.LocalModelPath);
        _classifierModelPath = _state.LocalModelPath;
        return _classifier;
    }

    private List<string> BuildInferenceQueue()
    {
        var queued = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (_state.AiPriorityQueue.Count > 0)
        {
            var filename = _state.AiPriorityQueue.Dequeue();
            if (seen.Add(filename))
            {
                queued.Add(filename);
            }
        }

        foreach (var filename in _state.Store.Items.SelectMany(x => x).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(filename))
            {
                queued.Add(filename);
            }
        }

        return queued;
    }

    private static async Task WriteAiPayloadAsync(string outputPath, AiPayload payload, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temp = $"{outputPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(payload, AppState.JsonOptions), Encoding.UTF8, cancellationToken);
        File.Move(temp, outputPath, overwrite: true);
    }

    private void UpdateTaskProgress(int done, int total)
    {
        var pct = total == 0 ? 0 : done * 100.0 / total;
        AiProgress.Value = pct;
        AiProgressText.Text = $"进度 {pct:0.0}%";
        AiProcessedText.Text = $"已处理：{done}";
    }

    private void DisposeClassifier()
    {
        _classifier?.Dispose();
        _classifier = null;
        _classifierModelPath = "";
    }

    private void CopyErrors_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(AiErrorLog.Text);
        Clipboard.SetContent(package);
    }

    private void BuildMoveMenu()
    {
        var flyout = new MenuFlyout();
        foreach (var cls in FaceClasses.Classifiable)
        {
            flyout.Items.Add(MenuItem(cls.DisplayName, (_, _) => MoveAiItems(cls.Index)));
        }

        MoveAiDropDown.Flyout = flyout;
    }

    private void RestoreThresholdLockState()
    {
        _thresholdLocked = false;
        _thresholdDatasetClassIndex = -1;
        _thresholdOnnxClassId = -1;

        if (!string.IsNullOrWhiteSpace(_state.CsvPath) &&
            ThresholdFilterStore.TryGetLockedState(_state.CsvPath, out var lockedOnnxClassId, out var lockedFaceClassIndex))
        {
            if (lockedFaceClassIndex < 0 && TryGetSavedThresholdFaceClass(lockedOnnxClassId, out var savedFaceClassIndex))
            {
                ThresholdFilterStore.MigrateOldFiles(_state.CsvPath, lockedOnnxClassId, savedFaceClassIndex);
                lockedFaceClassIndex = savedFaceClassIndex;
            }

            if (lockedFaceClassIndex is >= 0 and <= 14 &&
                MapFaceClassToOnnxClass(lockedFaceClassIndex) == lockedOnnxClassId)
            {
                _thresholdDatasetClassIndex = lockedFaceClassIndex;
                _thresholdOnnxClassId = lockedOnnxClassId;
                ThresholdDatasetClassBox.SelectedIndex = lockedFaceClassIndex;
                _thresholdLocked = _thresholdStore.IsLocked(_state.CsvPath, lockedOnnxClassId, lockedFaceClassIndex);
                if (_thresholdLocked)
                {
                    _thresholdStore.Load(_state.CsvPath, lockedOnnxClassId, lockedFaceClassIndex);
                    _state.WriteLocalSetting("threshold_dataset_class_index", lockedFaceClassIndex.ToString());
                }

                return;
            }
        }

        if (int.TryParse(_state.ReadLocalSetting("threshold_dataset_class_index", "-1"), out var saved) &&
            saved is >= 0 and <= 14)
        {
            _thresholdDatasetClassIndex = saved;
            _thresholdOnnxClassId = MapFaceClassToOnnxClass(saved);
            ThresholdDatasetClassBox.SelectedIndex = saved;
        }
    }

    private bool TryGetSavedThresholdFaceClass(int onnxClassId, out int faceClassIndex)
    {
        faceClassIndex = -1;
        if (!int.TryParse(_state.ReadLocalSetting("threshold_dataset_class_index", "-1"), out var saved) ||
            saved is < 0 or > 14 ||
            MapFaceClassToOnnxClass(saved) != onnxClassId)
        {
            return false;
        }

        faceClassIndex = saved;
        return true;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_controlsReady) return;
        PopulateBrowse();
        UpdateSummaryAndActions();
    }

    private void ThresholdState_Changed(object sender, RoutedEventArgs e)
    {
        if (!_controlsReady)
        {
            return;
        }

        if (!_thresholdLocked)
        {
            _thresholdDatasetClassIndex = ThresholdDatasetClassBox.SelectedIndex;
            _thresholdOnnxClassId = _thresholdDatasetClassIndex >= 0 ? MapFaceClassToOnnxClass(_thresholdDatasetClassIndex) : -1;
        }

        if (_thresholdMode)
        {
            UpdateThresholdModeState();
            PopulateBrowse();
            UpdateSummaryAndActions();
        }
    }

    private void ThresholdMappingHelpButton_Click(object sender, RoutedEventArgs e)
    {
        BuildThresholdMappingTip();
        ThresholdMappingTeachingTip.IsOpen = true;
    }

    private void BuildThresholdMappingTip()
    {
        ThresholdMappingText.Blocks.Clear();
        for (var i = 0; i < 15; i++)
        {
            var face = FaceClasses.ByIndex(i);
            var onnx = OnnxClasses.ById(MapFaceClassToOnnxClass(i));
            var run = new Run { Text = $"{face.Code} {face.Name} → {onnx.Id} {onnx.DisplayName}" };
            var paragraph = new Paragraph { Margin = new Thickness(0, i == 0 ? 0 : 2, 0, 0) };
            if (i == ThresholdDatasetClassBox.SelectedIndex)
            {
                run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            }

            paragraph.Inlines.Add(run);
            ThresholdMappingText.Blocks.Add(paragraph);
        }
    }

    private void ThresholdValueBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_controlsReady)
        {
            return;
        }

        var next = double.IsNaN(args.NewValue) ? _thresholdPercent : Math.Clamp(args.NewValue, 0, 100);
        if (Math.Abs(next - _thresholdPercent) < 0.001)
        {
            return;
        }

        _thresholdPercent = next;
        _state.SetAiProbabilityThresholdPercent(next);
        if (!double.IsNaN(args.NewValue) && Math.Abs(args.NewValue - next) > 0.001)
        {
            sender.Value = next;
        }

        if (_thresholdMode)
        {
            PopulateBrowse();
            UpdateSummaryAndActions();
        }
    }

    private async void ThresholdLockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_thresholdLocked)
        {
            var confirmed = await ConfirmThresholdLockChangeAsync();
            if (!confirmed)
            {
                return;
            }

            _thresholdStore.Load(_state.CsvPath, _thresholdOnnxClassId, _thresholdDatasetClassIndex);
            _state.Store.AddToUnprocessed(_thresholdStore.Train.Concat(_thresholdStore.Valid).ToList());
            _thresholdStore.Unlock(_state.CsvPath, _thresholdOnnxClassId, _thresholdDatasetClassIndex);
            _state.Store.RecreateClassifiableCsvFiles();
            _thresholdLocked = false;
            _state.WriteLocalSetting("threshold_dataset_class_index", "-1");
            _thresholdDatasetClassIndex = ThresholdDatasetClassBox.SelectedIndex;
            _thresholdOnnxClassId = _thresholdDatasetClassIndex >= 0 ? MapFaceClassToOnnxClass(_thresholdDatasetClassIndex) : -1;
            ShowToast("已解锁数据集类别，阈值过滤列表已重置。");
        }
        else
        {
            _thresholdDatasetClassIndex = ThresholdDatasetClassBox.SelectedIndex;
            if (_thresholdDatasetClassIndex is < 0 or > 14 || string.IsNullOrWhiteSpace(_state.CsvPath))
            {
                return;
            }

            if (_state.Store.IsHatExtensionEnabled)
            {
                await ShowHatEnabledForThresholdDialogAsync();
                return;
            }

            if (!AreCoreClassListsEmpty())
            {
                await ShowCoreClassListsNotEmptyDialogAsync();
                return;
            }

            if (!IsLocalOnnxInferenceComplete(out var done, out var total))
            {
                await ShowLocalOnnxIncompleteDialogAsync(done, total);
                return;
            }

            var confirmed = await ConfirmThresholdLockChangeAsync();
            if (!confirmed)
            {
                return;
            }

            _thresholdOnnxClassId = MapFaceClassToOnnxClass(_thresholdDatasetClassIndex);
            _thresholdStore.Lock(_state.CsvPath, _thresholdOnnxClassId, _thresholdDatasetClassIndex);
            _state.Store.DeleteClassifiableCsvFiles();
            _thresholdLocked = true;
            _state.WriteLocalSetting("threshold_dataset_class_index", _thresholdDatasetClassIndex.ToString());
            ShowToast($"已锁定数据集类别：{FaceClasses.ByIndex(_thresholdDatasetClassIndex).DisplayName}");
        }

        UpdateThresholdModeState();
        PopulateBrowse();
        UpdateSummaryAndActions();
        _state.NotifyDataChanged();
    }

    private async Task<bool> ConfirmThresholdLockChangeAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "操作确认",
            Content = "锁定类别后会新建train、valid的CSV文件，并删除00-14分类CSV文件，锁定过程中无法修改类别。\n解锁类别会删除新建的CSV文件，并重新创建空白的00-14分类CSV文件。",
            PrimaryButtonText = _thresholdLocked ? "解锁" : "锁定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private bool AreCoreClassListsEmpty()
        => _state.Store.IsLoaded && Enumerable.Range(0, 15).All(index => _state.Store.Items[index].Count == 0);

    private bool IsLocalOnnxInferenceComplete(out int done, out int total)
    {
        var required = _state.Store.IsLoaded
            ? _state.Store.Items[15].Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        total = required.Length;
        done = 0;

        if (LocalOnnxBatchInferenceService.Current.IsRunning)
        {
            return false;
        }

        var results = _state.ReadAiResultsFromDisk();
        _state.AiResults.Clear();
        foreach (var (fileName, payload) in results)
        {
            _state.AiResults[fileName] = payload;
        }

        done = required.Count(fileName =>
            results.TryGetValue(fileName, out var payload) &&
            payload.Result.Probability is not null &&
            OnnxClasses.IdFromResult(payload.Result) is not null);
        return done == total;
    }

    private async Task ShowCoreClassListsNotEmptyDialogAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "无法锁定数据集类别",
            Content = "阈值过滤会把 15_unprocessed.csv 作为未处理列表使用。\n锁定前需要 00-14 这 15 个分类 CSV 为空。",
            CloseButtonText = "确认",
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private async Task ShowHatEnabledForThresholdDialogAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "无法锁定数据集类别",
            Content = "锁定阈值过滤前需要先停用帽子分类。",
            CloseButtonText = "确认",
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private async Task ShowLocalOnnxIncompleteDialogAsync(int done, int total)
    {
        var message = LocalOnnxBatchInferenceService.Current.IsRunning
            ? "本地 ONNX 推理任务仍在运行，请等待任务完成后再锁定阈值过滤。"
            : $"本地 ONNX 推理结果尚未完整。\n已完成：{done}/{total}\n请先完成本地推理任务后再锁定阈值过滤。";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "无法锁定数据集类别",
            Content = message,
            CloseButtonText = "确认",
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private void AiZoom_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _thumbnailSize = e.NewValue;
        foreach (var item in _items)
        {
            item.SetThumbnailSize(_thumbnailSize);
        }
    }

    private void AiVerticalSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var totalWidth = AiBrowseLeftColumn.ActualWidth + AiBrowseRightColumn.ActualWidth;
        if (totalWidth <= 0)
        {
            return;
        }

        var leftWidth = Math.Clamp(AiBrowseLeftColumn.ActualWidth + e.HorizontalChange, AiBrowseLeftColumn.MinWidth, totalWidth - AiBrowseRightColumn.MinWidth);
        var rightWidth = Math.Max(AiBrowseRightColumn.MinWidth, totalWidth - leftWidth);
        AiBrowseLeftColumn.Width = new GridLength(leftWidth);
        AiBrowseRightColumn.Width = new GridLength(rightWidth);
        _browseLeftRatio = leftWidth / Math.Max(1, leftWidth + rightWidth);
    }

    private void BrowsePane_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_adjustingBrowseColumns || e.NewSize.Width <= 0)
        {
            return;
        }

        var availableWidth = e.NewSize.Width - 8 - (BrowsePane.ColumnSpacing * 2);
        if (availableWidth <= AiBrowseLeftColumn.MinWidth + AiBrowseRightColumn.MinWidth)
        {
            return;
        }

        var leftWidth = Math.Clamp(
            availableWidth * _browseLeftRatio,
            AiBrowseLeftColumn.MinWidth,
            availableWidth - AiBrowseRightColumn.MinWidth);
        var rightWidth = availableWidth - leftWidth;

        _adjustingBrowseColumns = true;
        AiBrowseLeftColumn.Width = new GridLength(leftWidth);
        AiBrowseRightColumn.Width = new GridLength(rightWidth);
        _adjustingBrowseColumns = false;
    }

    private void AiHorizontalSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        => AiInfoTopRow.Height = new GridLength(Math.Max(200, AiInfoTopRow.ActualHeight + e.VerticalChange));

    private void AiSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (BrowsePane is null || ModelClassList is null || StatusPane is null)
        {
            return;
        }

        if (ReferenceEquals(sender.SelectedItem, ModelSelectorItem))
        {
            ShowPivotPane(ModelClassList);
        }
        else if (ReferenceEquals(sender.SelectedItem, BrowseSelectorItem))
        {
            SetBrowseMode(false);
            PopulateBrowse();
            ShowPivotPane(BrowsePane);
        }
        else if (ReferenceEquals(sender.SelectedItem, ThresholdSelectorItem))
        {
            SetBrowseMode(true);
            PopulateBrowse();
            ShowPivotPane(BrowsePane);
        }
        else if (ReferenceEquals(sender.SelectedItem, StatusSelectorItem))
        {
            ShowPivotPane(StatusPane);
        }
    }

    private void SetBrowseMode(bool thresholdMode)
    {
        _thresholdMode = thresholdMode;
        AiFilterLabel.Text = thresholdMode ? "展示项目" : "类别";
        AiClassFilter.Visibility = thresholdMode ? Visibility.Collapsed : Visibility.Visible;
        AiThresholdFilter.Visibility = thresholdMode ? Visibility.Visible : Visibility.Collapsed;
        HideClassified.Visibility = thresholdMode ? Visibility.Collapsed : Visibility.Visible;
        DisableThresholdChoiceCheck.Visibility = thresholdMode ? Visibility.Visible : Visibility.Collapsed;
        ThresholdTopControls.Visibility = thresholdMode ? Visibility.Visible : Visibility.Collapsed;
        MoveAiDropDown.Visibility = thresholdMode ? Visibility.Collapsed : Visibility.Visible;
        ThresholdLockButton.Visibility = thresholdMode ? Visibility.Visible : Visibility.Collapsed;
        AiGrid.CanDragItems = thresholdMode || !_state.IsThresholdFilterEnabled;
        AiActionHint.Text = thresholdMode ? "选择并锁定数据集类别来处理数据。" : "隐藏已分类项来快捷操作。";
        if (thresholdMode)
        {
            UpdateThresholdModeState();
        }
        else
        {
            BuildMoveMenu();
        }
    }

    private void ShowPivotPane(FrameworkElement visiblePane)
    {
        ModelClassList.Visibility = visiblePane == ModelClassList ? Visibility.Visible : Visibility.Collapsed;
        BrowsePane.Visibility = visiblePane == BrowsePane ? Visibility.Visible : Visibility.Collapsed;
        StatusPane.Visibility = visiblePane == StatusPane ? Visibility.Visible : Visibility.Collapsed;
        SetLoading(_isRefreshing);
    }

    private static string GlyphFor(string name) => name switch
    {
        "Contact" => "\uE77B",
        "ContactPresence" => "\uE8FA",
        "DockBottom" => "\uE90E",
        "Hide" => "\uED1A",
        "Contrast" => "\uE7A1",
        "Cloud" => "\uE753",
        "Brightness" => "\uE706",
        "QuietHours" => "\uE708",
        "Blocked" => "\uE733",
        _ => "\uE8FD"
    };

    private static bool PassesThresholdFilter(float? probability, int filterIndex, float threshold)
        => filterIndex switch
        {
            1 => probability is not null && probability.Value >= threshold,
            2 => probability is not null && probability.Value < threshold,
            _ => true
        };

    private void UpdateThresholdModeState()
    {
        AiThresholdFilter.IsEnabled = _thresholdLocked;
        DisableThresholdChoiceCheck.IsEnabled = _thresholdLocked;
        var enableChoices = _thresholdLocked && DisableThresholdChoiceCheck.IsChecked != true;
        ThresholdHighRadio.IsEnabled = enableChoices;
        ThresholdLowRadio.IsEnabled = enableChoices;
        ThresholdCorrectRadio.IsEnabled = enableChoices;
        ThresholdWrongRadio.IsEnabled = enableChoices;
        ThresholdDatasetClassBox.IsEnabled = !_thresholdLocked;
        ThresholdLockButton.Content = _thresholdLocked ? "解锁数据集类别" : "锁定数据集类别";
        UpdateSummaryAndActions();
    }

    private static int MapFaceClassToOnnxClass(int faceClassIndex)
        => faceClassIndex switch
        {
            0 => 0,
            1 => 3,
            2 => 4,
            3 => 5,
            4 => 4,
            5 => 0,
            6 => 1,
            7 => 1,
            8 => 1,
            9 => 1,
            10 => 2,
            11 => 6,
            12 => 7,
            13 => 7,
            14 => 8,
            _ => 8
        };

    private bool IsAiBrowseReadOnly() => !_thresholdMode && _state.IsThresholdFilterEnabled;

    private void ShowToast(string message)
    {
        if (!_state.ShowOperationTips)
        {
            return;
        }

        _toastCloseTimer.Stop();
        AiToastBar.Title = message;
        AiToastBar.Visibility = Visibility.Visible;
        AiToastBar.IsOpen = true;
        _toastCloseTimer.Start();
    }

    private void AiToastCloseTimer_Tick(object? sender, object e)
    {
        _toastCloseTimer.Stop();
        AiToastBar.IsOpen = false;
        AiToastBar.Visibility = Visibility.Collapsed;
    }

    private void AiToastBar_CloseButtonClick(InfoBar sender, object args)
    {
        _toastCloseTimer.Stop();
        AiToastBar.Visibility = Visibility.Collapsed;
    }

    private sealed record ModelClassRow(int OnnxId, string Glyph, string Title, string Subtitle, string PendingText);
}
