using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinUI3Shell.Oj;

/// <summary>单个 OJ 的配置：搜索地址、抓取脚本、登录检测方式（来自 Assets/oj/oj-config.json）。</summary>
public sealed class OjDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#0078D4";
    public string HomeUrl { get; set; } = string.Empty;
    public string SearchUrl { get; set; } = string.Empty;
    public string ProblemUrl { get; set; } = string.Empty;
    public string LoginUrl { get; set; } = string.Empty;
    /// <summary>提交页地址模板，{id} 会被替换成题目 id；为空时回退到题目页。</summary>
    public string SubmitUrlPattern { get; set; } = string.Empty;
    public string SearchScript { get; set; } = string.Empty;
    public string ProblemScript { get; set; } = string.Empty;
    public string LoginCheckScript { get; set; } = string.Empty;
    public List<string> LoginCookies { get; set; } = new();
    public int SettleMs { get; set; } = 900;

    /// <summary>题面页需要更多渲染时间时单独设置（0 表示沿用 SettleMs）。</summary>
    public int ProblemSettleMs { get; set; }

    public int EffectiveProblemSettleMs => ProblemSettleMs > 0 ? ProblemSettleMs : SettleMs;

    /// <summary>抓取搜索页时禁用页面脚本：用于首屏数据只在服务端渲染里的站点（如牛客）。</summary>
    public bool DisableJs { get; set; }

    /// <summary>搜不到结果时是否重试一次（部分站点会间歇性返回不带数据的空壳页面）。</summary>
    public bool RetryEmpty { get; set; }

    /// <summary>搜索时最多翻几页（每页通常 50 条；0 或 1 表示只取第一页）。</summary>
    public int MaxPages { get; set; } = 1;

    public string BuildSearchUrl(string query, int page = 1) =>
        SearchUrl
            .Replace("{q}", Uri.EscapeDataString(query))
            .Replace("{page}", page.ToString());

    public string BuildProblemUrl(string id, string fallbackUrl) =>
        string.IsNullOrEmpty(ProblemUrl) || ProblemUrl.Contains("{id}", StringComparison.Ordinal) == false
            ? fallbackUrl
            : ProblemUrl.Replace("{id}", id);

    /// <summary>提交页地址；未配置提交模板时直接用题目页。</summary>
    public string SubmitUrl(string id, string fallbackUrl) =>
        string.IsNullOrEmpty(SubmitUrlPattern)
            ? BuildProblemUrl(id, fallbackUrl)
            : SubmitUrlPattern.Replace("{id}", id);
}

public sealed class OjConfig
{
    public string UserAgent { get; set; } = string.Empty;
    public List<OjDefinition> Ojs { get; set; } = new();

    /// <summary>从应用目录读取 Assets/oj/oj-config.json，缺失时返回空配置而不是抛异常。</summary>
    public static OjConfig Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "oj", "oj-config.json");
        try
        {
            if (File.Exists(path))
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                OjConfig? config = JsonSerializer.Deserialize<OjConfig>(File.ReadAllText(path), options);
                if (config is not null && config.Ojs.Count > 0)
                {
                    return config;
                }
            }
        }
        catch (Exception)
        {
            // 配置损坏时退回空列表，界面会给出提示
        }

        return new OjConfig();
    }

    public OjDefinition? Find(string id) =>
        Ojs.FirstOrDefault(oj => string.Equals(oj.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed class OjProblemItem : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _isStarred;

    public string OjId { get; set; } = string.Empty;
    public string OjName { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Meta { get; set; } = string.Empty;

    /// <summary>总提交次数（用于排序；没有数据时为 0）。</summary>
    public long SubmitCount { get; set; }

    /// <summary>通过率 0~1（用于排序；没有数据时为 -1）。</summary>
    public double PassRate { get; set; } = -1;

    /// <summary>难度数值（洛谷 0~7，其他平台 -1）。</summary>
    public int Difficulty { get; set; } = -1;

    /// <summary>列表左侧的平台图标（跟随明暗主题切换）。</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value))
            {
                return;
            }

            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key => OjId + ":" + Id;

    /// <summary>是否已标星（影响搜索结果置顶和标星列表）。</summary>
    public bool IsStarred
    {
        get => _isStarred;
        set
        {
            if (_isStarred == value)
            {
                return;
            }

            _isStarred = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStarred)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StarGlyph)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StarForeground)));
        }
    }

    /// <summary>标星图标：已标星为实心星，否则为空心星。</summary>
    public string StarGlyph => _isStarred ? "\uE735" : "\uE734";

    /// <summary>标星按钮颜色：已标星用主题强调色，否则用灰色。</summary>
    public Brush StarForeground => _isStarred
        ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
        : (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"];

    /// <summary>列表项的无障碍名称（也是 UIA 自动化测试里唯一能区分条目的文本）。</summary>
    public string DisplayName => OjName + " · " + Title + " " + Id;

    public override string ToString() => DisplayName;
}

