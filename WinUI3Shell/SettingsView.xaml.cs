using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;

namespace WinUI3Shell;

/// <summary>设置面板：左侧导航（通用 / 编辑器 / 编译选项 / 新文件模板），右侧 WinUI 3 卡片式内容。</summary>
public sealed partial class SettingsView : UserControl
{
    private const string TemplateHostName = "mny.settings";

    private AppSettings _settings = new();
    private bool _loading;
    private bool _templateReady;
    private bool _templateEditable = true;
    private string _pendingTemplate = string.Empty;

    public SettingsView()
    {
        InitializeComponent();

        ApplyRanges();

        Loaded += SettingsView_Loaded;
    }

    /// <summary>
    /// 设置各数值控件的上下限。RangeBase 校验 Minimum 不得超过 Maximum，
    /// 且 XBF 不保证属性赋值顺序与源码一致，所以统一在代码里先设 Maximum 再设 Minimum。
    /// </summary>
    private void ApplyRanges()
    {
        // 设置上下限会连带修正 Value，触发 ValueChanged，避免被当成用户改动
        _loading = true;
        try
        {
            UiZoomSlider.Maximum = 200;
            UiZoomSlider.Minimum = 50;

            AutoSaveSecondsBox.Maximum = 600;
            AutoSaveSecondsBox.Minimum = 5;

            FontSizeBox.Maximum = 40;
            FontSizeBox.Minimum = 8;

            TabSizeBox.Maximum = 8;
            TabSizeBox.Minimum = 1;

            LargeFileBox.Maximum = 512;
            LargeFileBox.Minimum = 1;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>打开面板时把当前设置填进控件。</summary>
    public void Load(AppSettings settings)
    {
        _loading = true;
        _settings = settings;

        AppThemeBox.SelectedIndex = IndexOfTheme(settings.AppTheme);
        EditorThemeBox.SelectedIndex = IndexOfEditorTheme(settings.EditorTheme);
        UiZoomSlider.Value = Math.Clamp(settings.UiZoom * 100, 50, 200);
        UpdateZoomText();

        AutoSaveSwitch.IsOn = settings.AutoSave;
        AutoSaveSecondsBox.Value = settings.AutoSaveSeconds;
        SaveEncodingBox.SelectedIndex = IndexOfEncoding(settings.SaveEncoding);

        PauseOnExitSwitch.IsOn = settings.PauseOnExit;
        TerminalDirectoryBox.SelectedIndex = settings.TerminalDirectory == "app" ? 1 : 0;

        FontFamilyBox.Text = settings.EditorFontFamily;
        FontSizeBox.Value = settings.EditorFontSize;
        FontLigaturesSwitch.IsOn = settings.EditorFontLigatures;
        IndentBox.SelectedIndex = settings.EditorInsertSpaces ? 0 : 1;
        TabSizeBox.Value = settings.EditorTabSize;

        WordWrapBox.SelectedIndex = IndexOfWordWrap(settings.EditorWordWrap);
        MinimapSwitch.IsOn = settings.EditorMinimap;
        WhitespaceBox.SelectedIndex = IndexOfWhitespace(settings.EditorWhitespace);
        CursorStyleBox.SelectedIndex = IndexOfCursorStyle(settings.EditorCursorStyle);
        CursorBlinkingBox.SelectedIndex = IndexOfCursorBlinking(settings.EditorCursorBlinking);
        LargeFileBox.Value = settings.LargeFileThresholdMB;

        SuggestSwitch.IsOn = settings.EditorSuggest;
        ParameterHintsSwitch.IsOn = settings.EditorParameterHints;
        HoverSwitch.IsOn = settings.EditorHover;
        BracketColorizationSwitch.IsOn = settings.EditorBracketColorization;
        StickyScrollSwitch.IsOn = settings.EditorStickyScroll;
        OccurrencesHighlightSwitch.IsOn = settings.EditorOccurrencesHighlight;

        StandardBox.SelectedIndex = IndexOfStandard(settings.CppStandard);
        WarningBox.SelectedIndex = IndexOfWarning(settings.WarningLevel);

        CompilerFlagsBox.Text = settings.CompilerFlags;
        LinkerFlagsBox.Text = settings.LinkerFlags;

        CompilerFlagsSwitch.IsOn = settings.EnableCompilerFlags;
        LinkerFlagsSwitch.IsOn = settings.EnableLinkerFlags;
        CompilerFlagsBox.IsEnabled = settings.EnableCompilerFlags;
        LinkerFlagsBox.IsEnabled = settings.EnableLinkerFlags;

        InsertTemplateSwitch.IsOn = settings.InsertTemplateOnNewFile;
        SetTemplateEditorEnabled(settings.InsertTemplateOnNewFile);

        PracticeDifficultyBox.SelectedIndex = IndexOfTag(PracticeDifficultyBox, settings.PracticeDifficulty);
        PracticeSortBox.SelectedIndex = IndexOfTag(PracticeSortBox, settings.PracticeSortMode);

        _loading = false;

        SendTemplateToEditor(settings.TemplateCode);
        SendAppearanceToEditor();
    }

    /// <summary>编辑器内的操作（换行、字号等）改变了设置时，把最新值同步回界面。</summary>
    public void Refresh(AppSettings settings)
    {
        _loading = true;
        _settings = settings;

        WordWrapBox.SelectedIndex = IndexOfWordWrap(settings.EditorWordWrap);
        MinimapSwitch.IsOn = settings.EditorMinimap;
        FontSizeBox.Value = settings.EditorFontSize;
        UiZoomSlider.Value = Math.Clamp(settings.UiZoom * 100, 50, 200);
        UpdateZoomText();

        _loading = false;
    }

    /// <summary>把编辑器主题、字体同步给设置页里的模板编辑器，预览效果与主编辑器一致。</summary>
    public void ApplyAppearance(AppSettings settings)
    {
        _settings = settings;
        SendAppearanceToEditor();
    }

    public event Action? SettingsChanged;
    public event Action? CloseRequested;

    private void RaiseChanged()
    {
        if (!_loading)
        {
            SettingsChanged?.Invoke();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            ShowPane(tag);
        }
    }

    private string _activePaneTag = "general";

    /// <summary>页面切换动画时长（毫秒），要和 XAML 里的过渡时长一致。</summary>
    private const int PaneTransitionDuration = 180;

    /// <summary>页面切换时的横向位移（像素）。</summary>
    private const float PaneSlideOffset = 24f;

    private ScrollViewer PaneFor(string tag) => tag switch
    {
        "editor" => EditorPane,
        "compile" => CompilePane,
        "template" => TemplatePane,
        "practice" => PracticePane,
        "about" => AboutPane,
        _ => GeneralPane,
    };

    private void ShowPane(string tag)
    {
        if (tag == _activePaneTag)
        {
            return;
        }

        ScrollViewer outgoingRoot = PaneFor(_activePaneTag);
        ScrollViewer incomingRoot = PaneFor(tag);
        bool leavingTemplate = _activePaneTag == "template";
        _activePaneTag = tag;

        // 模板页的 Monaco 不在 XAML 渲染链上，用遮罩淡出配合
        if (leavingTemplate)
        {
            FadeTemplateEditorVeil(showVeil: true);
        }

        // 旧页面淡出并向左滑出
        outgoingRoot.IsHitTestVisible = false;
        outgoingRoot.Opacity = 0;
        outgoingRoot.Translation = new Vector3(-PaneSlideOffset, 0, 0);

        // 新页面从右侧滑入并淡入
        Canvas.SetZIndex(outgoingRoot, 0);
        Canvas.SetZIndex(incomingRoot, 1);
        SetStartState(incomingRoot, 0, PaneSlideOffset);
        incomingRoot.Visibility = Visibility.Visible;
        incomingRoot.IsHitTestVisible = true;
        incomingRoot.Opacity = 1;
        incomingRoot.Translation = Vector3.Zero;

        if (tag == "template")
        {
            FadeTemplateEditorVeil(showVeil: false);
        }

        if (tag == "about")
        {
            LoadHardwareInfo();
        }

        _ = CollapsePaneLaterAsync(outgoingRoot);
    }

    /// <summary>直接落到动画起始状态，不播过渡，避免切换前多播一次动画。</summary>
    private static void SetStartState(FrameworkElement element, double opacity, float offsetX)
    {
        ScalarTransition? opacityTransition = element.OpacityTransition;
        Vector3Transition? translationTransition = element.TranslationTransition;
        element.OpacityTransition = null;
        element.TranslationTransition = null;
        try
        {
            element.Opacity = opacity;
            element.Translation = new Vector3(offsetX, 0, 0);
        }
        finally
        {
            element.OpacityTransition = opacityTransition;
            element.TranslationTransition = translationTransition;
        }
    }

    /// <summary>
    /// 切换模板页时在编辑器上盖一层它自己的背景色再淡出，切走时淡入盖住。
    /// WebView2 的画面由它自己渲染，XAML 过渡动不了，只能这样配合。
    /// </summary>
    private void FadeTemplateEditorVeil(bool showVeil)
    {
        TemplateEditorVeil.Background = new SolidColorBrush(IsEditorDark()
            ? Windows.UI.Color.FromArgb(255, 0x1E, 0x20, 0x27)
            : Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));

        if (showVeil)
        {
            TemplateEditorVeil.Opacity = 1;
            return;
        }

        // 先无动画地铺满，再淡出，保证每次都从头开始
        ScalarTransition? transition = TemplateEditorVeil.OpacityTransition;
        TemplateEditorVeil.OpacityTransition = null;
        TemplateEditorVeil.Opacity = 1;
        TemplateEditorVeil.OpacityTransition = transition;
        TemplateEditorVeil.Opacity = 0;
    }

    /// <summary>模板编辑器的实际明暗，跟随编辑器主题设置。</summary>
    private bool IsEditorDark()
    {
        if (_settings.EditorTheme == "dark")
        {
            return true;
        }
        if (_settings.EditorTheme == "light")
        {
            return false;
        }
        return ActualTheme == ElementTheme.Dark;
    }

    /// <summary>旧页面淡出结束后再收起，否则动画会被裁掉。</summary>
    private async Task CollapsePaneLaterAsync(ScrollViewer root)
    {
        await Task.Delay(PaneTransitionDuration);

        // 动画期间又切回了这一页，就保持显示
        if (ReferenceEquals(root, PaneFor(_activePaneTag)))
        {
            return;
        }

        root.Visibility = Visibility.Collapsed;
    }

    /* ---------------- 关于 ---------------- */

    private const string CurrentVersion = "Beta_1.2.20261118";
    private const string TagsApi = "https://api.github.com/repos/MansonL289/HiMny-MnyCppIDE/tags";

    private static readonly HttpClient Http = CreateHttp();
    private bool _hardwareLoaded;

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MnyCppIDE/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private void LoadHardwareInfo()
    {
        if (_hardwareLoaded)
        {
            return;
        }
        _hardwareLoaded = true;

        CpuText.Text = "CPU：" + HardwareInfo.CpuName();
        GpuText.Text = "显卡：" + HardwareInfo.GpuName();
        (long total, long avail) = HardwareInfo.Memory();
        MemoryText.Text = total > 0
            ? $"内存：{total / 1024.0:F0} GB（可用 {avail / 1024.0:F0} GB）"
            : "内存：未知";
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckRing.Visibility = Visibility.Visible;
        CheckRing.IsActive = true;
        UpdateText.Text = "正在检查…";

        try
        {
            string json = await Http.GetStringAsync(TagsApi);
            JsonArray? tags = JsonNode.Parse(json)?.AsArray();
            string? latest = tags?.Select(t => t?["name"]?.GetValue<string>())
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

            if (latest == null)
            {
                UpdateText.Text = "没有读取到版本标签。";
            }
            else if (latest.Equals(CurrentVersion, StringComparison.OrdinalIgnoreCase))
            {
                UpdateText.Text = $"已是最新版本（{CurrentVersion}）。";
            }
            else
            {
                UpdateText.Text = $"发现新版本：{latest}（当前 {CurrentVersion}）。";
            }
        }
        catch (Exception)
        {
            UpdateText.Text = "检查失败，请稍后再试，或直接打开上面的更新链接。";
        }
        finally
        {
            CheckRing.IsActive = false;
            CheckRing.Visibility = Visibility.Collapsed;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /* ---------------- 通用 ---------------- */

    private void AppThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.AppTheme = ThemeFromIndex(AppThemeBox.SelectedIndex);
        RaiseChanged();
    }

    private void EditorThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorTheme = EditorThemeFromIndex(EditorThemeBox.SelectedIndex);
        RaiseChanged();
    }

    private void UiZoomSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateZoomText();
        if (_loading)
        {
            return;
        }
        _settings.UiZoom = Math.Round(UiZoomSlider.Value / 100.0, 2);
        RaiseChanged();
    }

