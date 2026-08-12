using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using DataFlushWinUI.Models;
using Windows.Storage;

namespace DataFlushWinUI.Services;

public sealed class AppState
{
    public static AppState Current { get; } = new();

    private readonly ISettingsStore _settings = CreateSettingsStore();

    private AppState()
    {
        DatasetPath = ReadSetting("dataset_dir");
        CsvPath = ReadSetting("csv_dir");
        DifyBaseUrl = ReadSetting("dify_base_url");
        DifyApiKey = ReadSetting("dify_api_key");
        LocalModelPath = ReadSetting("local_model_path");
        AutoStartOnnxInference = bool.TryParse(ReadSetting("auto_start_onnx_inference", "true"), out var autoStartOnnx) ? autoStartOnnx : true;
        UseOnnxMeanStdPreprocessing = bool.TryParse(ReadSetting("use_onnx_mean_std_preprocessing", "true"), out var useMeanStd) ? useMeanStd : true;
        AiProbabilityThresholdPercent = double.TryParse(ReadSetting("ai_probability_threshold_percent", "85"), NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
            ? Math.Clamp(threshold, 0, 100)
            : 85;
        ShowOperationTips = bool.TryParse(ReadSetting("show_operation_tips", "true"), out var showOperationTips) ? showOperationTips : true;
        PreviewInfoVisible = bool.TryParse(ReadSetting("preview_info_visible"), out var previewInfoVisible) && previewInfoVisible;
        ShowHatClassesInCleanPage = bool.TryParse(ReadSetting("show_hat_classes_in_clean_page"), out var showHatClasses) && showHatClasses;
        OnnxMapping = ReadOnnxMapping();
    }

    public CsvStore Store { get; } = new();

    public ObservableCollection<ImageItem> CurrentItems { get; } = [];

    public ConcurrentDictionary<string, AiPayload> AiResults { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Queue<string> AiPriorityQueue { get; } = new();

    public MoveRecord? UndoRecord { get; set; }

    public string DatasetPath { get; private set; }

    public string CsvPath { get; private set; }

    public string DifyBaseUrl { get; set; }

    public string DifyApiKey { get; set; }

    public string LocalModelPath { get; private set; }

    public bool AutoStartOnnxInference { get; private set; }

    public bool UseOnnxMeanStdPreprocessing { get; private set; }

    public double AiProbabilityThresholdPercent { get; private set; }

    public bool ShowOperationTips { get; set; }

    public bool PreviewInfoVisible { get; private set; }

    public bool ShowHatClassesInCleanPage { get; private set; }

    public Dictionary<int, List<int>> OnnxMapping { get; private set; }

    public event EventHandler? DataChanged;

    public event EventHandler<bool>? PreviewInfoVisibleChanged;

    public event EventHandler? HatSettingsChanged;

    public void SetDatasetPath(string path, bool resetCsvPath = false)
    {
        DatasetPath = path;
        WriteSetting("dataset_dir", path);
        if (resetCsvPath)
        {
            CsvPath = "";
            WriteSetting("csv_dir", "");
            UndoRecord = null;
            AiResults.Clear();
            AiPriorityQueue.Clear();
            Store.Clear();
        }

        Store.Configure(DatasetPath, CsvPath);
        NotifyDataChanged();
    }

    public void SetCsvPath(string path)
    {
        CsvPath = path;
        WriteSetting("csv_dir", path);
        Store.Configure(DatasetPath, CsvPath);
        NotifyDataChanged();
    }

    public void SaveDifySettings()
    {
        WriteSetting("dify_base_url", DifyBaseUrl);
        WriteSetting("dify_api_key", DifyApiKey);
    }

    public void SetLocalModelPath(string path)
    {
        LocalModelPath = path;
        WriteSetting("local_model_path", path);
    }

    public void SetAutoStartOnnxInference(bool enabled)
    {
        AutoStartOnnxInference = enabled;
        WriteSetting("auto_start_onnx_inference", enabled.ToString());
    }

    public void SetUseOnnxMeanStdPreprocessing(bool enabled)
    {
        UseOnnxMeanStdPreprocessing = enabled;
        WriteSetting("use_onnx_mean_std_preprocessing", enabled.ToString());
    }

    public void SetAiProbabilityThresholdPercent(double thresholdPercent)
    {
        AiProbabilityThresholdPercent = Math.Clamp(thresholdPercent, 0, 100);
        WriteSetting("ai_probability_threshold_percent", AiProbabilityThresholdPercent.ToString("0.###", CultureInfo.InvariantCulture));
    }

    public void SaveOnnxMapping(Dictionary<int, List<int>> mapping)
    {
        OnnxMapping = mapping;
        WriteSetting("onnx_mapping", System.Text.Json.JsonSerializer.Serialize(mapping));
    }

    public void SaveGeneralSettings()
    {
        WriteSetting("show_operation_tips", ShowOperationTips.ToString());
    }

    public void SetPreviewInfoVisible(bool visible)
    {
        if (PreviewInfoVisible == visible)
        {
            return;
        }

        PreviewInfoVisible = visible;
        WriteSetting("preview_info_visible", visible.ToString());
        PreviewInfoVisibleChanged?.Invoke(this, visible);
    }

    public bool IsHatClassificationEnabled => Store.IsHatExtensionEnabled;

    public bool IsThresholdFilterEnabled => ThresholdFilterStore.HasAnyLockedList(CsvPath);

    public void SetShowHatClassesInCleanPage(bool visible)
    {
        if (!IsHatClassificationEnabled)
        {
            visible = false;
        }

        if (ShowHatClassesInCleanPage == visible)
        {
            return;
        }

        ShowHatClassesInCleanPage = visible;
        WriteSetting("show_hat_classes_in_clean_page", visible.ToString());
        HatSettingsChanged?.Invoke(this, EventArgs.Empty);
        NotifyDataChanged();
    }

    public void NotifyHatSettingsChanged()
    {
        if (!IsHatClassificationEnabled && ShowHatClassesInCleanPage)
        {
            ShowHatClassesInCleanPage = false;
            WriteSetting("show_hat_classes_in_clean_page", false.ToString());
        }

        HatSettingsChanged?.Invoke(this, EventArgs.Empty);
        NotifyDataChanged();
    }

    public string ReadLocalSetting(string key, string fallback = "") => ReadSetting(key, fallback);

    public void WriteLocalSetting(string key, string value) => WriteSetting(key, value);

    public void NotifyDataChanged() => DataChanged?.Invoke(this, EventArgs.Empty);

    public void LoadAiResults()
    {
        var results = ReadAiResultsFromDisk();
        AiResults.Clear();
        foreach (var (fileName, payload) in results)
        {
            AiResults[fileName] = payload;
        }
    }

    public Dictionary<string, AiPayload> ReadAiResultsFromDisk()
    {
        var results = new Dictionary<string, AiPayload>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(CsvPath))
        {
            return results;
        }

        var dir = Path.Combine(CsvPath, "AI_landmark");
        if (!Directory.Exists(dir))
        {
            return results;
        }

        foreach (var jsonPath in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var root = document.RootElement;
                var fileName = root.TryGetProperty("filename", out var filenameElement)
                    ? filenameElement.GetString() ?? ""
                    : Path.GetFileNameWithoutExtension(jsonPath);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                var result = new AiResult();
                var hasRaw = root.TryGetProperty("raw", out var rawElement);
                var raw = hasRaw ? JsonSerializer.Deserialize<object>(rawElement.GetRawText()) : null;
                if (root.TryGetProperty("result", out var resultElement) && resultElement.ValueKind == JsonValueKind.Object)
                {
                    result.ClassId = ReadJsonString(resultElement, "class_id", "14");
                    result.ClassName = ReadJsonString(resultElement, "class_name", FaceClasses.ByIndex(FaceClasses.IndexFromCode(result.ClassId) ?? 14).Name);
                    result.PlatformName = ReadJsonString(resultElement, "platform_name", "");
                    result.Certainty = ReadJsonString(resultElement, "certainty", "中");
                    result.Probability = ReadJsonFloat(resultElement, "probability");
                    result.Info = ReadJsonString(resultElement, "info", "");
                }

                if (result.Probability is null && hasRaw && rawElement.ValueKind == JsonValueKind.Object)
                {
                    result.Probability = ReadJsonFloat(rawElement, "probability");
                }

                results[fileName] = new AiPayload
                {
                    FileName = fileName,
                    Result = result,
                    Raw = raw
                };
            }
            catch
            {
                // Broken cache files are ignored so the rest of the dataset remains usable.
            }
        }

        return results;
    }

