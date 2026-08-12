namespace DataFlushWinUI.Services;

public sealed class ThresholdFilterStore
{
    private static readonly System.Text.RegularExpressions.Regex NewTrainRegex = new(@"^(?<onnx>[0-8])_(?<face>0[0-9]|1[0-4])_train\.csv$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex OldTrainRegex = new(@"^(?<onnx>[0-8])_train\.csv$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public List<string> Train { get; private set; } = [];

    public List<string> Valid { get; private set; } = [];

    public bool IsLocked(string csvPath, int onnxClassId, int faceClassIndex)
        => File.Exists(TrainPath(csvPath, onnxClassId, faceClassIndex)) && File.Exists(ValidPath(csvPath, onnxClassId, faceClassIndex));

    public static bool HasAnyLockedList(string csvPath)
        => TryGetLockedState(csvPath, out _, out _);

    public static bool TryGetLockedState(string csvPath, out int onnxClassId, out int faceClassIndex)
    {
        onnxClassId = -1;
        faceClassIndex = -1;
        if (string.IsNullOrWhiteSpace(csvPath) || !Directory.Exists(csvPath))
        {
            return false;
        }

        foreach (var trainPath in Directory.EnumerateFiles(csvPath, "*_train.csv"))
        {
            var fileName = Path.GetFileName(trainPath);
            var match = NewTrainRegex.Match(fileName);
            if (!match.Success)
            {
                continue;
            }

            var onnx = int.Parse(match.Groups["onnx"].Value);
            var face = int.Parse(match.Groups["face"].Value);
            if (File.Exists(ValidPath(csvPath, onnx, face)))
            {
                onnxClassId = onnx;
                faceClassIndex = face;
                return true;
            }
        }

        foreach (var trainPath in Directory.EnumerateFiles(csvPath, "*_train.csv"))
        {
            var fileName = Path.GetFileName(trainPath);
            var match = OldTrainRegex.Match(fileName);
            if (!match.Success)
            {
                continue;
            }

            var onnx = int.Parse(match.Groups["onnx"].Value);
            if (File.Exists(OldValidPath(csvPath, onnx)))
            {
                onnxClassId = onnx;
                return true;
            }
        }

        return false;
    }

    public static void MigrateOldFiles(string csvPath, int onnxClassId, int faceClassIndex)
    {
        var oldTrain = OldTrainPath(csvPath, onnxClassId);
        var oldValid = OldValidPath(csvPath, onnxClassId);
        var newTrain = TrainPath(csvPath, onnxClassId, faceClassIndex);
        var newValid = ValidPath(csvPath, onnxClassId, faceClassIndex);
        if (File.Exists(oldTrain) && !File.Exists(newTrain))
        {
            File.Move(oldTrain, newTrain);
        }

        if (File.Exists(oldValid) && !File.Exists(newValid))
        {
            File.Move(oldValid, newValid);
        }
    }

    public void Load(string csvPath, int onnxClassId, int faceClassIndex)
    {
        Train = ReadCsv(TrainPath(csvPath, onnxClassId, faceClassIndex));
        Valid = ReadCsv(ValidPath(csvPath, onnxClassId, faceClassIndex));
    }

    public void Lock(string csvPath, int onnxClassId, int faceClassIndex)
    {
        Directory.CreateDirectory(csvPath);
        var trainPath = TrainPath(csvPath, onnxClassId, faceClassIndex);
        var validPath = ValidPath(csvPath, onnxClassId, faceClassIndex);
        if (!File.Exists(trainPath))
        {
            WriteCsv(trainPath, []);
        }

        if (!File.Exists(validPath))
        {
            WriteCsv(validPath, []);
        }

        Load(csvPath, onnxClassId, faceClassIndex);
    }

    public void Unlock(string csvPath, int onnxClassId, int faceClassIndex)
    {
        Train.Clear();
        Valid.Clear();
        DeleteIfExists(TrainPath(csvPath, onnxClassId, faceClassIndex));
        DeleteIfExists(ValidPath(csvPath, onnxClassId, faceClassIndex));
        DeleteIfExists(OldTrainPath(csvPath, onnxClassId));
        DeleteIfExists(OldValidPath(csvPath, onnxClassId));
    }

    public bool Move(IReadOnlyList<string> fileNames, ThresholdListKind target, IReadOnlySet<string> allowedSource)
    {
        if (fileNames.Count == 0)
        {
            return false;
        }

        var wanted = fileNames
            .Where(allowedSource.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return false;
        }

        Train = Train.Where(x => !wanted.Contains(x)).ToList();
        Valid = Valid.Where(x => !wanted.Contains(x)).ToList();
        if (target == ThresholdListKind.Train)
        {
            AddUnique(Train, wanted);
        }
        else if (target == ThresholdListKind.Valid)
        {
            AddUnique(Valid, wanted);
        }

        return true;
    }

    public void Save(string csvPath, int onnxClassId, int faceClassIndex)
    {
        WriteCsv(TrainPath(csvPath, onnxClassId, faceClassIndex), Train);
        WriteCsv(ValidPath(csvPath, onnxClassId, faceClassIndex), Valid);
    }

    public static string TrainPath(string csvPath, int onnxClassId, int faceClassIndex) => Path.Combine(csvPath, $"{onnxClassId}_{faceClassIndex:00}_train.csv");

    public static string ValidPath(string csvPath, int onnxClassId, int faceClassIndex) => Path.Combine(csvPath, $"{onnxClassId}_{faceClassIndex:00}_valid.csv");

    private static string OldTrainPath(string csvPath, int onnxClassId) => Path.Combine(csvPath, $"{onnxClassId}_train.csv");

    private static string OldValidPath(string csvPath, int onnxClassId) => Path.Combine(csvPath, $"{onnxClassId}_valid.csv");

    private static void AddUnique(List<string> target, IEnumerable<string> fileNames)
    {
        var existing = target.ToHashSet(StringComparer.OrdinalIgnoreCase);
        target.AddRange(fileNames.Where(existing.Add).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    private static List<string> ReadCsv(string path)
        => File.Exists(path)
            ? File.ReadLines(path, System.Text.Encoding.UTF8)
                .Select(line => line.Trim().Trim('\uFEFF'))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Split(',')[0].Trim('"'))
                .ToList()
            : [];

    private static void WriteCsv(string path, IEnumerable<string> names)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllLines(temp, names.Select(EscapeCsv), new System.Text.UTF8Encoding(true));
        File.Move(temp, path, overwrite: true);
    }

    private static string EscapeCsv(string value) => value.Contains(',') || value.Contains('"')
        ? $"\"{value.Replace("\"", "\"\"")}\""
        : value;

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

public enum ThresholdListKind
{
    Unprocessed,
    Train,
    Valid
}
