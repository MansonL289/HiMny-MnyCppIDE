using System;
using System.IO;
using System.Text.Json;

namespace WinUI3Shell;

/// <summary>应用设置（Dev-C++ 风格的编译选项 + 新文件模板），优先保存到 %LOCALAPPDATA%\MnyC++\settings.json。</summary>
public sealed class AppSettings
{
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

    /// <summary>编译时加入以下命令（留空表示不加）。</summary>
    public string CompilerFlags { get; set; } = string.Empty;

    /// <summary>在连接器命令行加入以下命令（留空表示不加）。</summary>
    public string LinkerFlags { get; set; } = string.Empty;

    /// <summary>新建 C++ 文件时自动插入的代码（留空表示不插入）。</summary>
    public string TemplateCode { get; set; } = string.Empty;

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

    /// <summary>把两个输入框里的命令按行拆成 g++ 参数列表（空行／留空直接跳过）。</summary>
    public string BuildExtraArguments()
    {
        var parts = new System.Collections.Generic.List<string>();
        parts.AddRange(SplitFlags(CompilerFlags));
        parts.AddRange(SplitFlags(LinkerFlags));
        return string.Join(' ', parts);
    }

    private static System.Collections.Generic.IEnumerable<string> SplitFlags(string text)
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
