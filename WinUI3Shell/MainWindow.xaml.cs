﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace WinUI3Shell;

public sealed partial class MainWindow : Window
{
    private const string EditorHostName = "mny.local";

    /// <summary>分块发送到编辑器的每块字符数，避开 WebView2 单条消息的大小上限。</summary>
    private const int ChunkChars = 256 * 1024;

    private readonly Dictionary<TreeViewNode, FileEntry> _fileNodes = new();
    private readonly List<object> _pendingMessages = new();
    private readonly ConcurrentDictionary<int, TerminalSession> _terminals = new();

    private bool _editorInitialized;
    private bool _editorReady;
    private string? _activeActivity;
    private string? _rootFolderPath;
    private AppSettings _settings = AppSettings.Load();

    /// <summary>是否存在未保存的标签页（由编辑器的 tabsChanged 消息同步）。</summary>
    private bool _hasUnsavedFiles;

    /// <summary>关闭确认已通过，再次进入 Closing 时直接放行。</summary>
    private bool _closeConfirmed;

    private static readonly SolidColorBrush SelectedActivityBrush =
        new(Color.FromArgb(0x33, 0x00, 0x78, 0xD4));

    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    public MainWindow()
    {
        InitializeComponent();

        Title = "MnyC++ IDE";
        SystemBackdrop = new MicaBackdrop();

        // 窗口：屏幕工作区的 2/3 并居中；按 DPI 换算成逻辑尺寸并保底，
        // 避免高分屏笔记本上 2/3 物理像素太小、界面挤成一团
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = GetDisplayScale();
        int logicalW = Math.Max(1, (int)(workArea.Width / scale));
        int logicalH = Math.Max(1, (int)(workArea.Height / scale));
        int winW = (int)(Math.Min(Math.Max(logicalW * 2 / 3, 900), logicalW) * scale);
        int winH = (int)(Math.Min(Math.Max(logicalH * 2 / 3, 600), logicalH) * scale);
        AppWindow.Resize(new SizeInt32(winW, winH));
        AppWindow.Move(new PointInt32(
            workArea.X + (workArea.Width - winW) / 2,
            workArea.Y + (workArea.Height - winH) / 2));

        // 任务栏/窗口图标
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // 内容延伸到标题栏；只有左侧空白区作为拖动区，顶栏按钮不在其中，
        // 这样连续快速点按钮不会被系统当成双击标题栏（双击标题栏 = 最大化）
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        TryApplyRoundedCorners();

        // 右侧让出系统窗口按钮（最小化/最大化/关闭）的宽度，避免设置按钮被遮挡
        RootGrid.Loaded += RootGrid_Loaded;
        RootGrid.SizeChanged += (_, _) => UpdateTitleBarPadding();

        // 与设置、任务管理器一致的高标题栏（48px），标题栏按钮垂直居中
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        UpdateTitleBarColors();

        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) => UpdateTitleBarColors();
        }

        // 中文 Windows 上 cmd 输出是 GBK（代码页 936），.NET Core 需要注册代码页提供程序
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // 设置面板：关闭时隐藏，任何改动立即保存并同步给编辑器页面
        SettingsViewControl.CloseRequested += HideSettings;
        SettingsViewControl.SettingsChanged += OnSettingsChanged;

        // 启动时套用保存下来的应用主题
        ApplyAppTheme();

        // 窗口关闭时清理所有终端进程；若有未保存文件先弹确认框
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) =>
        {
            StopDebug();
            _debugWindow?.CloseForShutdown();
            KillAllTerminals();
        };
    }

    /// <summary>当前窗口所在显示器的缩放比例（DPI / 96），用于把物理像素换算成逻辑尺寸。</summary>
    private double GetDisplayScale()
    {
        try
        {
            uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (dpi > 0)
            {
                return dpi / 96.0;
            }
        }
        catch (Exception)
        {
            // 取不到 DPI 时按 100% 处理
        }

        return 1.0;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// 窗口关闭拦截：有未保存文件时取消关闭并弹确认框。
    /// ContentDialog 是异步的，AppWindow.Closing 本身是同步事件，所以先 args.Cancel=true 取消关闭，
    /// 再异步弹框，用户确认后用 _closeConfirmed 标记放行并再次 Close()。
    /// </summary>
    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
        {
            return;
        }

        if (!_hasUnsavedFiles)
        {
            return;
        }

        args.Cancel = true;
        _ = AskCloseConfirmationAsync();
    }

    private async Task AskCloseConfirmationAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "未保存的更改",
            Content = "有文件存在未保存的更改，关闭后将丢失这些更改。是否保存？",
            PrimaryButtonText = "保存",
            SecondaryButtonText = "不保存",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };

        ContentDialogResult result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            // 让编辑器批量保存所有脏标签，保存完成后回传 saveAllDone 再关闭
            SendCommand("saveAll");
        }
        else if (result == ContentDialogResult.Secondary)
        {
            _closeConfirmed = true;
            Close();
        }
        // 取消：什么都不做，窗口保持打开
    }

    /// <summary>切换整个窗口的主题（ElementTheme.Default 表示跟随系统）。</summary>
    public void ApplyTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }

        UpdateTitleBarColors();
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateTitleBarPadding();
        UpdateActivityVisuals();
        // 先确定欢迎页状态并补播入场动画，避免「先显示出来、编辑器初始化完再重播一次」的闪一下
        UpdateWelcomeVisibility(tabCount: 0);
        await InitializeEditorAsync();
    }

    /* ---------------- 欢迎页 ---------------- */

    private void WelcomeAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tag)
        {
            return;
        }

        switch (tag)
        {
            case "w:openFolder":
                _ = PickAndOpenFolderAsync();
                break;
            case "w:openFile":
                _ = PickAndOpenFileAsync();
                break;
            case "w:newFile":
                _ = NewUntitledAsync();
                break;
            case "w:settings":
                ShowSettings();
                break;
            case "w:practice":
                ShowPractice();
                break;
        }
    }

    private void UpdateWelcomeVisibility(int tabCount)
    {
        if (tabCount == 0)
        {
            EditorWebView.Visibility = Visibility.Collapsed;
            if (WelcomeOverlay.Visibility == Visibility.Visible)
            {
                // 已经在显示，就不要再重播入场动画
                return;
            }

            WelcomeOverlay.Visibility = Visibility.Visible;
            PlayWelcomeAnimation(showing: true);
            UpdateTerminalButtonState();
        }
        else
        {
            EditorWebView.Visibility = Visibility.Visible;
            UpdateTerminalButtonState();
            PlayWelcomeAnimation(showing: false, onCompleted: () => WelcomeOverlay.Visibility = Visibility.Collapsed);
        }
    }

    private void PlayWelcomeAnimation(bool showing, Action? onCompleted = null)
    {
        var fade = new DoubleAnimation
        {
            From = showing ? 0.0 : 1.0,
            To = showing ? 1.0 : 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(showing ? 260 : 160)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = showing ? EasingMode.EaseOut : EasingMode.EaseIn },
        };
        Storyboard.SetTarget(fade, WelcomeContent);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation
        {
            From = showing ? 16.0 : 0.0,
            To = showing ? 0.0 : 8.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(showing ? 260 : 160)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = showing ? EasingMode.EaseOut : EasingMode.EaseIn },
        };
        Storyboard.SetTarget(slide, WelcomeTransform);
        Storyboard.SetTargetProperty(slide, "TranslateY");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(slide);
        if (onCompleted is not null)
        {
            storyboard.Completed += (_, _) => onCompleted();
        }
        storyboard.Begin();
    }

    /// <summary>按系统窗口按钮的实时宽度给标题栏内容留出右侧空间。</summary>
    private void UpdateTitleBarPadding()
    {
        // RightInset 是物理像素，XAML 的 Thickness 用的是逻辑像素，需要按缩放比例换算
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        AppTitleBar.Padding = new Thickness(0, 0, AppWindow.TitleBar.RightInset / scale, 0);
    }

    private void UpdateTitleBarColors()
    {
        bool isDark = Content is FrameworkElement root && root.ActualTheme == ElementTheme.Dark;
        AppWindowTitleBar titleBar = AppWindow.TitleBar;

        // 按钮背景透明，让标题栏区域与 Mica 融为一体
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonInactiveForegroundColor = isDark
            ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x80, 0x00, 0x00, 0x00);
        titleBar.ButtonHoverBackgroundColor = isDark
            ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x15, 0x00, 0x00, 0x00);
        titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonPressedBackgroundColor = isDark
            ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x20, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
    }

    /* ---------------- 编辑器（WebView2 + Monaco） ---------------- */

    private async Task InitializeEditorAsync()
    {
        if (_editorInitialized)
        {
            return;
        }
        _editorInitialized = true;

        string monacoDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "monaco");
        if (!Directory.Exists(monacoDirectory))
        {
            await ShowMessageAsync("提示", $"未找到编辑器资源目录：{monacoDirectory}");
            return;
        }

        try
        {
            await EditorWebView.EnsureCoreWebView2Async();
            CoreWebView2 core = EditorWebView.CoreWebView2;
            // 页面自己画圆角，WebView2 底色透明，这样卡片四角的圆角不会被这块方形表面盖住
            EditorWebView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
            // 关掉 WebView2 自带的 Ctrl+滚轮 / Ctrl+加号缩放，交给页面自己处理（字号、界面缩放）
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(
                EditorHostName, monacoDirectory, CoreWebView2HostResourceAccessKind.Allow);
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += EditorWebMessageReceived;
            core.Navigate($"https://{EditorHostName}/index.html?v={DateTime.Now.Ticks}");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(
                "编辑器初始化失败",
                "编辑器（WebView2）没有加载成功。\n\n"
                + "最常见的原因是系统缺少 Microsoft Edge WebView2 运行时，安装后重启本程序即可。\n\n"
                + DescribeWebView2() + "\n\n"
                + "详细信息：" + ex.Message);
        }
    }

    private void EditorWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(args.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }
        if (message is null)
        {
            return;
        }

        string type = message["type"]?.GetValue<string>() ?? string.Empty;
        switch (type)
        {
            case "ready":
                _editorReady = true;
                UpdateTerminalButtonState();
                FlushPendingMessages();
                SendSettingsToEditor();
                break;
            case "saveRequest":
                _ = HandleSaveRequestAsync(message, saveAs: false);
                break;
            case "saveAsRequest":
                _ = HandleSaveRequestAsync(message, saveAs: true);
                break;
            case "tabsChanged": {
                int count = message["count"]?.GetValue<int>() ?? 0;
                _hasUnsavedFiles = message["hasUnsaved"]?.GetValue<bool>() ?? false;
                UpdateWelcomeVisibility(count);
                UpdateRunButtonVisibility(message["name"]?.GetValue<string>());
                break;
            }
            case "terminalCreate":
                StartTerminal(message["id"]?.GetValue<int>() ?? 0);
                break;
            case "terminalInput":
                WriteTerminal(
                    message["id"]?.GetValue<int>() ?? 0,
                    message["text"]?.GetValue<string>() ?? string.Empty);
                break;
            case "terminalKill":
                KillTerminal(message["id"]?.GetValue<int>() ?? 0);
                break;
            case "buildRequest":
                HostBuild(
                    message["kind"]?.GetValue<string>() ?? string.Empty,
                    message["path"]?.GetValue<string>() ?? string.Empty);
                break;
            case "hostMessage":
                ShowHostMessage(message["text"]?.GetValue<string>() ?? string.Empty);
                break;
            case "editorStateChanged":
                ApplyEditorState(message);
                break;
            case "setEncoding":
                string? encoding = message["value"]?.GetValue<string>();
                if (encoding is "utf8" or "utf8bom" or "gbk")
                {
                    _settings.SaveEncoding = encoding;
                    _settings.Save();
                }
                break;
            case "autoSave":
                _ = HandleAutoSaveAsync(message);
                break;
            case "checkRequest":
                _ = CheckFileAsync(
                    message["path"]?.GetValue<string>() ?? string.Empty,
                    message["name"]?.GetValue<string>());
                break;
            case "outline":
                ApplyOutline(message);
                break;
            case "breakpointToggle":
                HandleBreakpointToggle(
                    message["path"]?.GetValue<string>(),
                    message["line"]?.GetValue<int>() ?? 0,
                    message["enabled"]?.GetValue<bool>() ?? false);
                break;
            case "debugStartRequest":
                _ = StartDebugAsync(message["path"]?.GetValue<string>() ?? string.Empty);
                break;
            case "debugContinue":
                _ = ContinueDebugAsync();
                break;
            case "debugStop":
                StopDebug();
                break;
            case "debugStepOver":
                _ = StepOverDebugAsync();
                break;
            case "debugStepInto":
                _ = StepIntoDebugAsync();
                break;
            case "saveAllDone": {
                bool cancelled = message["cancelled"]?.GetValue<bool>() ?? false;
                if (!cancelled)
                {
                    _closeConfirmed = true;
                    Close();
                }
                break;
            }
        }
    }

    /// <summary>只有当前标签页是 C/C++ 源码文件时才显示顶栏的「运行」按钮。</summary>
    private void UpdateRunButtonVisibility(string? fileName)
    {
        bool runnable = false;
        if (!string.IsNullOrEmpty(fileName))
        {
            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            runnable = extension is ".cpp" or ".cc" or ".cxx" or ".c++" or ".c";
        }

        RunDropDownButton.Visibility = runnable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FlushPendingMessages()
    {
        if (_pendingMessages.Count == 0)
        {
            return;
        }

        var pending = _pendingMessages.ToArray();
        _pendingMessages.Clear();
        foreach (object payload in pending)
        {
            SendToEditor(payload);
        }
    }

    private void SendToEditor(object payload)
    {
        if (!_editorReady)
        {
            _pendingMessages.Add(payload);
            return;
        }

        EditorWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
    }

    private void SendCommand(string name) => SendToEditor(new { type = "command", name });

    private async Task NewUntitledAsync()
    {
        if (!await EnsureEditorReadyAsync())
        {
            return;
        }

        SendCommand("newUntitled");
    }

    /// <summary>
    /// 编辑器页面就绪前，新建/打开文件等操作发给 WebView2 只会排队，用户看到的就是“点了没反应”。
    /// 这里先等一小段时间；仍然没就绪就明确说明原因（最常见是缺少 WebView2 运行时）。
    /// </summary>
    private async Task<bool> EnsureEditorReadyAsync()
    {
        for (int i = 0; i < 40 && !_editorReady; i++)
        {
            await Task.Delay(100);
        }

        if (_editorReady)
        {
            return true;
        }

        await ShowMessageAsync(
            "编辑器未就绪",
            "编辑器页面还没有加载完成，所以新建/打开文件暂时没有反应。\n\n"
            + "如果一直如此，通常是系统缺少 Microsoft Edge WebView2 运行时，"
            + "安装后重启本程序即可。\n\n"
            + DescribeWebView2());
        return false;
    }

    private static string DescribeWebView2()
    {
        try
        {
            return "当前 WebView2 运行时版本：" + CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            return "且未检测到 WebView2 运行时：" + ex.Message;
        }
    }

    private void RunEditorAction(string id) => SendToEditor(new { type = "action", id });

    private async Task HandleSaveRequestAsync(JsonNode message, bool saveAs)
    {
        string? path = message["path"]?.GetValue<string>();
        string name = message["name"]?.GetValue<string>() ?? "未命名";
        string content = message["content"]?.GetValue<string>() ?? string.Empty;

        if (saveAs || string.IsNullOrEmpty(path))
        {
            const string filter =
                "C++ 源文件 (*.cpp;*.h;*.c;*.cc)\0*.cpp;*.h;*.c;*.cc\0其他源文件 (*.cs;*.py;*.js;*.ts)\0*.cs;*.py;*.js;*.ts\0所有文件 (*.*)\0*.*\0\0";

            path = await ShowSaveDialogAsync(name, filter);
            if (path is null)
            {
                // 用户取消了保存对话框。若编辑器正在批量保存，通知它中止并放弃关闭。
                SendToEditor(new { type = "saveCancelled" });
                return;
            }

            name = Path.GetFileName(path);
        }

        try
        {
            File.WriteAllText(path, content, GetSaveEncoding());
            SendToEditor(new { type = "saved", path, name });
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("保存失败", ex.Message);
            SendToEditor(new { type = "saveCancelled" });
        }
    }

    /// <summary>
    /// 弹另存为对话框。这个流程由 WebView2 的消息回调触发，
    /// 直接在回调里弹模态框会重入编辑器线程，所以先排到下一个消息循环再弹。
    /// </summary>
    private async Task<string?> ShowSaveDialogAsync(string suggestedName, string filter)
    {
        var result = new TaskCompletionSource<string?>();

        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    result.SetResult(
                        NativeFileDialog.PickSaveFile(WindowHandle, "另存为", suggestedName, filter));
                }
                catch (Exception ex)
                {
                    result.SetException(ex);
                }
            }))
        {
            return null;
        }

        return await result.Task;
    }

    private async void MenuCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tag)
        {
            return;
        }

        if (tag.StartsWith("action:", StringComparison.Ordinal))
        {
            RunEditorAction(tag["action:".Length..]);
            return;
        }

        if (tag.StartsWith("cmd:", StringComparison.Ordinal))
        {
            await RunCommandAsync(tag["cmd:".Length..]);
        }
    }

    private async Task RunCommandAsync(string command)
    {
        switch (command)
        {
            case "newUntitled":
                await NewUntitledAsync();
                return;
            case "closeActive":
            case "requestSave":
            case "saveAs":
            case "toggleWordWrap":
            case "toggleMinimap":
            case "fontSizeUp":
            case "fontSizeDown":
            case "fontSizeReset":
            case "toggleTerminal":
            case "newTerminal":
            case "clearTerminal":
            case "closeAllTerminals":
            case "hideTerminalPanel":
            case "build":
            case "run":
            case "buildRun":
            case "check":
            case "debugStart":
                SendCommand(command);
                return;
            case "debugContinue":
                await ContinueDebugAsync();
                return;
            case "debugStop":
                StopDebug();
                return;
            case "debugStepOver":
                await StepOverDebugAsync();
                return;
            case "debugStepInto":
                await StepIntoDebugAsync();
                return;
            case "debugStepOut":
                await StepOutDebugAsync();
                return;
            case "openFile":
                await PickAndOpenFileAsync();
                return;
            case "openFolder":
                await PickAndOpenFolderAsync();
                return;
            case "practice":
                ShowPractice();
                return;
            case "exit":
                Close();
                return;
            default:
                return;
        }
    }

    /* ---------------- 打开文件 / 文件夹 ---------------- */

    private async Task PickAndOpenFileAsync()
    {
        if (!await EnsureEditorReadyAsync())
        {
            return;
        }

        try
        {
            string? path = NativeFileDialog.PickFile(WindowHandle, "打开文件");
            if (path is null)
            {
                return;
            }

            await OpenFileAsync(path);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("无法打开文件选择器", ex.ToString());
        }
    }

    private async Task PickAndOpenFolderAsync()
    {
        try
        {
            string? path = NativeFileDialog.PickFolder(WindowHandle, "打开文件夹");
            if (path is null)
            {
                return;
            }

            await SetRootFolderAsync(path);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("无法打开文件夹选择器", ex.ToString());
        }
    }

    private async Task OpenFileAsync(string path)
    {
        try
        {
            var info = new FileInfo(path);
            await SendFileToEditorAsync(path, info.Name, info.Length > _settings.LargeFileModeBytes);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("无法打开文件", ex.Message);
        }
    }

    /// <summary>流式读取并分块发送，避免在 C# 侧再保留一份完整文件字符串。</summary>
    private async Task SendFileToEditorAsync(string path, string name, bool largeFile)
    {
        SendToEditor(new { type = "openFileBegin", path, name, largeFile });

        using var reader = new StreamReader(path, Encoding.UTF8, true);
        char[] buffer = new char[ChunkChars];
        int count;
        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            SendToEditor(new { type = "openFileChunk", text = new string(buffer, 0, count) });
        }

        SendToEditor(new { type = "openFileEnd" });

        // 重新打开文件时把该文件已有的断点画回去
        if (_breakpoints.TryGetValue(path, out SortedSet<int>? lines) && lines.Count > 0)
        {
            SendToEditor(new { type = "breakpoints", path, lines = lines.ToArray() });
        }
    }

    private IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /* ---------------- 左侧活动栏 ---------------- */

    private void ActivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string id)
        {
            return;
        }


        if (_activeActivity == id)
        {
            CollapseSidePanel();
            return;
        }

        ShowActivity(id);
    }

    private void ShowActivity(string id)
    {
        _activeActivity = id;
        SidePanel.Visibility = Visibility.Visible;
        // 文件树窄一点，检查器/大纲/调试需要更多横向空间
        SidePanel.Width = id == "explorer" ? 220 : 240;

        ExplorerPanel.Visibility = id == "explorer" ? Visibility.Visible : Visibility.Collapsed;
        CheckerPanel.Visibility = id == "checker" ? Visibility.Visible : Visibility.Collapsed;
        OutlinePanel.Visibility = id == "outline" ? Visibility.Visible : Visibility.Collapsed;
        DebugPanel.Visibility = id == "debug" ? Visibility.Visible : Visibility.Collapsed;

        // 离开大纲栏目后不再跟着内容变化刷新
        if (id != "outline")
        {
            SendToEditor(new { type = "stopOutline" });
        }

        switch (id)
        {
            case "checker":
                SendCommand("check");
                break;
            case "outline":
                SendToEditor(new { type = "requestOutline" });
                break;
            case "debug":
                PrepareDebugPanel();
                ShowDebugWindow();
                break;
        }

        UpdateActivityVisuals();
    }

    private void CollapseSidePanel()
    {
        _activeActivity = null;
        SidePanel.Visibility = Visibility.Collapsed;
        SendToEditor(new { type = "stopOutline" });
        UpdateActivityVisuals();
    }

    private void UpdateActivityVisuals()
    {
        foreach (Button button in new[]
        {
            ExplorerButton, CheckerButton, OutlineButton, DebugButton,
        })
        {
            bool selected = button.Tag is string id && id == _activeActivity;
            button.Background = selected ? SelectedActivityBrush : TransparentBrush;
            button.Opacity = selected ? 1.0 : 0.75;
        }
    }

    /* ---------------- 代码检查器 ---------------- */

    private sealed class DiagnosticItem
    {
        public string Severity { get; init; } = "error";

        public string Message { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        public int Line { get; init; }

        public int Column { get; init; }

        public string Location
        {
            get
            {
                string name = string.IsNullOrEmpty(Path) ? string.Empty : System.IO.Path.GetFileName(Path);
                return name.Length > 0 ? $"{name}:{Line}" : $"第 {Line} 行";
            }
        }

        public string Glyph => Severity == "warning" ? "\uE7BA" : "\uE783";

        public SolidColorBrush Brush => Severity == "warning" ? WarningBrush : ErrorBrush;
    }

    private static readonly SolidColorBrush ErrorBrush = new(Color.FromArgb(0xFF, 0xE8, 0x11, 0x23));
    private static readonly SolidColorBrush WarningBrush = new(Color.FromArgb(0xFF, 0xD9, 0x8C, 0x00));

    private static readonly Regex DiagnosticRegex = new(
        @"^(?<file>.+?):(?<line>\d+):(?<col>\d+):\s*(?<sev>fatal error|error|warning|note):\s*(?<msg>.*)$",
        RegexOptions.Compiled);

    private bool _checking;

    private static bool IsCSourceFile(string path)
    {
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return extension is ".c" or ".cc" or ".cpp" or ".cxx" or ".c++" or ".h" or ".hpp" or ".hxx";
    }

    private void RecheckButton_Click(object sender, RoutedEventArgs e) => SendCommand("check");

    /// <summary>对当前文件执行 g++ -fsyntax-only，把错误/警告显示到侧栏并画波浪线。</summary>
    private async Task CheckFileAsync(string sourcePath, string? name)
    {
        if (_checking)
        {
            return;
        }

        if (string.IsNullOrEmpty(sourcePath))
        {
            DiagnosticsList.ItemsSource = null;
            CheckerStatusText.Text = "没有打开的文件";
            CheckerEmptyText.Text = "打开一个 C/C++ 文件后会自动检查。";
            CheckerEmptyText.Visibility = Visibility.Visible;
            return;
        }

        if (!IsCSourceFile(sourcePath))
        {
            DiagnosticsList.ItemsSource = null;
            CheckerStatusText.Text = "仅支持 C/C++ 源文件";
            CheckerEmptyText.Text = "当前文件不是 C/C++ 源文件。";
            CheckerEmptyText.Visibility = Visibility.Visible;
            return;
        }

        string? compiler = FindCompiler();
        if (compiler is null)
        {
            CheckerStatusText.Text = "未找到 g++";
            CheckerEmptyText.Text = "未找到编译器，请确认 Assets\\mingw64\\bin\\g++.exe 存在。";
            CheckerEmptyText.Visibility = Visibility.Visible;
            return;
        }

        _checking = true;
        CheckerStatusText.Text = $"正在检查 {name ?? System.IO.Path.GetFileName(sourcePath)}…";
        SendToEditor(new { type = "clearDiagnostics", path = sourcePath });

        var items = new List<DiagnosticItem>();
        string errorMessage = string.Empty;
        try
        {
            string extraArguments = _settings.BuildExtraArguments();
            string arguments =
                $"-fsyntax-only -Wall{(extraArguments.Length > 0 ? " " + extraArguments : string.Empty)} {Quote(sourcePath)}";

            var startInfo = new ProcessStartInfo
            {
                FileName = compiler,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(sourcePath) ?? AppContext.BaseDirectory,
                StandardOutputEncoding = Encoding.GetEncoding(936),
                StandardErrorEncoding = Encoding.GetEncoding(936),
            };

            using Process process = Process.Start(startInfo)!;
            // 同时读 stdout/stderr，避免大量诊断输出时管道塞满导致死锁
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync();
            string stdout = stdoutTask.Result;
            string stderr = stderrTask.Result;

            foreach (string line in (stderr + "\n" + stdout).Split('\n'))
            {
                Match match = DiagnosticRegex.Match(line.TrimEnd('\r'));
                if (!match.Success)
                {
                    continue;
                }

                string severity = match.Groups["sev"].Value;
                if (severity == "note")
                {
                    continue;
                }

                if (!int.TryParse(match.Groups["line"].Value, out int lineNumber) || lineNumber <= 0)
                {
                    continue;
                }

                _ = int.TryParse(match.Groups["col"].Value, out int column);

                items.Add(new DiagnosticItem
                {
                    Severity = severity == "warning" ? "warning" : "error",
                    Message = match.Groups["msg"].Value.Trim(),
                    Path = sourcePath,
                    Line = lineNumber,
                    Column = Math.Max(1, column),
                });
            }
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            _checking = false;
        }

        if (errorMessage.Length > 0)
        {
            CheckerStatusText.Text = "检查失败";
            CheckerEmptyText.Text = errorMessage;
            CheckerEmptyText.Visibility = Visibility.Visible;
            return;
        }

        DiagnosticsList.ItemsSource = items;
        int errors = items.Count(item => item.Severity == "error");
        int warnings = items.Count - errors;
        CheckerStatusText.Text = items.Count == 0
            ? "检查通过"
            : $"{errors} 个错误，{warnings} 个警告";
        CheckerEmptyText.Text = items.Count == 0 ? "没有发现问题。" : string.Empty;
        CheckerEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SendToEditor(new
        {
            type = "diagnostics",
            path = sourcePath,
            items = items.Select(item => new
            {
                severity = item.Severity,
                message = item.Message,
                line = item.Line,
                column = item.Column,
                endLine = item.Line,
                endColumn = item.Column + 1,
            }).ToArray(),
        });
    }

    private async void DiagnosticsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DiagnosticItem item)
        {
            return;
        }

        if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path))
        {
            await OpenFileAsync(item.Path);
        }

        SendToEditor(new { type = "reveal", line = item.Line, column = item.Column });
    }

    /* ---------------- 代码大纲 ---------------- */

    private sealed class SymbolView
    {
        public SymbolView(string name, string kind, int line, int column)
        {
            Name = name;
            Kind = kind;
            Line = line;
            Column = column;
        }

        public string Name { get; }

        public string Kind { get; }

        public int Line { get; }

        public int Column { get; }

        public string Detail => $"第 {Line} 行";

        public string Glyph => Kind switch
        {
            "namespace" => "\uE8B7",
            "class" or "struct" or "union" => "\uE8F1",
            "enum" => "\uE8EF",
            "macro" => "\uE8AC",
            _ => "\uE943",
        };
    }

    private void ApplyOutline(JsonNode message)
    {
        OutlineTree.RootNodes.Clear();

        int count = 0;
        if (message["symbols"] is JsonArray symbols)
        {
            foreach (JsonNode? symbol in symbols)
            {
                if (symbol is null)
                {
                    continue;
                }

                TreeViewNode? node = CreateSymbolNode(symbol);
                if (node is not null)
                {
                    node.IsExpanded = true;
                    OutlineTree.RootNodes.Add(node);
                    count += 1;
                }
            }
        }

        OutlineEmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private TreeViewNode? CreateSymbolNode(JsonNode symbol)
    {
        string name = symbol["name"]?.GetValue<string>() ?? string.Empty;
        if (name.Length == 0)
        {
            return null;
        }

        string kind = symbol["kind"]?.GetValue<string>() ?? "function";
        int line = symbol["line"]?.GetValue<int>() ?? 0;
        int column = symbol["column"]?.GetValue<int>() ?? 1;

        var node = new TreeViewNode
        {
            Content = new SymbolView(name, kind, line, column),
        };

        if (symbol["children"] is JsonArray children)
        {
            foreach (JsonNode? child in children)
            {
                if (child is null)
                {
                    continue;
                }

                TreeViewNode? childNode = CreateSymbolNode(child);
                if (childNode is not null)
                {
                    node.Children.Add(childNode);
                }
            }
        }

        return node;
    }

    private void OutlineTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode node || node.Content is not SymbolView symbol)
        {
            return;
        }

        SendToEditor(new { type = "reveal", line = symbol.Line, column = symbol.Column });
    }

    /* ---------------- 调试（gdb） ---------------- */

    private readonly Dictionary<string, SortedSet<int>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _breakpointNumbers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _watchExpressions = new();

    private GdbSession? _debugSession;
    private string? _debugSourcePath;
    private bool _debugStarting;

    /// <summary>调试控制窗口；右侧栏只看监视，控制/局部变量/调用栈在这个独立窗口里。</summary>
    private DebugWindow? _debugWindow;

    /// <summary>缓存最近一次的变量与调用栈，窗口晚于调试状态创建时能立刻显示。</summary>
    private IReadOnlyList<DebugVariableItem> _debugLocals = Array.Empty<DebugVariableItem>();
    private IReadOnlyList<DebugFrameItem> _debugFrames = Array.Empty<DebugFrameItem>();

    private static string BreakpointKey(string path, int line) => path + ":" + line;

    private static string? FindGdb()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "mingw64", "bin", "gdb.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
            {
                continue;
            }

            try
            {
                string candidate = Path.Combine(directory.Trim('"'), "gdb.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // PATH 中有非法路径，跳过
            }
        }

        return null;
    }

    private void PrepareDebugPanel()
    {
        if (_debugSession is { IsAlive: true })
        {
            return;
        }

        int count = _breakpoints.Sum(pair => pair.Value.Count);
        DebugStatus = count > 0 ? $"就绪，共 {count} 个断点（F5 开始）" : "未启动（F5 开始调试）";
    }

    private async Task StartDebugAsync(string sourcePath)
    {
        if (_debugSession is { IsAlive: true })
        {
            await ContinueDebugAsync();
            return;
        }

        if (_activeActivity != "debug")
        {
            ShowActivity("debug");
        }
        else
        {
            // 浮窗被手动关掉后，再次开始调试时重新显示
            ShowDebugWindow();
        }

        if (string.IsNullOrEmpty(sourcePath) || !IsCSourceFile(sourcePath))
        {
            DebugStatus = "请先打开一个 C/C++ 源文件";
            return;
        }

        if (!File.Exists(sourcePath))
        {
            DebugStatus = "源文件不存在";
            return;
        }

        string? gdb = FindGdb();
        if (gdb is null)
        {
            DebugStatus = "未找到 gdb.exe，请放入 Assets\\mingw64\\bin";
            SendToEditor(new
            {
                type = "terminalNote",
                text = "[调试] 未找到 gdb.exe，请把 MinGW-w64 的 gdb.exe 放入 Assets\\mingw64\\bin 后重试。",
            });
            return;
        }

        if (_debugStarting || _building)
        {
            DebugStatus = "正在编译，请稍候…";
            return;
        }

        // 上一次会话已经结束（进程退出但对象还在）时先清理干净
        if (_debugSession is not null)
        {
            FinishDebugSession("已停止");
        }

        _debugStarting = true;
        try
        {
            _debugSourcePath = sourcePath;
            string exePath = Path.ChangeExtension(sourcePath, ".exe");
            DebugStatus = "正在编译…";
            SendToEditor(new { type = "terminalNote", text = $"[调试] 编译 {Path.GetFileName(sourcePath)}" });

            if (!await CompileForDebugAsync(sourcePath, exePath))
            {
                DebugStatus = "编译失败";
                return;
            }

            GdbSession? session = GdbSession.TryStart(
                gdb, exePath, Path.GetDirectoryName(sourcePath) ?? AppContext.BaseDirectory);
            if (session is null)
            {
                DebugStatus = "gdb 启动失败";
                return;
            }

            _debugSession = session;
            session.Stopped += (_, e) => DispatcherQueue.TryEnqueue(() => _ = HandleDebugStoppedAsync(e));
            session.Running += (_, _) => DispatcherQueue.TryEnqueue(() => DebugStatus = "运行中…");
            session.ConsoleOutput += (_, text) =>
                DispatcherQueue.TryEnqueue(() => SendToEditor(new { type = "terminalStdout", text }));
            session.Exited += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(_debugSession, session))
                {
                    FinishDebugSession("调试进程已退出");
                }
            });

            await session.InitializeAsync();

            _breakpointNumbers.Clear();
            foreach (KeyValuePair<string, SortedSet<int>> pair in _breakpoints)
            {
                foreach (int line in pair.Value)
                {
                    int number = await session.InsertBreakpointAsync(pair.Key, line);
                    if (number > 0)
                    {
                        _breakpointNumbers[BreakpointKey(pair.Key, line)] = number;
                    }
                }
            }

            DebugStatus = "已启动，运行中…";
            _ = session.RunAsync();
        }
        catch (Exception ex)
        {
            DebugStatus = "调试启动失败";
            SendToEditor(new { type = "terminalStdout", text = "[调试] " + ex.Message });
            StopDebug();
        }
        finally
        {
            _debugStarting = false;
        }
    }

    private async Task<bool> CompileForDebugAsync(string sourcePath, string exePath)
    {
        string? compiler = FindCompiler();
        if (compiler is null)
        {
            SendToEditor(new { type = "terminalStdout", text = "[调试] 未找到 g++。" });
            return false;
        }

        string extraArguments = _settings.BuildExtraArguments();
        string arguments =
            $"-g -static{(extraArguments.Length > 0 ? " " + extraArguments : string.Empty)} {Quote(sourcePath)} -o {Quote(exePath)}";

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = compiler,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(sourcePath) ?? AppContext.BaseDirectory,
                StandardOutputEncoding = Encoding.GetEncoding(936),
                StandardErrorEncoding = Encoding.GetEncoding(936),
            };

            using Process process = Process.Start(startInfo)!;
            process.OutputDataReceived += (_, e) => SendBuildOutput(e.Data);
            process.ErrorDataReceived += (_, e) => SendBuildOutput(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            SendToEditor(new { type = "terminalStdout", text = "[调试] " + ex.Message });
            return false;
        }
    }

    private async Task ContinueDebugAsync()
    {
        if (_debugSession is not { IsAlive: true } session)
        {
            SendCommand("debugStart");
            return;
        }

        DebugStatus = "运行中…";
        await session.ContinueAsync();
    }

    private async Task StepOverDebugAsync()
    {
        if (_debugSession is not { IsAlive: true } session)
        {
            SendCommand("debugStart");
            return;
        }

        DebugStatus = "单步执行…";
        await session.NextAsync();
    }

    private async Task StepIntoDebugAsync()
    {
        if (_debugSession is not { IsAlive: true } session)
        {
            SendCommand("debugStart");
            return;
        }

        DebugStatus = "单步执行…";
        await session.StepAsync();
    }

    private async Task StepOutDebugAsync()
    {
        if (_debugSession is not { IsAlive: true } session)
        {
            return;
        }

        DebugStatus = "单步跳出…";
        await session.FinishAsync();
    }

    private async Task HandleDebugStoppedAsync(GdbStopEventArgs info)
    {
        GdbSession? session = _debugSession;
        if (session is null)
        {
            return;
        }

        if (info.Reason is "exited-normally" or "exited-signalled" or "exited")
        {
            FinishDebugSession(info.Reason == "exited-normally" ? "程序已正常退出" : "程序已退出");
            return;
        }

        if (!string.IsNullOrEmpty(info.File) && info.Line > 0)
        {
            string file = info.File;
            if (!string.Equals(file, _debugSourcePath, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
            {
                await OpenFileAsync(file);
                _debugSourcePath = file;
            }

            SendToEditor(new { type = "debugLine", path = file, line = info.Line });
            string function = info.Function.Length > 0 ? $"（{info.Function}）" : string.Empty;
            DebugStatus = $"已暂停：{Path.GetFileName(file)}:{info.Line}{function}";
        }
        else
        {
            DebugStatus = "已暂停";
        }

        await RefreshDebugStateAsync();
    }

    private async Task RefreshDebugStateAsync()
    {
        GdbSession? session = _debugSession;
        if (session is null)
        {
            return;
        }

        List<GdbFrame> frames = await session.GetFramesAsync();
        _debugFrames = frames.Select(frame => new DebugFrameItem
        {
            Level = frame.Level,
            File = frame.File,
            Line = frame.Line,
            Text = $"#{frame.Level} {frame.Function}" +
                   (frame.File.Length > 0 ? $"  {Path.GetFileName(frame.File)}:{frame.Line}" : string.Empty),
        }).ToList();
        _debugWindow?.SetFrames(_debugFrames);

        List<GdbVariable> variables = await session.GetVariablesAsync();
        _debugLocals = variables
            .Select(variable => new DebugVariableItem { Name = variable.Name, Value = variable.Value })
            .ToList();
        _debugWindow?.SetLocals(_debugLocals);

        await RefreshWatchAsync();
    }

    private async Task RefreshWatchAsync()
    {
        GdbSession? session = _debugSession;
        if (session is not { IsAlive: true } || _watchExpressions.Count == 0)
        {
            DebugWatchList.ItemsSource = null;
            return;
        }

        var items = new List<DebugVariableItem>();
        foreach (string expression in _watchExpressions)
        {
            items.Add(new DebugVariableItem
            {
                Name = expression,
                Value = await session.EvaluateAsync(expression),
            });
        }

        DebugWatchList.ItemsSource = items;
    }

    private void FinishDebugSession(string status)
    {
        GdbSession? session = _debugSession;
        _debugSession = null;
        _breakpointNumbers.Clear();

        try { session?.Dispose(); }
        catch (Exception) { }

        SendToEditor(new { type = "debugLineClear" });
        _debugLocals = Array.Empty<DebugVariableItem>();
        _debugFrames = Array.Empty<DebugFrameItem>();
        _debugWindow?.SetLocals(_debugLocals);
        _debugWindow?.SetFrames(_debugFrames);
        DebugWatchList.ItemsSource = null;
        DebugStatus = status;
    }

    private void StopDebug() => FinishDebugSession("已停止");

    private async void HandleBreakpointToggle(string? path, int line, bool enabled)
    {
        if (string.IsNullOrEmpty(path) || line <= 0)
        {
            return;
        }

        if (!_breakpoints.TryGetValue(path, out SortedSet<int>? lines))
        {
            lines = new SortedSet<int>();
            _breakpoints[path] = lines;
        }

        if (enabled)
        {
            lines.Add(line);
        }
        else
        {
            lines.Remove(line);
        }

        if (lines.Count == 0)
        {
            _breakpoints.Remove(path);
        }

        if (_debugSession is not { IsAlive: true } session)
        {
            return;
        }

        string key = BreakpointKey(path, line);
        if (enabled)
        {
            int number = await session.InsertBreakpointAsync(path, line);
            if (number > 0)
            {
                _breakpointNumbers[key] = number;
            }
        }
        else if (_breakpointNumbers.TryGetValue(key, out int number))
        {
            await session.DeleteBreakpointAsync(number);
            _breakpointNumbers.Remove(key);
        }
    }

    /* ---------------- 调试控制窗口 ---------------- */

    /// <summary>同时更新左侧栏与调试窗口里的状态文字。</summary>
    private string DebugStatus
    {
        set
        {
            DebugStatusText.Text = value;
            _debugWindow?.SetStatus(value);
        }
    }

    /// <summary>显示/唤出调试控制窗口（独立 WinUI 窗口，避免自绘浮层在 WebView2 上收不到输入）。</summary>
    private void ShowDebugWindow()
    {
        if (_debugWindow is null)
        {
            _debugWindow = new DebugWindow();
            _debugWindow.StartRequested += () => _ = ContinueDebugAsync();
            _debugWindow.StopRequested += StopDebug;
            _debugWindow.StepOverRequested += () => _ = StepOverDebugAsync();
            _debugWindow.StepIntoRequested += () => _ = StepIntoDebugAsync();
            _debugWindow.StepOutRequested += () => _ = StepOutDebugAsync();
            _debugWindow.FrameActivated += frame => _ = ActivateFrameAsync(frame);
            _debugWindow.SetLocals(_debugLocals);
            _debugWindow.SetFrames(_debugFrames);
            _debugWindow.SetStatus(DebugStatusText.Text);
            PositionDebugWindow(_debugWindow);
        }

        _debugWindow.ShowWindow();
    }

    /// <summary>把调试窗口放到主窗口右侧，便于同时看代码和变量。</summary>
    private void PositionDebugWindow(DebugWindow window)
    {
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        window.AppWindow.Move(new PointInt32(
            position.X + Math.Max(0, size.Width - 480),
            position.Y + 96));
    }

    private async Task ActivateFrameAsync(DebugFrameItem frame)
    {
        if (_debugSession is not { IsAlive: true } session)
        {
            return;
        }

        await session.SelectFrameAsync(frame.Level);

        if (!string.IsNullOrEmpty(frame.File) && File.Exists(frame.File))
        {
            if (!string.Equals(frame.File, _debugSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                await OpenFileAsync(frame.File);
                _debugSourcePath = frame.File;
            }

            SendToEditor(new { type = "reveal", line = frame.Line, column = 1 });
        }

        await RefreshDebugStateAsync();
    }

    private void DebugWatchInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        string expression = DebugWatchInput.Text.Trim();
        if (expression.Length == 0)
        {
            return;
        }

        if (!_watchExpressions.Contains(expression))
        {
            _watchExpressions.Add(expression);
        }

        DebugWatchInput.Text = string.Empty;
        _ = RefreshWatchAsync();
    }

    /* ---------------- 左侧文件树 ---------------- */

    private sealed class FileEntry
    {
        public FileEntry(string path, bool isFolder)
        {
            Path = path;
            IsFolder = isFolder;
        }

        public string Path { get; }

        public bool IsFolder { get; }
    }

    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules",
    };

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        await PickAndOpenFolderAsync();
    }

    private async Task SetRootFolderAsync(string folderPath)
    {
        _rootFolderPath = folderPath;
        FileTree.RootNodes.Clear();
        _fileNodes.Clear();

        TreeViewNode root = CreateNode(folderPath, isFolder: true);
        FileTree.RootNodes.Add(root);
        await LoadChildrenAsync(root);
        root.IsExpanded = true;

        // 已经有根目录了，侧栏顶部的「打开文件夹」入口不再需要，
        // 改成显示当前文件夹名，避免顶部留空
        string folderName = Path.GetFileName(folderPath.TrimEnd('\\', '/'));
        ExplorerFolderName.Text = string.IsNullOrEmpty(folderName) ? folderPath : folderName;
        ExplorerFolderName.Visibility = Visibility.Visible;
        OpenFolderButton.Visibility = Visibility.Collapsed;
        ShowActivity("explorer");
    }

    private TreeViewNode CreateNode(string path, bool isFolder)
    {
        string displayName = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(displayName))
        {
            displayName = path;
        }

        // 节点里只放数据，图标/名字/悬浮路径全部交给 XAML 的 ItemTemplate 渲染。
        // 直接把 UIElement 塞进 TreeViewNode.Content，部分行会渲染成空白。
        var node = new TreeViewNode
        {
            Content = new FileNodeView(displayName, path, isFolder),
            HasUnrealizedChildren = isFolder,
        };
        _fileNodes[node] = new FileEntry(path, isFolder);
        return node;
    }

    private async void FileTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.HasUnrealizedChildren)
        {
            await LoadChildrenAsync(args.Node);
        }
    }

    private async void FileTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode node ||
            !_fileNodes.TryGetValue(node, out FileEntry? entry))
        {
            return;
        }

        // 单击文件夹展开/收起，单击文件打开到编辑器。
        // 点击左侧箭头由 TreeView 自己切换且不会触发这个事件，因此不会重复切换。
        if (entry.IsFolder)
        {
            node.IsExpanded = !node.IsExpanded;
            return;
        }

        await OpenFileAsync(entry.Path);
    }

    private async Task LoadChildrenAsync(TreeViewNode node)
    {
        if (!_fileNodes.TryGetValue(node, out FileEntry? entry) || !entry.IsFolder)
        {
            node.HasUnrealizedChildren = false;
            return;
        }

        node.HasUnrealizedChildren = false;
        var children = await Task.Run(() => EnumerateChildren(entry.Path));

        node.Children.Clear();
        foreach ((string childPath, bool isFolder) in children)
        {
            node.Children.Add(CreateNode(childPath, isFolder));
        }
    }

    private static List<(string Path, bool IsFolder)> EnumerateChildren(string folder)
    {
        var directories = new List<string>();
        var files = new List<string>();

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(folder))
            {
                if (!IgnoredFolders.Contains(Path.GetFileName(directory)))
                {
                    directories.Add(directory);
                }
            }

            foreach (string file in Directory.EnumerateFiles(folder))
            {
                files.Add(file);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        directories.Sort(StringComparer.CurrentCultureIgnoreCase);
        files.Sort(StringComparer.CurrentCultureIgnoreCase);

        var result = new List<(string Path, bool IsFolder)>(directories.Count + files.Count);
        foreach (string directory in directories)
        {
            result.Add((directory, true));
        }
        foreach (string file in files)
        {
            result.Add((file, false));
        }
        return result;
    }

    /* ---------------- 通用 ---------------- */

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private bool _settingsOpen;
    private bool _settingsTransitioning;

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ToggleSettings();

    private void ToggleSettings()
    {
        // 页面切换尚未结束时忽略重复点击，避免连续开关把动画状态打乱
        if (_settingsTransitioning)
        {
            return;
        }

        if (_settingsOpen)
        {
            HideSettings();
        }
        else
        {
            ShowSettings();
        }
    }

    private void UpdateSettingsTitleBar()
    {
        bool overlay = _settingsOpen || _practiceOpen;
        TitleTextBlock.Text = _settingsOpen
            ? "MnyC++ IDE - 设置"
            : _practiceOpen ? "MnyC++ IDE - 刷题模式" : "MnyC++ IDE";
        TitleAppIcon.Visibility = overlay ? Visibility.Collapsed : Visibility.Visible;
        TitleIconButton.Visibility = overlay ? Visibility.Visible : Visibility.Collapsed;
        TitleIconButton.IsHitTestVisible = overlay;
        TitleIconGlyph.Glyph = "\uE80F";
        ToolTipService.SetToolTip(
            TitleIconButton,
            _practiceOpen ? "回到默认模式" : _settingsOpen ? "返回主页" : null);
        SettingsButton.Visibility = _practiceOpen ? Visibility.Collapsed : Visibility.Visible;
        SettingsButtonIcon.Glyph = "\uE713";
        ToolTipService.SetToolTip(SettingsButton, "设置");
    }

    /* ---------------- 刷题模式 ---------------- */

    private bool _practiceOpen;

    /// <summary>打开刷题模式：隐藏菜单栏和主编辑区，整个内容区换成刷题界面。</summary>
    private void ShowPractice()
    {
        if (_practiceOpen)
        {
            return;
        }

        if (_settingsOpen)
        {
            HideSettings();
        }

        _practiceOpen = true;
        UpdateSettingsTitleBar();
        MenuBarArea.Visibility = Visibility.Collapsed;
        MainContentArea.Visibility = Visibility.Collapsed;
        PracticeOverlay.Visibility = Visibility.Visible;
        PracticeViewControl.Activate(_settings);
        UpdateTerminalButtonState();
    }

    /// <summary>回家：从刷题模式回到默认 IDE 界面（编辑器和文件状态保持不变）。</summary>
    private void HidePractice()
    {
        if (!_practiceOpen)
        {
            return;
        }

        _practiceOpen = false;
        PracticeViewControl.Suspend();
        PracticeOverlay.Visibility = Visibility.Collapsed;
        MenuBarArea.Visibility = Visibility.Visible;
        MainContentArea.Visibility = Visibility.Visible;
        UpdateSettingsTitleBar();
        UpdateTerminalButtonState();
        SendCommand("focus");
    }

    private void TitleIconButton_Click(object sender, RoutedEventArgs e)
    {
        if (_practiceOpen)
        {
            HidePractice();
        }
        else if (_settingsOpen)
        {
            HideSettings();
        }
    }

    /// <summary>打开设置面板（标题栏退出按钮或 Esc 退出）。</summary>
    private void ShowSettings()
    {
        if (_settingsOpen || _settingsTransitioning)
        {
            return;
        }

        _settingsOpen = true;
        UpdateSettingsTitleBar();
        SettingsViewControl.Load(_settings);
        UpdateTerminalButtonState();
        PlaySettingsAnimation(showing: true);
        SettingsViewControl.FocusFirst();
    }

    private void HideSettings()
    {
        if (!_settingsOpen || _settingsTransitioning)
        {
            return;
        }

        _settingsOpen = false;
        UpdateSettingsTitleBar();
        PlaySettingsAnimation(
            showing: false,
            onCompleted: () =>
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
                UpdateTerminalButtonState();
            });
        SendCommand("focus");
    }

    private int _settingsAnimToken;
    private Storyboard? _settingsStoryboard;

    /// <summary>
    /// 设置页切换动画：先淡出主页面，再让设置整页从右侧滑入；
    /// 关闭时先把设置页滑出右侧，再恢复主页面。
    /// </summary>
    private void PlaySettingsAnimation(bool showing, Action? onCompleted = null)
    {
        int token = ++_settingsAnimToken;
        _settingsTransitioning = true;

        // 快速反向切换时，从当前动画位置接续，避免跳变
        double oldContentOpacity = MenuBarArea.Opacity;
        double settingsX = SettingsTransform.X;

        _settingsStoryboard?.Stop();
        _settingsStoryboard = null;

        double pageWidth = SettingsOverlay.ActualWidth;
        if (pageWidth < 1)
        {
            pageWidth = RootGrid.ActualWidth;
        }
        if (pageWidth < 1)
        {
            pageWidth = 800;
        }

        // 初次打开从整个页面宽度的右侧开始；反向切换则从当前位置继续
        double startX = showing && settingsX <= 1 ? pageWidth : settingsX;

        SettingsOverlay.Opacity = 1;
        MenuBarArea.Opacity = oldContentOpacity;
        MainContentArea.Opacity = oldContentOpacity;
        SettingsViewControl.Opacity = showing ? 0 : 1;
        SettingsTransform.X = showing ? startX : settingsX;
        SettingsTransform.Y = 0;

        // 先放到窗口右侧，再显示容器，避免打开时先闪一下最终位置再跳回右侧
        if (showing)
        {
            SettingsOverlay.Visibility = Visibility.Visible;
        }

        var storyboard = new Storyboard();

        void AddFade(
            DependencyObject target,
            double from,
            double to,
            int milliseconds,
            EasingMode easingMode,
            int beginMilliseconds = 0)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                BeginTime = TimeSpan.FromMilliseconds(beginMilliseconds),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = easingMode },
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, "Opacity");
            storyboard.Children.Add(animation);
        }

        void AddSlide(
            double from,
            double to,
            int milliseconds,
            EasingMode easingMode,
            int beginMilliseconds = 0)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                BeginTime = TimeSpan.FromMilliseconds(beginMilliseconds),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = easingMode },
            };
            Storyboard.SetTarget(animation, SettingsTransform);
            Storyboard.SetTargetProperty(animation, "X");
            storyboard.Children.Add(animation);
        }

        void AddVisibility(DependencyObject target, Visibility value, int atMilliseconds)
        {
            var animation = new ObjectAnimationUsingKeyFrames();
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, "Visibility");
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(atMilliseconds)),
                Value = value,
            });
            storyboard.Children.Add(animation);
        }

        if (showing)
        {
            // 先隐藏主页面，再让设置整页从右侧进入。
            // 淡出后真正收起原内容，透明的设置页下方才会显示主窗口的 Mica，而不是编辑器表面。
            AddFade(MenuBarArea, oldContentOpacity, 0, 160, EasingMode.EaseIn);
            AddFade(MainContentArea, oldContentOpacity, 0, 160, EasingMode.EaseIn);
            AddVisibility(MenuBarArea, Visibility.Collapsed, 160);
            AddVisibility(MainContentArea, Visibility.Collapsed, 160);
            AddSlide(startX, 0, 300, EasingMode.EaseOut, 160);
            AddFade(SettingsViewControl, 0, 1, 180, EasingMode.EaseOut, 160);
        }
        else
        {
            // 先让设置页滑出右侧，再恢复主页面
            AddSlide(settingsX, pageWidth, 220, EasingMode.EaseIn);
            AddVisibility(MenuBarArea, Visibility.Visible, 180);
            AddVisibility(MainContentArea, Visibility.Visible, 180);
            AddFade(MenuBarArea, oldContentOpacity, 1, 200, EasingMode.EaseOut, 180);
            AddFade(MainContentArea, oldContentOpacity, 1, 200, EasingMode.EaseOut, 180);
        }

        storyboard.Completed += (_, _) =>
        {
            // 期间又开关过一次，就不再执行旧动画的回调（避免刚打开又被收起）
            if (token != _settingsAnimToken)
            {
                return;
            }

            storyboard.Stop();
            _settingsStoryboard = null;
            _settingsTransitioning = false;
            MenuBarArea.Opacity = showing ? 0 : 1;
            MainContentArea.Opacity = showing ? 0 : 1;
            MenuBarArea.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
            MainContentArea.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
            SettingsViewControl.Opacity = 1;
            // 关闭后停留在右侧，下一次打开不会复用到“已显示在中央”的旧渲染帧
            SettingsTransform.X = showing ? 0 : pageWidth;
            onCompleted?.Invoke();
        };
        _settingsStoryboard = storyboard;
        storyboard.Begin();
    }

    private void OnSettingsChanged()
    {
        _settings.Save();
        ApplyAppTheme();
        SendSettingsToEditor();
        SettingsViewControl.ApplyAppearance(_settings);
        PracticeViewControl.ApplySettings(_settings);
    }

    /// <summary>按设置切换应用主题（跟随系统时交给系统决定）。</summary>
    private void ApplyAppTheme()
    {
        ElementTheme theme = _settings.AppTheme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ApplyTheme(theme);
    }

    /// <summary>把设置同步给编辑器页面（外观、行为、新文件模板）。</summary>
    private void SendSettingsToEditor()
    {
        SendToEditor(new
        {
            type = "settings",
            template = _settings.TemplateCode,
            insertTemplate = _settings.InsertTemplateOnNewFile,
            theme = _settings.EditorTheme,
            uiZoom = _settings.UiZoom,
            fontFamily = _settings.EditorFontFamily,
            fontSize = _settings.EditorFontSize,
            fontLigatures = _settings.EditorFontLigatures,
            wordWrap = _settings.EditorWordWrap,
            minimap = _settings.EditorMinimap,
            whitespace = _settings.EditorWhitespace,
            cursorStyle = _settings.EditorCursorStyle,
            cursorBlinking = _settings.EditorCursorBlinking,
            tabSize = _settings.EditorTabSize,
            insertSpaces = _settings.EditorInsertSpaces,
            suggest = _settings.EditorSuggest,
            parameterHints = _settings.EditorParameterHints,
            hover = _settings.EditorHover,
            bracketColorization = _settings.EditorBracketColorization,
            stickyScroll = _settings.EditorStickyScroll,
            occurrencesHighlight = _settings.EditorOccurrencesHighlight,
            autoSave = _settings.AutoSave,
            autoSaveSeconds = _settings.AutoSaveSeconds,
            saveEncoding = _settings.SaveEncoding,
        });
    }

    /// <summary>编辑器里的交互（换行、字号、缩放）改动了设置，同步回来并持久化。</summary>
    private void ApplyEditorState(JsonNode message)
    {
        bool changed = false;

        string? wordWrap = message["wordWrap"]?.GetValue<string>();
        if (wordWrap is not null && wordWrap != _settings.EditorWordWrap)
        {
            _settings.EditorWordWrap = wordWrap;
            changed = true;
        }

        bool? minimap = message["minimap"]?.GetValue<bool>();
        if (minimap is not null && minimap != _settings.EditorMinimap)
        {
            _settings.EditorMinimap = minimap.Value;
            changed = true;
        }

        int? fontSize = message["fontSize"]?.GetValue<int>();
        if (fontSize is not null && fontSize != _settings.EditorFontSize)
        {
            _settings.EditorFontSize = Math.Clamp(fontSize.Value, 8, 40);
            changed = true;
        }

        double? uiZoom = message["uiZoom"]?.GetValue<double>();
        if (uiZoom is not null && Math.Abs(uiZoom.Value - _settings.UiZoom) > 0.001)
        {
            _settings.UiZoom = Math.Clamp(uiZoom.Value, 0.5, 2.0);
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        _settings.Save();
        if (_settingsOpen)
        {
            SettingsViewControl.Refresh(_settings);
        }
    }

    /// <summary>自动保存：直接写盘，不弹对话框。</summary>
    private async Task HandleAutoSaveAsync(JsonNode message)
    {
        string? path = message["path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        string content = message["content"]?.GetValue<string>() ?? string.Empty;
        try
        {
            await File.WriteAllTextAsync(path, content, GetSaveEncoding());
            SendToEditor(new { type = "saved", path, name = Path.GetFileName(path) });
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("自动保存失败", ex.Message);
        }
    }

    /// <summary>按设置选择保存时使用的编码。</summary>
    private Encoding GetSaveEncoding() => _settings.SaveEncoding switch
    {
        "utf8bom" => new UTF8Encoding(true),
        "gbk" => Encoding.GetEncoding(936),
        _ => new UTF8Encoding(false),
    };

    /// <summary>终端面板在编辑器 WebView 里，编辑器没就绪或被欢迎页/设置页挡住时按钮置灰。</summary>
    private void UpdateTerminalButtonState()
    {
        TerminalMenuItem.IsEnabled =
            _editorReady
            && !_practiceOpen
            && EditorWebView.Visibility == Visibility.Visible
            && SettingsOverlay.Visibility != Visibility.Visible;
    }

    /// <summary>XAML 层的快捷键：焦点在文件树、菜单、欢迎页等控件上时生效。</summary>
    private void Shortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // 刷题模式下快捷键不应传给主编辑器（避免误触编译/运行）
        if (_practiceOpen)
        {
            return;
        }

        bool control = sender.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
        bool shift = sender.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);

        string? command = null;
        switch (sender.Key)
        {
            case Windows.System.VirtualKey.F12:
                command = "build";
                break;
            case Windows.System.VirtualKey.F11:
                command = "buildRun";
                break;
            case Windows.System.VirtualKey.F10:
                command = "run";
                break;
            case Windows.System.VirtualKey.F5:
                command = shift ? "debugStop" : "debugContinue";
                break;
            case Windows.System.VirtualKey.F7:
                command = "debugStepOver";
                break;
            case Windows.System.VirtualKey.F8:
                command = "debugStepInto";
                break;
            case Windows.System.VirtualKey.N when control:
                command = "newUntitled";
                break;
            case Windows.System.VirtualKey.O when control:
                command = "openFile";
                break;
            case Windows.System.VirtualKey.S when control && shift:
                command = "saveAs";
                break;
            case Windows.System.VirtualKey.S when control:
                command = "requestSave";
                break;
            case Windows.System.VirtualKey.W when control:
                command = "closeActive";
                break;
        }

        if (command is null)
        {
            return;
        }

        args.Handled = true;
        _ = RunCommandAsync(command);
    }

    /// <summary>设置页打开时用 Esc 关闭设置（焦点落在下拉框等控件上时的兜底）。</summary>
    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_practiceOpen)
        {
            // 刷题模式下 Esc 只负责收起登录浮层，不退出刷题模式
            args.Handled = true;
            PracticeViewControl.HandleEscape();
            return;
        }

        if (!_settingsOpen)
        {
            return;
        }

        args.Handled = true;
        HideSettings();
    }

    /// <summary>Win11 强制窗口圆角；Win10 不支持该属性，调用失败也不影响使用。</summary>
    private void TryApplyRoundedCorners()
    {
        const int DwmWindowCornerPreference = 33;
        const int DwmWindowCornerRound = 2;

        try
        {
            int preference = DwmWindowCornerRound;
            _ = DwmSetWindowAttribute(WindowHandle, DwmWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception)
        {
            // 老系统忽略
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /* ---------------- 编译 / 运行 ---------------- */

    /// <summary>内置工具链路径（随应用打包，用户安装后可直接使用）。</summary>
    private static string BundledCompiler => Path.Combine(AppContext.BaseDirectory, "Assets", "mingw64", "bin", "g++.exe");

    private static string BundledCompilerDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "mingw64", "bin");

    private static string? FindCompiler()
    {
        if (File.Exists(BundledCompiler))
        {
            return BundledCompiler;
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
            {
                continue;
            }

            try
            {
                string candidate = Path.Combine(directory.Trim('"'), "g++.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // PATH 中有非法路径，跳过
            }
        }

        return null;
    }

    /// <summary>把编辑器的编译/运行请求转换成一条 cmd 命令，交给终端执行。</summary>
    private void HostBuild(string kind, string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            ShowHostMessage("当前没有可编译的文件。");
            return;
        }

        if (!File.Exists(sourcePath))
        {
            ShowHostMessage($"文件不存在：{sourcePath}");
            return;
        }

        string? compiler = FindCompiler();
        if (compiler is null)
        {
            ShowHostMessage("未找到 g++。请确认 Assets\\mingw64\\bin\\g++.exe 存在，或系统中已安装 MinGW-w64。");
            return;
        }

        string exePath = Path.ChangeExtension(sourcePath, ".exe");
        string fileName = Path.GetFileName(sourcePath);

        // 运行：像 Dev-C++ 那样弹一个独立控制台窗口，程序结束后显示退出码、时间和 pause
        if (kind == "run")
        {
            if (!File.Exists(exePath))
            {
                ShowHostMessage($"尚未生成可执行文件：{Path.GetFileName(exePath)}\n请先编译（F12）。");
                return;
            }

            StartRunConsole(exePath);
            SendToEditor(new { type = "terminalNote", text = $"[运行] {Path.GetFileName(exePath)} 已在独立控制台窗口中启动。" });
            return;
        }

        // 编译 / 编译并运行：宿主直接调用 g++，把日志回显到终端面板。
        // 不能交给隐藏的 cmd 终端执行，否则它启动的控制台会继承那个隐藏控制台，弹不出窗口。
        _ = RunCompileAsync(kind, sourcePath, exePath, fileName);
    }

    private bool _building;

    /// <summary>执行 g++ 并把输出逐行回显到终端面板；编译成功后按需启动独立控制台窗口。</summary>
    private async Task RunCompileAsync(string kind, string sourcePath, string exePath, string fileName)
    {
        if (_building)
        {
            ShowHostMessage("正在编译，请稍候…");
            return;
        }

        string? compiler = FindCompiler();
        if (compiler is null)
        {
            ShowHostMessage("未找到 g++。请确认 Assets\\mingw64\\bin\\g++.exe 存在，或系统中已安装 MinGW-w64。");
            return;
        }

        // 基础参数保持 -static，保证编译出的 exe 脱离 Assets\mingw64 也能直接运行；
        // 语言标准与警告等级来自设置页，用户自己填的命令追加在最后，可以覆盖默认（如 -std=c++11）
        string extraArguments = _settings.BuildExtraArguments();
        string arguments = $"-g -static{(extraArguments.Length > 0 ? " " + extraArguments : string.Empty)} {Quote(sourcePath)} -o {Quote(exePath)}";
        string banner = kind == "compile" ? $"[编译] {fileName}" : $"[编译并运行] {fileName}";

        _building = true;
        try
        {
            SendToEditor(new { type = "terminalNote", text = banner });
            SendToEditor(new { type = "terminalNote", text = "❯ " + Quote(compiler) + " " + arguments });

            var startInfo = new ProcessStartInfo
            {
                FileName = compiler,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(sourcePath) ?? AppContext.BaseDirectory,
                StandardOutputEncoding = Encoding.GetEncoding(936),
                StandardErrorEncoding = Encoding.GetEncoding(936),
            };

            using Process process = Process.Start(startInfo)!;
            process.OutputDataReceived += (_, e) => SendBuildOutput(e.Data);
            process.ErrorDataReceived += (_, e) => SendBuildOutput(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();
            process.WaitForExit(); // 等异步读取把剩余输出吐完

            int exitCode = process.ExitCode;
            SendBuildOutput($"[编译结束，退出码 {exitCode}]  {DateTime.Now:HH:mm:ss}");

            if (kind != "compile" && exitCode == 0)
            {
                StartRunConsole(exePath);
                SendBuildOutput("[已启动独立控制台窗口运行程序]");
            }
        }
        catch (Exception ex)
        {
            SendBuildOutput($"[编译失败] {ex.Message}");
        }
        finally
        {
            _building = false;
        }
    }

    /// <summary>从任意线程安全地把一行编译输出送到终端面板。</summary>
    private void SendBuildOutput(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() => SendToEditor(new { type = "terminalStdout", text }));
    }

    /// <summary>生成一个带退出码、时间的运行脚本，并写入临时目录。</summary>
    private string WriteRunScript(string exePath)
    {
        string directory = Path.Combine(Path.GetTempPath(), "mnycpp-run");
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, $"run-{DateTime.Now:HHmmssfff}.bat");
        string workDirectory = Path.GetDirectoryName(exePath) ?? directory;

        // 脚本内容按 GBK 写入，才能和中文 Windows 的控制台代码页（936）对上。
        // 运行前记录起始时间，结束后用百分之一秒为单位算出耗时，
        // 输出形如：程序运行完毕，退出码  0 ，运行时间  0.35s
        string content =
            "@echo off\r\n" +
            $"title MnyC++ - {Path.GetFileName(exePath)}\r\n" +
            $"cd /d {Quote(workDirectory)}\r\n" +
            "for /f \"tokens=1-4 delims=:.,\" %%a in (\"%time%\") do set /a t1=(((1%%a%%100*60)+1%%b%%100)*60+1%%c%%100)*100+1%%d%%100\r\n" +
            Quote(exePath) + "\r\n" +
            "set EXITCODE=%errorlevel%\r\n" +
            "for /f \"tokens=1-4 delims=:.,\" %%a in (\"%time%\") do set /a t2=(((1%%a%%100*60)+1%%b%%100)*60+1%%c%%100)*100+1%%d%%100\r\n" +
            "set /a ELAPSED=t2-t1\r\n" +
            "if %ELAPSED% lss 0 set /a ELAPSED+=8640000\r\n" +
            "set /a SECS=%ELAPSED% / 100\r\n" +
            "set /a CS=%ELAPSED% %% 100\r\n" +
            "if %CS% lss 10 set CS=0%CS%\r\n" +
            "echo.\r\n" +
            "echo 程序运行完毕，退出码  %EXITCODE% ，运行时间  %SECS%.%CS%s\r\n" +
            (_settings.PauseOnExit ? "pause\r\n" : string.Empty);

        File.WriteAllText(script, content, Encoding.GetEncoding(936));
        return script;
    }

    private void StartRunConsole(string exePath)
    {
        try
        {
            string script = WriteRunScript(exePath);

            // 不用 ShellExecute：IDE 进程本身没有控制台，CreateProcess 会给 cmd 分配一个新的控制台窗口
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + Quote(script),
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Path.GetTempPath(),
            });
        }
        catch (Exception ex)
        {
            ShowHostMessage($"无法启动程序：{ex.Message}");
        }
    }

    /// <summary>给路径加引号，避免空格、方括号等字符破坏 cmd 命令行。</summary>
    private static string Quote(string value) => "\"" + value + "\"";

    private void ShowHostMessage(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // 弹窗不能在 WebView2 消息回调里直接开，先排到下一个消息循环
        DispatcherQueue.TryEnqueue(() => _ = ShowMessageAsync("提示", text));
    }

    /* ---------------- 终端 ---------------- */

    private sealed class TerminalSession
    {
        public TerminalSession(Process process)
        {
            Process = process;
        }

        public Process Process { get; }
    }

    private void StartTerminal(int id)
    {
        if (id <= 0 || _terminals.ContainsKey(id))
        {
            return;
        }

        // cmd 内部命令按系统 OEM 代码页输出（中文系统是 GBK/936）
        Encoding oemEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        string workingDirectory = _settings.TerminalDirectory == "app"
            ? AppContext.BaseDirectory
            : (_rootFolderPath ?? AppContext.BaseDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // /v:on 开启延迟展开，编译/运行命令才能用 !errorlevel! 取到真实退出码
            Arguments = "/q /v:on",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = oemEncoding,
            StandardErrorEncoding = oemEncoding,
        };

        // 让终端（以及 g++ 生成的可执行文件）能找到内置工具链
        string? path = startInfo.Environment["PATH"];
        startInfo.Environment["PATH"] = BundledCompilerDirectory + ";" + (path ?? string.Empty);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var session = new TerminalSession(process);
        if (!_terminals.TryAdd(id, session))
        {
            return;
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                SendTerminalLine(id, "terminalOutput", e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                SendTerminalLine(id, "terminalOutput", e.Data);
            }
        };
        process.Exited += (_, _) =>
        {
            _terminals.TryRemove(id, out _);
            int code;
            try { code = process.ExitCode; } catch { code = -1; }
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    SendToEditor(new { type = "terminalExit", id, code });
                }
                catch (Exception)
                {
                    // 窗口正在关闭，忽略
                }
            });
            process.Dispose();
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _terminals.TryRemove(id, out _);
            process.Dispose();
            SendToEditor(new { type = "terminalOutput", id, text = "[无法启动终端] " + ex.Message });
            SendToEditor(new { type = "terminalExit", id, code = -1 });
        }
    }

    /// <summary>输出回调在线程池线程上，发消息给 WebView2 前切回 UI 线程。</summary>
    private void SendTerminalLine(int id, string type, string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                SendToEditor(new { type, id, text });
            }
            catch (Exception)
            {
                // 窗口正在关闭，忽略
            }
        });
    }

    private void WriteTerminal(int id, string text)
    {
        if (!_terminals.TryGetValue(id, out TerminalSession? session))
        {
            return;
        }

        try
        {
            session.Process.StandardInput.WriteLine(text);
            session.Process.StandardInput.Flush();
        }
        catch (Exception)
        {
            // 进程已退出
        }
    }

    private void KillTerminal(int id)
    {
        if (!_terminals.TryRemove(id, out TerminalSession? session))
        {
            return;
        }

        try
        {
            session.Process.Kill();
        }
        catch (Exception)
        {
            // 进程已退出
        }
    }

    private void KillAllTerminals()
    {
        foreach (int id in _terminals.Keys)
        {
            KillTerminal(id);
        }
    }
}

/// <summary>文件树节点的显示数据，由 MainWindow.xaml 里的 ItemTemplate 渲染。</summary>
internal sealed class FileNodeView
{
    public FileNodeView(string name, string path, bool isFolder)
    {
        Name = name;
        Path = path;
        IsFolder = isFolder;
    }

    public string Name { get; }

    public string Path { get; }

    public bool IsFolder { get; }

    public string Glyph => IsFolder ? "\uE8B7" : "\uE8A5";
}
