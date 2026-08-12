using DataFlushWinUI.Models;
using DataFlushWinUI.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;

namespace DataFlushWinUI;

public sealed partial class PreviewWindow : Window
{
    private static readonly List<PreviewWindow> OpenWindows = [];

    private const int GwlStyle = -16;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsSysMenu = 0x00080000;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpFrameChanged = 0x0020;
    private const int HwndTopMost = -1;
    private const int HwndNoTopMost = -2;
    private const int InfoPanelWidth = 280;

    private bool _infoVisible;
    private bool _topMost;
    private bool _sizedToInitialImage;
    private bool _draggingWindow;
    private POINT _dragStartCursor;
    private PointInt32 _dragStartWindow;
    private int _contentWidth = 980;
    private int _contentHeight = 680;

    public PreviewWindow()
    {
        InitializeComponent();
        OpenWindows.Add(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(PreviewTitleBar);
        this.AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
        }
        DisableMaximizeButton();
        var closeAccelerator = new KeyboardAccelerator { Key = VirtualKey.Space };
        closeAccelerator.Invoked += (_, args) =>
        {
            Close();
            args.Handled = true;
        };
        WindowRoot.KeyboardAccelerators.Add(closeAccelerator);

        _infoVisible = AppState.Current.PreviewInfoVisible;
        ApplyInfoPanelVisibility(resizeWindow: false);
        if (_infoVisible)
        {
            _contentWidth += InfoPanelWidth;
        }
        AppState.Current.PreviewInfoVisibleChanged += AppState_PreviewInfoVisibleChanged;
        Closed += (_, _) =>
        {
            AppState.Current.PreviewInfoVisibleChanged -= AppState_PreviewInfoVisibleChanged;
            OpenWindows.Remove(this);
        };
        this.AppWindow.Resize(new SizeInt32(_contentWidth, _contentHeight));
        this.AppWindow.MoveAndResize(new RectInt32(120, 80, _contentWidth, _contentHeight));
        _topMost = true;
        TopMostButton.IsChecked = true;
        SetTopMost(true);
    }

    public static void CloseAllOpenWindows()
    {
        foreach (var window in OpenWindows.ToArray())
        {
            window.Close();
        }
    }

    public void ShowImage(ImageItem item)
    {
        Title = item.FileName;
        PreviewTitleText.Text = TrimMiddle(item.FileName);
        ToolTipService.SetToolTip(PreviewTitleText, item.FileName);
        FileNameText.Text = item.FileName;
        if (item.ImageUri is null)
        {
            PreviewImage.Source = null;
        }
        else
        {
            var bitmap = new BitmapImage(item.ImageUri);
            PreviewImage.Source = bitmap;
            bitmap.ImageOpened += (_, _) =>
            {
                if (!_sizedToInitialImage)
                {
                    ResizeToImage(bitmap.PixelWidth, bitmap.PixelHeight);
                    _sizedToInitialImage = true;
                }
            };
        }

        if (item.AiPayload?.Result is { } result)
        {
            AiClassText.Text = $"{result.ClassId} {result.ClassName}";
            AiCertaintyText.Text = $"置信度：{result.Certainty}";
            AiInfoText.Text = result.Info;
        }
        else
        {
            AiClassText.Text = "AI处理中，请等待";
            AiCertaintyText.Text = "";
            AiInfoText.Text = "AI处理中，请等待";
        }
    }

    private void Window_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Space)
        {
            Close();
        }
        else if (e.Key == Windows.System.VirtualKey.F)
        {
            ToggleInfoPanel();
        }
    }

    private static string TrimMiddle(string text)
    {
        const int maxLength = 72;
        const string ellipsis = "...";
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        var keep = maxLength - ellipsis.Length;
        var left = keep / 2;
        var right = keep - left;
        return $"{text[..left]}{ellipsis}{text[^right..]}";
    }

    private void ImageDragSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(ImageDragSurface).Properties.IsLeftButtonPressed)
        {
            _draggingWindow = true;
            GetCursorPos(out _dragStartCursor);
            _dragStartWindow = AppWindow.Position;
            ImageDragSurface.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void ImageDragSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingWindow)
        {
            return;
        }

        GetCursorPos(out var current);
        var dx = current.X - _dragStartCursor.X;
        var dy = current.Y - _dragStartCursor.Y;
        AppWindow.Move(new PointInt32(_dragStartWindow.X + dx, _dragStartWindow.Y + dy));
        e.Handled = true;
    }

    private void ImageDragSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e.Pointer);
    }

    private void ImageDragSurface_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e.Pointer);
    }

    private void EndWindowDrag(Pointer pointer)
    {
        if (!_draggingWindow)
        {
            return;
        }

        _draggingWindow = false;
        ImageDragSurface.ReleasePointerCapture(pointer);
    }

    private void RootGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta;
        var factor = delta > 0 ? 1.08 : 0.92;
        ResizeWindowByFactor(factor);
        e.Handled = true;
    }

    private void TopMostButton_Click(object sender, RoutedEventArgs e)
    {
        _topMost = TopMostButton.IsChecked == true;
        SetTopMost(_topMost);
    }

    private void ResizeToImage(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return;
        }

        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var maxWidth = Math.Max(420, displayArea.WorkArea.Width - 160);
        var maxHeight = Math.Max(320, displayArea.WorkArea.Height - 160);
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / pixelWidth, (double)maxHeight / pixelHeight));
        var imageWidth = Math.Max(320, (int)Math.Round(pixelWidth * scale));
        _contentWidth = imageWidth + (_infoVisible ? InfoPanelWidth : 0);
        _contentHeight = Math.Max(240, (int)Math.Round(pixelHeight * scale));
        AppWindow.Resize(new SizeInt32(_contentWidth, _contentHeight));
    }

    private void ResizeWindowByFactor(double factor)
    {
        _contentWidth = Math.Clamp((int)Math.Round(AppWindow.Size.Width * factor), 240, 2400);
        _contentHeight = Math.Clamp((int)Math.Round(AppWindow.Size.Height * factor), 180, 1800);
        AppWindow.Resize(new SizeInt32(_contentWidth, _contentHeight));
    }

    private void ToggleInfoPanel()
        => AppState.Current.SetPreviewInfoVisible(!_infoVisible);

    private void AppState_PreviewInfoVisibleChanged(object? sender, bool visible)
    {
        if (_infoVisible == visible)
        {
            return;
        }

        var oldVisible = _infoVisible;
        _infoVisible = visible;
        ApplyInfoPanelVisibility(resizeWindow: true, oldVisible);
    }

    private void ApplyInfoPanelVisibility(bool resizeWindow, bool? previousVisible = null)
    {
        InfoColumn.Width = _infoVisible ? new GridLength(InfoPanelWidth) : new GridLength(0);
        if (!resizeWindow)
        {
            return;
        }

        var wasVisible = previousVisible ?? !_infoVisible;
        if (wasVisible == _infoVisible)
        {
            return;
        }

        var delta = _infoVisible ? InfoPanelWidth : -InfoPanelWidth;
        _contentWidth = Math.Max(240, AppWindow.Size.Width + delta);
        _contentHeight = AppWindow.Size.Height;
        AppWindow.Resize(new SizeInt32(_contentWidth, _contentHeight));
    }

    private void DisableMaximizeButton()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style & ~WsMaximizeBox));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
    }

    private void SetTopMost(bool topMost)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetWindowPos(hwnd, new IntPtr(topMost ? HwndTopMost : HwndNoTopMost), 0, 0, 0, 0, SwpNoMove | SwpNoSize);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