    public void PromoteAi(IEnumerable<string> fileNames)
    {
        foreach (var fileName in fileNames.Reverse().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (AiResults.ContainsKey(fileName))
            {
                continue;
            }

            var path = DifyService.AiJsonPath(CsvPath, fileName);
            if (File.Exists(path))
            {
                continue;
            }

            AiPriorityQueue.Enqueue(fileName);
        }
    }

    public static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    private string ReadSetting(string key, string fallback = "") => _settings.TryGetValue(key, out var value) ? value : fallback;

    private void WriteSetting(string key, string value) => _settings.SetValue(key, value);

    private static ISettingsStore CreateSettingsStore()
    {
        try
        {
            return new ApplicationDataSettingsStore(ApplicationData.Current.LocalSettings);
        }
        catch
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DataFlushWinUI",
                "settings.json");
            return new JsonSettingsStore(path);
        }
    }

    private Dictionary<int, List<int>> ReadOnnxMapping()
    {
        var fallback = OnnxClasses.DefaultMapping.ToDictionary(x => x.Key, x => x.Value.ToList());
        try
        {
            var json = ReadSetting("onnx_mapping");
            if (string.IsNullOrWhiteSpace(json))
            {
                return fallback;
            }

            var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, List<int>>>(json);
            if (loaded is null)
            {
                return fallback;
            }

            foreach (var onnxClass in OnnxClasses.All)
            {
                if (!loaded.TryGetValue(onnxClass.Id, out var targets) || targets.Count == 0)
                {
                    loaded[onnxClass.Id] = fallback[onnxClass.Id];
                }
                else
                {
                    loaded[onnxClass.Id] = targets.Where(i => i >= 0 && i < 15).Distinct().ToList();
                }
            }

            return loaded;
        }
        catch
        {
            return fallback;
        }
    }

    private static string ReadJsonString(JsonElement element, string name, string fallback)
        => element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString()
            : fallback;

    private static float? ReadJsonFloat(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            float.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private interface ISettingsStore
    {
        bool TryGetValue(string key, out string value);

        void SetValue(string key, string value);
    }

    private sealed class ApplicationDataSettingsStore(ApplicationDataContainer settings) : ISettingsStore
    {
        public bool TryGetValue(string key, out string value)
        {
            if (settings.Values.TryGetValue(key, out var raw) && raw is not null)
            {
                value = raw.ToString() ?? "";
                return true;
            }

            value = "";
            return false;
        }

        public void SetValue(string key, string value) => settings.Values[key] = value;
    }

    private sealed class JsonSettingsStore : ISettingsStore
    {
        private readonly string _path;
        private readonly object _gate = new();
        private Dictionary<string, string> _values;

        public JsonSettingsStore(string path)
        {
            _path = path;
            _values = Load(path);
        }

        public bool TryGetValue(string key, out string value)
        {
            lock (_gate)
            {
                return _values.TryGetValue(key, out value!);
            }
        }

        public void SetValue(string key, string value)
        {
            lock (_gate)
            {
                _values[key] = value;
                Save();
            }
        }

        private static Dictionary<string, string> Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                return loaded is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void Save()
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(_values, JsonOptions));
        }
    }
}