    private void UpdateZoomText()
    {
        UiZoomValueText.Text = $"{Math.Round(UiZoomSlider.Value)}%";
    }

    private void AutoSaveSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.AutoSave = AutoSaveSwitch.IsOn;
        RaiseChanged();
    }

    private void AutoSaveSecondsBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.AutoSaveSeconds = (int)Math.Clamp(e.NewValue, 5, 600);
        RaiseChanged();
    }

    private void SaveEncodingBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.SaveEncoding = EncodingFromIndex(SaveEncodingBox.SelectedIndex);
        RaiseChanged();
    }

    private void PauseOnExitSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.PauseOnExit = PauseOnExitSwitch.IsOn;
        RaiseChanged();
    }

    private void TerminalDirectoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.TerminalDirectory = TerminalDirectoryBox.SelectedIndex == 1 ? "app" : "project";
        RaiseChanged();
    }

    /* ---------------- 编辑器 ---------------- */

    private void FontFamilyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFontFamilySetting();
    }

    private void FontFamilyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        UpdateFontFamilySetting();
    }

    /// <summary>可编辑下拉框没有 TextChanged，选中项或失焦时统一取当前文本。</summary>
    private void UpdateFontFamilySetting()
    {
        if (_loading)
        {
            return;
        }

        string font = FontFamilyBox.Text.Trim();
        _settings.EditorFontFamily = font.Length > 0 ? font : AppSettings.DefaultEditorFont;
        RaiseChanged();
    }

    private void FontSizeBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorFontSize = (int)Math.Clamp(e.NewValue, 8, 40);
        RaiseChanged();
    }

    private void FontLigaturesSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorFontLigatures = FontLigaturesSwitch.IsOn;
        RaiseChanged();
    }

    private void IndentBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorInsertSpaces = IndentBox.SelectedIndex == 0;
        RaiseChanged();
    }

    private void TabSizeBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorTabSize = (int)Math.Clamp(e.NewValue, 1, 8);
        RaiseChanged();
    }

    private void WordWrapBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorWordWrap = WordWrapFromIndex(WordWrapBox.SelectedIndex);
        RaiseChanged();
    }

    private void MinimapSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorMinimap = MinimapSwitch.IsOn;
        RaiseChanged();
    }

    private void WhitespaceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorWhitespace = WhitespaceFromIndex(WhitespaceBox.SelectedIndex);
        RaiseChanged();
    }

    private void CursorStyleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorCursorStyle = CursorStyleFromIndex(CursorStyleBox.SelectedIndex);
        RaiseChanged();
    }

    private void CursorBlinkingBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorCursorBlinking = CursorBlinkingFromIndex(CursorBlinkingBox.SelectedIndex);
        RaiseChanged();
    }

    private void LargeFileBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.LargeFileThresholdMB = (int)Math.Clamp(e.NewValue, 1, 512);
        RaiseChanged();
    }

    private void SuggestSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorSuggest = SuggestSwitch.IsOn;
        RaiseChanged();
    }

    private void ParameterHintsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorParameterHints = ParameterHintsSwitch.IsOn;
        RaiseChanged();
    }

    private void HoverSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorHover = HoverSwitch.IsOn;
        RaiseChanged();
    }

    private void BracketColorizationSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorBracketColorization = BracketColorizationSwitch.IsOn;
        RaiseChanged();
    }

    private void StickyScrollSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorStickyScroll = StickyScrollSwitch.IsOn;
        RaiseChanged();
    }

    private void OccurrencesHighlightSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.EditorOccurrencesHighlight = OccurrencesHighlightSwitch.IsOn;
        RaiseChanged();
    }

    /* ---------------- 编译选项 ---------------- */

    private void StandardBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.CppStandard = StandardFromIndex(StandardBox.SelectedIndex);
        RaiseChanged();
    }

    private void WarningBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.WarningLevel = WarningFromIndex(WarningBox.SelectedIndex);
        RaiseChanged();
    }

    private void CompilerFlags_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.CompilerFlags = CompilerFlagsBox.Text;
        RaiseChanged();
    }

    private void LinkerFlags_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.LinkerFlags = LinkerFlagsBox.Text;
        RaiseChanged();
    }

    private void CompilerFlagsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.EnableCompilerFlags = CompilerFlagsSwitch.IsOn;
        CompilerFlagsBox.IsEnabled = CompilerFlagsSwitch.IsOn;
        RaiseChanged();
    }

    private void LinkerFlagsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.EnableLinkerFlags = LinkerFlagsSwitch.IsOn;
        LinkerFlagsBox.IsEnabled = LinkerFlagsSwitch.IsOn;
        RaiseChanged();
    }

    /* ---------------- 新文件模板 ---------------- */

    private void InsertTemplateSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        SetTemplateEditorEnabled(InsertTemplateSwitch.IsOn);
        if (_loading)
        {
            return;
        }
        _settings.InsertTemplateOnNewFile = InsertTemplateSwitch.IsOn;
        RaiseChanged();
    }

    /// <summary>未勾选时模板编辑器只读（WebView2 没有 IsEnabled，改为让页面里的 Monaco 只读）。</summary>
    private void SetTemplateEditorEnabled(bool enabled)
    {
        _templateEditable = enabled;
        TemplateEditorBorder.Opacity = enabled ? 1.0 : 0.5;
        if (!_templateReady)
        {
            return;
        }

        string json = JsonSerializer.Serialize(new { type = "setEditable", editable = enabled });
        TemplateWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    /* ---------------- 模板编辑器 ---------------- */

    private async void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await TemplateWebView.EnsureCoreWebView2Async();
            CoreWebView2 core = TemplateWebView.CoreWebView2;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(
                TemplateHostName,
                Path.Combine(AppContext.BaseDirectory, "Assets", "monaco"),
                CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += TemplateWebMessageReceived;
            core.Navigate($"https://{TemplateHostName}/template.html?v={DateTime.Now.Ticks}");
        }
        catch
        {
            // 编辑器初始化失败时保持空白，不影响其它设置项
        }
    }

    private void TemplateWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
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

        switch (message["type"]?.GetValue<string>())
        {
            case "ready":
                _templateReady = true;
                SendTemplateToEditor(_settings.TemplateCode);
                SendAppearanceToEditor();
                SetTemplateEditorEnabled(_templateEditable);
                break;

            case "templateChanged":
                _settings.TemplateCode = message["text"]?.GetValue<string>() ?? string.Empty;
                RaiseChanged();
                break;

            case "escape":
                CloseRequested?.Invoke();
                break;
        }
    }

    private void SendTemplateToEditor(string text)
    {
        _pendingTemplate = text;
        if (!_templateReady)
        {
            return;
        }

        string json = JsonSerializer.Serialize(new
        {
            type = "setTemplate",
            text = _pendingTemplate,
        });
        TemplateWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void SendAppearanceToEditor()
    {
        if (!_templateReady)
        {
            return;
        }

        string json = JsonSerializer.Serialize(new
        {
            type = "appearance",
            theme = _settings.EditorTheme,
            fontFamily = _settings.EditorFontFamily,
            // 预览区高度只有 240，字号过大会把代码撑出可视区域，这里限高只影响预览
            fontSize = Math.Clamp(_settings.EditorFontSize, 10, 18),
        });
        TemplateWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    /// <summary>打开面板后把键盘焦点放到导航区，Esc 才能生效。</summary>
    public void FocusFirst()
    {
        NavView.Focus(FocusState.Programmatic);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke();
        }
    }

    /* ---------------- 下拉框索引与取值互转 ---------------- */

    private static string ThemeFromIndex(int index) => index switch
    {
        1 => "light",
        2 => "dark",
        _ => "system",
    };

    private static int IndexOfTheme(string theme) => theme switch
    {
        "light" => 1,
        "dark" => 2,
        _ => 0,
    };

    private static string EditorThemeFromIndex(int index) => index switch
    {
        1 => "dark",
        2 => "light",
        _ => "system",
    };

    private static int IndexOfEditorTheme(string theme) => theme switch
    {
        "dark" => 1,
        "light" => 2,
        _ => 0,
    };

    private static string EncodingFromIndex(int index) => index switch
    {
        1 => "utf8bom",
        2 => "gbk",
        _ => "utf8",
    };

    private static int IndexOfEncoding(string encoding) => encoding switch
    {
        "utf8bom" => 1,
        "gbk" => 2,
        _ => 0,
    };

    private static string WordWrapFromIndex(int index) => index switch
    {
        1 => "bounded",
        2 => "wordWrapColumn",
        _ => "off",
    };

    private static int IndexOfWordWrap(string wordWrap) => wordWrap switch
    {
        "bounded" => 1,
        "wordWrapColumn" => 2,
        _ => 0,
    };

    private static string WhitespaceFromIndex(int index) => index switch
    {
        1 => "boundary",
        2 => "selection",
        3 => "trailing",
        4 => "all",
        _ => "none",
    };

    private static int IndexOfWhitespace(string whitespace) => whitespace switch
    {
        "boundary" => 1,
        "selection" => 2,
        "trailing" => 3,
        "all" => 4,
        _ => 0,
    };

    private static string CursorStyleFromIndex(int index) => index switch
    {
        1 => "block",
        2 => "underline",
        3 => "line-thin",
        4 => "block-outline",
        5 => "underline-thin",
        _ => "line",
    };

    private static int IndexOfCursorStyle(string style) => style switch
    {
        "block" => 1,
        "underline" => 2,
        "line-thin" => 3,
        "block-outline" => 4,
        "underline-thin" => 5,
        _ => 0,
    };

    private static string CursorBlinkingFromIndex(int index) => index switch
    {
        1 => "solid",
        2 => "phase",
        3 => "fade",
        4 => "explicit",
        _ => "blink",
    };

    private static int IndexOfCursorBlinking(string blinking) => blinking switch
    {
        "solid" => 1,
        "phase" => 2,
        "fade" => 3,
        "explicit" => 4,
        _ => 0,
    };

    private static string StandardFromIndex(int index) => index switch
    {
        0 => "c++11",
        1 => "c++14",
        2 => "c++17",
        3 => "c++20",
        _ => "default",
    };

    private static int IndexOfStandard(string standard) => standard switch
    {
        "c++11" => 0,
        "c++14" => 1,
        "c++17" => 2,
        "c++20" => 3,
        _ => 4,
    };

    private static string WarningFromIndex(int index) => index switch
    {
        1 => "wall",
        2 => "wextra",
        _ => "default",
    };

    private static int IndexOfWarning(string warning) => warning switch
    {
        "wall" => 1,
        "wextra" => 2,
        _ => 0,
    };

    /// <summary>按 ComboBoxItem.Tag 找选中项下标，找不到时回落到 0。</summary>
    private static int IndexOfTag(ComboBox box, string tag)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item && item.Tag is string t &&
                string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return 0;
    }

    /// <summary>取 ComboBox 当前选中项的 Tag 字符串。</summary>
    private static string SelectedTag(ComboBox box, string fallback) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    /* ---------------- 刷题模式 ---------------- */

    private void PracticeDifficultyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.PracticeDifficulty = SelectedTag(PracticeDifficultyBox, "any");
        RaiseChanged();
    }

    private void PracticeSortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.PracticeSortMode = SelectedTag(PracticeSortBox, "SubmitAsc");
        RaiseChanged();
    }
}
