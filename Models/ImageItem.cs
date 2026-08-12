using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DataFlushWinUI.Services;

namespace DataFlushWinUI.Models;

public sealed class ImageItem : INotifyPropertyChanged
{
    public ImageItem(string fileName, int classIndex)
    {
        FileName = fileName;
        ClassIndex = classIndex;
    }

    public string FileName { get; }

    public int ClassIndex { get; set; }

    public string ImagePath => AppState.Current.DatasetPath is { Length: > 0 } dataset
        ? System.IO.Path.Combine(dataset, FileName)
        : "";

    public Uri? ImageUri => ImagePath.Length > 0 ? new Uri(ImagePath) : null;

    public AiPayload? AiPayload => AppState.Current.AiResults.TryGetValue(FileName, out var payload) ? payload : null;

    public double ThumbnailWidth { get; private set; } = 132;

    public double ThumbnailHeight { get; private set; } = 104;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetThumbnailSize(double width)
    {
        ThumbnailWidth = width;
        ThumbnailHeight = Math.Max(56, width * 0.78);
        OnPropertyChanged(nameof(ThumbnailWidth));
        OnPropertyChanged(nameof(ThumbnailHeight));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
