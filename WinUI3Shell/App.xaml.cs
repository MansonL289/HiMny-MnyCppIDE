using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace WinUI3Shell;

public partial class App : Application
{
    private Window? _window;
    private Mutex? _sharedWebViewProfileMutex;

    public App()
    {
        InitializeComponent();

        string version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "未知";
        AppLog.Info($"启动 MnyC++ IDE {version}，系统 {Environment.OSVersion.VersionString}，目录 {AppContext.BaseDirectory}");

        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("未处理的后台异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        };

        ConfigureWebView2Profile();
    }

    /// <summary>兜底捕获界面线程异常：写日志并提示用户，避免进程无提示消失。</summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLog.Error("未处理的界面异常", e.Exception);
        e.Handled = true;

        try
        {
            MessageBoxW(
                IntPtr.Zero,
                "程序遇到异常，详细信息已写入日志：\n" + AppLog.CurrentFile + "\n\n建议保存工作后重启程序。",
                "MnyC++ IDE",
                0x00000010 /* MB_ICONERROR */);
        }
        catch (Exception)
        {
            // 提示失败时仍然保留日志
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);


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
