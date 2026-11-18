using System;
using System.Globalization;

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
