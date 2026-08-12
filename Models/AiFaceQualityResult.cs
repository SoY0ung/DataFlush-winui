using System.Text.Json;
using DataFlushWinUI.Services;

namespace DataFlushWinUI.Models;

public sealed class AiFaceQualityResult
{
    public int ClassIndex { get; init; }

    public string ClassId { get; init; } = "8";

    public string PlatformName { get; init; } = "nonface";

    public string ClassName { get; init; } = "非人脸";

    public string Certainty { get; init; } = "低";

    public float Probability { get; init; }

    public float[] Logits { get; init; } = [];

    public float[] Probabilities { get; init; } = [];

    public AiResult ToAiResult() => new()
    {
        ClassId = ClassId,
        PlatformName = PlatformName,
        ClassName = ClassName,
        Certainty = Certainty,
        Probability = Probability,
        Info = $"本地 ONNX 模型推理结果，概率：{Probability:P2}"
    };

    public string ToJson()
    {
        var payload = new
        {
            class_id = ClassId,
            platform_name = PlatformName,
            class_name = ClassName,
            certainty = Certainty,
            probability = Probability,
            logits = Logits,
            probabilities = Probabilities
        };

        return JsonSerializer.Serialize(payload, AppState.JsonOptions);
    }
}
