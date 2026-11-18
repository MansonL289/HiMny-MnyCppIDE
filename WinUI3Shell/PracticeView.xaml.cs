using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using WinUI3Shell.Oj;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace WinUI3Shell;

/// <summary>
/// 刷题模式：搜索各大 OJ → 抓取题面 → Markdown/LaTeX 展示 + 草稿编辑。
/// 所有抓取都在隐藏的 WebView2 里进行（真实内核 + 伪装 UA）。
/// </summary>
public sealed partial class PracticeView : UserControl
{
    private const string PracticeHostName = "practice.local";

    private readonly OjConfig _config = OjConfig.Load();
    private readonly OjBrowser _fetchBrowser;
    private readonly ObservableCollection<OjProblemItem> _results = new();
    private readonly ObservableCollection<OjProblemItem> _starred = new();
    private readonly ObservableCollection<OjProblemItem> _trainingProblems = new();
    private readonly ObservableCollection<LuoguTrainingSummary> _trainings = new();
    private readonly ObservableCollection<LuoguCategory> _categories = new();
    private readonly ObservableCollection<LuoguCategory> _srqcCategories = new();
    private readonly ObservableCollection<LuoguCategory> _monthCategories = new();
    private readonly ObservableCollection<LuoguCategory> _contestCategories = new();
    private readonly ObservableCollection<LuoguCategory> _bookCategories = new();
    private readonly ObservableCollection<LuoguCategory> _selectCategories = new();
    private readonly ObservableCollection<OjProblemItem> _visibleTrainingProblems = new();
    private readonly ObservableCollection<OjProblemItem> _trainingSearchResults = new();

    /// <summary>板块内跨题单搜题：HTTP 索引 + 防抖计时 + 取消源。</summary>
    private readonly LuoguIndex _luoguIndex;
    private DispatcherQueueTimer? _trainingSearchTimer;
    private CancellationTokenSource? _trainingSearchCts;
    private readonly ObservableCollection<OjSample> _samples = new();
    private readonly HashSet<string> _seenItems = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>板块列表本次运行只抓一次。</summary>
    private bool _categoriesLoaded;
    private LuoguCategory? _currentCategory;
    private int _trainingPage = 1;
    private int _trainingTotal;
    private bool _trainingLoading;
    private LuoguTrainingSummary? _currentTraining;

    /// <summary>题目详情是从哪个列表打开的，返回时回到对应面板。</summary>
    private ProblemOrigin _problemOrigin = ProblemOrigin.Results;

    /// <summary>本次运行中已确认登录过的 OJ：页面判断不了登录态时用它避免重复提示。</summary>
    private readonly HashSet<string> _loggedInOjs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本次打开题目是否已自动弹过登录窗（含用户手动关闭），避免同一题反复弹。</summary>
    private bool _loginAutoPrompted;
    private readonly List<string> _pendingProblemMessages = new();
    private readonly List<string> _pendingEditorMessages = new();

    private AppSettings _settings = AppSettings.Load();
    private OjDefinition? _currentOj;
    private OjProblemItem? _currentItem;

    /// <summary>题面翻译用的 HTTP 客户端（免费接口，无需密钥）。</summary>
    private static readonly HttpClient TranslateHttp = CreateTranslateHttp();

    /// <summary>当前题面的原始 Markdown / 译文，以及是否正在显示译文。</summary>
    private string _problemMarkdown = string.Empty;
    private string _translatedMarkdown = string.Empty;
    private string _problemViewerTitle = string.Empty;
    private bool _showingTranslation;
    private readonly Dictionary<string, string> _translationCache = new(StringComparer.OrdinalIgnoreCase);
    private string _currentLanguage = "cpp";
    private int _searchToken;
    private int _problemToken;
    private CancellationTokenSource? _cancelSource;
    private bool _problemViewerInitialized;
    private bool _problemViewerReady;
    private bool _editorInitialized;
    private bool _editorReady;
    private bool _loginViewInitialized;
    private bool _loginChecking;

    /// <summary>浮层当前是提交页而不是登录页：提交页跳转不能被当成登录成功而自动关闭。</summary>
    private bool _overlaySubmitMode;

    /// <summary>登录页落到只含脚本的验证页时自动重载的次数，避免无限刷新。</summary>
    private int _loginReloadCount;
    private bool _captchaViewInitialized;
    private TaskCompletionSource<bool>? _captchaTcs;
    private TaskCompletionSource<string>? _codeTcs;
    private bool _isDark;
    private DispatcherQueueTimer? _loginTimer;

    /// <summary>刷题页缩放比例：题面与草稿两侧同步，宿主侧按钮保持原大小。</summary>
    private double _uiZoom = 1;

    public PracticeView()
    {
        InitializeComponent();

        _fetchBrowser = new OjBrowser(FetchWebView)
        {
            UserAgent = _config.UserAgent,
            PreludeScript = OjScripts.Prelude,
        };

        _luoguIndex = new LuoguIndex(_config.UserAgent);

        ResultList.ItemsSource = _results;
        StarredList.ItemsSource = _starred;
        TrainingGrid.ItemsSource = _trainings;
        SrqcGrid.ItemsSource = _srqcCategories;
        MonthGrid.ItemsSource = _monthCategories;
        ContestGrid.ItemsSource = _contestCategories;
        BookGrid.ItemsSource = _bookCategories;
        SelectGrid.ItemsSource = _selectCategories;
        TrainingProblemList.ItemsSource = _visibleTrainingProblems;
        TrainingSearchResultList.ItemsSource = _trainingSearchResults;

        _trainingSearchTimer = DispatcherQueue.CreateTimer();
        _trainingSearchTimer.Interval = TimeSpan.FromMilliseconds(600);
        _trainingSearchTimer.IsRepeating = false;
        _trainingSearchTimer.Tick += (_, _) => _ = RunCategorySearchAsync();

        _isDark = ActualTheme == ElementTheme.Dark;
        ApplyOjIcons();
        SizeChanged += PracticeRoot_SizeChanged;
        ActualThemeChanged += (_, _) =>
        {
            _isDark = ActualTheme == ElementTheme.Dark;
            ApplyOjIcons();
            RefreshResultIcons();
            RefreshTrainingIcons();
            if (_currentItem is not null)
            {
                ProblemOjIcon.Source = OjIcons.Get(_currentItem.OjId, _isDark);
            }

            SendTheme();
        };
    }

    /// <summary>首页筛选图标：按明暗主题换成白色/黑色单色图。</summary>
    private void ApplyOjIcons()
    {
        LuoguIcon.Source = OjIcons.Get("luogu", _isDark);
        AtCoderIcon.Source = OjIcons.Get("atcoder", _isDark);
        NowCoderIcon.Source = OjIcons.Get("nowcoder", _isDark);
    }

    private void RefreshResultIcons()
    {
        foreach (OjProblemItem item in _results)
        {
            item.Icon = OjIcons.Get(item.OjId, _isDark);
        }
    }

    private void RefreshTrainingIcons()
    {
        foreach (OjProblemItem item in _trainingProblems)
        {
            item.Icon = OjIcons.Get(item.OjId, _isDark);
        }
    }

    /* ---------------- 缩放：题面与草稿同步，宿主按钮不缩放 ---------------- */

    private void ChangeUiZoom(int delta)
    {
        double next = delta == 0 ? 1 : Math.Round(_uiZoom + delta * 0.1, 2);
        _uiZoom = Math.Min(2, Math.Max(0.5, next));
        ApplyUiZoom();
    }

    private void ApplyUiZoom()
    {
        SendProblemMessage(new { type = "zoom", zoom = _uiZoom });
        SendEditorMessage(new { type = "zoom", zoom = _uiZoom });
    }

