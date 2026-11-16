using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
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
    private readonly ObservableCollection<OjSample> _samples = new();
    private readonly HashSet<string> _seenItems = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本次运行中已确认登录过的 OJ：页面判断不了登录态时用它避免重复提示。</summary>
    private readonly HashSet<string> _loggedInOjs = new(StringComparer.OrdinalIgnoreCase);
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

        _isDark = ActualTheme == ElementTheme.Dark;
        ApplyOjIcons();
        SizeChanged += PracticeRoot_SizeChanged;
        ActualThemeChanged += (_, _) =>
        {
            _isDark = ActualTheme == ElementTheme.Dark;
            ApplyOjIcons();
            RefreshResultIcons();
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

        storyboard.Begin();
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

    /// <summary>Esc：先收起登录浮层，其他状态交给主窗口。</summary>
    public void HandleEscape()
    {
        if (LoginOverlay.Visibility == Visibility.Visible)
        {
            CloseLogin();
        }
    }

    private void CloseLogin()
    {
        _loginTimer?.Stop();
        _loginTimer = null;
        LoginRing.IsActive = false;
        LoginOverlay.Visibility = Visibility.Collapsed;
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
        if (SelectedOjs().Count > 0)
        {
            return;
        }

        if (sender is ToggleButton toggle)
        {
            toggle.IsChecked = true;
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
                return (0, error.Length > 0 ? oj.Name + "（" + error + "）" : string.Empty, false, true);
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
        PlayEnter(ResultsPanel, ResultsTransform);
    }

    private void BackToHome_Click(object sender, RoutedEventArgs e)
    {
        ResultsPanel.Visibility = Visibility.Collapsed;
        ProblemPanel.Visibility = Visibility.Collapsed;
        SearchLoadingOverlay.Visibility = Visibility.Collapsed;
        HomePanel.Visibility = Visibility.Visible;
        PlayEnter(HomeStack, HomeStackTransform);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void BackToResults_Click(object sender, RoutedEventArgs e)
    {
        ProblemPanel.Visibility = Visibility.Collapsed;
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
                              ?? _starred.FirstOrDefault(r => r.Key == key);
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

    /* ---------------- 打开题目 ---------------- */

    private async void ResultList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OjProblemItem item)
        {
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
        ProblemPanel.Visibility = Visibility.Visible;
        PlayEnter(ProblemPanel, ProblemTransform);

        ProblemOjText.Text = item.OjName;
        ProblemOjIcon.Source = OjIcons.Get(item.OjId, _isDark);
        ProblemTitleText.Text = item.Title;
        ProblemStatusText.Text = "正在抓取题面…";
        LoginButton.Content = "登录";
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

        // 先在后台检查登录态：配置了登录 Cookie 的 OJ 才检测，未登录才弹出登录窗；
        // 没配置 Cookie 的站点不自动弹窗，用户可点「登录」手动登录。
        bool loggedIn = oj.LoginCookies.Count > 0
            ? await CheckLoginAsync(oj, includePageScript: false)
            : true;
        if (token != _problemToken)
        {
            return;
        }
        if (!loggedIn)
        {
            _ = ShowLoginAsync();
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

    private Task UpdateLoginStateAsync(OjDefinition oj, bool? pageLoginState)
    {
        // 登录态由打开题面前的后台检测负责弹窗；这里只记录已登录的 OJ，
        // 登录按钮始终显示「登录」，不再显示「已登录」。
        if (pageLoginState == true)
        {
            _loggedInOjs.Add(oj.Id);
        }

        LoginButton.Content = "登录";
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

    private async void TestSample_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem is null) return;

        string code = await GetEditorCodeAsync();
        if (string.IsNullOrWhiteSpace(code))
        {
            await ShowSimpleDialogAsync("测试", "草稿是空的，先写点代码再测试吧。");
            return;
        }

        string? compiler = FindCompiler();
        if (compiler is null)
        {
            await ShowSimpleDialogAsync("测试", "未找到 g++。请确认 Assets\\mingw64\\bin\\g++.exe 存在，或系统已安装 MinGW-w64。");
            return;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "mny-practice");
        Directory.CreateDirectory(tempDir);
        string sourcePath = Path.Combine(tempDir, "solution.cpp");
        string exePath = Path.Combine(tempDir, "solution.exe");
        File.WriteAllText(sourcePath, code);

        // 编译
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
            await ShowSimpleDialogAsync("测试", "无法启动编译器。");
            return;
        }
        string compileErr = await compile.StandardError.ReadToEndAsync();
        await compile.WaitForExitAsync();
        if (compile.ExitCode != 0)
        {
            await ShowSimpleDialogAsync("编译失败", compileErr.Length > 1500 ? compileErr[..1500] : compileErr);
            return;
        }

        // 逐个样例运行
        var results = new List<string>();
        int passed = 0;
        foreach (OjSample sample in _samples)
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
            if (run is null) { results.Add($"❌ {sample.Title}: 无法启动程序"); continue; }

            await run.StandardInput.WriteAsync(sample.Input);
            run.StandardInput.Close();
            string output = (await run.StandardOutput.ReadToEndAsync()).Trim();
            string runErr = (await run.StandardError.ReadToEndAsync()).Trim();
            await run.WaitForExitAsync();

            string expected = sample.Output.Trim();
            bool ok = run.ExitCode == 0 && string.Equals(Normalize(output), Normalize(expected), StringComparison.Ordinal);
            if (ok) passed++;
            string status = ok ? "✅ 通过" : "❌ 错误";
            string detail = ok
                ? string.Empty
                : $"\n  期望: {Truncate(expected)}\n  实际: {Truncate(output)}"
                  + (runErr.Length > 0 ? $"\n  stderr: {Truncate(runErr)}" : string.Empty);
            results.Add($"{status}  {sample.Title}{detail}");
        }

        string title = $"测试结果：{passed}/{_samples.Count} 通过";
        await ShowSimpleDialogAsync(title, string.Join("\n\n", results));
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
            _codeTcs.TrySetResult(message["value"]?.ToString() ?? string.Empty);
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
}
