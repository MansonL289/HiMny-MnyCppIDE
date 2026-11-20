using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WinUI3Shell.Oj;

/// <summary>洛谷官方题库标签（来自官网 /_lfe/tags 的标签字典）。</summary>
public sealed class LuoguTag
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>1 区域、2 算法、3 来源、4 时间、5 特殊题目。</summary>
    public int Type { get; set; }

    public int? Parent { get; set; }
}

/// <summary>筛选面板里的一个分组：标题 + 可选标签。</summary>
public sealed class LuoguTagGroup
{
    public string Name { get; init; } = string.Empty;

    public List<LuoguTag> Tags { get; init; } = new();
}

/// <summary>
/// 洛谷筛选条件：离线读取随程序分发的官方标签字典，
/// 按官网「算法 / 来源 / 时间 / 区域 / 特殊题目」五个分类组织成可选标签组。
/// </summary>
public static class LuoguFilters
{
    public const int Algorithm = 2;
    public const int Source = 3;
    public const int Time = 4;
    public const int Region = 1;
    public const int Special = 5;

    /// <summary>界面上的筛选标签页（顺序与洛谷官网一致）。</summary>
    public static readonly (int Type, string Name)[] Tabs =
    {
        (Algorithm, "算法"),
        (Source, "来源"),
        (Time, "时间"),
        (Region, "区域"),
        (Special, "特殊题目"),
    };

    /// <summary>题目难度档位；-1 表示全部，取值与洛谷 difficulty 参数一致。</summary>
    public static readonly (int Value, string Name)[] Difficulties =
    {
        (-1, "全部"),
        (1, "入门"),
        (2, "普及-"),
        (3, "普及/提高-"),
        (4, "普及+/提高"),
        (5, "提高+/省选-"),
        (6, "省选/NOI-"),
        (7, "NOI/NOI+/CTSC"),
    };

    private static Dictionary<int, LuoguTag>? _byId;

    public static string DifficultyName(int value)
    {
        foreach ((int v, string name) in Difficulties)
        {
            if (v == value)
            {
                return name;
            }
        }

        return string.Empty;
    }

    public static LuoguTag? Find(int id) => Load().TryGetValue(id, out LuoguTag? tag) ? tag : null;

    /// <summary>取某个分类下的标签组；同一顶层标签的子孙标签归到同一组。</summary>
    public static IReadOnlyList<LuoguTagGroup> GetGroups(int type)
    {
        Dictionary<int, LuoguTag> byId = Load();
        List<LuoguTag> tags = byId.Values.Where(t => t.Type == type).ToList();
        if (tags.Count == 0)
        {
            return Array.Empty<LuoguTagGroup>();
        }

        if (type == Time)
        {
            List<LuoguTag> years = tags
                .OrderByDescending(t => int.TryParse(t.Name, out int year) ? year : int.MinValue)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
            return new[] { new LuoguTagGroup { Name = "年份", Tags = years } };
        }

        var groups = new List<LuoguTagGroup>();
        var groupIndex = new Dictionary<int, LuoguTagGroup>();

        foreach (LuoguTag tag in tags)
        {
            LuoguTag root = RootOf(tag, byId);
            if (!groupIndex.TryGetValue(root.Id, out LuoguTagGroup? group))
            {
                group = new LuoguTagGroup { Name = root.Name };
                groupIndex[root.Id] = group;
                groups.Add(group);
            }

            group.Tags.Add(tag);
        }

        // 顶层标签排在所在组的第一位，和官网面板一致
        foreach (LuoguTagGroup group in groups)
        {
            group.Tags.Sort((a, b) =>
            {
                bool aRoot = a.Parent is null;
                bool bRoot = b.Parent is null;
                return aRoot == bRoot ? 0 : (aRoot ? -1 : 1);
            });
        }

        return groups.Where(g => g.Tags.Count > 0).ToList();
    }

    private static Dictionary<int, LuoguTag> Load()
    {
        if (_byId is not null)
        {
            return _byId;
        }

        var byId = new Dictionary<int, LuoguTag>();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "oj", "luogu-tags.json");
            if (File.Exists(path))
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var file = JsonSerializer.Deserialize<TagFile>(File.ReadAllText(path), options);
                if (file?.Tags is not null)
                {
                    foreach (LuoguTag tag in file.Tags)
                    {
                        // 官方数据里部分标签名前面带有 U+FEFF，直接显示会在按钮左侧多出一个空隙
                        string name = tag.Name.Trim().Trim('\uFEFF').Trim();
                        if (name.Length > 0)
                        {
                            tag.Name = name;
                            byId[tag.Id] = tag;
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // 标签字典缺失或损坏时返回空集合，筛选面板会给出提示
        }

        _byId = byId;
        return _byId;
    }

    /// <summary>沿 parent 链向上找到顶层标签；父级不在字典里时以自身为顶层。</summary>
    private static LuoguTag RootOf(LuoguTag tag, Dictionary<int, LuoguTag> byId)
    {
        LuoguTag current = tag;
        var visited = new HashSet<int>();
        while (current.Parent is int parentId && visited.Add(current.Id) &&
               byId.TryGetValue(parentId, out LuoguTag? parent))
        {
            current = parent;
        }

        return current;
    }

    private sealed class TagFile
    {
        public List<LuoguTag> Tags { get; set; } = new();
    }
}
