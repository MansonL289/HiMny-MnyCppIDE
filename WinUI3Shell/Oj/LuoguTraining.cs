using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WinUI3Shell.Oj;

/// <summary>
/// 洛谷训练题单：板块（data.categories）→ 板块内题单（type=板块 key，可翻页）→ 题单题目。
/// 全部从页面内嵌的 lentille-context JSON 读取，不需要登录。
/// </summary>
internal static class LuoguTraining
{
    public const string ListUrl = "https://www.luogu.com.cn/training/list";

    /// <summary>某个板块第 page 页（从 1 开始）的题单列表。</summary>
    public static string ListUrlFor(string type, int page) =>
        "https://www.luogu.com.cn/training/list?type=" + Uri.EscapeDataString(type) + "&page=" + page.ToString(CultureInfo.InvariantCulture);

    public static string DetailUrl(string id) => "https://www.luogu.com.cn/training/" + id;

    /// <summary>读取 data.categories 与 data.trainings.result。</summary>
    public const string ListScript = """
(function () {
  var tags = document.querySelectorAll('script[type="application/json"]');
  for (var i = 0; i < tags.length; i++) {
    var j = null;
    try { j = JSON.parse(tags[i].textContent); } catch (e) { continue; }
    var tr = j && j.data && j.data.trainings;
    if (!tr) continue;
    var cats = [], cs = j.data.categories || [];
    for (var c = 0; c < cs.length; c++) {
      cats.push({ key: cs[c].key, name: cs[c].name });
    }
    var arr = tr.result || [], out = [];
    for (var k = 0; k < arr.length; k++) {
      var t = arr[k];
      out.push({
        id: t.id,
        title: t.name,
        problemCount: t.problemCount || 0,
        markCount: t.markCount || 0
      });
    }
    return { categories: cats, items: out, total: tr.count || out.length };
  }
  if (document.querySelector('img[src*="captcha"],[class*="captcha"]') || /captcha|verification/i.test(document.title)) {
    return { categories: [], items: [], captcha: true };
  }
  return { categories: [], items: [] };
})()
""";

    /// <summary>读取 data.training 里的题目数组。</summary>
    public const string DetailScript = """
(function () {
  var tags = document.querySelectorAll('script[type="application/json"]');
  for (var i = 0; i < tags.length; i++) {
    var j = null;
    try { j = JSON.parse(tags[i].textContent); } catch (e) { continue; }
    var tr = j && j.data && j.data.training;
    if (!tr) continue;
    var ps = tr.problems || [];
    var out = [];
    for (var k = 0; k < ps.length; k++) {
      var p = ps[k];
      out.push({
        id: p.pid,
        title: p.name,
        difficulty: (typeof p.difficulty === 'number') ? p.difficulty : -1,
        submitCount: p.totalSubmit || 0,
        acceptedCount: p.totalAccepted || 0
      });
    }
    return { title: tr.name || '', items: out };
  }
  if (document.querySelector('img[src*="captcha"],[class*="captcha"]') || /captcha|verification/i.test(document.title)) {
    return { items: [], captcha: true };
  }
  return { items: [] };
})()
""";
}

