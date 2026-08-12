using System.Text;
using System.Text.Json;
using DataFlushWinUI.Models;

namespace DataFlushWinUI.Services.Ai;

public sealed class LocalOnnxBatchInferenceService : IDisposable
{
    public static LocalOnnxBatchInferenceService Current { get; } = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private FaceQualityOnnxClassifier? _classifier;
    private string _classifierModelPath = "";
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private volatile bool _paused;
    private bool _disposed;

    private LocalOnnxBatchInferenceService()
    {
    }

    public bool IsRunning { get; private set; }

    public bool IsPaused => _paused;

    public event EventHandler<string>? Message;

    public event EventHandler<LocalOnnxProgress>? ProgressChanged;

    public event EventHandler<bool>? PauseChanged;

    public bool CanStart(AppState state)
        => !IsRunning
           && state.Store.IsLoaded
           && Directory.Exists(state.DatasetPath)
           && Directory.Exists(state.CsvPath)
           && !string.IsNullOrWhiteSpace(state.LocalModelPath)
           && File.Exists(state.LocalModelPath);

    public async Task<bool> StartAsync(AppState state, CancellationToken cancellationToken = default)
    {
        if (!CanStart(state))
        {
            return false;
        }

        var job = LocalOnnxJob.FromState(state);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanStart(state))
            {
                return false;
            }

            _paused = false;
            IsRunning = true;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        _runTask = Task.Run(() => RunAsync(state, job, _runCts.Token), CancellationToken.None);
        return true;
    }

    public async Task StopAsync()
    {
        Task? task;
        await _gate.WaitAsync();
        try
        {
            if (!IsRunning || _runCts is null)
            {
                return;
            }

            _runCts.Cancel();
            task = _runTask;
        }
        finally
        {
            _gate.Release();
        }

        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async Task<bool> RestartAsync(AppState state, CancellationToken cancellationToken = default)
    {
        await StopAsync();
        return await StartAsync(state, cancellationToken);
    }

    private async Task RunAsync(AppState state, LocalOnnxJob job, CancellationToken cancellationToken)
    {
        try
        {
            var classifier = GetClassifier(job.ModelPath);
            Message?.Invoke(this, "本地 ONNX 后台推理已启动。");

            var all = job.Queue;
            var total = all.Count;
            var done = all.Count(x => File.Exists(DifyService.AiJsonPath(job.CsvPath, x)));
            ProgressChanged?.Invoke(this, new LocalOnnxProgress(done, total));

            foreach (var filename in all)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (_paused)
                {
                    await Task.Delay(250, cancellationToken);
                }

                var outputPath = DifyService.AiJsonPath(job.CsvPath, filename);
                if (File.Exists(outputPath))
                {
                    continue;
                }

                try
                {
                    var imagePath = Path.Combine(job.DatasetPath, filename);
                    var result = await classifier.PredictAsync(imagePath, cancellationToken);
                    var payload = new AiPayload
                    {
                        FileName = filename,
                        Result = result.ToAiResult(),
                        Raw = JsonSerializer.Deserialize<object>(result.ToJson())
                    };

                    await WriteAiPayloadAsync(outputPath, payload, cancellationToken);
                    state.AiResults[filename] = payload;
                }
                catch (Exception ex)
                {
                    Message?.Invoke(this, $"{filename}: {ex.Message}");
                }

                done++;
                ProgressChanged?.Invoke(this, new LocalOnnxProgress(done, total));
            }

            Message?.Invoke(this, "本地 ONNX 后台推理完成。");
        }
        catch (OperationCanceledException)
        {
            Message?.Invoke(this, "本地 ONNX 后台推理已取消。");
        }
        catch (Exception ex)
        {
            Message?.Invoke(this, $"本地 ONNX 后台推理失败：{ex.Message}");
        }
        finally
        {
            _paused = false;
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
            _runTask = null;
            PauseChanged?.Invoke(this, false);
        }
    }

    public bool TogglePause()
    {
        if (!IsRunning)
        {
            return false;
        }

        _paused = !_paused;
        PauseChanged?.Invoke(this, _paused);
        Message?.Invoke(this, _paused ? "本地 ONNX 后台推理已暂停。" : "本地 ONNX 后台推理已继续。");
        return _paused;
    }

    private FaceQualityOnnxClassifier GetClassifier(string modelPath)
    {
        if (_classifier is not null && string.Equals(_classifierModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            return _classifier;
        }

        _classifier?.Dispose();
        _classifier = new FaceQualityOnnxClassifier(modelPath);
        _classifierModelPath = modelPath;
        return _classifier;
    }

    private static async Task WriteAiPayloadAsync(string outputPath, AiPayload payload, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temp = $"{outputPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(payload, AppState.JsonOptions), Encoding.UTF8, cancellationToken);
        File.Move(temp, outputPath, overwrite: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _classifier?.Dispose();
        _gate.Dispose();
        _disposed = true;
    }
}

public sealed record LocalOnnxProgress(int Done, int Total);

internal sealed record LocalOnnxJob(string DatasetPath, string CsvPath, string ModelPath, List<string> Queue)
{
    public static LocalOnnxJob FromState(AppState state)
        => new(state.DatasetPath, state.CsvPath, state.LocalModelPath, BuildQueue(state));

    private static List<string> BuildQueue(AppState state)
    {
        var queued = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (state.AiPriorityQueue.Count > 0)
        {
            var filename = state.AiPriorityQueue.Dequeue();
            if (seen.Add(filename))
            {
                queued.Add(filename);
            }
        }

        foreach (var filename in state.Store.Items.SelectMany(x => x).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(filename))
            {
                queued.Add(filename);
            }
        }

        return queued;
    }
}
