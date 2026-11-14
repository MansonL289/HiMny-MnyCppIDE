using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WinUI3Shell;

/// <summary>应用设置（通用 / 编辑器 / 编译选项 / 新文件模板），优先保存到 %LOCALAPPDATA%\MnyC++\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>默认等宽字体。</summary>
    public const string DefaultEditorFont = "Cascadia Mono, Consolas, \"Microsoft YaHei UI\", monospace";

    /// <summary>优先用户目录；不可写时退回程序目录（便携版／受限环境）。</summary>
    private static readonly string[] CandidateFiles =
    {
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MnyC++",
            "settings.json"),
        Path.Combine(AppContext.BaseDirectory, "settings.json"),
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /* ---------------- 通用 ---------------- */

    /// <summary>应用主题：system / light / dark。</summary>
    public string AppTheme { get; set; } = "system";

    /// <summary>编辑器主题：system / dark / light。</summary>
    public string EditorTheme { get; set; } = "system";

    /// <summary>整个编程界面（标签栏、编辑器、终端）的缩放比例，0.5 ~ 2.0。</summary>
    public double UiZoom { get; set; } = 1.0;

    /* ---------------- 编辑器（Monaco） ---------------- */

    /// <summary>编辑器字体族。</summary>
    public string EditorFontFamily { get; set; } = DefaultEditorFont;

    /// <summary>编辑器字号。</summary>
    public int EditorFontSize { get; set; } = 13;

    /// <summary>是否开启字体连字。</summary>
    public bool EditorFontLigatures { get; set; } = true;

    /// <summary>自动换行：off / bounded / wordWrapColumn。</summary>
    public string EditorWordWrap { get; set; } = "off";

    /// <summary>是否显示迷你地图。</summary>
    public bool EditorMinimap { get; set; } = true;

    /// <summary>空白字符渲染：none / boundary / selection / trailing / all。</summary>
    public string EditorWhitespace { get; set; } = "selection";

    /// <summary>光标样式：line / block / underline / line-thin / block-outline / underline-thin。</summary>
    public string EditorCursorStyle { get; set; } = "line";

    /// <summary>光标闪烁：blink / solid / phase / fade / explicit。</summary>
    public string EditorCursorBlinking { get; set; } = "blink";

    /// <summary>缩进宽度（制表符宽度）。</summary>
    public int EditorTabSize { get; set; } = 4;

    /// <summary>是否用空格缩进。</summary>
    public bool EditorInsertSpaces { get; set; } = true;

    /// <summary>是否开启代码补全。</summary>
    public bool EditorSuggest { get; set; } = true;

    /// <summary>是否开启参数提示。</summary>
    public bool EditorParameterHints { get; set; } = true;

    /// <summary>是否开启悬浮提示。</summary>
    public bool EditorHover { get; set; } = true;

    /// <summary>是否开启括号配对着色。</summary>
    public bool EditorBracketColorization { get; set; } = true;

    /// <summary>是否开启粘性滚动。</summary>
    public bool EditorStickyScroll { get; set; } = true;

    /// <summary>是否开启标识符高亮（概览标尺）。</summary>
    public bool EditorOccurrencesHighlight { get; set; } = true;

    /// <summary>超过该大小的文件自动进入大文件模式（MB）。</summary>
    public int LargeFileThresholdMB { get; set; } = 30;

    /* ---------------- 文件 ---------------- */

    /// <summary>新建文件时是否插入模板代码。</summary>
    public bool InsertTemplateOnNewFile { get; set; } = true;

    /// <summary>新建 C++ 文件时自动插入的代码。</summary>
    public string TemplateCode { get; set; } = string.Empty;

    /// <summary>是否开启自动保存。</summary>
    public bool AutoSave { get; set; }

    /// <summary>自动保存间隔（秒）。</summary>
    public int AutoSaveSeconds { get; set; } = 60;

    /// <summary>保存编码：utf8 / utf8bom / gbk。</summary>
    public string SaveEncoding { get; set; } = "utf8";

    /* ---------------- 编译 ---------------- */

    /// <summary>编译时加入以下命令（留空表示不加）。</summary>
    public string CompilerFlags { get; set; } = string.Empty;

    /// <summary>在连接器命令行加入以下命令（留空表示不加）。</summary>
    public string LinkerFlags { get; set; } = string.Empty;

    /// <summary>C++ 语言标准：c++11 / c++14 / c++17 / c++20 / default（不指定）。</summary>
    public string CppStandard { get; set; } = "c++17";

    /// <summary>警告等级：default / wall / wextra。</summary>
    public string WarningLevel { get; set; } = "default";

    /* ---------------- 终端 ---------------- */

    /// <summary>程序运行结束后是否暂停（等待按键）。</summary>
    public bool PauseOnExit { get; set; } = true;

    /// <summary>终端工作目录：project（当前项目目录）/ app（程序所在目录）。</summary>
    public string TerminalDirectory { get; set; } = "project";

    public static AppSettings Load()
    {
        // 多个候选位置同时存在时，以最近修改的那份为准
        string? newest = null;
        DateTime newestTime = DateTime.MinValue;
        foreach (string file in CandidateFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    DateTime written = File.GetLastWriteTimeUtc(file);
                    if (written > newestTime)
                    {
                        newestTime = written;
                        newest = file;
                    }
                }
            }
            catch
            {
                // 忽略不可访问的位置
            }
        }

        if (newest is not null)
        {
            try
            {
                string json = File.ReadAllText(newest);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch
            {
                // 配置文件损坏时退回默认设置，不影响启动
            }
        }

        return new AppSettings();
    }

    public void Save()
    {
        string json = JsonSerializer.Serialize(this, JsonOptions);
        foreach (string file in CandidateFiles)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, json);
                return;
            }
            catch
            {
                // 换下一个位置
            }
        }
    }

    /// <summary>大文件模式阈值（字节）。</summary>
    public long LargeFileModeBytes => Math.Max(1, LargeFileThresholdMB) * 1024 * 1024;

    /// <summary>语言标准对应的编译参数（不指定标准时返回空）。</summary>
    public string StandardArgument => CppStandard switch
    {
        "c++11" => "-std=c++11",
        "c++14" => "-std=c++14",
        "c++17" => "-std=c++17",
        "c++20" => "-std=c++20",
        _ => string.Empty,
    };

    /// <summary>警告等级对应的编译参数。</summary>
    public string WarningArgument => WarningLevel switch
    {
        "wall" => "-Wall",
        "wextra" => "-Wall -Wextra",
        _ => string.Empty,
    };

    /// <summary>把命令按行拆成 g++ 参数列表（空行／留空直接跳过）。</summary>
    public string BuildExtraArguments()
    {
        var parts = new List<string>();

        string standard = StandardArgument;
        if (standard.Length > 0)
        {
            parts.Add(standard);
        }

        string warning = WarningArgument;
        if (warning.Length > 0)
        {
            parts.Add(warning);
        }

        parts.AddRange(SplitFlags(CompilerFlags));
        parts.AddRange(SplitFlags(LinkerFlags));
        return string.Join(' ', parts);
    }

    private static IEnumerable<string> SplitFlags(string text)
    {
        foreach (string line in text.Split('\n', '\r'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }
}
