using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly ObservableCollection<LuoguTrainingSummary> _visibleTrainings = new();
    private readonly ObservableCollection<LuoguCategory> _categories = new();
    private readonly ObservableCollection<LuoguCategory> _srqcCategories = new();
    private readonly ObservableCollection<LuoguCategory> _monthCategories = new();
    private readonly ObservableCollection<LuoguCategory> _contestCategories = new();
    private readonly ObservableCollection<LuoguCategory> _bookCategories = new();
    private readonly ObservableCollection<LuoguCategory> _selectCategories = new();
    private readonly ObservableCollection<OjProblemItem> _visibleTrainingProblems = new();
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

        ResultList.ItemsSource = _results;
        StarredList.ItemsSource = _starred;
        TrainingGrid.ItemsSource = _visibleTrainings;
        SrqcGrid.ItemsSource = _srqcCategories;
        MonthGrid.ItemsSource = _monthCategories;
        ContestGrid.ItemsSource = _contestCategories;
        BookGrid.ItemsSource = _bookCategories;
        SelectGrid.ItemsSource = _selectCategories;
        TrainingProblemList.ItemsSource = _visibleTrainingProblems;

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
        RandomLuoguIcon.Source = OjIcons.Get("luogu", _isDark);
        CodeforcesIcon.Source = OjIcons.Get("codeforces", _isDark);
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
        LoginRing.IsActive = false;
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

    private void OjToggle_Click(object sender, RoutedEventArgs e)
    {
        // 至少保留一个平台，否则搜索没有意义
        if (SelectedOjs().Count == 0 && sender is ToggleButton forced)
        {
            forced.IsChecked = true;
        }

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

    private async void RandomProblem_Click(object sender, RoutedEventArgs e)
    {
        OjDefinition? oj = _config.Ojs.FirstOrDefault(o => o.Id.Equals("luogu", StringComparison.OrdinalIgnoreCase));
        if (oj is null)
        {
            ShowOjToggleTip("没有找到洛谷题库配置");
            return;
        }

        string difficulty = _settings.PracticeDifficulty;
        int luoguDifficulty = difficulty == "any" ? -1 : PickLuoguDifficulty(difficulty);
        string WithDifficulty(string url) => luoguDifficulty >= 0 ? url + "&difficulty=" + luoguDifficulty : url;

        RandomProblemButton.IsEnabled = false;
        try
        {
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
                    _cancelSource?.Token ?? default);

                if (node?["items"] is not JsonArray listItems || listItems.Count == 0)
                {
                    ShowOjToggleTip("洛谷题单加载失败，请稍后重试");
                    return;
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
                    _cancelSource?.Token ?? default);

                if (pageNode?["items"] is not JsonArray pageItems || pageItems.Count == 0)
                {
                    ShowOjToggleTip("洛谷题单加载失败，请稍后重试");
                    return;
                }

                items = pageItems;
            }

            JsonNode? row = items[Math.Min(index, items.Count - 1)];
            string url = row?["url"]?.ToString() ?? string.Empty;
            string title = row?["title"]?.ToString() ?? string.Empty;
            if (url.Length == 0 || title.Length == 0)
            {
                ShowOjToggleTip("没抽到题目，再试一次");
                return;
            }

            var item = new OjProblemItem
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

            _problemOrigin = ProblemOrigin.Home;
            await OpenProblemAsync(item);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            RandomProblemButton.IsEnabled = true;
        }
    }

    private async Task RunSearchAsync()
    {
        string query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            ResultStatusText.Text = "请输入要搜索的题目关键词或题号";
            return;
        }

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
            ResultStatusText.Text = "请至少选择一个搜索范围";
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
        foreach (ToggleButton toggle in new[] { LuoguToggle, CodeforcesToggle, AtCoderToggle, NowCoderToggle })
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
        _currentCategory = category;
        _trainingPage = 1;
        _trainingTotal = 0;
        _trainings.Clear();
        TrainingSearchBox.Text = string.Empty;

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

        ApplyTrainingFilter();
        return true;
    }

    private void TrainingSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyTrainingFilter();

    /// <summary>在已加载的题单里按标题过滤，只影响当前板块的子页面。</summary>
    private void ApplyTrainingFilter()
    {
        string query = TrainingSearchBox.Text.Trim();
        bool filtering = query.Length > 0;

        _visibleTrainings.Clear();
        foreach (LuoguTrainingSummary training in _trainings)
        {
            if (filtering && training.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            _visibleTrainings.Add(training);
        }

        TrainingMoreButton.Visibility = !filtering && _trainings.Count > 0 && _trainings.Count < _trainingTotal
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (filtering)
        {
            TrainingListStatus.Text = "匹配 " + _visibleTrainings.Count + " 个题单";
        }
        else if (_trainings.Count == 0)
        {
            TrainingListStatus.Text = "正在加载…";
        }
        else
        {
            TrainingListStatus.Text = _trainings.Count + " / " + _trainingTotal + " 个题单";
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
        TrainingPanel.Visibility = Visibility.Collapsed;
        TrainingListPanel.Visibility = Visibility.Collapsed;
        HomePanel.Visibility = Visibility.Visible;
        PlayEnter(HomeStack, HomeStackTransform);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void TrainingListBack_Click(object sender, RoutedEventArgs e)
    {
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

        HomePanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        TrainingPanel.Visibility = Visibility.Collapsed;
        TrainingListPanel.Visibility = Visibility.Collapsed;
        TrainingDetailPanel.Visibility = Visibility.Collapsed;
        ProblemPanel.Visibility = Visibility.Visible;
        PlayEnter(ProblemPanel, ProblemTransform);

        ProblemOjText.Text = item.OjName;
        ProblemOjIcon.Source = OjIcons.Get(item.OjId, _isDark);
        ProblemTitleText.Text = item.Title;
        ProblemStatusText.Text = "正在抓取题面…";
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
                if (_samples.Count > 0)
                {
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

                SendProblemMessage(new
                {
                    type = "render",
                    title = TitleTextForViewer(item, title),
                    oj = oj.Name,
                    meta = item.Id,
                    markdown,
                    theme = _isDark ? "dark" : "light",
                    baseUrl = item.Url,
                });

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
        LoginTitleText.Text = "登录 " + oj.Name;
        LoginHintText.Text = string.Empty;
        LoginRing.IsActive = true;
        UpdateLoginOverlaySize();
        LoginOverlay.Visibility = Visibility.Visible;
        PlayEnter(LoginOverlay);

        await EnsureLoginViewAsync();
        if (LoginWebView.CoreWebView2 is null)
        {
            LoginHintText.Text = "登录页初始化失败，请检查 WebView2 运行库";
            return;
        }

        // 先显示加载占位，等页面 NavigationCompleted 后再露出内容
        LoginLoadingPanel.Visibility = Visibility.Visible;
        LoginWebView.CoreWebView2.Navigate(oj.LoginUrl);
        StartLoginWatch();
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
        if (_currentOj is null || _loginChecking)
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
            LoginRing.IsActive = false;
            LoginHintText.Text = string.Empty;
            _loggedInOjs.Add(_currentOj.Id);
            ApplyLoginButtonVisibility(_currentOj);
            await Task.Delay(300);
            LoginOverlay.Visibility = Visibility.Collapsed;

            // 之前因未登录抓取失败时，登录成功后自动重试一次
            if (_samples.Count == 0)
            {
                await FetchProblemAsync(_problemToken);
            }
        }
        finally
        {
            _loginChecking = false;
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
        LoginHintText.Text = "请粘贴到代码框后提交";
        LoginRing.IsActive = false;
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
            core.NavigationCompleted += (_, _) => LoginLoadingPanel.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrEmpty(_config.UserAgent))
            {
                core.Settings.UserAgent = _config.UserAgent;
            }

            // 外部登录页画不了圆角，这里注入 CSS 让 WebView2 的方形表面跟随卡片圆角
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "document.documentElement.style.overflow='hidden';" +
                "document.documentElement.style.borderRadius='12px';" +
                "document.documentElement.style.background='transparent';");
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
    }
}
