using System;
using System.IO;
using System.Threading;
using Microsoft.UI.Xaml;

namespace WinUI3Shell;

public partial class App : Application
{
    private Window? _window;
    private Mutex? _sharedWebViewProfileMutex;

    public App()
    {
        InitializeComponent();

        ConfigureWebView2Profile();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 每个进程拥有独立主窗口，互斥锁只用于分配 WebView2 数据目录，不限制实例数量。
        var window = new MainWindow();
        _window = window;
        _window.Activate();
    }

    /// <summary>
    /// 首个实例沿用原持久化目录，保留既有登录 Cookie；额外实例使用独立临时目录，
    /// 避免当前 WebView2 运行时在多进程共享目录时返回空 CoreWebView2。
    /// </summary>
    private void ConfigureWebView2Profile()
    {
        var profileMutex = new Mutex(
            initiallyOwned: true,
            @"Local\MnyCppIDE.WebView2Profile",
            out bool ownsSharedProfile);
        string folder;

        if (ownsSharedProfile)
        {
            _sharedWebViewProfileMutex = profileMutex;
            folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MnyCppIDE",
                "WebView2");
        }
        else
        {
            profileMutex.Dispose();
            folder = Path.Combine(
                Path.GetTempPath(),
                "MnyCppIDE",
                $"WebView2-{Environment.ProcessId}");
        }

        try
        {
            Directory.CreateDirectory(folder);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", folder, EnvironmentVariableTarget.Process);
        }
        catch (Exception)
        {
            // 目录不可写时退回 WebView2 默认位置，不影响启动
        }
    }
}