/// <summary>
/// 用普通 HTTP 直接读洛谷页面的 SSR 数据（lentille-context），
/// 用于在板块页里跨题单搜索题目：并发拉取各题单的题目并缓存。
/// </summary>
internal sealed class LuoguIndex
{
    private static readonly Regex ContextRegex = new(
        "<script id=\"lentille-context\" type=\"application/json\">(.*?)</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(6);
    private readonly ConcurrentDictionary<string, List<LuoguTrainingSummary>> _trainings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<LuoguProblemEntry>> _problems = new(StringComparer.Ordinal);

    public LuoguIndex(string userAgent)
    {
        _client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
    }

    /// <summary>某个板块下的全部题单（会自动翻完所有分页）。</summary>
    public async Task<List<LuoguTrainingSummary>> GetTrainingsAsync(string type, CancellationToken token)
    {
        if (_trainings.TryGetValue(type, out List<LuoguTrainingSummary>? cached))
        {
            return cached;
        }

        var all = new List<LuoguTrainingSummary>();
        int page = 1;
        int pages = 1;
        while (page <= pages)
        {
            JsonNode? node = await ReadContextAsync(LuoguTraining.ListUrlFor(type, page), token).ConfigureAwait(false);
            JsonNode? trainings = node?["data"]?["trainings"];
            if (trainings is null)
            {
                break;
            }

            int total = trainings["count"]?.GetValue<int>() ?? 0;
            int perPage = Math.Max(1, trainings["perPage"]?.GetValue<int>() ?? 30);
            pages = Math.Clamp((total + perPage - 1) / perPage, 1, 60);

            if (trainings["result"] is JsonArray rows)
            {
                foreach (JsonNode? row in rows)
                {
                    string id = row?["id"]?.ToString() ?? string.Empty;
                    string name = row?["name"]?.ToString() ?? string.Empty;
                    if (id.Length == 0 || name.Length == 0)
                    {
                        continue;
                    }

                    all.Add(new LuoguTrainingSummary
                    {
                        Id = id,
                        Title = name,
                        ProblemCount = row?["problemCount"]?.GetValue<int>() ?? 0,
                        MarkCount = row?["markCount"]?.GetValue<long>() ?? 0,
                    });
                }
            }

            page++;
        }

        _trainings[type] = all;
        return all;
    }

    /// <summary>某个题单里的全部题目；失败时缓存空列表，避免反复重试。</summary>
    public async Task<List<LuoguProblemEntry>> GetProblemsAsync(string trainingId, CancellationToken token)
    {
        if (_problems.TryGetValue(trainingId, out List<LuoguProblemEntry>? cached))
        {
            return cached;
        }

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_problems.TryGetValue(trainingId, out cached))
            {
                return cached;
            }

            var list = new List<LuoguProblemEntry>();
            JsonNode? node = await ReadContextAsync(LuoguTraining.DetailUrl(trainingId), token).ConfigureAwait(false);
            if (node?["data"]?["training"]?["problems"] is JsonArray problems)
            {
                foreach (JsonNode? problem in problems)
                {
                    string pid = problem?["pid"]?.ToString() ?? string.Empty;
                    string name = problem?["name"]?.ToString() ?? string.Empty;
                    if (pid.Length == 0 || name.Length == 0)
                    {
                        continue;
                    }

                    list.Add(new LuoguProblemEntry
                    {
                        Pid = pid,
                        Title = name,
                        Difficulty = problem?["difficulty"]?.GetValue<int>() ?? -1,
                        SubmitCount = problem?["totalSubmit"]?.GetValue<long>() ?? 0,
                        AcceptedCount = problem?["totalAccepted"]?.GetValue<long>() ?? 0,
                    });
                }
            }

            _problems[trainingId] = list;
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonNode?> ReadContextAsync(string url, CancellationToken token)
    {
        try
        {
            using HttpResponseMessage response = await _client.GetAsync(url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string html = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            Match match = ContextRegex.Match(html);
            return match.Success ? JsonNode.Parse(match.Groups[1].Value) : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>题单里的一道题（用于跨题单搜索）。</summary>
public sealed class LuoguProblemEntry
{
    public string Pid { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public int Difficulty { get; init; } = -1;

    public long SubmitCount { get; init; }

    public long AcceptedCount { get; init; }

    public bool Matches(string query) =>
        Pid.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Title.Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>题单板块（官方训练页顶部的大分类）。</summary>
public sealed class LuoguCategory
{
    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}

/// <summary>题单列表里的一项。</summary>
public sealed class LuoguTrainingSummary
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public int ProblemCount { get; init; }

    public long MarkCount { get; init; }

    /// <summary>列表里的序号（01、02…），纯装饰。</summary>
    public string Index { get; init; } = string.Empty;

    public string Meta => ProblemCount.ToString(CultureInfo.InvariantCulture) + " 道题 · " + FormatCount(MarkCount) + "收藏";

    private static string FormatCount(long value)
    {
        if (value >= 10000)
        {
            return (value / 10000.0).ToString("0.#", CultureInfo.InvariantCulture) + " 万";
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }
}
