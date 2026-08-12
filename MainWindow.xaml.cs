using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using DataFlushWinUI.Pages;
using DataFlushWinUI.Services;
using System.Runtime.InteropServices;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace DataFlushWinUI;

public sealed partial class MainWindow : Window
{
    public static MainWindow AppWindowInstance { get; private set; } = null!;

    public MainWindow()
    {
        AppWindowInstance = this;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
        if (NavView.SettingsItem is FrameworkElement settingsItem)
        {
            ToolTipService.SetToolTip(settingsItem, "设置");
        }
        AppState.Current.DataChanged += (_, _) => UpdateWindowTitle();
        UpdateWindowTitle();
        WindowRoot.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(WindowRoot_KeyDown), true);
        Closed += (_, _) => PreviewWindow.CloseAllOpenWindows();
        MaximizeMainWindow();
        NavFrame.Navigate(typeof(CleanPage));
    }

    public Task<string?> PickFolderAsync(string suggestedPath = "", string title = "")
    {
        var dialog = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(FileOpenDialogClsid)!)!;
        dialog.GetOptions(out var options);
        dialog.SetOptions(options | FOS.PickFolders | FOS.ForceFileSystem | FOS.PathMustExist);
        if (!string.IsNullOrWhiteSpace(title))
        {
            dialog.SetTitle(title);
        }

        if (Directory.Exists(suggestedPath) &&
            SHCreateItemFromParsingName(suggestedPath, IntPtr.Zero, typeof(IShellItem).GUID, out var folderItem) == 0)
        {
            dialog.SetFolder(folderItem);
        }

        var hr = dialog.Show(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (hr == HResultCancelled)
        {
            return Task.FromResult<string?>(null);
        }

        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        dialog.GetResult(out var result);
        result.GetDisplayName(SIGDN.FileSysPath, out var pathPtr);
        try
        {
            return Task.FromResult(Marshal.PtrToStringUni(pathPtr));
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    public async Task<string?> PickFileAsync(params string[] fileTypes)
    {
        var picker = new FileOpenPicker();
        foreach (var fileType in fileTypes.Length == 0 ? ["*"] : fileTypes)
        {
            picker.FileTypeFilter.Add(fileType);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "确定"
        };
        await dialog.ShowAsync();
    }

    public async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static void OpenInExplorer(string path)
    {
        if (Directory.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
    }

    private void UpdateWindowTitle()
    {
        var title = BuildWindowTitle(AppState.Current.DatasetPath, AppState.Current.CsvPath);
        Title = title;
        AppTitleBar.Title = title;
        AppWindow.Title = title;
    }

    private static string BuildWindowTitle(string datasetPath, string csvPath)
    {
        if (string.IsNullOrWhiteSpace(datasetPath))
        {
            return "DataFlush";
        }

        return string.IsNullOrWhiteSpace(csvPath)
            ? $"{datasetPath} · DataFlush"
            : $"{datasetPath} · {csvPath} · DataFlush";
    }

    private void NavigateTo(NavigationViewItem item, Type pageType)
    {
        NavView.SelectedItem = item;
        if (NavFrame.CurrentSourcePageType != pageType)
        {
            NavFrame.Navigate(pageType);
        }
    }

    private void WindowRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsControlDown())
        {
            return;
        }

        if (e.Key == VirtualKey.Number1)
        {
            NavigateTo(CleanNavItem, typeof(CleanPage));
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Number2)
        {
            NavigateTo(AiNavItem, typeof(AiPage));
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Number3)
        {
            NavigateTo(HatNavItem, typeof(HatPage));
            e.Handled = true;
        }
        else if (e.Key == (VirtualKey)188)
        {
            NavView.SelectedItem = NavView.SettingsItem;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.B)
        {
            NavView.IsPaneOpen = !NavView.IsPaneOpen;
            e.Handled = true;
        }
    }

    private static bool IsControlDown()
    {
        var left = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftControl);
        var right = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightControl);
        return left.HasFlag(CoreVirtualKeyStates.Down) || right.HasFlag(CoreVirtualKeyStates.Down);
    }

    private void MaximizeMainWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private const int HResultCancelled = unchecked((int)0x800704C7);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    private static readonly Guid FileOpenDialogClsid = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig]
        int Show(IntPtr hwndOwner);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(FOS fos);
        void GetOptions(out FOS pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [Flags]
    private enum FOS : uint
    {
        PickFolders = 0x00000020,
        ForceFileSystem = 0x00000040,
        PathMustExist = 0x00000800
    }

    private enum SIGDN : uint
    {
        FileSysPath = 0x80058000
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "clean": SafeNavigate(typeof(CleanPage), "清洗分类"); break;
                case "ai": SafeNavigate(typeof(AiPage), "AI辅助"); break;
                case "hat": SafeNavigate(typeof(HatPage), "帽子分类"); break;
            }
        }
    }

    private async void SafeNavigate(Type pageType, string pageName)
    {
        try
        {
            NavFrame.Navigate(pageType);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync($"{pageName}打开失败", ex.Message);
        }
    }
}


