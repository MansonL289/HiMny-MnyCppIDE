using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace WinUI3Shell;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        ConfigureWebView2Profile();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 主题由 MainWindow 构造时按设置套用，这里不要再覆盖成 ElementTheme.Default
        var window = new MainWindow();
        _window = window;
        _window.Activate();
    }

    /// <summary>
    /// 把 WebView2 的用户数据目录固定到 LocalAppData，登录 Cookie 才能跨版本、
    /// 跨安装目录永久保存（默认目录跟着 exe 走，更新安装时容易被清掉）。
    /// </summary>
    private static void ConfigureWebView2Profile()
    {
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MnyCppIDE",
                "WebView2");
            Directory.CreateDirectory(folder);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", folder, EnvironmentVariableTarget.Process);
        }
        catch (Exception)
        {
            // 目录不可写时退回 WebView2 默认位置，不影响启动
        }
    }
}
