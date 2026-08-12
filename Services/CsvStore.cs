using DataFlushWinUI.Models;

namespace DataFlushWinUI.Services;

public sealed class CsvStore
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };

    public string DatasetPath { get; private set; } = "";

    public string CsvPath { get; private set; } = "";

    public List<string>[] Items { get; private set; } = Enumerable.Range(0, FaceClasses.All.Count).Select(_ => new List<string>()).ToArray();

    public bool IsLoaded { get; private set; }

    public int DatasetImageTotal { get; private set; }

    public void Configure(string datasetPath, string csvPath)
    {
        DatasetPath = datasetPath;
        CsvPath = csvPath;
    }

    public void Clear()
    {
        Items = Enumerable.Range(0, FaceClasses.All.Count).Select(_ => new List<string>()).ToArray();
        IsLoaded = false;
        DatasetImageTotal = 0;
    }

    public string ClassCsvPath(int classIndex) => Path.Combine(CsvPath, FaceClasses.ByIndex(classIndex).CsvFileName);

    public bool HasCompleteCsvSet()
    {
        if (!Directory.Exists(CsvPath))
        {
            return false;
        }

        if (ThresholdFilterStore.HasAnyLockedList(CsvPath))
        {
            return File.Exists(ClassCsvPath(15));
        }

        return FaceClasses.Core.All(c => File.Exists(ClassCsvPath(c.Index)));
    }

    public bool IsHatExtensionEnabled => FaceClasses.IsHatEnabled(CsvPath);

    public IReadOnlyList<string> FindMissingDatasetFiles(int maxCount = 20)
    {
        if (!Directory.Exists(DatasetPath))
        {
            throw new InvalidOperationException("数据集目录不存在。");
        }

        var datasetFiles = Directory.EnumerateFiles(DatasetPath)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var names = ThresholdFilterStore.TryGetLockedState(CsvPath, out var onnxClassId, out var faceClassIndex)
            ? ThresholdDatasetFiles(onnxClassId, faceClassIndex)
            : Items.SelectMany(x => x);

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !datasetFiles.Contains(name))
            .Take(maxCount)
            .ToList();
    }

    public void Initialize()
    {
        if (!Directory.Exists(DatasetPath))
        {
            throw new InvalidOperationException("数据集目录不存在。");
        }

        Directory.CreateDirectory(CsvPath);
        Items = Enumerable.Range(0, FaceClasses.All.Count).Select(_ => new List<string>()).ToArray();
        Items[15] = Directory.EnumerateFiles(DatasetPath)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        DatasetImageTotal = Items[15].Count;
        SaveCore();
        IsLoaded = true;
    }

    public void Load()
    {
        if (!Directory.Exists(CsvPath))
        {
            throw new InvalidOperationException("CSV目录不存在。");
        }

        var thresholdLocked = ThresholdFilterStore.HasAnyLockedList(CsvPath);
        var loaded = new List<string>[FaceClasses.All.Count];
        for (var i = 0; i < FaceClasses.All.Count; i++)
        {
            var path = ClassCsvPath(i);
            if (thresholdLocked && i < 15)
            {
                loaded[i] = [];
                continue;
            }

            if (!File.Exists(path) && i < FaceClasses.Core.Count)
            {
                throw new FileNotFoundException($"缺少CSV文件：{path}");
            }

            loaded[i] = File.Exists(path) ? ReadCsv(path) : [];
        }

        Items = loaded;
        DatasetImageTotal = thresholdLocked && ThresholdFilterStore.TryGetLockedState(CsvPath, out var onnxClassId, out var faceClassIndex)
            ? ThresholdDatasetFiles(onnxClassId, faceClassIndex).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            : Items.SelectMany(x => x).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        IsLoaded = true;
    }

    public MoveRecord? Move(int source, int target, IReadOnlyList<string> fileNames)
    {
        if (source == target || fileNames.Count == 0)
        {
            return null;
        }

        var wanted = fileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moved = Items[source].Where(wanted.Contains).ToList();
        if (moved.Count == 0)
        {
            return null;
        }

        var movedSet = moved.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Items[source] = Items[source].Where(name => !movedSet.Contains(name)).ToList();

        var existing = Items[target].ToHashSet(StringComparer.OrdinalIgnoreCase);
        Items[target].AddRange(moved.Where(name => !existing.Contains(name)));
        SaveClasses(source, target);
        return new MoveRecord(source, target, moved);
    }

    public void Undo(MoveRecord record)
    {
        var moved = record.FileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Items[record.Target] = Items[record.Target].Where(name => !moved.Contains(name)).ToList();

        var existing = Items[record.Source].ToHashSet(StringComparer.OrdinalIgnoreCase);
        Items[record.Source].AddRange(record.FileNames.Where(name => !existing.Contains(name)));
        SaveClasses(record.Source, record.Target);
    }

    public void RemoveFromUnprocessed(IReadOnlyList<string> fileNames)
    {
        if (fileNames.Count == 0 || Items.Length <= 15)
        {
            return;
        }

        var moved = fileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Items[15] = Items[15].Where(name => !moved.Contains(name)).ToList();
        SaveClasses(15);
    }

    public void AddToUnprocessed(IReadOnlyList<string> fileNames)
    {
        if (fileNames.Count == 0 || Items.Length <= 15)
        {
            return;
        }

        var existing = Items[15].ToHashSet(StringComparer.OrdinalIgnoreCase);
        Items[15].AddRange(fileNames.Where(existing.Add).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        SaveClasses(15);
    }

    public void EnableHatExtension()
    {
        if (string.IsNullOrWhiteSpace(CsvPath))
        {
            throw new InvalidOperationException("CSV目录不存在。");
        }

        Directory.CreateDirectory(CsvPath);
        EnsureItemCapacity();
        foreach (var cls in FaceClasses.HatExtension)
        {
            if (!File.Exists(ClassCsvPath(cls.Index)))
            {
                Items[cls.Index] = [];
                WriteCsv(ClassCsvPath(cls.Index), Items[cls.Index]);
            }
        }
    }

    public void DisableHatExtension()
    {
        EnsureItemCapacity();
        foreach (var hatClass in FaceClasses.HatExtension)
        {
            var original = hatClass.Index switch
            {
                16 => 0,
                17 => 1,
                18 => 5,
                _ => 15
            };

            var existing = Items[original].ToHashSet(StringComparer.OrdinalIgnoreCase);
            Items[original].AddRange(Items[hatClass.Index].Where(name => !existing.Contains(name)));
            Items[hatClass.Index].Clear();
            if (File.Exists(ClassCsvPath(hatClass.Index)))
            {
                File.Delete(ClassCsvPath(hatClass.Index));
            }

            SaveClasses(original);
        }
    }

    public void DeleteClassifiableCsvFiles()
    {
        Directory.CreateDirectory(CsvPath);
        EnsureItemCapacity();
        for (var i = 0; i < 15; i++)
        {
            Items[i].Clear();
            var path = ClassCsvPath(i);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public void RecreateClassifiableCsvFiles()
    {
        Directory.CreateDirectory(CsvPath);
        EnsureItemCapacity();
        for (var i = 0; i < 15; i++)
        {
            Items[i].Clear();
            WriteCsv(ClassCsvPath(i), Items[i]);
        }
    }

    private void SaveCore()
    {
        Directory.CreateDirectory(CsvPath);
        for (var i = 0; i < FaceClasses.Core.Count; i++)
        {
            WriteCsv(ClassCsvPath(i), Items[i]);
        }
    }

    private void SaveClasses(params int[] indices)
    {
        Directory.CreateDirectory(CsvPath);
        foreach (var index in indices.Distinct())
        {
            WriteCsv(ClassCsvPath(index), Items[index]);
        }
    }

    private IEnumerable<string> ThresholdDatasetFiles(int onnxClassId, int faceClassIndex)
    {
        foreach (var name in Items[15])
        {
            yield return name;
        }

        if (faceClassIndex is < 0 or > 14)
        {
            yield break;
        }

        var thresholdStore = new ThresholdFilterStore();
        thresholdStore.Load(CsvPath, onnxClassId, faceClassIndex);
        foreach (var name in thresholdStore.Train)
        {
            yield return name;
        }

        foreach (var name in thresholdStore.Valid)
        {
            yield return name;
        }
    }

    private void EnsureItemCapacity()
    {
        if (Items.Length == FaceClasses.All.Count)
        {
            return;
        }

        var resized = Enumerable.Range(0, FaceClasses.All.Count).Select(_ => new List<string>()).ToArray();
        for (var i = 0; i < Math.Min(Items.Length, resized.Length); i++)
        {
            resized[i] = Items[i];
        }

        Items = resized;
    }

    private static List<string> ReadCsv(string path) => File.ReadLines(path, System.Text.Encoding.UTF8)
        .Select(line => line.Trim().Trim('\uFEFF'))
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => line.Split(',')[0].Trim('"'))
        .ToList();

    private static void WriteCsv(string path, IEnumerable<string> names)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllLines(temp, names.Select(EscapeCsv), new System.Text.UTF8Encoding(true));
        File.Move(temp, path, overwrite: true);
    }

    private static string EscapeCsv(string value) => value.Contains(',') || value.Contains('"')
        ? $"\"{value.Replace("\"", "\"\"")}\""
        : value;
}
