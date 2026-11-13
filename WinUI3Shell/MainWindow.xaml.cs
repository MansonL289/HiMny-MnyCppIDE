using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>超过该字节数的文件使用大文件模式，不再设置打开大小上限。</summary>
    private const long LargeFileModeBytes = 30L * 1024 * 1024;

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

    private static readonly SolidColorBrush SelectedActivityBrush =
        new(Color.FromArgb(0x33, 0x00, 0x78, 0xD4));

    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    public MainWindow()
    {
        InitializeComponent();

        Title = "MnyC++ IDE";
        SystemBackdrop = new MicaBackdrop();

        // 窗口：屏幕工作区的 2/3 大小并居中
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int winW = workArea.Width * 2 / 3, winH = workArea.Height * 2 / 3;
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

        // 窗口关闭时清理所有终端进程
        Closed += (_, _) => KillAllTerminals();
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
                SendCommand("newUntitled");
                break;
            case "w:settings":
                ShowSettings();
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
            await ShowMessageAsync("编辑器初始化失败", ex.Message);
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
                return;
            }

            name = Path.GetFileName(path);
        }

        try
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
            SendToEditor(new { type = "saved", path, name });
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("保存失败", ex.Message);
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

    private Task RunCommandAsync(string command)
    {
        switch (command)
        {
            case "newUntitled":
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
                SendCommand(command);
                return Task.CompletedTask;
            case "openFile":
                return PickAndOpenFileAsync();
            case "openFolder":
                return PickAndOpenFolderAsync();
            case "exit":
                Close();
                return Task.CompletedTask;
            default:
                return Task.CompletedTask;
        }
    }

    /* ---------------- 打开文件 / 文件夹 ---------------- */

    private async Task PickAndOpenFileAsync()
    {
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
            await SendFileToEditorAsync(path, info.Name, info.Length > LargeFileModeBytes);
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

        bool isExplorer = id == "explorer";
        ExplorerPanel.Visibility = isExplorer ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPanel.Visibility = isExplorer ? Visibility.Collapsed : Visibility.Visible;

        if (!isExplorer)
        {
            PlaceholderText.Text = id switch
            {
                "search" => "搜索面板尚未实现。",
                "scm" => "源代码管理面板尚未实现。",
                "run" => "运行与调试面板尚未实现，编译运行功能后续加入。",
                _ => string.Empty,
            };
        }

        UpdateActivityVisuals();
    }

    private void CollapseSidePanel()
    {
        _activeActivity = null;
        SidePanel.Visibility = Visibility.Collapsed;
        UpdateActivityVisuals();
    }

    private void UpdateActivityVisuals()
    {
        foreach (Button button in new[]
        {
            ExplorerButton, SearchButton, SourceControlButton, RunButton,
        })
        {
            bool selected = button.Tag is string id && id == _activeActivity;
            button.Background = selected ? SelectedActivityBrush : TransparentBrush;
            button.Opacity = selected ? 1.0 : 0.75;
        }
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
        TitleTextBlock.Text = _settingsOpen ? "MnyC++ IDE - 设置" : "MnyC++ IDE";
        TitleAppIcon.Visibility = _settingsOpen ? Visibility.Collapsed : Visibility.Visible;
        TitleIconButton.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
        TitleIconButton.IsHitTestVisible = _settingsOpen;
        ToolTipService.SetToolTip(TitleIconButton, _settingsOpen ? "返回主页" : null);
        SettingsButtonIcon.Glyph = "\uE713";
        ToolTipService.SetToolTip(SettingsButton, "设置");
    }

    private void TitleIconButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsOpen)
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
        SendSettingsToEditor();
    }

    /// <summary>把新文件模板同步给编辑器页面。</summary>
    private void SendSettingsToEditor()
    {
        SendToEditor(new
        {
            type = "settings",
            template = _settings.TemplateCode,
        });
    }

    /// <summary>终端面板在编辑器 WebView 里，编辑器没就绪或被欢迎页/设置页挡住时按钮置灰。</summary>
    private void UpdateTerminalButtonState()
    {
        TerminalMenuItem.IsEnabled =
            _editorReady
            && EditorWebView.Visibility == Visibility.Visible
            && SettingsOverlay.Visibility != Visibility.Visible;
    }

    /// <summary>XAML 层的快捷键：焦点在文件树、菜单、欢迎页等控件上时生效。</summary>
    private void Shortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
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
        // 用户在设置里勾选的命令追加在后面，可覆盖默认（如 -std=c++11）
        string extraArguments = _settings.BuildExtraArguments();
        string arguments = $"-std=c++17 -g -static{(extraArguments.Length > 0 ? " " + extraArguments : string.Empty)} {Quote(sourcePath)} -o {Quote(exePath)}";
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

    /// <summary>生成一个带退出码、时间与 pause 的运行脚本，并写入临时目录。</summary>
    private static string WriteRunScript(string exePath)
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
            "echo ----------------------------------------------\r\n" +
            "echo 程序运行完毕，退出码  %EXITCODE% ，运行时间  %SECS%.%CS%s\r\n" +
            "pause\r\n";

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
        string workingDirectory = _rootFolderPath ?? AppContext.BaseDirectory;

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
