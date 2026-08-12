using DataFlushWinUI.Services;
using DataFlushWinUI.Services.Ai;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DataFlushWinUI.Pages;

public sealed partial class SettingsPage : Page
{
    private const string HealthDefaultText = "测试提供的API服务和密钥是否可用";

    private readonly AppState _state = AppState.Current;
    private readonly DispatcherTimer _infoCloseTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        _loading = true;
        OperationTipsSwitch.IsOn = _state.ShowOperationTips;
        PreviewInfoSwitch.IsOn = _state.PreviewInfoVisible;
        ShowHatClassesSwitch.IsOn = _state.ShowHatClassesInCleanPage;
        AutoStartOnnxSwitch.IsOn = _state.AutoStartOnnxInference;
        MeanStdPreprocessSwitch.IsOn = _state.UseOnnxMeanStdPreprocessing;
        DatasetBox.Text = _state.DatasetPath;
        CsvBox.Text = _state.CsvPath;
        LocalModelBox.Text = _state.LocalModelPath;
        BaseUrlBox.Text = _state.DifyBaseUrl;
        ApiKeyBox.Password = _state.DifyApiKey;
        ApiKeyPlainBox.Text = _state.DifyApiKey;
        _loading = false;
        _infoCloseTimer.Tick += InfoCloseTimer_Tick;
        _state.PreviewInfoVisibleChanged += AppState_PreviewInfoVisibleChanged;
        _state.HatSettingsChanged += AppState_HatSettingsChanged;
        Unloaded += (_, _) =>
        {
            _state.PreviewInfoVisibleChanged -= AppState_PreviewInfoVisibleChanged;
            _state.HatSettingsChanged -= AppState_HatSettingsChanged;
        };
        UpdateCommandState();
    }

    private async void PickDataset_Click(object sender, RoutedEventArgs e)
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
        DatasetBox.Text = path;
        CsvBox.Text = "";
        UpdateCommandState();
    }

    private async void PickCsv_Click(object sender, RoutedEventArgs e)
        => await PickCsvDirectoryAsync();

    private async Task PickCsvDirectoryAsync()
    {
        if (!Directory.Exists(_state.DatasetPath))
        {
            await MainWindow.AppWindowInstance.ShowMessageAsync("先打开数据集", "请先选择数据集目录，再选择 CSV 目录。");
            return;
        }

        var path = await MainWindow.AppWindowInstance.PickFolderAsync(_state.CsvPath, CsvPickerTitle());
        if (string.IsNullOrWhiteSpace(path)) return;
        _state.SetCsvPath(path);
        _state.Store.Configure(_state.DatasetPath, _state.CsvPath);
        CsvBox.Text = path;
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
        else
        {
            UpdateCommandState();
        }
    }

    private string CsvPickerTitle() => $"为数据集 {_state.DatasetPath} 选择CSV文件夹";

    private async Task InitializeCsvAsync()
    {
        try
        {
            _state.Store.Configure(_state.DatasetPath, _state.CsvPath);
            _state.Store.Initialize();
            _state.UndoRecord = null;
            _state.NotifyDataChanged();
            UpdateCommandState();
            await TryStartLocalOnnxTaskAfterCsvReadyAsync();
        }
        catch (Exception ex)
        {
            _ = MainWindow.AppWindowInstance.ShowMessageAsync("初始化失败", ex.Message);
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

            await Task.Run(_state.LoadAiResults);
            _state.UndoRecord = null;
            _state.NotifyDataChanged();
            UpdateCommandState();
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
        _state.NotifyDataChanged();
        UpdateCommandState();
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

    private async Task<bool> TryStartLocalOnnxTaskAfterCsvReadyAsync()
    {
        if (!_state.AutoStartOnnxInference)
        {
            return false;
        }

        var started = await LocalOnnxBatchInferenceService.Current.RestartAsync(_state);
        if (started)
        {
            ShowInfo("已启动本地 ONNX 后台任务。");
        }

        return started;
    }

    private void ShowInfo(string message)
    {
        SettingsInfoBar.Message = message;
        SettingsInfoBar.IsOpen = true;
        _infoCloseTimer.Stop();
        _infoCloseTimer.Start();
    }

    private void InfoCloseTimer_Tick(object? sender, object e)
    {
        _infoCloseTimer.Stop();
        SettingsInfoBar.IsOpen = false;
    }

    private void UpdateCommandState()
    {
        CsvBrowseButton.IsEnabled = Directory.Exists(_state.DatasetPath);
        AutoStartOnnxSwitch.IsEnabled = HasValidLocalModel();
        ShowHatClassesSwitch.IsEnabled = _state.Store.IsHatExtensionEnabled && !_state.IsThresholdFilterEnabled;
        if (!ShowHatClassesSwitch.IsEnabled && ShowHatClassesSwitch.IsOn)
        {
            _loading = true;
            ShowHatClassesSwitch.IsOn = false;
            _loading = false;
        }
    }

    private void OperationTipsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _state.ShowOperationTips = OperationTipsSwitch.IsOn;
        _state.SaveGeneralSettings();
    }

    private void PreviewInfoSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _state.SetPreviewInfoVisible(PreviewInfoSwitch.IsOn);
    }

    private void AutoStartOnnxSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _state.SetAutoStartOnnxInference(AutoStartOnnxSwitch.IsOn);
        UpdateCommandState();
    }

    private async void MeanStdPreprocessSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var requested = MeanStdPreprocessSwitch.IsOn;
        var confirm = await MainWindow.AppWindowInstance.ShowConfirmAsync(
            "是否切换预处理方式",
            "切换预处理方式会重置已处理的AI结果。");
        if (!confirm)
        {
            _loading = true;
            MeanStdPreprocessSwitch.IsOn = _state.UseOnnxMeanStdPreprocessing;
            _loading = false;
            return;
        }

        await LocalOnnxBatchInferenceService.Current.StopAsync();
        _state.SetUseOnnxMeanStdPreprocessing(requested);
        ClearAiLandmarkCache();
        _state.NotifyDataChanged();

        if (_state.AutoStartOnnxInference)
        {
            var started = await TryStartLocalOnnxTaskAfterCsvReadyAsync();
            if (!started)
            {
                ShowInfo("已切换预处理方式并清空 AI 结果。自动启动推理未启动，请检查数据集、CSV目录和本地ONNX模型设置。");
            }
        }
        else
        {
            ShowInfo("已切换预处理方式并清空 AI 结果。");
        }
    }

    private void ShowHatClassesSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _state.SetShowHatClassesInCleanPage(ShowHatClassesSwitch.IsOn);
        UpdateCommandState();
    }

    private void AppState_PreviewInfoVisibleChanged(object? sender, bool visible)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (PreviewInfoSwitch.IsOn == visible)
            {
                return;
            }

            _loading = true;
            PreviewInfoSwitch.IsOn = visible;
            _loading = false;
        });
    }

    private void AppState_HatSettingsChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _loading = true;
            ShowHatClassesSwitch.IsOn = _state.ShowHatClassesInCleanPage && _state.Store.IsHatExtensionEnabled;
            _loading = false;
            UpdateCommandState();
        });
    }

    private async void PickLocalModel_Click(object sender, RoutedEventArgs e)
    {
        var path = await MainWindow.AppWindowInstance.PickFileAsync(".onnx");
        if (string.IsNullOrWhiteSpace(path)) return;
        _state.SetLocalModelPath(path);
        LocalModelBox.Text = path;
        UpdateCommandState();
    }

    private bool HasValidLocalModel()
        => !string.IsNullOrWhiteSpace(_state.LocalModelPath) && File.Exists(_state.LocalModelPath);

    private void ClearAiLandmarkCache()
    {
        if (!string.IsNullOrWhiteSpace(_state.CsvPath))
        {
            var aiCacheDir = Path.Combine(_state.CsvPath, "AI_landmark");
            if (Directory.Exists(aiCacheDir))
            {
                Directory.Delete(aiCacheDir, recursive: true);
            }
        }

        _state.AiResults.Clear();
        _state.AiPriorityQueue.Clear();
    }

    private async void EditOnnxMapping_Click(object sender, RoutedEventArgs e)
    {
        var working = _state.OnnxMapping.ToDictionary(x => x.Key, x => x.Value.ToHashSet());
        var panel = new StackPanel { Spacing = 0, MinWidth = 1220 };

        foreach (var onnxClass in OnnxClasses.All)
        {
            working.TryAdd(onnxClass.Id, []);
            var row = new Grid { ColumnSpacing = 18 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(900) });

            row.Children.Add(new TextBlock
            {
                Text = $"{onnxClass.Id} {onnxClass.DetailName}",
                VerticalAlignment = VerticalAlignment.Center
            });

            var checks = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
            for (var column = 0; column < 5; column++)
            {
                checks.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            }

            for (var rowIndex = 0; rowIndex < 3; rowIndex++)
            {
                checks.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            foreach (var cls in FaceClasses.Classifiable)
            {
                var checkBox = new CheckBox
                {
                    Content = cls.DisplayName,
                    IsChecked = working[onnxClass.Id].Contains(cls.Index),
                    Width = 176
                };
                checkBox.Checked += (_, _) => working[onnxClass.Id].Add(cls.Index);
                checkBox.Unchecked += (_, _) => working[onnxClass.Id].Remove(cls.Index);
                Grid.SetColumn(checkBox, cls.Index % 5);
                Grid.SetRow(checkBox, cls.Index / 5);
                checks.Children.Add(checkBox);
            }

            Grid.SetColumn(checks, 1);
            row.Children.Add(checks);
            panel.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Colors.DimGray),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 0, 0, 16),
                Margin = new Thickness(0, 0, 0, 16),
                Child = row
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = MainWindow.AppWindowInstance.Content.XamlRoot,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Title = "ONNX模型映射规则",
            Content = new ScrollViewer { Content = panel, MaxHeight = 620, MinWidth = 1220 },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            MinWidth = 1280,
            MaxWidth = 1400,
            Width = 1280
        };
        dialog.Resources["ContentDialogMinWidth"] = 1280d;
        dialog.Resources["ContentDialogMaxWidth"] = 1400d;

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _state.SaveOnnxMapping(working.ToDictionary(x => x.Key, x => x.Value.Order().ToList()));
        }
    }

    private void Settings_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _state.DifyBaseUrl = BaseUrlBox.Text.Trim();
        if (ApiKeyPlainBox.Visibility == Visibility.Visible) _state.DifyApiKey = ApiKeyPlainBox.Text;
        _state.SaveDifySettings();
        HealthDescription.Text = HealthDefaultText;
        ClearHealthStatus();
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _state.DifyApiKey = ApiKeyBox.Password;
        ApiKeyPlainBox.Text = ApiKeyBox.Password;
        _state.SaveDifySettings();
        HealthDescription.Text = HealthDefaultText;
        ClearHealthStatus();
    }

    private void ApiKeyVisible_Click(object sender, RoutedEventArgs e)
    {
        var visible = ApiKeyVisible.IsChecked == true;
        ApiKeyPlainBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyBox.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        if (visible) ApiKeyPlainBox.Text = ApiKeyBox.Password;
        else ApiKeyBox.Password = ApiKeyPlainBox.Text;
    }

    private async void Health_Click(object sender, RoutedEventArgs e)
    {
        _state.DifyBaseUrl = BaseUrlBox.Text.Trim();
        _state.DifyApiKey = ApiKeyPlainBox.Visibility == Visibility.Visible ? ApiKeyPlainBox.Text : ApiKeyBox.Password;
        _state.SaveDifySettings();
        HealthRing.IsActive = true;
        HealthDescription.Text = "测试中...";
        ClearHealthStatus();
        try
        {
            var name = await new DifyService().CheckHealthAsync(_state.DifyBaseUrl, _state.DifyApiKey);
            HealthDescription.Text = $"测试通过！工作流名称：{name}";
            SetHealthStatus(true);
        }
        catch (Exception ex)
        {
            HealthDescription.Text = $"测试失败：{ex.Message}";
            SetHealthStatus(false);
        }
        finally
        {
            HealthRing.IsActive = false;
        }
    }

    private void ClearHealthStatus()
    {
        HealthStatusIcon.Visibility = Visibility.Collapsed;
    }

    private void SetHealthStatus(bool success)
    {
        HealthStatusIcon.Glyph = success ? "\uE8FB" : "\uE711";
        HealthStatusIcon.Foreground = new SolidColorBrush(success ? Colors.ForestGreen : Colors.Firebrick);
        HealthStatusIcon.Visibility = Visibility.Visible;
    }
}

