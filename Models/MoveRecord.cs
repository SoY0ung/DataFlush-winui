namespace DataFlushWinUI.Models;

public sealed record MoveRecord(int Source, int Target, IReadOnlyList<string> FileNames);