public sealed class OjSample
{
    public string Title { get; set; } = string.Empty;
    public string Input { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    public Visibility NoteVisibility => string.IsNullOrWhiteSpace(Note) ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class OjProblemDetail
{
    public string Title { get; set; } = string.Empty;
    public string Markdown { get; set; } = string.Empty;
    public List<OjSample> Samples { get; set; } = new();
}

/// <summary>搜索结果排序方式。</summary>
public enum OjSortMode
{
    /// <summary>提交量升序（默认）。</summary>
    SubmitAsc,
    /// <summary>提交量降序。</summary>
    SubmitDesc,
    /// <summary>通过率升序。</summary>
    PassRateAsc,
    /// <summary>通过率降序。</summary>
    PassRateDesc,
    /// <summary>难度升序。</summary>
    DifficultyAsc,
    /// <summary>难度降序。</summary>
    DifficultyDesc,
    /// <summary>标题字母升序。</summary>
    TitleAsc,
}

internal static class OjSorter
{
    public static IEnumerable<OjProblemItem> Sort(IEnumerable<OjProblemItem> items, OjSortMode mode)
    {
        // 标星题目始终置顶，其余按所选方式排序
        var list = items.ToList();
        var starred = list.Where(i => i.IsStarred).ToList();
        var rest = list.Where(i => !i.IsStarred);
        IEnumerable<OjProblemItem> sortedRest = mode switch
        {
            OjSortMode.SubmitAsc => rest.OrderBy(i => i.SubmitCount <= 0 ? long.MaxValue : i.SubmitCount),
            OjSortMode.SubmitDesc => rest.OrderByDescending(i => i.SubmitCount),
            OjSortMode.PassRateAsc => rest.OrderBy(i => i.PassRate < 0 ? double.MaxValue : i.PassRate),
            OjSortMode.PassRateDesc => rest.OrderByDescending(i => i.PassRate < 0 ? -1 : i.PassRate),
            OjSortMode.DifficultyAsc => rest.OrderBy(i => i.Difficulty < 0 ? int.MaxValue : i.Difficulty),
            OjSortMode.DifficultyDesc => rest.OrderByDescending(i => i.Difficulty),
            OjSortMode.TitleAsc => rest.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase),
            _ => rest,
        };
        return starred.Concat(sortedRest);
    }
}

internal static class OjIcons
{
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 读取 Assets/oj/icons/{id}.{dark|light}.png：已抠图并转成单色的平台图标，
    /// dark 为白色图标（暗色主题用），light 为黑色图标（亮色主题用）。
    /// </summary>
    public static ImageSource Get(string ojId, bool isDark)
    {
        string variant = isDark ? "dark" : "light";
        string key = ojId + "|" + variant;
        if (Cache.TryGetValue(key, out ImageSource? cached))
        {
            return cached;
        }

        var image = new BitmapImage(new Uri(
            Path.Combine(AppContext.BaseDirectory, "Assets", "oj", "icons", ojId + "." + variant + ".png")));
        Cache[key] = image;
        return image;
    }
}

/// <summary>标星题目的持久化：存在用户 AppData 下的 starred.json。</summary>
public sealed class StarredEntry
{
    public string OjId { get; set; } = string.Empty;
    public string OjName { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Meta { get; set; } = string.Empty;
}

internal static class StarService
{
    private static readonly string StarPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MnyC++", "starred.json");

    private static readonly object Lock = new();
    private static List<StarredEntry>? _cache;

    private static List<StarredEntry> Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            if (File.Exists(StarPath))
            {
                _cache = JsonSerializer.Deserialize<List<StarredEntry>>(File.ReadAllText(StarPath)) ?? new List<StarredEntry>();
                return _cache;
            }
        }
        catch (Exception) { }

        _cache = new List<StarredEntry>();
        return _cache;
    }

    private static void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(StarPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(StarPath, JsonSerializer.Serialize(_cache ?? new List<StarredEntry>()));
        }
        catch (Exception) { }
    }

    public static bool IsStarred(string ojId, string id)
    {
        lock (Lock)
        {
            return Load().Any(e => string.Equals(e.OjId, ojId, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static IReadOnlyList<StarredEntry> All()
    {
        lock (Lock)
        {
            return Load().ToList();
        }
    }

    public static void Toggle(OjProblemItem item)
    {
        lock (Lock)
        {
            var list = Load();
            int idx = list.FindIndex(e =>
                string.Equals(e.OjId, item.OjId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Id, item.Id, StringComparison.OrdinalIgnoreCase));

            if (idx >= 0)
            {
                list.RemoveAt(idx);
                item.IsStarred = false;
            }
            else
            {
                list.Insert(0, new StarredEntry
                {
                    OjId = item.OjId,
                    OjName = item.OjName,
                    Id = item.Id,
                    Title = item.Title,
                    Url = item.Url,
                    Meta = item.Meta,
                });
                item.IsStarred = true;
            }

            Save();
        }
    }
}
