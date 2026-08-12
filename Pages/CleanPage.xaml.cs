using System.Collections.ObjectModel;
using DataFlushWinUI.Models;
using DataFlushWinUI.Services;
using DataFlushWinUI.Services.Ai;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace DataFlushWinUI.Pages;

public sealed partial class CleanPage : Page
{
    private readonly AppState _state = AppState.Current;
    private readonly Dictionary<GridView, ObservableCollection<ImageItem>> _items = [];
    private readonly Dictionary<GridView, ComboBox> _classes = [];
    private readonly Dictionary<GridView, TextBlock> _statuses = [];
    private readonly Dictionary<GridView, double> _zoomSizes = [];
    private readonly Dictionary<GridView, bool> _listModes = [];
    private readonly Dictionary<GridView, Border> _selectionRects = [];
    private readonly Dictionary<GridView, Point> _selectionStartPoints = [];
    private readonly Dictionary<GridView, long> _lastMarqueeTicks = [];
    private readonly Dictionary<GridView, HashSet<ImageItem>> _lastMarqueeSelections = [];
    private readonly Dictionary<GridView, HashSet<ImageItem>> _marqueeBaseSelections = [];
    private readonly HashSet<GridView> _marqueeAdditive = [];
    private readonly HashSet<GridView> _marqueeSelecting = [];
    private readonly Dictionary<GridView, ImageItem> _lastPreviewItems = [];
    private readonly DispatcherTimer _toastCloseTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _restoringLayout;
    private bool _dataChangedSubscribed;
    private bool _updatingCategorySources;
    private GridView? _activeList;
    private PreviewWindow? _previewWindow;

    public CleanPage()
    {
        InitializeComponent();
        var previewAccelerator = new KeyboardAccelerator
        {
            Key = VirtualKey.Space
        };
        previewAccelerator.Invoked += PreviewAccelerator_Invoked;
        KeyboardAccelerators.Add(previewAccelerator);

        _restoringLayout = true;
        ConfigurePane(PaneAList, PaneAClass, PaneAStatus, PaneAZoom, PaneASelectionRect, 15);
        ConfigurePane(PaneBList, PaneBClass, PaneBStatus, PaneBZoom, PaneBSelectionRect, 0);
        ConfigurePane(PaneCList, PaneCClass, PaneCStatus, PaneCZoom, PaneCSelectionRect, 1);
        RestoreLayoutSettings();
        _toastCloseTimer.Tick += ToastCloseTimer_Tick;
        _state.Store.Configure(_state.DatasetPath, _state.CsvPath);
        Loaded += CleanPage_Loaded;
        Unloaded += CleanPage_Unloaded;
        UpdateCommandState();
        if (_state.Store.IsLoaded)
        {
            RefreshAll();
        }
    }

