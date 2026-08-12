namespace DataFlushWinUI.Models;

public sealed class AiResult
{
    public string ClassId { get; set; } = "14";

    public string ClassName { get; set; } = "非人脸";

    public string PlatformName { get; set; } = "";

    public string Certainty { get; set; } = "中";

    public float? Probability { get; set; }

    public string Info { get; set; } = "";
}

public sealed class AiPayload
{
    public string FileName { get; set; } = "";

    public AiResult Result { get; set; } = new();

    public object? Raw { get; set; }
}