    /// <summary>焦点在宿主控件（搜索框等）上时的 Ctrl + '+/-/0'。</summary>
    private void PracticeRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(CoreVirtualKeyStates.Down))
        {
            return;
        }

        switch ((int)e.Key)
        {
            case 187: // '=' 与 '+'
            case 107: // 小键盘 +
                ChangeUiZoom(1);
                e.Handled = true;
                break;
            case 189: // '-'
            case 109: // 小键盘 -
                ChangeUiZoom(-1);
                e.Handled = true;
                break;
            case 48: // 0 恢复默认
                ChangeUiZoom(0);
                e.Handled = true;
                break;
        }
    }

    /// <summary>登录 / 提交浮层占窗口 4/5。</summary>
    private void PracticeRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLoginOverlaySize();
    }

    private void UpdateLoginOverlaySize()
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        LoginBorder.Width = Math.Max(360, w * 0.8);
        LoginBorder.Height = Math.Max(280, h * 0.8);
    }

    /// <summary>面板入场动画：淡入 + 轻微上移。</summary>
    private static void PlayEnter(UIElement target, TranslateTransform? slide = null)
    {
        // 先设动画初始状态，避免元素在动画播放前以最终状态闪现
        target.Opacity = 0;
        if (slide is not null)
        {
            slide.Y = 14;
        }

        var storyboard = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, target);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        if (slide is not null)
        {
            var move = new DoubleAnimation
            {
                From = 14,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(340),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(move, slide);
            Storyboard.SetTargetProperty(move, "Y");
            storyboard.Children.Add(move);
        }

        // 元素从 Collapsed 变为 Visible 的同帧直接 Begin 可能不会播放动画，
        // 延迟到布局/渲染更新后再播放，确保淡入与滑动生效。
        target.DispatcherQueue.TryEnqueue(() => storyboard.Begin());
    }

    private static string ViewerPageUrl => $"https://{PracticeHostName}/practice/problem.html";

    private static string EditorPageUrl => $"https://{PracticeHostName}/practice/editor.html";

    /* ---------------- 主窗口调用 ---------------- */

    /// <summary>进入刷题模式时调用。</summary>
    public void Activate(AppSettings settings)
    {
        _settings = settings;
        _isDark = ActualTheme == ElementTheme.Dark;
        // 每次进入都换新的取消源，旧的在 Suspend 时已取消
        _cancelSource = new CancellationTokenSource();
        if (ProblemPanel.Visibility != Visibility.Visible)
        {
            HomePanel.Visibility = Visibility.Visible;
            PlayEnter(HomeStack, HomeStackTransform);
            SearchBox.Focus(FocusState.Programmatic);
        }

        ApplySettingsToEditor();
        SendTheme();
        ApplyDefaultSortMode();
    }

    /// <summary>把设置里的默认排序应用到结果页排序下拉框。</summary>
    private void ApplyDefaultSortMode()
    {
        string mode = _settings.PracticeSortMode;
        for (int i = 0; i < SortComboBox.Items.Count; i++)
        {
            if (SortComboBox.Items[i] is ComboBoxItem item && item.Tag is string tag && tag == mode)
            {
                SortComboBox.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>离开刷题模式时调用：取消搜索/抓题、停掉登录轮询，收起浮层。</summary>
    public void Suspend()
    {
        // 取消所有进行中的搜索和抓题
        _cancelSource?.Cancel();
        _cancelSource?.Dispose();
        _cancelSource = null;
        _trainingSearchCts?.Cancel();
        _trainingSearchTimer?.Stop();
        _searchToken++;
        _problemToken++;
        SearchLoadingOverlay.Visibility = Visibility.Collapsed;
        CloseLogin();
    }

    /// <summary>Esc：先收起登录浮层，再按层级退回上一页。</summary>
    public void HandleEscape()
    {
        if (LoginOverlay.Visibility == Visibility.Visible)
        {
            CloseLogin();
            return;
        }

        if (TrainingDetailPanel.Visibility == Visibility.Visible)
        {
            TrainingDetailBack_Click(this, new RoutedEventArgs());
            return;
        }

        if (TrainingListPanel.Visibility == Visibility.Visible)
        {
            TrainingListBack_Click(this, new RoutedEventArgs());
            return;
        }

        if (TrainingPanel.Visibility == Visibility.Visible)
        {
            TrainingBackToHome_Click(this, new RoutedEventArgs());
        }
    }

    private void CloseLogin()
    {
        _loginTimer?.Stop();
        _loginTimer = null;
        _overlaySubmitMode = false;
        LoginRing.IsActive = false;
        LoginRing.Visibility = Visibility.Collapsed;
        LoginOverlay.Visibility = Visibility.Collapsed;
        // 用户主动关闭登录窗后，本题不再自动弹出，避免打扰看题。
        _loginAutoPrompted = true;
    }

    /// <summary>设置页里的外观改动实时同步到草稿编辑器。</summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        ApplySettingsToEditor();
    }

    /* ---------------- 搜索 ---------------- */

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            try
            {
                await RunSearchAsync();
            }
            catch (OperationCanceledException)
            {
                // 离开刷题模式时取消搜索，正常退出
            }
        }
    }

    private async void SearchSubmit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunSearchAsync();
        }
        catch (OperationCanceledException)
        {
            // 离开刷题模式时取消搜索，正常退出
        }
    }

    private void OjToggle_Click(object sender, RoutedEventArgs e)
    {
        // 允许关掉全部平台，点搜索时再提示
        OjSourceHint.Visibility = Visibility.Collapsed;

        // 点击提示：告诉用户这次改动让哪些平台参与搜索
        if (sender is ToggleButton toggle && toggle.Tag is string ojId)
        {
            string name = _config.Find(ojId)?.Name ?? ojId;
            bool included = toggle.IsChecked == true;
            ShowOjToggleTip(included ? $"{name} 内的题目会进行搜索" : $"{name} 内的题目不会被搜索到");
        }
    }

    private DispatcherTimer? _ojTipTimer;

    /// <summary>在图标下方弹一个 2 秒自动消失的轻提示（不拦截输入，点击可穿透）。</summary>
    private void ShowOjToggleTip(string message)
    {
        _ojTipTimer?.Stop();
        OjTipText.Text = message;
        OjTipBubble.Visibility = Visibility.Visible;

        _ojTipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _ojTipTimer.Tick += (_, _) =>
        {
            OjTipBubble.Visibility = Visibility.Collapsed;
            _ojTipTimer?.Stop();
        };
        _ojTipTimer.Start();
    }

    /* ---------------- 随机抽题 ---------------- */

    private static readonly Random _rng = new();

    /// <summary>洛谷题单的题数信息（按难度缓存），随机抽题时省掉一次列表页抓取。</summary>
    private readonly Dictionary<string, (int Total, int PerPage)> _luoguListInfo = new();

    /// <summary>把难度档位映射成一个具体的洛谷难度值（0-7），用于服务端过滤。</summary>
    private static int PickLuoguDifficulty(string difficulty)
    {
        int[] pool = difficulty switch
        {
            "easy" => new[] { 1 },
            "medium" => new[] { 2, 3 },
            "hard" => new[] { 4, 5 },
            "expert" => new[] { 6, 7 },
            _ => new[] { 0, 1, 2, 3, 4, 5, 6, 7 },
        };
        return pool[_rng.Next(pool.Length)];
    }

    /// <summary>
    /// 先切到题目加载页，抽题的题单请求在加载页后面进行，
    /// 避免点「随机抽题」后长时间停留在主页、看起来像没反应。
    /// </summary>
    private async Task ShowRandomLoadingPageAsync()
    {
        _problemOrigin = ProblemOrigin.Home;
        HomePanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        TrainingPanel.Visibility = Visibility.Collapsed;
        TrainingListPanel.Visibility = Visibility.Collapsed;
        TrainingDetailPanel.Visibility = Visibility.Collapsed;
        ProblemPanel.Visibility = Visibility.Visible;
        PlayEnter(ProblemPanel, ProblemTransform);

        ProblemOjText.Text = "题库";
        ProblemOjIcon.Source = null;
        ProblemTitleText.Text = "随机抽题";
        ProblemStatusText.Text = "正在随机抽题…";
        TranslateButton.Visibility = Visibility.Collapsed;
        TranslateButton.Content = "翻译";
        _problemMarkdown = string.Empty;
        _translatedMarkdown = string.Empty;
        _showingTranslation = false;
        _samples.Clear();

        await Task.Yield();
        await EnsureProblemViewerAsync();
        SendProblemMessage(new { type = "loading", text = "正在随机抽题…" });
    }

    private async void RandomProblem_Click(object sender, RoutedEventArgs e)
    {
        // 随机抽题固定走洛谷（题单支持分页和难度筛选，题库最全）
        OjDefinition? luogu = _config.Ojs.FirstOrDefault(
            o => o.Id.Equals("luogu", StringComparison.OrdinalIgnoreCase));
        if (luogu is null)
        {
            ShowOjToggleTip("洛谷题库配置缺失");
            return;
        }

        string difficulty = _settings.PracticeDifficulty;
        int luoguDifficulty = difficulty == "any" ? -1 : PickLuoguDifficulty(difficulty);
        CancellationToken token = _cancelSource?.Token ?? default;

        RandomProblemButton.IsEnabled = false;
        try
        {
            // 立即跳转到加载页，下面的题单抓取在加载页后面进行
            await ShowRandomLoadingPageAsync();

            (OjProblemItem? picked, string lastError) =
                await PickRandomLuoguAsync(luogu, luoguDifficulty, token);
            if (picked is null)
            {
                FailProblem(lastError.Length > 0 ? lastError : "没抽到题目，再试一次");
                return;
            }

            _problemOrigin = ProblemOrigin.Home;
            await OpenProblemAsync(picked);
        }
        catch (OperationCanceledException)
        {
            // 离开刷题模式会取消抽题；此时已经跳到加载页，给个明确状态而不是一直转
            FailProblem("随机抽题已取消");
        }
        catch (Exception)
        {
            FailProblem("随机抽题失败，请稍后重试");
        }
        finally
        {
            RandomProblemButton.IsEnabled = true;
        }
    }

    /// <summary>洛谷题单支持分页和难度筛选，随机取一页里的随机一条。</summary>
    private async Task<(OjProblemItem? Item, string Error)> PickRandomLuoguAsync(
        OjDefinition oj, int luoguDifficulty, CancellationToken token)
    {
        string WithDifficulty(string url) => luoguDifficulty >= 0 ? url + "&difficulty=" + luoguDifficulty : url;

        // 1. 只有第一次抽题需要先拿总题数/每页题数，之后直接用缓存，少抓一次列表页
        string cacheKey = luoguDifficulty.ToString();
        JsonArray? firstItems = null;
        if (!_luoguListInfo.TryGetValue(cacheKey, out (int Total, int PerPage) info))
        {
            JsonNode? node = await _fetchBrowser.RunAsync(
                WithDifficulty(oj.BuildSearchUrl(string.Empty, 1)),
                oj.SearchScript,
                oj.SettleMs,
                oj.DisableJs,
                token);

            if (node?["items"] is not JsonArray listItems || listItems.Count == 0)
            {
                return (null, "洛谷题单加载失败，请稍后重试");
            }

            firstItems = listItems;
            info = (
                Math.Max(listItems.Count, node?["count"]?.GetValue<int>() ?? listItems.Count),
                Math.Max(1, node?["perPage"]?.GetValue<int>() ?? listItems.Count));
            _luoguListInfo[cacheKey] = info;
        }

        int perPage = info.PerPage;
        int pages = Math.Max(1, (info.Total + perPage - 1) / perPage);

        // 2. 随机页 + 随机位置，直接打开抽到的题目
        int page = _rng.Next(1, pages + 1);
        int index = _rng.Next(0, perPage);

        JsonArray items = firstItems ?? new JsonArray();
        if (firstItems is null || page != 1)
        {
            JsonNode? pageNode = await _fetchBrowser.RunAsync(
                WithDifficulty(oj.BuildSearchUrl(string.Empty, page)),
                oj.SearchScript,
                oj.SettleMs,
                oj.DisableJs,
                token);

            if (pageNode?["items"] is not JsonArray pageItems || pageItems.Count == 0)
            {
                return (null, "洛谷题单加载失败，请稍后重试");
            }

            items = pageItems;
        }

        JsonNode? row = items[Math.Min(index, items.Count - 1)];
        OjProblemItem? item = BuildRandomItem(oj, row);
        return (item, item is null ? "没抽到题目，再试一次" : string.Empty);
    }

    private OjProblemItem? BuildRandomItem(OjDefinition oj, JsonNode? row)
    {
        string url = row?["url"]?.ToString() ?? string.Empty;
        string title = row?["title"]?.ToString() ?? string.Empty;
        if (url.Length == 0 || title.Length == 0)
        {
            return null;
        }

        return new OjProblemItem
        {
            OjId = oj.Id,
            OjName = oj.Name,
            Id = row?["id"]?.ToString() ?? string.Empty,
            Title = title.Trim(),
            Url = url,
            Meta = row?["meta"]?.ToString() ?? string.Empty,
            Difficulty = row?["difficulty"]?.GetValue<int>() ?? -1,
            Icon = OjIcons.Get(oj.Id, _isDark),
        };
    }

    private async Task RunSearchAsync()
    {
        string query = SearchBox.Text.Trim();
        List<OjDefinition> selected = SelectedOjs();
        if (_config.Ojs.Count == 0)
        {
            ShowResults();
            ResultQueryText.Text = "刷题模式";
            ResultStatusText.Text = "未找到 Assets/oj/oj-config.json，无法读取 OJ 配置";
            return;
        }

        if (selected.Count == 0)
        {
            // 没有勾选任何平台时留在主页，在搜索源下面提示
            OjSourceHint.Visibility = Visibility.Visible;
            return;
        }

        OjSourceHint.Visibility = Visibility.Collapsed;

        if (query.Length == 0)
        {
            ResultStatusText.Text = "请输入要搜索的题目关键词或题号";
            return;
        }

        int token = ++_searchToken;
        ShowResults();
        ResultQueryText.Text = $"“{query}”的搜索结果";
        ResultStatusText.Text = "正在搜索…";
        EmptyStateText.Visibility = Visibility.Collapsed;
        SearchLoadingOverlay.Visibility = Visibility.Visible;
        SearchLoadingText.Text = "正在搜索…";
        _results.Clear();
        _seenItems.Clear();

        var failed = new List<string>();
        bool captchaHit = false;

        async Task<(int Added, string Failure, bool Captcha, bool StopPaging)> SearchOneAsync(OjDefinition oj, int page)
        {
            JsonNode? node;
            try
            {
                node = await _fetchBrowser.RunAsync(oj.BuildSearchUrl(query, page), oj.SearchScript, oj.SettleMs, oj.DisableJs, _cancelSource?.Token ?? default);
            }
            catch (OperationCanceledException)
            {
                return (0, string.Empty, false, true);
            }
            if (token != _searchToken)
            {
                return (0, string.Empty, false, true);
            }

            if (node is null)
            {
                return (0, oj.Name + "（连接失败）", false, true);
            }

            // 洛谷等站点会在请求过快时弹验证码：把当前页面交给用户手动完成
            if (node["captcha"] is not null && node["captcha"]!.GetValue<bool>())
            {
                return (0, string.Empty, true, true);
            }

            string error = node["error"]?.ToString() ?? string.Empty;
            if (node["items"] is not JsonArray items)
            {
                return (0, error.Length > 0 ? oj.Name + "（" + error + "）" : oj.Name + "（页面解析失败）", false, true);
            }

            int added = 0;
            foreach (JsonNode? row in items)
            {
                string url = row?["url"]?.ToString() ?? string.Empty;
                string title = row?["title"]?.ToString() ?? string.Empty;
                if (url.Length == 0 || title.Length == 0)
                {
                    continue;
                }

                if (!_seenItems.Add(oj.Id + "|" + url))
                {
                    continue;
                }

                long submit = row?["submitCount"]?.GetValue<long>() ?? 0;
                double pass = row?["passRate"]?.GetValue<double>() ?? -1;
                int diff = row?["difficulty"]?.GetValue<int>() ?? -1;

                _results.Add(new OjProblemItem
                {
                    OjId = oj.Id,
                    OjName = oj.Name,
                    Id = row?["id"]?.ToString() ?? string.Empty,
                    Title = title.Trim(),
                    Url = url,
                    Meta = row?["meta"]?.ToString() ?? string.Empty,
                    SubmitCount = submit,
                    PassRate = pass,
                    Difficulty = diff,
                    Icon = OjIcons.Get(oj.Id, _isDark),
                    IsStarred = StarService.IsStarred(oj.Id, row?["id"]?.ToString() ?? string.Empty),
                });
                added++;
            }

            if (error.Length > 0)
            {
                return (added, oj.Name + "（" + error + "）", false, true);
            }

            string note = node["note"]?.ToString() ?? string.Empty;
            if (added == 0 && note.Length > 0)
            {
                return (0, oj.Name + " 未返回结果", false, true);
            }

            // 当前页没数据了就停止翻页
            return (added, string.Empty, false, added == 0);
        }

        foreach (OjDefinition oj in selected)
        {
            if (token != _searchToken)
            {
                return;
            }

            int maxPages = Math.Max(1, oj.MaxPages);
            int ojAdded = 0;
            for (int page = 1; page <= maxPages; page++)
            {
                if (token != _searchToken)
                {
                    return;
                }

                string status = $"正在搜索 {oj.Name}（第 {page} 页，已找到 {_results.Count} 条）";
                ResultStatusText.Text = status;
                SearchLoadingText.Text = status;

                (int added, string failure, bool captcha, bool stop) = await SearchOneAsync(oj, page);
                if (captcha)
                {
                    // 把触发验证码的页面交给用户，完成后从第一页重试该 OJ
                    string captchaUrl = oj.BuildSearchUrl(query, page);
                    bool done = await ShowCaptchaAsync(captchaUrl);
                    if (done)
                    {
                        page = 0; // for 循环会 ++ 变 1，从第一页重来
                        ojAdded = 0;
                        continue;
                    }

                    captchaHit = true;
                    failed.Add(oj.Name + " 触发验证码（已跳过）");
                    break;
                }

                if (failure.Length > 0)
                {
                    failed.Add(failure);
                }

                ojAdded += added;

                if (stop)
                {
                    break;
                }
            }

            // 整站一条都没搜到且没明确失败信息时，按站点重试一次空壳页
            if (ojAdded == 0 && oj.RetryEmpty && !captchaHit && !failed.Any(f => f.StartsWith(oj.Name, StringComparison.Ordinal)))
            {
                for (int attempt = 1; attempt < 3; attempt++)
                {
                    await Task.Delay(1500, _cancelSource?.Token ?? default);
                    if (token != _searchToken)
                    {
                        return;
                    }

                    string retryStatus = $"正在重试 {oj.Name}…（已找到 {_results.Count} 条）";
                    ResultStatusText.Text = retryStatus;
                    SearchLoadingText.Text = retryStatus;
                    (int added, string failure, bool captcha, _) = await SearchOneAsync(oj, 1);
                    if (captcha)
                    {
                        captchaHit = true;
                        failed.Add(oj.Name + " 触发验证码");
                        break;
                    }

                    ojAdded += added;
                    if (added > 0)
                    {
                        break;
                    }

                    if (failure.Length > 0)
                    {
                        failed.Add(failure);
                    }
                }
            }
        }

        if (token != _searchToken)
        {
            return;
        }

        ApplySort();

        SearchLoadingOverlay.Visibility = Visibility.Collapsed;

        string summary = _results.Count == 0
            ? "没有找到相关题目"
            : $"共 {_results.Count} 条结果";
        if (failed.Count > 0)
        {
            summary += " · 未完成：" + string.Join("；", failed);
        }

        ResultStatusText.Text = summary;
        EmptyStateText.Text = _results.Count == 0
            ? "没有搜到题目。试试更短的关键词、题号（如 P1001 / 1A），或检查网络与登录状态。"
            : string.Empty;
        EmptyStateText.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (captchaHit)
        {
            ResultStatusText.Text += "（部分站点触发验证码，已自动暂停该站点）";
        }
    }

    private List<OjDefinition> SelectedOjs()
    {
        var selected = new List<OjDefinition>();
        foreach (ToggleButton toggle in new[] { LuoguToggle, AtCoderToggle, NowCoderToggle })
        {
            if (toggle.IsChecked != true || toggle.Tag is not string id)
            {
                continue;
            }

            OjDefinition? oj = _config.Find(id);
            if (oj is not null)
            {
                selected.Add(oj);
            }
        }

        return selected;
    }

    private OjSortMode CurrentSortMode()
    {
        if (SortComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            Enum.TryParse<OjSortMode>(tag, out OjSortMode mode))
        {
            return mode;
        }
        return OjSortMode.SubmitAsc;
    }

    private void ApplySort()
    {
        var sorted = OjSorter.Sort(_results, CurrentSortMode()).ToList();
        _results.Clear();
        foreach (OjProblemItem item in sorted)
        {
            _results.Add(item);
        }
    }

    private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_results.Count > 0)
        {
            ApplySort();
        }
    }

    private void ShowResults()
    {
        HomePanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Visible;
        ProblemPanel.Visibility = Visibility.Collapsed;
        TrainingPanel.Visibility = Visibility.Collapsed;
        TrainingListPanel.Visibility = Visibility.Collapsed;
        TrainingDetailPanel.Visibility = Visibility.Collapsed;
        PlayEnter(ResultsPanel, ResultsTransform);
    }

    private void BackToHome_Click(object sender, RoutedEventArgs e)
    {
        ResultsPanel.Visibility = Visibility.Collapsed;
        ProblemPanel.Visibility = Visibility.Collapsed;
        TrainingPanel.Visibility = Visibility.Collapsed;
        TrainingListPanel.Visibility = Visibility.Collapsed;
        TrainingDetailPanel.Visibility = Visibility.Collapsed;
        SearchLoadingOverlay.Visibility = Visibility.Collapsed;
        HomePanel.Visibility = Visibility.Visible;
        PlayEnter(HomeStack, HomeStackTransform);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void BackToResults_Click(object sender, RoutedEventArgs e)
    {
        ProblemPanel.Visibility = Visibility.Collapsed;
        if (_problemOrigin == ProblemOrigin.TrainingSearch)
        {
            TrainingPanel.Visibility = Visibility.Collapsed;
            TrainingDetailPanel.Visibility = Visibility.Collapsed;
            TrainingListPanel.Visibility = Visibility.Visible;
            PlayEnter(TrainingListPanel, TrainingListTransform);
            return;
        }

        if (_problemOrigin == ProblemOrigin.Training && _currentTraining is not null)
        {
            TrainingListPanel.Visibility = Visibility.Collapsed;
            TrainingDetailPanel.Visibility = Visibility.Visible;
            PlayEnter(TrainingDetailPanel, TrainingDetailTransform);
            return;
        }

        if (_problemOrigin == ProblemOrigin.Starred && _starred.Count > 0)
        {
            StarredPanel.Visibility = Visibility.Visible;
            PlayEnter(StarredPanel, StarredTransform);
            return;
        }

        if (_results.Count > 0)
        {
            ResultsPanel.Visibility = Visibility.Visible;
            PlayEnter(ResultsPanel, ResultsTransform);
        }
        else
        {
            HomePanel.Visibility = Visibility.Visible;
            PlayEnter(HomeStack, HomeStackTransform);
        }
    }

    /* ---------------- 标星 ---------------- */

    private void StarResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key)
        {
            return;
        }

        OjProblemItem? item = _results.FirstOrDefault(r => r.Key == key)
                              ?? _starred.FirstOrDefault(r => r.Key == key)
                              ?? _trainingProblems.FirstOrDefault(r => r.Key == key);
        if (item is null)
        {
            return;
        }

        bool wasStarred = item.IsStarred;
        StarService.Toggle(item);

        // 从标星列表里取消标星时，从列表移除该项
        if (wasStarred && !item.IsStarred)
        {
            _starred.Remove(item);
        }

        ApplySort();
    }

    private void HomeStarButton_Click(object sender, RoutedEventArgs e)
    {
        _starred.Clear();
        foreach (StarredEntry entry in StarService.All())
        {
            _starred.Add(new OjProblemItem
            {
                OjId = entry.OjId,
                OjName = entry.OjName,
                Id = entry.Id,
                Title = entry.Title,
                Url = entry.Url,
                Meta = entry.Meta,
                IsStarred = true,
                Icon = OjIcons.Get(entry.OjId, _isDark),
            });
        }

        HomePanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        ProblemPanel.Visibility = Visibility.Collapsed;
        TrainingPanel.Visibility = Visibility.Collapsed;
        TrainingListPanel.Visibility = Visibility.Collapsed;
        TrainingDetailPanel.Visibility = Visibility.Collapsed;
        StarredPanel.Visibility = Visibility.Visible;
        PlayEnter(StarredPanel, StarredTransform);
    }

    private void StarredBack_Click(object sender, RoutedEventArgs e)
    {
        StarredPanel.Visibility = Visibility.Collapsed;
        HomePanel.Visibility = Visibility.Visible;
        PlayEnter(HomeStack, HomeStackTransform);
    }

    private async void StarredList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OjProblemItem item)
        {
            _problemOrigin = ProblemOrigin.Starred;
            await OpenProblemAsync(item);
        }
    }

    private void CancelSearch_Click(object sender, RoutedEventArgs e)
    {
        _searchToken++;
        SearchLoadingOverlay.Visibility = Visibility.Collapsed;
        if (_results.Count > 0)
        {
            ResultStatusText.Text = $"已取消 · 共 {_results.Count} 条结果";
        }
    }

    /* ---------------- 洛谷官方题单：板块 → 题单 → 题目 ---------------- */

    private async void TrainingButton_Click(object sender, RoutedEventArgs e) => await ShowTrainingsAsync();

    /// <summary>第一级：板块列表。</summary>
    private async Task ShowTrainingsAsync()
    {
        HomePanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        ProblemPanel.Visibility = Visibility.Collapsed;
        TrainingListPanel.Visibility = Visibility.Collapsed;
        TrainingDetailPanel.Visibility = Visibility.Collapsed;
        TrainingPanel.Visibility = Visibility.Visible;
        PlayEnter(TrainingPanel, TrainingTransform);
        CategorySearchBox.Text = string.Empty;

        if (_categoriesLoaded)
        {
            ApplyCategoryFilter();
            return;
        }

        TrainingEmptyText.Visibility = Visibility.Collapsed;
        TrainingRing.IsActive = true;
        TrainingRing.Visibility = Visibility.Visible;
        TrainingStatusText.Text = "正在加载…";

        JsonNode? node;
        try
        {
            node = await FetchTrainingAsync(LuoguTraining.ListUrl, LuoguTraining.ListScript);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            TrainingRing.IsActive = false;
            TrainingRing.Visibility = Visibility.Collapsed;
        }

        _categories.Clear();
        if (node?["categories"] is JsonArray categories)
        {
            foreach (JsonNode? row in categories)
            {
                string key = row?["key"]?.ToString() ?? string.Empty;
                string name = row?["name"]?.ToString() ?? string.Empty;
                if (key.Length == 0 || name.Length == 0)
                {
                    continue;
                }

                _categories.Add(new LuoguCategory { Key = key, Name = name });
            }

            // 官网导航里的「精选用户分享」不在分类 JSON 中，单独补到末尾
            if (!_categories.Any(c => c.Key == "select"))
            {
                _categories.Add(new LuoguCategory { Key = "select", Name = "精选用户分享" });
            }
        }

        _categoriesLoaded = _categories.Count > 0;
        ApplyCategoryFilter();
        if (!_categoriesLoaded)
        {
            TrainingStatusText.Text = "板块加载失败";
            TrainingEmptyText.Text = "题单加载失败，请检查网络后重新打开";
            TrainingEmptyText.Visibility = Visibility.Visible;
        }
    }

    private void CategorySearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyCategoryFilter();

    /// <summary>按「深入浅出 / 月赛 / 竞赛 / 书籍 / 用户分享」把板块填进各自的分组，并应用搜索关键词。</summary>
    private void ApplyCategoryFilter()
    {
        string query = CategorySearchBox.Text.Trim();
        FillCategoryGroup(_srqcCategories, "srqc-", query, SrqcHeader, SrqcGrid);
        FillCategoryGroup(_monthCategories, "month-", query, MonthHeader, MonthGrid);
        FillCategoryGroup(_contestCategories, "contest.", query, ContestHeader, ContestGrid);
        FillCategoryGroup(_bookCategories, "book.", query, BookHeader, BookGrid);
        FillCategoryGroup(_selectCategories, "select", query, SelectHeader, SelectGrid);

        int shown = _srqcCategories.Count + _monthCategories.Count + _contestCategories.Count
                    + _bookCategories.Count + _selectCategories.Count;
        if (query.Length > 0)
        {
            TrainingEmptyText.Text = "没有匹配的板块";
            TrainingEmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (_categoriesLoaded)
        {
            TrainingEmptyText.Text = "题单加载失败，请检查网络后重新打开";
            TrainingEmptyText.Visibility = _categories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_categoriesLoaded)
        {
            TrainingStatusText.Text = query.Length > 0
                ? "匹配 " + shown + " 个板块"
                : _categories.Count + " 个板块";
        }
    }

    private void FillCategoryGroup(
        ObservableCollection<LuoguCategory> target,
        string prefix,
        string query,
        TextBlock header,
        GridView grid)
    {
        target.Clear();
        foreach (LuoguCategory category in _categories)
        {
            if (!category.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (query.Length > 0 && category.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            target.Add(category);
        }

        Visibility visibility = target.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        header.Visibility = visibility;
        grid.Visibility = visibility;
    }

    private async void CategoryGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LuoguCategory category)
        {
            await OpenCategoryAsync(category);
        }
    }

    /// <summary>第二级：板块内的题单，支持分页加载更多。</summary>
    private async Task OpenCategoryAsync(LuoguCategory category)
    {
        ResetTrainingSearch();
        _currentCategory = category;
        _trainingPage = 1;
        _trainingTotal = 0;
        _trainings.Clear();

        TrainingListTitle.Text = category.Name;
        TrainingListStatus.Text = "正在加载…";
        TrainingMoreButton.Visibility = Visibility.Collapsed;
        TrainingPanel.Visibility = Visibility.Collapsed;
        TrainingDetailPanel.Visibility = Visibility.Collapsed;
        TrainingListPanel.Visibility = Visibility.Visible;
        PlayEnter(TrainingListPanel, TrainingListTransform);

        await LoadTrainingPageAsync(append: false);
    }

    private async void TrainingMore_Click(object sender, RoutedEventArgs e)
    {
        _trainingPage++;
        if (!await LoadTrainingPageAsync(append: true))
        {
            _trainingPage--;
        }
    }

    private async Task<bool> LoadTrainingPageAsync(bool append)
    {
        if (_currentCategory is null || _trainingLoading)
        {
            return false;
        }

        _trainingLoading = true;
        TrainingMoreButton.IsEnabled = false;
        TrainingListRing.IsActive = true;
        TrainingListRing.Visibility = Visibility.Visible;

        JsonNode? node;
        try
        {
            node = await FetchTrainingAsync(
                LuoguTraining.ListUrlFor(_currentCategory.Key, _trainingPage),
                LuoguTraining.ListScript);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _trainingLoading = false;
            TrainingMoreButton.IsEnabled = true;
            TrainingListRing.IsActive = false;
            TrainingListRing.Visibility = Visibility.Collapsed;
        }

        if (node?["items"] is not JsonArray items)
        {
            TrainingListStatus.Text = "加载失败，请稍后重试";
            return false;
        }

        if (node?["total"] is JsonValue totalValue && totalValue.TryGetValue(out int total))
        {
            _trainingTotal = total;
        }

        if (!append)
        {
            _trainings.Clear();
        }

        int index = _trainings.Count + 1;
        foreach (JsonNode? row in items)
        {
            string id = row?["id"]?.ToString() ?? string.Empty;
            string title = row?["title"]?.ToString() ?? string.Empty;
            if (id.Length == 0 || title.Length == 0)
            {
                continue;
            }

            _trainings.Add(new LuoguTrainingSummary
            {
                Id = id,
                Title = title,
                ProblemCount = row?["problemCount"]?.GetValue<int>() ?? 0,
                MarkCount = row?["markCount"]?.GetValue<int>() ?? 0,
                Index = index.ToString("00", CultureInfo.InvariantCulture),
            });
            index++;
        }

        if (_trainingTotal < _trainings.Count)
        {
            _trainingTotal = _trainings.Count;
        }

        UpdateTrainingListStatus();
        return true;
    }

    /// <summary>刷新题单页的状态文字和「加载更多」按钮。</summary>
    private void UpdateTrainingListStatus()
    {
        if (TrainingSearchBox.Text.Trim().Length > 0)
        {
            return;
        }

        TrainingMoreButton.Visibility = _trainings.Count > 0 && _trainings.Count < _trainingTotal
            ? Visibility.Visible
            : Visibility.Collapsed;

        TrainingListStatus.Text = _trainings.Count == 0
            ? "这个板块暂时没有题单"
            : _trainings.Count + " / " + _trainingTotal + " 个题单";
    }

    /* ---------------- 板块内跨题单搜题 ---------------- */

    private void TrainingSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _trainingSearchTimer?.Stop();
        _trainingSearchCts?.Cancel();

        if (TrainingSearchBox.Text.Trim().Length == 0)
        {
            ShowTrainingCards();
            return;
        }

        _trainingSearchTimer?.Start();
    }

    /// <summary>回到题单卡片视图（清空搜索结果）。</summary>
    private void ShowTrainingCards()
    {
        _trainingSearchResults.Clear();
        TrainingSearchResultList.Visibility = Visibility.Collapsed;
        TrainingSearchEmptyText.Visibility = Visibility.Collapsed;
        TrainingGrid.Visibility = Visibility.Visible;
        TrainingListRing.IsActive = false;
        TrainingListRing.Visibility = Visibility.Collapsed;
        UpdateTrainingListStatus();
    }

    /// <summary>离开板块页时取消搜索、清空输入。</summary>
    private void ResetTrainingSearch()
    {
        _trainingSearchTimer?.Stop();
        _trainingSearchCts?.Cancel();
        _trainingSearchCts?.Dispose();
        _trainingSearchCts = null;
        if (TrainingSearchBox.Text.Length > 0)
        {
            TrainingSearchBox.Text = string.Empty;
        }

        ShowTrainingCards();
    }

    /// <summary>在板块下的所有题单里搜题目：并发拉取每个题单的题目，匹配题号或标题。</summary>
    private async Task RunCategorySearchAsync()
    {
        string query = TrainingSearchBox.Text.Trim();
        if (query.Length == 0 || _currentCategory is null)
        {
            return;
        }

        _trainingSearchCts?.Cancel();
        _trainingSearchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _trainingSearchCts = cts;
        CancellationToken token = cts.Token;

        _trainingSearchResults.Clear();
        TrainingSearchEmptyText.Visibility = Visibility.Collapsed;
        TrainingSearchResultList.Visibility = Visibility.Collapsed;
        TrainingGrid.Visibility = Visibility.Collapsed;
        TrainingMoreButton.Visibility = Visibility.Collapsed;
        TrainingListRing.IsActive = true;
        TrainingListRing.Visibility = Visibility.Visible;
        TrainingListStatus.Text = "正在索引题单…";

        try
        {
            List<LuoguTrainingSummary> trainings = await _luoguIndex.GetTrainingsAsync(_currentCategory.Key, token);
            if (trainings.Count == 0)
            {
                trainings = _trainings.ToList();
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var found = new List<LuoguProblemEntry>();
            int done = 0;
            var tasks = new List<Task>(trainings.Count);
            foreach (LuoguTrainingSummary training in trainings)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        List<LuoguProblemEntry> problems = await _luoguIndex.GetProblemsAsync(training.Id, token);
                        lock (seen)
                        {
                            foreach (LuoguProblemEntry problem in problems)
                            {
                                if (problem.Matches(query) && seen.Add(problem.Pid))
                                {
                                    found.Add(problem);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    finally
                    {
                        int current = Interlocked.Increment(ref done);
                        if (current % 5 == 0 || current == trainings.Count)
                        {
                            int snapshot = current;
                            _ = DispatcherQueue.TryEnqueue(() =>
                            {
                                if (!token.IsCancellationRequested)
                                {
                                    TrainingListStatus.Text = "正在索引题单 " + snapshot + "/" + trainings.Count + "…";
                                }
                            });
                        }
                    }
                }, token));
            }

            await Task.WhenAll(tasks);
            token.ThrowIfCancellationRequested();

            found.Sort((a, b) => string.CompareOrdinal(a.Pid, b.Pid));
            foreach (LuoguProblemEntry problem in found)
            {
                _trainingSearchResults.Add(new OjProblemItem
                {
                    OjId = "luogu",
                    OjName = "洛谷",
                    Id = problem.Pid,
                    Title = problem.Title,
                    Url = "https://www.luogu.com.cn/problem/" + problem.Pid,
                    Difficulty = problem.Difficulty,
                    SubmitCount = problem.SubmitCount,
                    PassRate = problem.SubmitCount > 0 ? (double)problem.AcceptedCount / problem.SubmitCount : -1,
                    Icon = OjIcons.Get("luogu", _isDark),
                    IsStarred = StarService.IsStarred("luogu", problem.Pid),
                });
            }

            TrainingListStatus.Text = "找到 " + _trainingSearchResults.Count + " 道题（共 " + trainings.Count + " 个题单）";
            TrainingSearchResultList.Visibility = Visibility.Visible;
            TrainingSearchEmptyText.Visibility = _trainingSearchResults.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_trainingSearchCts, cts))
            {
                TrainingListRing.IsActive = false;
                TrainingListRing.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async void TrainingSearchResult_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OjProblemItem item)
        {
            _problemOrigin = ProblemOrigin.TrainingSearch;
            await OpenProblemAsync(item);
        }
    }

    private async void TrainingGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LuoguTrainingSummary summary)
        {
            await OpenTrainingAsync(summary);
        }
    }

    private async Task OpenTrainingAsync(LuoguTrainingSummary summary)
    {
        _currentTraining = summary;
        _trainingProblems.Clear();
        _visibleTrainingProblems.Clear();
        TrainingProblemSearchBox.Text = string.Empty;
        TrainingProblemTitle.Text = summary.Title;
        TrainingProblemMeta.Text = "正在加载…";
        TrainingPanel.Visibility = Visibility.Collapsed;
        TrainingListPanel.Visibility = Visibility.Collapsed;
        TrainingDetailPanel.Visibility = Visibility.Visible;
        PlayEnter(TrainingDetailPanel, TrainingDetailTransform);

        JsonNode? node;
        TrainingProblemRing.IsActive = true;
        TrainingProblemRing.Visibility = Visibility.Visible;
        try
        {
            node = await FetchTrainingAsync(LuoguTraining.DetailUrl(summary.Id), LuoguTraining.DetailScript);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            TrainingProblemRing.IsActive = false;
            TrainingProblemRing.Visibility = Visibility.Collapsed;
        }

        if (node?["items"] is not JsonArray items)
        {
            TrainingProblemMeta.Text = "题单加载失败";
            return;
        }

        foreach (JsonNode? row in items)
        {
            string pid = row?["id"]?.ToString() ?? string.Empty;
            string title = row?["title"]?.ToString() ?? string.Empty;
            if (pid.Length == 0 || title.Length == 0)
            {
                continue;
            }

            long submit = row?["submitCount"]?.GetValue<long>() ?? 0;
            long accepted = row?["acceptedCount"]?.GetValue<long>() ?? 0;
            _trainingProblems.Add(new OjProblemItem
            {
                OjId = "luogu",
                OjName = "洛谷",
                Id = pid,
                Title = title,
                Url = "https://www.luogu.com.cn/problem/" + pid,
                Difficulty = row?["difficulty"]?.GetValue<int>() ?? -1,
                SubmitCount = submit,
                PassRate = submit > 0 ? (double)accepted / submit : -1,
                Icon = OjIcons.Get("luogu", _isDark),
                IsStarred = StarService.IsStarred("luogu", pid),
            });
        }

        ApplyTrainingProblemFilter();
    }

    private void TrainingProblemSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyTrainingProblemFilter();

    /// <summary>在当前题单的题目里按标题/题号过滤。</summary>
    private void ApplyTrainingProblemFilter()
    {
        string query = TrainingProblemSearchBox.Text.Trim();
        _visibleTrainingProblems.Clear();
        foreach (OjProblemItem problem in _trainingProblems)
        {
            if (query.Length > 0
                && problem.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                && problem.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            _visibleTrainingProblems.Add(problem);
        }

        TrainingProblemEmptyText.Visibility = _trainingProblems.Count > 0 && _visibleTrainingProblems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_trainingProblems.Count == 0)
        {
            return;
        }

        TrainingProblemMeta.Text = query.Length > 0
            ? "匹配 " + _visibleTrainingProblems.Count + " 道题"
            : _trainingProblems.Count + " 道题";
    }

    private async void TrainingProblemList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OjProblemItem item)
        {
            _problemOrigin = ProblemOrigin.Training;
            await OpenProblemAsync(item);
        }
    }

    private void TrainingBackToHome_Click(object sender, RoutedEventArgs e)
    {
        ResetTrainingSearch();
        TrainingPanel.Visibility = Visibility.Collapsed;
        TrainingListPanel.Visibility = Visibility.Collapsed;
        HomePanel.Visibility = Visibility.Visible;
        PlayEnter(HomeStack, HomeStackTransform);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void TrainingListBack_Click(object sender, RoutedEventArgs e)
    {
        ResetTrainingSearch();
        TrainingListPanel.Visibility = Visibility.Collapsed;
        TrainingPanel.Visibility = Visibility.Visible;
        PlayEnter(TrainingPanel, TrainingTransform);
    }

    private void TrainingDetailBack_Click(object sender, RoutedEventArgs e)
    {
        TrainingDetailPanel.Visibility = Visibility.Collapsed;
        TrainingListPanel.Visibility = Visibility.Visible;
        PlayEnter(TrainingListPanel, TrainingListTransform);
    }

    /// <summary>抓取题单页面；遇到人机验证时先让用户完成，再重试一次。</summary>
    private async Task<JsonNode?> FetchTrainingAsync(string url, string script)
    {
        CancellationToken token = _cancelSource?.Token ?? default;
        JsonNode? node = await _fetchBrowser.RunAsync(url, script, 800, false, token);
        if (node?["captcha"] is JsonValue captcha && captcha.TryGetValue(out bool required) && required)
        {
            if (!await ShowCaptchaAsync(url))
            {
                return null;
            }

            node = await _fetchBrowser.RunAsync(url, script, 800, false, token);
        }

        return node;
    }

    /* ---------------- 打开题目 ---------------- */

    private async void ResultList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OjProblemItem item)
        {
            _problemOrigin = ProblemOrigin.Results;
            await OpenProblemAsync(item);
        }
    }

    private async Task OpenProblemAsync(OjProblemItem item)
    {
        OjDefinition? oj = _config.Find(item.OjId);
        if (oj is null)
        {
            return;
        }

        int token = ++_problemToken;
        _currentItem = item;
        _currentOj = oj;

        // 随机抽题会先显示加载页，这里直接沿用，不再重新播放入场动画
        bool alreadyVisible = ProblemPanel.Visibility == Visibility.Visible;
        HomePanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        TrainingPanel.Visibility = Visibility.Collapsed;
        TrainingListPanel.Visibility = Visibility.Collapsed;
        TrainingDetailPanel.Visibility = Visibility.Collapsed;
        ProblemPanel.Visibility = Visibility.Visible;
        if (!alreadyVisible)
        {
            PlayEnter(ProblemPanel, ProblemTransform);
        }

        ProblemOjText.Text = item.OjName;
        ProblemOjIcon.Source = OjIcons.Get(item.OjId, _isDark);
        ProblemTitleText.Text = item.Title;
        ProblemStatusText.Text = "正在抓取题面…";
        TranslateButton.Visibility = Visibility.Collapsed;
        TranslateButton.Content = "翻译";
        _problemMarkdown = string.Empty;
        _translatedMarkdown = string.Empty;
        _showingTranslation = false;
        ApplyLoginButtonVisibility(oj);
        _samples.Clear();

        await Task.Yield();
        await EnsureProblemViewerAsync();
        await EnsurePracticeEditorAsync();
        if (token != _problemToken)
        {
            return;
        }

        SendProblemMessage(new { type = "loading", text = "正在抓取题面…" });
        SendEditorOpen();

        // 后台检测登录：有登录 Cookie 的 OJ 先用 Cookie 预检，未登录就弹窗；
        // 其它 OJ（如洛谷）在抓题面时按页面返回的登录态再判断，未登录同样弹窗。
        _loginAutoPrompted = false;
        if (oj.LoginCookies.Count > 0 && !_loggedInOjs.Contains(oj.Id))
        {
            bool loggedIn = await CheckLoginAsync(oj, includePageScript: false);
            if (token != _problemToken)
            {
                return;
            }
            if (loggedIn)
            {
                _loggedInOjs.Add(oj.Id);
                ApplyLoginButtonVisibility(oj);
            }
            else
            {
                _loginAutoPrompted = true;
                _ = ShowLoginAsync();
            }
        }

        await FetchProblemAsync(token);
    }

    private async Task FetchProblemAsync(int token)
    {
        if (_currentOj is null || _currentItem is null)
        {
            return;
        }

        OjDefinition oj = _currentOj;
        OjProblemItem item = _currentItem;

        ProblemStatusText.Text = "正在抓取题面…";
        TranslateButton.Visibility = Visibility.Collapsed;
        _translatedMarkdown = string.Empty;
        _showingTranslation = false;
        TranslateButton.Content = "翻译";
        JsonNode? node;
        try
        {
            node = await _fetchBrowser.RunAsync(item.Url, oj.ProblemScript, oj.EffectiveProblemSettleMs, false, _cancelSource?.Token ?? default);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (token != _problemToken)
        {
            return;
        }

        if (node is null)
        {
            FailProblem("题面抓取失败：无法连接 " + oj.Name + "，请检查网络或登录状态");
        }
        else
        {
            string error = node["error"]?.ToString() ?? string.Empty;
            string markdown = node["markdown"]?.ToString() ?? string.Empty;
            string title = node["title"]?.ToString() ?? string.Empty;

            // 语言判断只看正文，样例代码块不参与
            string statement = markdown;

            if (markdown.Trim().Length == 0)
            {
                FailProblem(error.Length > 0 ? error : "题面为空，可能需要登录后再抓取");
            }
            else
            {
                _samples.Clear();
                if (node["samples"] is JsonArray samples)
                {
                    int index = 1;
                    foreach (JsonNode? sample in samples)
                    {
                        _samples.Add(new OjSample
                        {
                            Title = "样例 " + index,
                            Input = sample?["input"]?.ToString() ?? string.Empty,
                            Output = sample?["output"]?.ToString() ?? string.Empty,
                            Note = sample?["note"]?.ToString() ?? string.Empty,
                        });
                        index++;
                    }
                }

                // 把样例追加到题面 markdown 里（不再用右侧面板）
                if (_samples.Count > 0)                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine();
                    sb.AppendLine("## 样例");
                    sb.AppendLine();
                    for (int i = 0; i < _samples.Count; i++)
                    {
                        OjSample s = _samples[i];
                        sb.AppendLine("### 样例 " + (i + 1));
                        sb.AppendLine();
                        if (s.Input.Trim().Length > 0)
                        {
                            sb.AppendLine("**输入**");
                            sb.AppendLine();
                            sb.AppendLine("```");
                            sb.AppendLine(s.Input.TrimEnd());
                            sb.AppendLine("```");
                            sb.AppendLine();
                        }
                        if (s.Output.Trim().Length > 0)
                        {
                            sb.AppendLine("**输出**");
                            sb.AppendLine();
                            sb.AppendLine("```");
                            sb.AppendLine(s.Output.TrimEnd());
                            sb.AppendLine("```");
                            sb.AppendLine();
                        }
                        if (s.Note.Trim().Length > 0)
                        {
                            sb.AppendLine(s.Note.Trim());
                            sb.AppendLine();
                        }
                    }
                    markdown += sb.ToString();
                }

                if (title.Length > 0)
                {
                    ProblemTitleText.Text = title;
                }

                _problemMarkdown = markdown;
                _translatedMarkdown = string.Empty;
                _showingTranslation = false;
                _problemViewerTitle = TitleTextForViewer(item, title);
                TranslateButton.Content = "翻译";
                TranslateButton.Visibility = IsProbablyNonChinese(statement) ? Visibility.Visible : Visibility.Collapsed;

                RenderProblemMarkdown(markdown);

                ProblemStatusText.Text = "题面已抓取";
            }
        }

        bool? pageLoginState = null;
        if (node?["loggedIn"] is JsonValue loginValue && loginValue.TryGetValue(out bool loginBool))
        {
            pageLoginState = loginBool;
        }

        await UpdateLoginStateAsync(oj, pageLoginState);
    }

    private static string TitleTextForViewer(OjProblemItem item, string fetchedTitle)
    {
        if (string.IsNullOrWhiteSpace(fetchedTitle))
        {
            return item.Title;
        }

        return fetchedTitle;
    }

    private void FailProblem(string message)
    {
        ProblemStatusText.Text = message;
        SendProblemMessage(new { type = "error", text = message });
    }

    /* ---------------- 题面翻译（免费接口，无需密钥） ---------------- */

    private static HttpClient CreateTranslateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MnyCppIDE/1.2");
        return client;
    }

    private void RenderProblemMarkdown(string markdown)
    {
        SendProblemMessage(new
        {
            type = "render",
            title = _problemViewerTitle,
            oj = _currentOj?.Name ?? string.Empty,
            meta = _currentItem?.Id ?? string.Empty,
            markdown,
            theme = _isDark ? "dark" : "light",
            baseUrl = _currentItem?.Url ?? string.Empty,
        });
    }

    private async void TranslateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_problemMarkdown.Length == 0)
        {
            return;
        }

        // 已有译文：在原文和译文之间切换
        if (_translatedMarkdown.Length > 0)
        {
            _showingTranslation = !_showingTranslation;
            RenderProblemMarkdown(_showingTranslation ? _translatedMarkdown : _problemMarkdown);
            TranslateButton.Content = _showingTranslation ? "原文" : "翻译";
            return;
        }

        string cacheKey = _currentItem?.Url ?? string.Empty;
        if (cacheKey.Length > 0 && _translationCache.TryGetValue(cacheKey, out string? cached))
        {
            _translatedMarkdown = cached;
            _showingTranslation = true;
            RenderProblemMarkdown(cached);
            TranslateButton.Content = "原文";
            return;
        }

        TranslateButton.IsEnabled = false;
        ProblemStatusText.Text = "正在翻译题面…";
        try
        {
            string translated = await TranslateMarkdownAsync(
                _problemMarkdown, GuessSourceLanguage(_problemMarkdown), _cancelSource?.Token ?? default);

            if (translated.Trim().Length == 0 || translated.Trim() == _problemMarkdown.Trim())
            {
                ProblemStatusText.Text = "翻译失败，请检查网络后重试";
                return;
            }

            _translatedMarkdown = translated;
            if (cacheKey.Length > 0)
            {
                _translationCache[cacheKey] = translated;
            }

            _showingTranslation = true;
            RenderProblemMarkdown(translated);
            TranslateButton.Content = "原文";
            ProblemStatusText.Text = "已翻译（机器翻译，仅供参考）";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ProblemStatusText.Text = "翻译失败，请检查网络后重试";
        }
        finally
        {
            TranslateButton.IsEnabled = true;
        }
    }

    /// <summary>题面以拉丁字母为主时认为不是中文，才显示翻译按钮。</summary>
    private static bool IsProbablyNonChinese(string text)
    {
        int cjk = 0, latin = 0;
        foreach (char c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF)
            {
                cjk++;
            }
            else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
            {
                latin++;
            }
        }

        return cjk + latin >= 160 && cjk * 3 < latin;
    }

    private static string GuessSourceLanguage(string text)
    {
        int kana = 0, cyrillic = 0;
        foreach (char c in text)
        {
            if (c >= 0x3040 && c <= 0x30FF)
            {
                kana++;
            }
            else if (c >= 0x0400 && c <= 0x04FF)
            {
                cyrillic++;
            }
        }

        if (kana >= 40)
        {
            return "ja";
        }

        return cyrillic >= 40 ? "ru" : "en";
    }

    /// <summary>直接翻译整段 Markdown：只保留代码块、LaTeX 公式和行内代码，其余文本全部翻译。</summary>
    private async Task<string> TranslateMarkdownAsync(string markdown, string source, CancellationToken token)
    {
        string[] blocks = markdown.Replace("\r\n", "\n").Split("\n\n");
        var output = new List<string>(blocks.Length);
        int index = 0;
        while (index < blocks.Length)
        {
            string block = blocks[index];

            // 代码块内部可能含空行，先合并不完整的代码块
            while (CountFenceLines(block) % 2 != 0 && index + 1 < blocks.Length)
            {
                index++;
                block += "\n\n" + blocks[index];
            }

            index++;

            string trimmed = block.TrimStart();
            // 代码块整块保留；纯符号/数字块没有可翻译内容
            bool keepAsIs = trimmed.StartsWith("```") || trimmed.StartsWith("~~~") ||
                            !Regex.IsMatch(block, "[A-Za-z\\u0400-\\u04FF\\u3040-\\u30FF]");
            output.Add(keepAsIs ? block : await TranslateBlockAsync(block, source, token));
        }

        return string.Join("\n\n", output);
    }

    private static int CountFenceLines(string text) => Regex.Matches(text, "^```", RegexOptions.Multiline).Count;

    private static async Task<string> TranslateBlockAsync(string block, string source, CancellationToken token)
    {
        var parts = new List<string>();
        // LaTeX（$…$、$$…$$、\(…\)、\[…\]、\begin…\end）、行内代码、HTML 标签原样保留
        string masked = Regex.Replace(
            block,
            @"(\$\$[\s\S]*?\$\$|\$[^$\n]+\$|\\\([\s\S]*?\\\)|\\\[[\s\S]*?\\\]|\\begin\{[a-zA-Z*]+\}[\s\S]*?\\end\{[a-zA-Z*]+\}|`[^`\n]+`|<[^>]+>|\]\([^)\n]*\))",
            match =>
            {
                parts.Add(match.Value);
                return "[[" + (parts.Count - 1) + "]]";
            });

        var translated = new StringBuilder();
        foreach (string chunk in ChunkText(masked, 400))
        {
            token.ThrowIfCancellationRequested();
            translated.Append(await TranslateChunkAsync(chunk, source, token));
            await Task.Delay(60, token);
        }

        string result = translated.ToString();
        for (int i = 0; i < parts.Count; i++)
        {
            result = Regex.Replace(result, @"\[\[\s*" + i + @"\s*\]\]", _ => parts[i]);
        }

        return result;
    }

    private static IEnumerable<string> ChunkText(string text, int max)
    {
        string[] sentences = Regex.Split(text, @"(?<=[\.\!\?\;\。\！\？\；\n])");
        var current = new StringBuilder();
        foreach (string sentence in sentences)
        {
            if (sentence.Length == 0)
            {
                continue;
            }

            if (sentence.Length > max)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                for (int i = 0; i < sentence.Length; i += max)
                {
                    yield return sentence.Substring(i, Math.Min(max, sentence.Length - i));
                }

                continue;
            }

            if (current.Length > 0 && current.Length + sentence.Length > max)
            {
                yield return current.ToString();
                current.Clear();
            }

            current.Append(sentence);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    /// <summary>先走 Google 免费接口，失败再走 MyMemory（国内可直连）。</summary>
    private static async Task<string> TranslateChunkAsync(string text, string source, CancellationToken token)
    {
        try
        {
            string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=zh-CN&dt=t&q=" +
                         Uri.EscapeDataString(text);
            string raw = await TranslateHttp.GetStringAsync(url, token);
            using JsonDocument doc = JsonDocument.Parse(raw);
            var sb = new StringBuilder();
            foreach (JsonElement segment in doc.RootElement[0].EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0)
                {
                    sb.Append(segment[0].GetString());
                }
            }

            if (sb.Length > 0)
            {
                return sb.ToString();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
        }

        try
        {
            string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) +
                         "&langpair=" + Uri.EscapeDataString(source + "|zh-CN");
            string raw = await TranslateHttp.GetStringAsync(url, token);
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("responseStatus", out JsonElement status) &&
                status.TryGetInt32(out int code) && code != 200)
            {
                return text;
            }

            string? translated = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString();
            return string.IsNullOrWhiteSpace(translated) ? text : translated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return text;
        }
    }

    /* ---------------- 登录（圆角 WebView2） ---------------- */

    private async void LoginButton_Click(object sender, RoutedEventArgs e) => await ShowLoginAsync();

    private void CloseLogin_Click(object sender, RoutedEventArgs e) => CloseLogin();

    private async Task ShowLoginAsync()
    {
        if (_currentOj is null)
        {
            return;
        }

        OjDefinition oj = _currentOj;
        _overlaySubmitMode = false;
        _loginReloadCount = 0;

        // 已经登录（本次运行确认过，或 Cookie 仍在）就不再展示登录页
        if (_loggedInOjs.Contains(oj.Id) || await CheckLoginAsync(oj, includePageScript: false))
        {
            _loggedInOjs.Add(oj.Id);
            ApplyLoginButtonVisibility(oj);
            if (_samples.Count == 0)
            {
                await FetchProblemAsync(_problemToken);
            }

            return;
        }

        LoginTitleText.Text = "登录 " + oj.Name;
        LoginHintText.Text = "正在检查登录状态…";
        LoginRing.IsActive = true;
        LoginRing.Visibility = Visibility.Visible;
        LoginWebView.Visibility = Visibility.Collapsed;
        LoginLoadingPanel.Visibility = Visibility.Visible;
        UpdateLoginOverlaySize();
        LoginOverlay.Visibility = Visibility.Visible;
        PlayEnter(LoginOverlay);

        await EnsureLoginViewAsync();
        if (LoginWebView.CoreWebView2 is null)
        {
            LoginHintText.Text = "登录页初始化失败，请检查 WebView2 运行库";
            return;
        }

        // 先在隐藏的后台浏览器里确认一次登录态：已登录就直接关掉，不加载登录页
        if (await ProbeLoginAsync(oj))
        {
            await CompleteLoginAsync(oj);
            return;
        }

        LoginHintText.Text = string.Empty;
        LoginWebView.Visibility = Visibility.Visible;
        LoginLoadingPanel.Visibility = Visibility.Visible;
        LoginWebView.CoreWebView2.Navigate(oj.LoginUrl);
        StartLoginWatch();
    }

    /// <summary>在隐藏的后台浏览器里加载 OJ 主页，用登录检测脚本判断是否已经登录。</summary>
    private async Task<bool> ProbeLoginAsync(OjDefinition oj)
    {
        if (string.IsNullOrWhiteSpace(oj.LoginCheckScript))
        {
            return false;
        }

        try
        {
            JsonNode? node = await _fetchBrowser.RunAsync(
                oj.HomeUrl, oj.LoginCheckScript, oj.SettleMs, oj.DisableJs, _cancelSource?.Token ?? default);
            return node is JsonValue value && value.TryGetValue(out bool loggedIn) && loggedIn;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void StartLoginWatch()
    {
        _loginTimer?.Stop();
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1500);
        timer.Tick += LoginTimer_Tick;
        timer.Start();
        _loginTimer = timer;
    }

    private async void LoginTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_currentOj is null || _loginChecking || _overlaySubmitMode)
        {
            return;
        }

        _loginChecking = true;
        try
        {
            bool loggedIn = await CheckLoginAsync(_currentOj, includePageScript: true);
            if (!loggedIn)
            {
                return;
            }

            sender.Stop();
            _loginTimer = null;
            await CompleteLoginAsync(_currentOj);
        }
        finally
        {
            _loginChecking = false;
        }
    }

    /// <summary>登录成功后统一收尾：关掉登录浮层，并在需要时重试一次题面抓取。</summary>
    private async Task CompleteLoginAsync(OjDefinition oj)
    {
        if (_overlaySubmitMode || LoginOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        _loginTimer?.Stop();
        _loginTimer = null;
        LoginRing.IsActive = false;
        LoginRing.Visibility = Visibility.Collapsed;
        LoginHintText.Text = string.Empty;
        LoginLoadingPanel.Visibility = Visibility.Visible;
        _loggedInOjs.Add(oj.Id);
        ApplyLoginButtonVisibility(oj);
        await Task.Delay(200);
        LoginOverlay.Visibility = Visibility.Collapsed;
        LoginWebView.Visibility = Visibility.Visible;

        // 之前因未登录抓取失败时，登录成功后自动重试一次
        if (_samples.Count == 0)
        {
            await FetchProblemAsync(_problemToken);
        }
    }

    /// <summary>
    /// 登录页跳走（通常意味着登录成功跳回主页）时立刻盖住 WebView，
    /// 确认已登录就直接关掉浮层，不让用户看到登录后的主页。
    /// </summary>
    private void LoginWebView_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (_currentOj is null || _overlaySubmitMode || LoginOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? target) ||
            !Uri.TryCreate(_currentOj.LoginUrl, UriKind.Absolute, out Uri? loginPage))
        {
            return;
        }

        // 仍停留在登录页（含同站不同登录子路径）时不动
        if (!string.Equals(target.Host, loginPage.Host, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.AbsolutePath.TrimEnd('/'), loginPage.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LoginWebView.Visibility = Visibility.Collapsed;
        LoginLoadingPanel.Visibility = Visibility.Visible;
        LoginHintText.Text = "正在确认登录状态…";
        _ = ConfirmLoginAfterRedirectAsync(_currentOj);
    }

    /// <summary>登录页正文为空（站点校验页）时自动重载一次，最多两次。</summary>
    private async Task RetryBlankLoginPageAsync(CoreWebView2 core)
    {
        try
        {
            string raw = (await core.ExecuteScriptAsync(
                "document.body ? document.body.innerText.trim().length : 0"))?.Trim() ?? "0";
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int length))
            {
                return;
            }

            if (length > 5)
            {
                _loginReloadCount = 0;
                return;
            }

            if (_loginReloadCount >= 2)
            {
                return;
            }

            _loginReloadCount++;
            await Task.Delay(900);
            core.Reload();
        }
        catch (Exception)
        {
            // 页面正在跳转时执行脚本会失败，忽略即可
        }
    }

    private async Task ConfirmLoginAfterRedirectAsync(OjDefinition oj)
    {
        for (int i = 0; i < 8; i++)
        {
            if (LoginOverlay.Visibility != Visibility.Visible || _currentOj?.Id != oj.Id)
            {
                return;
            }

            await Task.Delay(i == 0 ? 500 : 700);
            if (await CheckLoginAsync(oj, includePageScript: true))
            {
                await CompleteLoginAsync(oj);
                return;
            }
        }

        // 判断不了（例如跳到了验证页），把页面还给用户继续操作
        if (LoginOverlay.Visibility == Visibility.Visible && _currentOj?.Id == oj.Id)
        {
            LoginHintText.Text = string.Empty;
            LoginWebView.Visibility = Visibility.Visible;
        }
    }

    /// <summary>已登录则隐藏登录按钮，未登录才显示。</summary>
    private void ApplyLoginButtonVisibility(OjDefinition oj)
    {
        LoginButton.Content = "登录";
        LoginButton.Visibility = _loggedInOjs.Contains(oj.Id) ? Visibility.Collapsed : Visibility.Visible;
    }

    private Task UpdateLoginStateAsync(OjDefinition oj, bool? pageLoginState)
    {
        // 已登录的 OJ 记录下来，本次运行不再重复检测。
        if (pageLoginState == true)
        {
            _loggedInOjs.Add(oj.Id);
        }
        else if (pageLoginState == false
            && !_loggedInOjs.Contains(oj.Id)
            && !_loginAutoPrompted
            && LoginOverlay.Visibility != Visibility.Visible
            && _currentOj?.Id == oj.Id)
        {
            // 后台抓到未登录状态，主动弹出登录窗让用户登录（每题只自动弹一次）。
            _loginAutoPrompted = true;
            _ = ShowLoginAsync();
        }

        ApplyLoginButtonVisibility(oj);
        return Task.CompletedTask;
    }

    private async Task<bool> CheckLoginAsync(OjDefinition oj, bool includePageScript)
    {
        try
        {
            if (await _fetchBrowser.HasCookieAsync(oj.HomeUrl, oj.LoginCookies, _cancelSource?.Token ?? default))
            {
                return true;
            }
        }
        catch (Exception)
        {
            // 忽略，继续用页面源码判断
        }

        if (includePageScript && _loginViewInitialized && !string.IsNullOrWhiteSpace(oj.LoginCheckScript) &&
            LoginWebView.CoreWebView2 is not null)
        {
            try
            {
                string result = await LoginWebView.CoreWebView2.ExecuteScriptAsync(oj.LoginCheckScript);
                if (string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 页面跳转中执行脚本可能失败，下一轮再试
            }
        }

        return false;
    }

    private void OpenOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem is null)
        {
            return;
        }

        _ = Launcher.LaunchUriAsync(new Uri(_currentItem.Url));
    }

    /* ---------------- 验证码 ---------------- */

    private async Task<bool> ShowCaptchaAsync(string url)
    {
        await EnsureCaptchaViewAsync();
        if (CaptchaWebView.CoreWebView2 is null)
        {
            return false;
        }

        _captchaTcs = new TaskCompletionSource<bool>();
        CaptchaWebView.CoreWebView2.Navigate(url);
        CaptchaOverlay.Visibility = Visibility.Visible;

        return await _captchaTcs.Task;
    }

    private void CaptchaDone_Click(object sender, RoutedEventArgs e)
    {
        CaptchaOverlay.Visibility = Visibility.Collapsed;
        _captchaTcs?.TrySetResult(true);
    }

    private void CaptchaCancel_Click(object sender, RoutedEventArgs e)
    {
        CaptchaOverlay.Visibility = Visibility.Collapsed;
        _captchaTcs?.TrySetResult(false);
    }

    private async Task EnsureCaptchaViewAsync()
    {
        if (_captchaViewInitialized)
        {
            return;
        }

        _captchaViewInitialized = true;
        try
        {
            await WaitForLoadedAsync(CaptchaWebView);
            await CaptchaWebView.EnsureCoreWebView2Async();
            CoreWebView2 core = CaptchaWebView.CoreWebView2;
            CaptchaWebView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
            core.Settings.IsZoomControlEnabled = false;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            if (!string.IsNullOrEmpty(_config.UserAgent))
            {
                core.Settings.UserAgent = _config.UserAgent;
            }
        }
        catch (Exception)
        {
            _captchaViewInitialized = false;
        }
    }

    /* ---------------- 测试 / 提交 ---------------- */

    private static string? FindCompiler()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "mingw64", "bin", "g++.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            try
            {
                string candidate = Path.Combine(directory.Trim('"'), "g++.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private Task<string> GetEditorCodeAsync()
    {
        _codeTcs = new TaskCompletionSource<string>();
        SendEditorMessage(new { type = "getCode" });
        return _codeTcs.Task;
    }

    private void TestSample_Click(object sender, RoutedEventArgs e) => ShowTestSetup();

    /// <summary>打开测试面板，默认勾选全部样例。</summary>
    private void ShowTestSetup()
    {
        if (_currentItem is null)
        {
            return;
        }

        TestOverlay.Visibility = Visibility.Visible;
        TestSetupPanel.Visibility = Visibility.Visible;
        TestResultPanel.Visibility = Visibility.Collapsed;
        RunTestButton.Visibility = Visibility.Visible;
        TestBackButton.Visibility = Visibility.Collapsed;
        TestCloseButton.Visibility = Visibility.Collapsed;
        TestRunRing.IsActive = false;
        TestRunRing.Visibility = Visibility.Collapsed;
        TestHintText.Visibility = Visibility.Collapsed;
        TestCompileErrorBox.Visibility = Visibility.Collapsed;

        TestTitleText.Text = "测试";
        TestSubtitleText.Text = _samples.Count > 0
            ? _samples.Count + " 个样例"
            : "本题没有抓到样例，可以手动输入";

        foreach (OjSample sample in _samples)
        {
            sample.IsSelected = true;
        }

        TestSampleList.ItemsSource = _samples;

        // 没抓到样例时直接切到手动输入
        if (_samples.Count == 0)
        {
            TestManualMode.IsChecked = true;
        }
        else
        {
            TestSampleMode.IsChecked = true;
        }

        ApplyTestMode();
    }

    private void TestMode_Checked(object sender, RoutedEventArgs e) => ApplyTestMode();

    private void ApplyTestMode()
    {
        if (TestSampleSection is null || TestManualSection is null)
        {
            return;
        }

        bool manual = TestManualMode.IsChecked == true;
        TestSampleSection.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
        TestManualSection.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TestSelectAll_Click(object sender, RoutedEventArgs e) => SetAllSamplesSelected(true);

    private void TestSelectNone_Click(object sender, RoutedEventArgs e) => SetAllSamplesSelected(false);

    private void SetAllSamplesSelected(bool selected)
    {
        foreach (OjSample sample in _samples)
        {
            sample.IsSelected = selected;
        }
    }

    private void TestBack_Click(object sender, RoutedEventArgs e) => ShowTestSetup();

    private void CloseTest_Click(object sender, RoutedEventArgs e)
    {
        TestOverlay.Visibility = Visibility.Collapsed;
        TestRunRing.IsActive = false;
        TestRunRing.Visibility = Visibility.Collapsed;
    }

    private void ShowTestHint(string text)
    {
        TestHintText.Text = text;
        TestHintText.Visibility = Visibility.Visible;
    }

    /// <summary>切到结果视图，先显示编译中的状态。</summary>
    private void ShowTestResultView()
    {
        TestHintText.Visibility = Visibility.Collapsed;
        TestSetupPanel.Visibility = Visibility.Collapsed;
        TestResultPanel.Visibility = Visibility.Visible;
        RunTestButton.Visibility = Visibility.Collapsed;
        TestBackButton.Visibility = Visibility.Visible;
        TestCloseButton.Visibility = Visibility.Visible;
        TestCompileErrorBox.Visibility = Visibility.Collapsed;
        TestResultList.ItemsSource = null;
        TestTitleText.Text = "测试结果";
        TestSubtitleText.Text = "正在编译…";
        TestRunRing.Visibility = Visibility.Visible;
        TestRunRing.IsActive = true;
    }

    private async void RunTests_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem is null)
        {
            return;
        }

        bool manual = TestManualMode.IsChecked == true;
        var cases = new List<(string Title, string Input, string Expected, bool Check)>();
        if (manual)
        {
            cases.Add(("手动输入", TestManualInput.Text ?? string.Empty, string.Empty, false));
        }
        else
        {
            foreach (OjSample sample in _samples.Where(s => s.IsSelected))
            {
                cases.Add((sample.Title, sample.Input, sample.Output, true));
            }

            if (cases.Count == 0)
            {
                ShowTestHint("请至少选择一个样例");
                return;
            }
        }

        string code = await GetEditorCodeAsync();
        if (string.IsNullOrWhiteSpace(code))
        {
            ShowTestHint("草稿是空的，先写点代码");
            return;
        }

        string? compiler = FindCompiler();
        if (compiler is null)
        {
            ShowTestHint("未找到 g++，请确认 Assets\\mingw64\\bin\\g++.exe 存在");
            return;
        }

        ShowTestResultView();

        string tempDir = Path.Combine(Path.GetTempPath(), "mny-practice");
        Directory.CreateDirectory(tempDir);
        string sourcePath = Path.Combine(tempDir, "solution.cpp");
        string exePath = Path.Combine(tempDir, "solution.exe");
        File.WriteAllText(sourcePath, code);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = compiler,
            Arguments = $"-g -static {Quote(sourcePath)} -o {Quote(exePath)}",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        var compile = System.Diagnostics.Process.Start(psi);
        if (compile is null)
        {
            TestRunRing.IsActive = false;
            TestRunRing.Visibility = Visibility.Collapsed;
            TestSubtitleText.Text = "编译失败";
            TestCompileError.Text = "无法启动编译器";
            TestCompileErrorBox.Visibility = Visibility.Visible;
            return;
        }

        string compileErr = await compile.StandardError.ReadToEndAsync();
        await compile.WaitForExitAsync();
        if (compile.ExitCode != 0)
        {
            TestRunRing.IsActive = false;
            TestRunRing.Visibility = Visibility.Collapsed;
            TestSubtitleText.Text = "编译失败";
            TestCompileError.Text = compileErr.Trim().Length > 0 ? compileErr.Trim() : "编译器返回了非零退出码";
            TestCompileErrorBox.Visibility = Visibility.Visible;
            return;
        }

        var results = new List<TestResultItem>();
        int passed = 0;
        foreach (var testCase in cases)
        {
            var runPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var run = System.Diagnostics.Process.Start(runPsi);
            if (run is null)
            {
                results.Add(new TestResultItem
                {
                    Title = testCase.Title,
                    Status = "无法启动",
                    Outcome = TestOutcome.Failed,
                });
                continue;
            }

            await run.StandardInput.WriteAsync(testCase.Input);
            run.StandardInput.Close();
            string output = (await run.StandardOutput.ReadToEndAsync()).Trim();
            string runErr = (await run.StandardError.ReadToEndAsync()).Trim();
            await run.WaitForExitAsync();

            if (!testCase.Check)
            {
                // 手动输入只展示运行结果，不做对错判定
                bool ok = run.ExitCode == 0;
                results.Add(new TestResultItem
                {
                    Title = testCase.Title,
                    Status = ok ? "运行完成" : "运行错误",
                    Outcome = ok ? TestOutcome.Info : TestOutcome.Failed,
                    Detail = BuildTestDetail(testCase.Input, null, output, runErr, run.ExitCode),
                });
                continue;
            }

            string expected = testCase.Expected.Trim();
            bool passedCase = run.ExitCode == 0
                              && string.Equals(Normalize(output), Normalize(expected), StringComparison.Ordinal);
            if (passedCase)
            {
                passed++;
            }

            results.Add(new TestResultItem
            {
                Title = testCase.Title,
                Status = passedCase ? "通过" : (run.ExitCode != 0 ? "运行错误" : "答案错误"),
                Outcome = passedCase ? TestOutcome.Passed : TestOutcome.Failed,
                Detail = passedCase
                    ? string.Empty
                    : BuildTestDetail(testCase.Input, expected, output, runErr, run.ExitCode),
            });
        }

        TestResultList.ItemsSource = results;
        TestRunRing.IsActive = false;
        TestRunRing.Visibility = Visibility.Collapsed;
        TestSubtitleText.Text = manual ? "运行完成" : $"{passed} / {cases.Count} 通过";
    }

    /// <summary>失败用例的详细信息：输入、期望输出、实际输出。</summary>
    private static string BuildTestDetail(string input, string? expected, string actual, string error, int exitCode)
    {
        var builder = new StringBuilder();
        builder.AppendLine("输入");
        builder.AppendLine(input.Trim().Length == 0 ? "（空）" : Truncate(input.Trim()));
        if (expected is not null)
        {
            builder.AppendLine("期望输出");
            builder.AppendLine(expected.Length == 0 ? "（空）" : Truncate(expected));
        }

        builder.AppendLine("实际输出");
        builder.AppendLine(actual.Length == 0 ? "（空）" : Truncate(actual));

        if (error.Length > 0)
        {
            builder.AppendLine("stderr");
            builder.Append(Truncate(error));
        }
        else if (exitCode != 0)
        {
            builder.Append("（退出码 ").Append(exitCode.ToString(CultureInfo.InvariantCulture)).Append('）');
        }

        return builder.ToString().TrimEnd();
    }

    private static string Normalize(string s) =>
        string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()));

    private static string Truncate(string s) => s.Length > 200 ? s[..200] + "…" : s;

    private static string Quote(string path) => "\"" + path + "\"";

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_currentOj is null || _currentItem is null) return;

        string code = await GetEditorCodeAsync();
        if (string.IsNullOrWhiteSpace(code))
        {
            await ShowSimpleDialogAsync("提交", "草稿是空的，先写点代码再提交吧。");
            return;
        }

        string submitUrl = _currentOj.SubmitUrl(_currentItem.Id, _currentItem.Url);
        await EnsureLoginViewAsync();
        if (LoginWebView.CoreWebView2 is null)
        {
            await ShowSimpleDialogAsync("提交", "WebView2 不可用。");
            return;
        }

        // 直接把代码复制到剪贴板，用户在页面里粘贴后提交
        var package = new DataPackage();
        package.SetText(code);
        Clipboard.SetContent(package);

        LoginTitleText.Text = "提交到 " + _currentOj.Name;
        LoginHintText.Text = "代码已复制，粘贴到代码框后提交";
        LoginRing.IsActive = false;
        LoginRing.Visibility = Visibility.Collapsed;
        // 提交页不是登录流程：停掉登录轮询，跳转时也不做登录判定
        _overlaySubmitMode = true;
        _loginReloadCount = 0;
        _loginTimer?.Stop();
        _loginTimer = null;
        LoginWebView.Visibility = Visibility.Visible;
        UpdateLoginOverlaySize();
        // 先显示加载占位，等页面 NavigationCompleted 后再露出内容
        LoginLoadingPanel.Visibility = Visibility.Visible;
        LoginWebView.CoreWebView2.Navigate(submitUrl);
        LoginOverlay.Visibility = Visibility.Visible;
    }

    private async Task ShowSimpleDialogAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    /* ---------------- 草稿编辑器 ---------------- */

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedItem is ComboBoxItem item && item.Tag is string language)
        {
            _currentLanguage = language;
            SendEditorMessage(new { type = "language", language });
        }
    }

    private void ResetDraft_Click(object sender, RoutedEventArgs e)
    {
        SendEditorMessage(new { type = "reset", code = NewFileTemplate() });
    }

    /// <summary>草稿的默认内容：设置 → 编辑器 → 新文件模板。</summary>
    private string NewFileTemplate() => _settings.InsertTemplateOnNewFile ? _settings.TemplateCode : string.Empty;

    private void SendEditorOpen()
    {
        SendEditorMessage(new
        {
            type = "open",
            key = _currentItem?.Key ?? string.Empty,
            language = _currentLanguage,
            code = NewFileTemplate(),
            theme = EditorThemeName(),
            fontSize = _settings.EditorFontSize,
            fontFamily = _settings.EditorFontFamily,
            fontLigatures = _settings.EditorFontLigatures,
            tabSize = _settings.EditorTabSize,
            insertSpaces = _settings.EditorInsertSpaces,
            wordWrap = _settings.EditorWordWrap,
            minimap = _settings.EditorMinimap,
            renderWhitespace = _settings.EditorWhitespace,
            cursorStyle = _settings.EditorCursorStyle,
            cursorBlinking = _settings.EditorCursorBlinking,
        });
    }

    private void ApplySettingsToEditor()
    {
        SendEditorMessage(new
        {
            type = "options",
            theme = EditorThemeName(),
            fontSize = _settings.EditorFontSize,
            fontFamily = _settings.EditorFontFamily,
            fontLigatures = _settings.EditorFontLigatures,
            tabSize = _settings.EditorTabSize,
            insertSpaces = _settings.EditorInsertSpaces,
            wordWrap = _settings.EditorWordWrap,
            minimap = _settings.EditorMinimap,
            renderWhitespace = _settings.EditorWhitespace,
            cursorStyle = _settings.EditorCursorStyle,
            cursorBlinking = _settings.EditorCursorBlinking,
        });
    }

    private string EditorThemeName()
    {
        return _settings.EditorTheme switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => _isDark ? "dark" : "light",
        };
    }

    private void SendTheme()
    {
        string theme = _isDark ? "dark" : "light";
        if (_problemViewerReady)
        {
            SendProblemMessage(new { type = "theme", theme });
        }

        SendEditorMessage(new { type = "theme", theme = EditorThemeName() });
    }

    /* ---------------- WebView2 初始化 ---------------- */

    private async Task EnsureProblemViewerAsync()
    {
        if (!_problemViewerInitialized)
        {
            _problemViewerInitialized = true;
            try
            {
                await WaitForLoadedAsync(ProblemWebView);
                await ProblemWebView.EnsureCoreWebView2Async();
                CoreWebView2 core = ProblemWebView.CoreWebView2;
                ProblemWebView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
                core.Settings.IsZoomControlEnabled = false;
                core.SetVirtualHostNameToFolderMapping(
                    PracticeHostName,
                    Path.Combine(AppContext.BaseDirectory, "Assets"),
                    CoreWebView2HostResourceAccessKind.Allow);
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.WebMessageReceived += ProblemViewer_MessageReceived;
                core.Navigate($"{ViewerPageUrl}?v={DateTime.Now.Ticks}");
            }
            catch (Exception)
            {
                _problemViewerInitialized = false;
                ProblemStatusText.Text = "题面面板初始化失败（WebView2 不可用）";
            }
        }
    }

    private async Task EnsurePracticeEditorAsync()
    {
        if (_editorInitialized)
        {
            return;
        }

        _editorInitialized = true;
        try
        {
            await WaitForLoadedAsync(PracticeEditorWebView);
            await PracticeEditorWebView.EnsureCoreWebView2Async();
            CoreWebView2 core = PracticeEditorWebView.CoreWebView2;
            PracticeEditorWebView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(
                PracticeHostName,
                Path.Combine(AppContext.BaseDirectory, "Assets"),
                CoreWebView2HostResourceAccessKind.Allow);
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.WebMessageReceived += PracticeEditor_MessageReceived;
            core.Navigate($"{EditorPageUrl}?v={DateTime.Now.Ticks}");
        }
        catch (Exception)
        {
            _editorInitialized = false;
            ProblemStatusText.Text = "草稿编辑器初始化失败（WebView2 不可用）";
        }
    }

    private async Task EnsureLoginViewAsync()
    {
        if (_loginViewInitialized)
        {
            return;
        }

        _loginViewInitialized = true;
        try
        {
            await WaitForLoadedAsync(LoginWebView);
            await LoginWebView.EnsureCoreWebView2Async();
            CoreWebView2 core = LoginWebView.CoreWebView2;
            LoginWebView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
            core.Settings.IsZoomControlEnabled = false;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            // 页面加载完成后才撤掉加载占位，避免用户看到 WebView 白屏/半加载过程
            core.NavigationCompleted += async (_, _) =>
            {
                LoginLoadingPanel.Visibility = Visibility.Collapsed;
                // 登录页已经显示出来了，右上角不再转圈
                LoginRing.IsActive = false;
                LoginRing.Visibility = Visibility.Collapsed;

                // 洛谷会先返回一个只带脚本的验证页（正文为空），浏览器执行完校验后需要再加载一次；
                // 不重载就会停在白屏，看起来像「加载不出来」
                await RetryBlankLoginPageAsync(core);
            };
            core.NavigationStarting += LoginWebView_NavigationStarting;
            if (!string.IsNullOrEmpty(_config.UserAgent))
            {
                core.Settings.UserAgent = _config.UserAgent;
            }

            // 外部登录页画不了圆角，这里注入 CSS 让 WebView2 的方形表面跟随卡片圆角
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "document.documentElement.style.overflow='hidden';" +
                "document.documentElement.style.borderRadius='12px';" +
                "document.documentElement.style.background='transparent';");

            // AtCoder 题目/提交页的语言下拉默认不是最新的 C++，页面出来后就切到 C++23
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                """
                (function () {
                  if (location.hostname.indexOf('atcoder.jp') < 0) { return; }
                  var WANT = 'C++23 (GCC 15.2.0)';
                  function pick() {
                    var list = document.querySelectorAll('select');
                    for (var k = 0; k < list.length; k++) {
                      var s = list[k];
                      if (!s.options || s.options.length < 2) { continue; }
                      var exact = -1, prefix = -1;
                      for (var i = 0; i < s.options.length; i++) {
                        var t = (s.options[i].textContent || '').trim();
                        if (t === WANT) { exact = i; break; }
                        if (prefix < 0 && t.indexOf('C++23') === 0) { prefix = i; }
                      }
                      var idx = exact >= 0 ? exact : prefix;
                      if (idx >= 0) {
                        if (s.selectedIndex !== idx) {
                          s.selectedIndex = idx;
                          s.dispatchEvent(new Event('change', { bubbles: true }));
                        }
                        return;
                      }
                    }
                  }
                  pick();
                  document.addEventListener('DOMContentLoaded', pick);
                  setTimeout(pick, 400);
                  setTimeout(pick, 1200);
                })();
                """);
        }
        catch (Exception)
        {
            _loginViewInitialized = false;
            LoginHintText.Text = "登录页初始化失败（WebView2 不可用）";
        }
    }

    private static Task WaitForLoadedAsync(FrameworkElement element)
    {
        if (element.IsLoaded)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource();
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            element.Loaded -= OnLoaded;
            completion.TrySetResult();
        }

        element.Loaded += OnLoaded;
        return completion.Task;
    }

    /* ---------------- WebView2 消息 ---------------- */

    private void ProblemViewer_MessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(args.WebMessageAsJson);
        }
        catch (Exception)
        {
            return;
        }

        string type = message?["type"]?.ToString() ?? string.Empty;
        switch (type)
        {
            case "ready":
                _problemViewerReady = true;
                foreach (string pending in _pendingProblemMessages)
                {
                    SendToProblemViewer(pending);
                }

                _pendingProblemMessages.Clear();
                break;
            case "zoomStep":
                ChangeUiZoom(message?["delta"]?.GetValue<int>() ?? 0);
                break;
            case "openExternal": {
                string url = message?["url"]?.ToString() ?? string.Empty;
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                {
                    _ = Launcher.LaunchUriAsync(uri);
                }

                break;
            }
        }
    }

    private void PracticeEditor_MessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(args.WebMessageAsJson);
        }
        catch (Exception)
        {
            return;
        }

        if ((message?["type"]?.ToString() ?? string.Empty) == "ready")
        {
            _editorReady = true;
            foreach (string pending in _pendingEditorMessages)
            {
                SendToPracticeEditor(pending);
            }

            _pendingEditorMessages.Clear();
            return;
        }

        if ((message?["type"]?.ToString() ?? string.Empty) == "code" && _codeTcs is not null)
        {
            _codeTcs.TrySetResult(message?["value"]?.ToString() ?? string.Empty);
            return;
        }

        if ((message?["type"]?.ToString() ?? string.Empty) == "zoomStep")
        {
            ChangeUiZoom(message?["delta"]?.GetValue<int>() ?? 0);
        }
    }

    private void SendProblemMessage(object payload)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(payload, payload.GetType(), PayloadOptions);
        }
        catch (Exception)
        {
            return;
        }

        if (!_problemViewerReady)
        {
            _pendingProblemMessages.Add(json);
            return;
        }

        SendToProblemViewer(json);
    }

    private void SendEditorMessage(object payload)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(payload, payload.GetType(), PayloadOptions);
        }
        catch (Exception)
        {
            return;
        }

        if (!_editorReady)
        {
            _pendingEditorMessages.Add(json);
            return;
        }

        SendToPracticeEditor(json);
    }

    private void SendToProblemViewer(string json)
    {
        try
        {
            ProblemWebView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception)
        {
            // 页面正在导航时忽略
        }
    }

    private void SendToPracticeEditor(string json)
    {
        try
        {
            PracticeEditorWebView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception)
        {
            // 页面正在导航时忽略
        }
    }

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>题目详情面板是从哪里打开的，用于返回时回到对应列表。</summary>
    private enum ProblemOrigin
    {
        Home,
        Results,
        Starred,
        Training,

        /// <summary>从板块页的跨题单搜索结果打开的题目。</summary>
        TrainingSearch,
    }
}