    private async void CleanPage_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeDataChanged();
        await TryAutoLoadSavedCsvAsync();
    }

    private void CleanPage_Unloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeDataChanged();
        TogglePreview(false);
    }

    private void SubscribeDataChanged()
    {
        if (_dataChangedSubscribed)
        {
            return;
        }

        _state.DataChanged += AppState_DataChanged;
        _dataChangedSubscribed = true;
    }

    private void UnsubscribeDataChanged()
    {
        if (!_dataChangedSubscribed)
        {
            return;
        }

        _state.DataChanged -= AppState_DataChanged;
        _dataChangedSubscribed = false;
    }

    private void AppState_DataChanged(object? sender, EventArgs e) => RefreshAll();

    private IReadOnlyList<FaceClass> CleanSelectableClasses()
        => _state.ShowHatClassesInCleanPage && _state.Store.IsHatExtensionEnabled
            ? FaceClasses.All
            : FaceClasses.Core;

    private void RefreshCategorySources()
    {
        _updatingCategorySources = true;
        var classes = CleanSelectableClasses();
        try
        {
            foreach (var combo in _classes.Values)
            {
                var selected = combo.SelectedIndex;
                combo.ItemsSource = classes;
                combo.DisplayMemberPath = nameof(FaceClass.DisplayName);
                combo.SelectedIndex = Math.Clamp(selected, 0, classes.Count - 1);
            }
        }
        finally
        {
            _updatingCategorySources = false;
        }
    }

    private async Task TryAutoLoadSavedCsvAsync()
    {
        if (_state.Store.IsLoaded ||
            !Directory.Exists(_state.DatasetPath) ||
            !Directory.Exists(_state.CsvPath))
        {
            UpdateCommandState();
            return;
        }

        _state.Store.Configure(_state.DatasetPath, _state.CsvPath);
        if (!_state.Store.HasCompleteCsvSet())
        {
            UpdateCommandState();
            return;
        }

        try
        {
            _state.Store.Load();
            if (!await ValidateLoadedCsvAsync())
            {
                return;
            }

            await ReloadAiResultsAsync();
            _state.UndoRecord = null;
            RefreshAll();
            ShowToast("已自动加载上次的数据集和 CSV。", false);
            await TryStartLocalOnnxTaskAfterCsvReadyAsync();
        }
        catch (Exception ex)
        {
            _ = MainWindow.AppWindowInstance.ShowMessageAsync("自动加载失败", ex.Message);
        }
    }

    private void ConfigurePane(GridView list, ComboBox combo, TextBlock status, Slider zoom, Border selectionRect, int classIndex)
    {
        combo.ItemsSource = CleanSelectableClasses();
        combo.DisplayMemberPath = nameof(FaceClass.DisplayName);
        combo.SelectedIndex = Math.Clamp(classIndex, 0, CleanSelectableClasses().Count - 1);
        var source = new ObservableCollection<ImageItem>();
        list.ItemsSource = source;
        _items[list] = source;
        _classes[list] = combo;
        _statuses[list] = status;
        _zoomSizes[list] = zoom.Value;
        _listModes[list] = false;
        _selectionRects[list] = selectionRect;
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

    private void RestoreLayoutSettings()
    {
        _restoringLayout = true;
        try
        {
            RestorePaneSettings("A", PaneAClass, PaneAZoom, PaneAList);
            RestorePaneSettings("B", PaneBClass, PaneBZoom, PaneBList);
            RestorePaneSettings("C", PaneCClass, PaneCZoom, PaneCList);

            var horizontalRatio = ReadDoubleSetting("clean_layout_horizontal_ratio", 2d / 3d);
            horizontalRatio = Math.Clamp(horizontalRatio, 0.25, 0.8);
            LeftColumn.Width = new GridLength(horizontalRatio, GridUnitType.Star);
            RightColumn.Width = new GridLength(1 - horizontalRatio, GridUnitType.Star);

            var rightVerticalRatio = ReadDoubleSetting("clean_layout_right_vertical_ratio", 0.5);
            rightVerticalRatio = Math.Clamp(rightVerticalRatio, 0.25, 0.75);
            RightTopRow.Height = new GridLength(rightVerticalRatio, GridUnitType.Star);
            RightBottomRow.Height = new GridLength(1 - rightVerticalRatio, GridUnitType.Star);
        }
        finally
        {
            _restoringLayout = false;
        }
    }

    private void RestorePaneSettings(string paneKey, ComboBox combo, Slider zoom, GridView list)
    {
        var classIndex = ReadIntSetting($"clean_layout_{paneKey}_class", combo.SelectedIndex);
        combo.SelectedIndex = Math.Clamp(classIndex, 0, CleanSelectableClasses().Count - 1);

        var zoomValue = ReadDoubleSetting($"clean_layout_{paneKey}_zoom", zoom.Value);
        zoom.Value = Math.Clamp(zoomValue, zoom.Minimum, zoom.Maximum);
        UpdatePaneZoom(list, zoom.Value);
    }

    private void SavePaneSettings()
    {
        if (_restoringLayout)
        {
            return;
        }

        WriteSetting("clean_layout_A_class", PaneAClass.SelectedIndex);
        WriteSetting("clean_layout_B_class", PaneBClass.SelectedIndex);
        WriteSetting("clean_layout_C_class", PaneCClass.SelectedIndex);
        WriteSetting("clean_layout_A_zoom", PaneAZoom.Value);
        WriteSetting("clean_layout_B_zoom", PaneBZoom.Value);
        WriteSetting("clean_layout_C_zoom", PaneCZoom.Value);
    }

    private void SaveSplitterSettings()
    {
        if (_restoringLayout)
        {
            return;
        }

        var totalWidth = LeftColumn.ActualWidth + RightColumn.ActualWidth;
        if (totalWidth > 0)
        {
            WriteSetting("clean_layout_horizontal_ratio", LeftColumn.ActualWidth / totalWidth);
        }

        var totalHeight = RightTopRow.ActualHeight + RightBottomRow.ActualHeight;
        if (totalHeight > 0)
        {
            WriteSetting("clean_layout_right_vertical_ratio", RightTopRow.ActualHeight / totalHeight);
        }
    }

    private int ReadIntSetting(string key, int fallback)
        => int.TryParse(_state.ReadLocalSetting(key), out var value) ? value : fallback;

    private double ReadDoubleSetting(string key, double fallback)
        => double.TryParse(_state.ReadLocalSetting(key), System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private void WriteSetting(string key, int value)
        => _state.WriteLocalSetting(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void WriteSetting(string key, double value)
        => _state.WriteLocalSetting(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private async void OpenDataset_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_state.DatasetPath) && _state.Store.IsLoaded)
        {
            var confirm = await MainWindow.AppWindowInstance.ShowConfirmAsync("打开新的数据集", "打开新的数据集会关闭当前数据集。是否继续？");
            if (!confirm) return;
        }

        var path = await MainWindow.AppWindowInstance.PickFolderAsync(_state.DatasetPath, "选择数据集目录");
        if (string.IsNullOrWhiteSpace(path)) return;
        await LocalOnnxBatchInferenceService.Current.StopAsync();
        _state.SetDatasetPath(path, resetCsvPath: true);
        _state.UndoRecord = null;
        RefreshAll();
        UpdateCommandState();
    }

    private async void OpenCsv_Click(object sender, RoutedEventArgs e)
        => await PickCsvDirectoryAsync();

    private async Task PickCsvDirectoryAsync()
    {
        var path = await MainWindow.AppWindowInstance.PickFolderAsync(_state.CsvPath, CsvPickerTitle());
        if (string.IsNullOrWhiteSpace(path)) return;
        _state.SetCsvPath(path);
        _state.Store.Configure(_state.DatasetPath, _state.CsvPath);
        if (_state.Store.HasCompleteCsvSet())
        {
            await LoadCsvAsync();
            return;
        }

        var create = await MainWindow.AppWindowInstance.ShowConfirmAsync("建立CSV文件", "所选目录下未找到完整CSV文件。是否建立新的CSV文件？");
        if (create)
        {
            await InitializeCsvAsync();
        }
    }

    private string CsvPickerTitle() => $"为数据集 {_state.DatasetPath} 选择CSV目录";

    private async void ResetCsv_Click(object sender, RoutedEventArgs e)
    {
        var (confirmed, clearAiCache) = await ConfirmResetCsvAsync();
        if (!confirmed) return;

        if (clearAiCache)
        {
            ClearAiLandmarkCache();
        }

        if (await InitializeCsvAsync(showStartedMessage: false))
        {
            await TryStartLocalOnnxTaskAfterCsvReadyAsync("CSV 已重置，已启动本地 ONNX 后台任务。");
        }
    }

    private async Task<(bool Confirmed, bool ClearAiCache)> ConfirmResetCsvAsync()
    {
        var clearCacheCheck = new CheckBox
        {
            Content = "清理AI_landmark缓存",
            IsChecked = true,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var content = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "重置会清除并重建CSV文件。是否继续？",
                    TextWrapping = TextWrapping.Wrap
                },
                clearCacheCheck
            }
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重置CSV",
            Content = content,
            PrimaryButtonText = "重置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        return (result == ContentDialogResult.Primary, clearCacheCheck.IsChecked == true);
    }

    private async Task<bool> InitializeCsvAsync(bool showStartedMessage = true)
    {
        try
        {
            _state.Store.Configure(_state.DatasetPath, _state.CsvPath);
            _state.Store.Initialize();
            _state.UndoRecord = null;
            RefreshAll();
            ShowToast("CSV 已初始化。", false);
            if (showStartedMessage)
            {
                await TryStartLocalOnnxTaskAfterCsvReadyAsync();
            }

            return true;
        }
        catch (Exception ex)
        {
            _ = MainWindow.AppWindowInstance.ShowMessageAsync("初始化失败", ex.Message);
            return false;
        }
    }

    private async Task TryStartLocalOnnxTaskAfterCsvReadyAsync(string startedMessage = "已启动本地 ONNX 后台任务。")
    {
        if (!_state.AutoStartOnnxInference)
        {
            return;
        }

        var service = LocalOnnxBatchInferenceService.Current;
        var started = await service.RestartAsync(_state);
        if (started)
        {
            ShowToast(startedMessage, false);
        }
    }

    private async Task LoadCsvAsync()
    {
        try
        {
            _state.Store.Load();
            if (!await ValidateLoadedCsvAsync())
            {
                return;
            }

            await ReloadAiResultsAsync();
            _state.UndoRecord = null;
            RefreshAll();
            ShowToast("CSV 已加载。", false);
            await TryStartLocalOnnxTaskAfterCsvReadyAsync();
        }
        catch (Exception ex)
        {
            _ = MainWindow.AppWindowInstance.ShowMessageAsync("加载失败", ex.Message);
        }
    }

    private async Task<bool> ValidateLoadedCsvAsync()
    {
        var missing = _state.Store.FindMissingDatasetFiles();
        if (missing.Count == 0)
        {
            return true;
        }

        _state.Store.Clear();
        _state.UndoRecord = null;
        RefreshAll();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "CSV目录有误",
            Content = $"CSV目录中的文件名在当前数据集中找不到。请重新选择CSV目录，或重置CSV。{Environment.NewLine}{Environment.NewLine}当前数据集目录：{Environment.NewLine}{_state.DatasetPath}{Environment.NewLine}{Environment.NewLine}当前CSV目录：{Environment.NewLine}{_state.CsvPath}",
            PrimaryButtonText = "重新选择CSV",
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await PickCsvDirectoryAsync();
        }

        return false;
    }

    private Task ReloadAiResultsAsync()
        => Task.Run(_state.LoadAiResults);

    private void ClearAiLandmarkCache()
    {
        if (string.IsNullOrWhiteSpace(_state.CsvPath))
        {
            return;
        }

        var aiCacheDir = Path.Combine(_state.CsvPath, "AI_landmark");
        if (Directory.Exists(aiCacheDir))
        {
            Directory.Delete(aiCacheDir, recursive: true);
        }

        _state.AiResults.Clear();
        _state.AiPriorityQueue.Clear();
    }

    private void PaneClass_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCategorySources)
        {
            return;
        }

        SavePaneSettings();
        RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshCategorySources();
        foreach (var (list, source) in _items)
        {
            source.Clear();
            var classIndex = _classes[list].SelectedIndex;
            if (!_state.Store.IsLoaded || classIndex < 0 || classIndex >= _state.Store.Items.Length) continue;
            foreach (var name in _state.Store.Items[classIndex])
            {
                var item = new ImageItem(name, classIndex);
                item.SetThumbnailSize(_zoomSizes.TryGetValue(list, out var size) ? size : 132);
                source.Add(item);
            }

            _statuses[list].Text = $"总数：{source.Count} 已选：{list.SelectedItems.Count}";
        }

        UpdateProgress();
        UpdateCommandState();
    }

    private void PaneSelection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is GridView list)
        {
            _activeList = list;
            var added = e.AddedItems.OfType<ImageItem>().LastOrDefault();
            if (added is not null)
            {
                _lastPreviewItems[list] = added;
            }
            else if (_lastPreviewItems.TryGetValue(list, out var previous) && !list.SelectedItems.Contains(previous))
            {
                _lastPreviewItems.Remove(list);
            }

            list.Focus(FocusState.Programmatic);
            _statuses[list].Text = $"总数：{_items[list].Count} 已选：{list.SelectedItems.Count}";
            FocusSelectedItem(list);
            UpdatePreview();
        }
    }

    private void Image_ItemClick(object sender, ItemClickEventArgs e)
    {
        _activeList = (GridView)sender;
        if (e.ClickedItem is ImageItem item)
        {
            _lastPreviewItems[_activeList] = item;
        }

        _activeList.Focus(FocusState.Programmatic);
        UpdatePreview();
    }

    private void Pane_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not GridView list) return;
        _activeList = list;
        var selected = SelectedNames(list);
        var readOnly = IsThresholdFilterActive();
        var flyout = new MenuFlyout();
        if (selected.Count == 0)
        {
            flyout.Items.Add(MenuItem("刷新缩略图", (_, _) => RefreshAll()));
            flyout.Items.Add(MenuItem("显示预览", (_, _) => TogglePreview(true)));
        }
        else
        {
            foreach (var target in SiblingClassIndices(list).Take(2))
            {
                var item = MenuItem($"移动到 {FaceClasses.ByIndex(target).Name}", (_, _) => MoveSelected(list, target));
                item.IsEnabled = !readOnly;
                flyout.Items.Add(item);
            }
            flyout.Items.Add(new MenuFlyoutSeparator());
            var sub = new MenuFlyoutSubItem { Text = "移动到..." };
            var source = CurrentClassIndex(list);
            foreach (var cls in CleanSelectableClasses().Where(c => c.Index != source))
            {
                var item = MenuItem(cls.DisplayName, (_, _) => MoveSelected(list, cls.Index));
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

    private void MoveSelected(GridView list, int target)
    {
        if (IsThresholdFilterActive())
        {
            ShowThresholdReadOnlyTip();
            return;
        }

        var selected = SelectedNames(list);
        var source = CurrentClassIndex(list);
        if (selected.Count == 0 || source == target) return;
        var order = _state.Store.Items[source].ToList();
        var record = _state.Store.Move(source, target, selected);
        if (record is null) return;
        _state.UndoRecord = record;
        RefreshAll();
        SelectNextAfterMove(list, order, record.FileNames);
        ShowToast($"已将 {record.FileNames.Count} 张图像从 {FaceClasses.ByIndex(source).Name} 移动到 {FaceClasses.ByIndex(target).Name}", true);
    }

    private void SelectNextAfterMove(GridView list, List<string> order, IReadOnlyList<string> moved)
    {
        var movedSet = moved.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maxIndex = order.Select((name, index) => (name, index)).Where(x => movedSet.Contains(x.name)).Select(x => x.index).DefaultIfEmpty(-1).Max();
        var next = order.Skip(maxIndex + 1).FirstOrDefault(name => !movedSet.Contains(name)) ?? order.LastOrDefault(name => !movedSet.Contains(name));
        if (next is null) return;
        var item = _items[list].FirstOrDefault(x => x.FileName.Equals(next, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            list.SelectedItems.Clear();
            list.SelectedItems.Add(item);
            _lastPreviewItems[list] = item;
            list.ScrollIntoView(item);
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (IsThresholdFilterActive())
        {
            ShowThresholdReadOnlyTip();
            return;
        }

        if (_state.UndoRecord is null) return;
        _state.Store.Undo(_state.UndoRecord);
        _state.UndoRecord = null;
        RefreshAll();
        ShowToast("已撤销。", false);
    }

    private void PreviewToggle_Click(object sender, RoutedEventArgs e) => TogglePreview(PreviewToggle.IsChecked == true);

    private void TogglePreview(bool open)
    {
        if (!open)
        {
            _previewWindow?.Close();
            _previewWindow = null;
            PreviewToggle.IsChecked = false;
            return;
        }

        if (_previewWindow is null)
        {
            _previewWindow = new PreviewWindow();
            _previewWindow.Closed += PreviewWindow_Closed;
        }

        _previewWindow.Activate();
        PreviewToggle.IsChecked = true;
        UpdatePreview();
    }

    private void PreviewWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_previewWindow is not null)
        {
            _previewWindow.Closed -= PreviewWindow_Closed;
        }

        _previewWindow = null;
        PreviewToggle.IsChecked = false;
    }

    private void UpdatePreview()
    {
        if (_previewWindow is null || _activeList is null) return;
        var item = _lastPreviewItems.TryGetValue(_activeList, out var last) && _activeList.SelectedItems.Contains(last)
            ? last
            : _activeList.SelectedItems.OfType<ImageItem>().LastOrDefault() ?? _activeList.SelectedItem as ImageItem;
        if (item is null) return;
        _previewWindow.ShowImage(item);
    }

    private void ProgressButton_Click(object sender, RoutedEventArgs e)
    {
        // DropDownButton opens its Flyout automatically; this handler keeps the command available for future telemetry.
    }

    private void UpdateProgress()
    {
        var total = _state.Store.DatasetImageTotal;
        var remaining = _state.Store.IsLoaded ? _state.Store.Items[15].Count : 0;
        var done = Math.Max(0, total - remaining);
        var pct = total == 0 ? 0 : done * 100.0 / total;
        ProgressButton.Content = $"完成：{pct:0.0}%";
        CleanProgressBar.Value = pct;
        ProgressPercentText.Text = $"{pct:0.0}%";
        ProgressRemainingText.Text = $"剩余：{remaining}";
        ProgressCountText.Text = $"{done}/{total}";
    }

    private void UpdateCommandState()
    {
        var datasetOpen = Directory.Exists(_state.DatasetPath);
        var csvPathOpen = Directory.Exists(_state.CsvPath);
        var csvLoaded = _state.Store.IsLoaded && csvPathOpen;
        OpenCsvButton.IsEnabled = datasetOpen;
        UndoButton.IsEnabled = _state.UndoRecord is not null && !IsThresholdFilterActive();
        OpenDatasetDirItem.IsEnabled = datasetOpen;
        OpenCsvDirItem.IsEnabled = csvPathOpen;
        ResetCsvItem.IsEnabled = datasetOpen && csvPathOpen;
        if (csvLoaded && IsThresholdFilterActive())
        {
            ShowThresholdReadOnlyTip();
        }
    }

    private int CurrentClassIndex(GridView list) => _classes[list].SelectedIndex;

    private IReadOnlyList<int> SiblingClassIndices(GridView list) => _classes.Where(x => x.Key != list).Select(x => x.Value.SelectedIndex).Where(i => i >= 0 && i != CurrentClassIndex(list)).Distinct().ToArray();

    private List<string> SelectedNames(GridView list) => list.SelectedItems.OfType<ImageItem>().Select(x => x.FileName).ToList();

    private void ShowToast(string message, bool undo)
    {
        if (!_state.ShowOperationTips)
        {
            return;
        }

        _toastCloseTimer.Stop();
        ToastBar.Title = message;
        ToastBar.ActionButton = undo ? new Button { Content = "撤销" } : null;
        if (ToastBar.ActionButton is Button button) button.Click += Undo_Click;
        ToastBar.Visibility = Visibility.Visible;
        ToastBar.IsOpen = true;
        _toastCloseTimer.Start();
    }

    private bool IsThresholdFilterActive() => _state.IsThresholdFilterEnabled;

    private void ShowThresholdReadOnlyTip()
        => ShowToast("当前已启用阈值过滤，请先到 AI辅助 > 阈值过滤 解锁后再使用清洗分类。", false);

    private void ToastCloseTimer_Tick(object? sender, object e)
    {
        _toastCloseTimer.Stop();
        ToastBar.IsOpen = false;
        ToastBar.Visibility = Visibility.Collapsed;
    }

    private void ToastBar_CloseButtonClick(InfoBar sender, object args)
    {
        _toastCloseTimer.Stop();
        ToastBar.Visibility = Visibility.Collapsed;
    }

    private void OpenDatasetInExplorer_Click(object sender, RoutedEventArgs e) => MainWindow.OpenInExplorer(_state.DatasetPath);
    private void OpenCsvInExplorer_Click(object sender, RoutedEventArgs e) => MainWindow.OpenInExplorer(_state.CsvPath);
    private void ModeIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var parts = tag.Split(':');
        if (parts.Length != 2)
        {
            return;
        }

        var list = parts[0] switch
        {
            "A" => PaneAList,
            "B" => PaneBList,
            "C" => PaneCList,
            _ => null
        };
        var modeButton = parts[0] switch
        {
            "A" => PaneAMode,
            "B" => PaneBMode,
            "C" => PaneCMode,
            _ => null
        };
        if (list is null || modeButton is null)
        {
            return;
        }

        var listMode = parts[1] == "list";
        _listModes[list] = listMode;
        list.ItemTemplate = (DataTemplate)Resources[listMode ? "PaneListItemTemplate" : "PaneItemTemplate"];
        list.ItemsPanel = (ItemsPanelTemplate)Resources[listMode ? "PaneListItemsPanel" : "PanePreviewItemsPanel"];
        modeButton.Content = new FontIcon
        {
            Glyph = listMode ? "\uE8FD" : "\uEB9F",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(listMode ? "Segoe MDL2 Assets" : "Segoe Fluent Icons")
        };
    }
    private void Zoom_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (PaneAList is null || PaneBList is null || PaneCList is null)
        {
            return;
        }

        if (ReferenceEquals(sender, PaneAZoom))
        {
            UpdatePaneZoom(PaneAList, e.NewValue);
        }
        else if (ReferenceEquals(sender, PaneBZoom))
        {
            UpdatePaneZoom(PaneBList, e.NewValue);
        }
        else if (ReferenceEquals(sender, PaneCZoom))
        {
            UpdatePaneZoom(PaneCList, e.NewValue);
        }

        SavePaneSettings();
    }

    private void PaneList_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not GridView list)
        {
            return;
        }

        var ctrlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if (!ctrlState.HasFlag(CoreVirtualKeyStates.Down))
        {
            return;
        }

        e.Handled = true;
        var delta = e.GetCurrentPoint(list).Properties.MouseWheelDelta > 0 ? 10 : -10;
        var slider = list == PaneAList ? PaneAZoom : list == PaneBList ? PaneBZoom : PaneCZoom;
        slider.Value = Math.Clamp(slider.Value + delta, slider.Minimum, slider.Maximum);
    }

    private void PaneList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not GridView list)
        {
            return;
        }

        _activeList = list;
        var properties = e.GetCurrentPoint(list).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        if (FindAncestor<GridViewItem>(e.OriginalSource as DependencyObject) is null)
        {
            _marqueeSelecting.Add(list);
            _selectionStartPoints[list] = ClampPointToElement(e.GetCurrentPoint(list).Position, list);
            _lastMarqueeSelections[list] = [];
            _lastMarqueeTicks[list] = 0;
            _marqueeBaseSelections[list] = list.SelectedItems.OfType<ImageItem>().ToHashSet();
            var ctrlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            if (ctrlState.HasFlag(CoreVirtualKeyStates.Down))
            {
                _marqueeAdditive.Add(list);
            }
            else
            {
                _marqueeAdditive.Remove(list);
                list.SelectedItems.Clear();
            }
            list.CapturePointer(e.Pointer);
            UpdateSelectionRect(list, _selectionStartPoints[list], _selectionStartPoints[list]);
            _statuses[list].Text = $"总数：{_items[list].Count} 已选：0";
            UpdatePreview();
            e.Handled = true;
        }
    }

    private void PaneList_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not GridView list || !_marqueeSelecting.Contains(list))
        {
            return;
        }

        var current = ClampPointToElement(e.GetCurrentPoint(list).Position, list);
        var start = _selectionStartPoints[list];
        UpdateSelectionRect(list, start, current);

        var now = Environment.TickCount64;
        if (_lastMarqueeTicks.TryGetValue(list, out var previous) && now - previous < 16)
        {
            e.Handled = true;
            return;
        }

        _lastMarqueeTicks[list] = now;
        SelectItemsInRect(list, MakeRect(start, current));
        e.Handled = true;
    }

    private void PaneList_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndMarqueeSelection(sender as GridView, e.Pointer);
    }

    private void PaneList_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndMarqueeSelection(sender as GridView, e.Pointer);
    }

    private void EndMarqueeSelection(GridView? list, Pointer pointer)
    {
        if (list is null || !_marqueeSelecting.Remove(list))
        {
            return;
        }

        list.ReleasePointerCapture(pointer);
        if (_selectionRects.TryGetValue(list, out var rect))
        {
            rect.Visibility = Visibility.Collapsed;
        }
        _lastMarqueeSelections.Remove(list);
        _lastMarqueeTicks.Remove(list);
        _marqueeBaseSelections.Remove(list);
        _marqueeAdditive.Remove(list);
    }

    private void PaneList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not GridView list)
        {
            return;
        }

        _activeList = list;
        if (e.Key == VirtualKey.Space)
        {
            if (list.SelectedItems.Count > 0)
            {
                TogglePreview(_previewWindow is null);
                e.Handled = true;
            }
        }
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Space)
        {
            TogglePreview(_previewWindow is null);
            e.Handled = true;
        }
    }

    private void PreviewAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_activeList?.SelectedItems.Count > 0)
        {
            TogglePreview(_previewWindow is null);
            args.Handled = true;
        }
    }

    private void FocusSelectedItem(GridView list)
    {
        var item = _lastPreviewItems.TryGetValue(list, out var last) && list.SelectedItems.Contains(last)
            ? last
            : list.SelectedItems.OfType<ImageItem>().LastOrDefault() ?? list.SelectedItem as ImageItem;
        if (item is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (list.ContainerFromItem(item) is GridViewItem container)
            {
                container.Focus(FocusState.Programmatic);
            }
        });
    }

    private void PaneList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (IsThresholdFilterActive())
        {
            e.Cancel = true;
            ShowThresholdReadOnlyTip();
            return;
        }

        if (sender is not GridView list)
        {
            return;
        }

        var names = e.Items.OfType<ImageItem>().Select(x => x.FileName).ToList();
        if (names.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText($"{CurrentClassIndex(list)}\n{string.Join('\n', names)}");
    }

    private void PaneList_DragOver(object sender, DragEventArgs e)
    {
        if (IsThresholdFilterActive())
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
        if (IsThresholdFilterActive())
        {
            ShowThresholdReadOnlyTip();
            e.Handled = true;
            return;
        }

        if (sender is not GridView targetList || !e.DataView.Contains(StandardDataFormats.Text))
        {
            return;
        }

        var text = await e.DataView.GetTextAsync();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || !int.TryParse(lines[0], out var source))
        {
            return;
        }

        var target = CurrentClassIndex(targetList);
        var names = lines.Skip(1).ToList();
        MoveItemsFromSource(source, target, names);
        e.Handled = true;
    }

    private void MoveItemsFromSource(int source, int target, List<string> names)
    {
        if (IsThresholdFilterActive())
        {
            ShowThresholdReadOnlyTip();
            return;
        }

        if (!_state.Store.IsLoaded || source == target || names.Count == 0)
        {
            return;
        }

        var sourceList = _classes.FirstOrDefault(x => x.Value.SelectedIndex == source).Key;
        var sourceOrder = _state.Store.Items[source].ToList();
        var record = _state.Store.Move(source, target, names);
        if (record is null)
        {
            return;
        }

        _state.UndoRecord = record;
        RefreshAll();
        if (sourceList is not null)
        {
            SelectNextAfterMove(sourceList, sourceOrder, record.FileNames);
        }
        ShowToast($"已将 {record.FileNames.Count} 张图像从 {FaceClasses.ByIndex(source).Name} 移动到 {FaceClasses.ByIndex(target).Name}", true);
    }

    private void UpdatePaneZoom(GridView list, double size)
    {
        if (!_items.ContainsKey(list))
        {
            return;
        }

        _zoomSizes[list] = size;
        foreach (var item in _items[list])
        {
            item.SetThumbnailSize(size);
        }
    }

    private void UpdateSelectionRect(GridView list, Point start, Point current)
    {
        if (!_selectionRects.TryGetValue(list, out var rect))
        {
            return;
        }

        var bounds = MakeRect(start, current);
        Canvas.SetLeft(rect, bounds.X);
        Canvas.SetTop(rect, bounds.Y);
        rect.Width = bounds.Width;
        rect.Height = bounds.Height;
        rect.Visibility = Visibility.Visible;
    }

    private void SelectItemsInRect(GridView list, Rect selection)
    {
        var nextSelection = _marqueeAdditive.Contains(list) && _marqueeBaseSelections.TryGetValue(list, out var baseSelection)
            ? baseSelection.ToHashSet()
            : [];
        foreach (var container in FindVisualChildren<GridViewItem>(list))
        {
            if (container.Content is not ImageItem item)
            {
                continue;
            }

            var transform = container.TransformToVisual(list);
            var itemBounds = transform.TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (Intersects(selection, itemBounds))
            {
                nextSelection.Add(item);
            }
        }

        if (_lastMarqueeSelections.TryGetValue(list, out var previous) && previous.SetEquals(nextSelection))
        {
            return;
        }

        _lastMarqueeSelections[list] = nextSelection;
        list.SelectedItems.Clear();
        foreach (var item in nextSelection)
        {
            list.SelectedItems.Add(item);
        }

        if (nextSelection.Count > 0)
        {
            _lastPreviewItems[list] = nextSelection.Last();
        }
        else
        {
            _lastPreviewItems.Remove(list);
        }

        _statuses[list].Text = $"总数：{_items[list].Count} 已选：{list.SelectedItems.Count}";
    }

    private static Rect MakeRect(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new Rect(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private static Point ClampPointToElement(Point point, FrameworkElement element)
        => new(
            Math.Clamp(point.X, 0, Math.Max(0, element.ActualWidth)),
            Math.Clamp(point.Y, 0, Math.Max(0, element.ActualHeight)));

    private static bool Intersects(Rect a, Rect b)
        => a.X < b.X + b.Width
           && a.X + a.Width > b.X
           && a.Y < b.Y + b.Height
           && a.Y + a.Height > b.Y;

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
    private void VerticalSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        LeftColumn.Width = new GridLength(Math.Max(280, LeftColumn.ActualWidth + e.HorizontalChange));
        SaveSplitterSettings();
    }
    private void HorizontalSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        RightTopRow.Height = new GridLength(Math.Max(160, RightTopRow.ActualHeight + e.VerticalChange));
        SaveSplitterSettings();
    }
}
