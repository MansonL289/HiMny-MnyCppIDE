using System;
using System.IO;
using System.Text;

namespace WinUI3Shell;

/// <summary>
/// 极简本地日志：写入 %LOCALAPPDATA%\MnyC++\logs，按天分文件。
/// 只用于记录启动信息和未处理异常，任何写入失败都静默忽略，绝不影响主流程。
/// </summary>
internal static class AppLog
{
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const int KeepDays = 7;

    private static readonly object Gate = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MnyC++",
        "logs");

    public static string CurrentFile => Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : message + Environment.NewLine + exception);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                Cleanup();
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(CurrentFile, line, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // 磁盘不可写时放弃记录
        }
    }

    /// <summary>删除过期日志；当天文件过大时改名归档，避免无限增长。</summary>
    private static void Cleanup()
    {
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (string file in Directory.EnumerateFiles(LogDirectory, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }

            var current = new FileInfo(CurrentFile);
            if (current.Exists && current.Length > MaxFileBytes)
            {
                string archived = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                File.Move(CurrentFile, archived, overwrite: true);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响后续写入
        }
    }
}
