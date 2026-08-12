namespace DataFlushWinUI.Models;

public sealed record FaceClass(int Index, string Code, string Name, string CsvFileName)
{
    public string DisplayName => $"{Code} {Name}";
}

