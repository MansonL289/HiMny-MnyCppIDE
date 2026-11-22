using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WinUI3Shell.Oj;

/// <summary>
/// 从本机 Chromium 系浏览器（Edge / Chrome / Brave / Vivaldi / Opera 等）的 Cookie 数据库
/// 导入指定站点的登录态。所有 Chromium 浏览器共用同一套加密格式：
/// Local State 里的 os_crypt 密钥（DPAPI 保护）+ v10/v11 的 AES-GCM，老版本直接 DPAPI。
/// Chrome 127+ / 部分新版切到了 App-Bound 加密（v20），无法解密，遇到时返回明确提示。
/// </summary>
internal static class EdgeCookieImporter
{
    public sealed record Result(int Count, string Message);

    public sealed record CookieRow(
        string Host,
        string Name,
        string Value,
        string Path,
        bool Secure,
        bool HttpOnly,
        double Expires);

    /// <summary>按域名分组读取：每个域名返回一组解密后的 Cookie（只包含有数据的域名）。</summary>
    public static Dictionary<string, List<CookieRow>> LoadByHost(IEnumerable<string> hosts, out Result result)
    {
        var byHost = new Dictionary<string, List<CookieRow>>(StringComparer.OrdinalIgnoreCase);
        var targetHosts = hosts
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim().TrimStart('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targetHosts.Count == 0)
        {
            result = new Result(0, "没有需要读取的域名。");
            return byHost;
        }

        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var roots = new List<string>();
        AddIfExists(roots, Path.Combine(localApp, "Microsoft", "Edge", "User Data"));
        AddIfExists(roots, Path.Combine(localApp, "Google", "Chrome", "User Data"));
        AddIfExists(roots, Path.Combine(localApp, "Google", "Chrome Beta", "User Data"));
        AddIfExists(roots, Path.Combine(localApp, "BraveSoftware", "Brave-Browser", "User Data"));
        AddIfExists(roots, Path.Combine(localApp, "Vivaldi", "User Data"));
        AddIfExists(roots, Path.Combine(localApp, "Chromium", "User Data"));
        // 360 / QQ / 其它 Chromium 套壳：扫一层 User Data 目录
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(localApp))
            {
                string candidate = Path.Combine(dir, "User Data");
                if (HasLocalStateKey(candidate) && !roots.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(candidate);
                }
            }
        }
        catch (Exception)
        {
            // 目录不可读时跳过
        }

        // Opera 的根目录是 "%APPDATA%\Opera Software\Opera Stable"，不含 User Data 子目录
        try
        {
            string operaRoot = Path.Combine(appData, "Opera Software");
            if (Directory.Exists(operaRoot))
            {
                foreach (string dir in Directory.EnumerateDirectories(operaRoot))
                {
                    if (HasLocalStateKey(dir))
                    {
                        roots.Add(dir);
                    }
                }
            }
        }
        catch (Exception)
        {
            // 忽略
        }

        bool anyBrowser = roots.Count > 0;
        bool appBound = false;
        int total = 0;

        foreach (string root in roots)
        {
            byte[]? key = TryGetKey(root);
            foreach (string cookiesPath in EnumerateCookiesFiles(root))
            {
                List<CookieRow> rows = ReadRows(cookiesPath, targetHosts, key, out bool thisAppBound);
                if (thisAppBound)
                {
                    appBound = true;
                }

                foreach (CookieRow row in rows)
                {
                    string? matched = MatchHost(row.Host, targetHosts);
                    if (matched is null)
                    {
                        continue;
                    }

                    if (!byHost.TryGetValue(matched, out List<CookieRow>? list))
                    {
                        list = new List<CookieRow>();
                        byHost[matched] = list;
                    }

                    // 同名 Cookie 以浏览器扫描顺序后者覆盖，避免重复写入
                    int idx = list.FindIndex(c => string.Equals(c.Name, row.Name, StringComparison.Ordinal));
                    if (idx >= 0)
                    {
                        list[idx] = row;
                    }
                    else
                    {
                        list.Add(row);
                    }

                    total++;
                }
            }
        }

        if (total == 0)
        {
            string msg = appBound
                ? "检测到浏览器使用了新版 App-Bound 加密，无法读取 Cookie，请改用应用内登录。"
                : anyBrowser
                    ? "浏览器里没有这些站点的 Cookie，请先在浏览器里登录对应平台。"
                    : "没有找到 Chromium 系浏览器（Edge / Chrome / Brave 等）的 Cookie 数据。";
            result = new Result(0, msg);
            return byHost;
        }

        result = new Result(total, $"从浏览器读取到 {total} 个 Cookie，覆盖 {byHost.Count} 个平台。");
        return byHost;
    }

    private static void AddIfExists(List<string> list, string path)
    {
        if (Directory.Exists(path) && !list.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(path);
        }
    }

    private static bool HasLocalStateKey(string root)
    {
        try
        {
            string localState = Path.Combine(root, "Local State");
            if (!File.Exists(localState))
            {
                return false;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(localState));
            return doc.RootElement.TryGetProperty("os_crypt", out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static List<string> EnumerateCookiesFiles(string root)
    {
        var files = new List<string>();
        string[] candidates = { "Network", string.Empty };
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(dir);
                bool isProfile = name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                                 name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) ||
                                 name.StartsWith("Guest", StringComparison.OrdinalIgnoreCase);
                if (!isProfile)
                {
                    continue;
                }

                foreach (string sub in candidates)
                {
                    string file = string.IsNullOrEmpty(sub)
                        ? Path.Combine(dir, "Cookies")
                        : Path.Combine(dir, sub, "Cookies");
                    if (File.Exists(file))
                    {
                        files.Add(file);
                    }
                }
            }
        }
        catch (Exception)
        {
            // 目录不可读时跳过
        }

        return files;
    }

    private static byte[]? TryGetKey(string root)
    {
        try
        {
            string localState = Path.Combine(root, "Local State");
            if (!File.Exists(localState))
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(localState));
            if (!doc.RootElement.TryGetProperty("os_crypt", out JsonElement crypt) ||
                !crypt.TryGetProperty("encrypted_key", out JsonElement keyNode))
            {
                return null;
            }

            string? text = keyNode.GetString();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            byte[] raw = Convert.FromBase64String(text);
            if (raw.Length <= 5)
            {
                return null;
            }

            byte[] encrypted = raw[5..]; // 前 5 字节是 "DPAPI" 标记
            return Unprotect(encrypted);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<CookieRow> ReadRows(string cookiesPath, List<string> targetHosts, byte[]? key, out bool appBound)
    {
        appBound = false;
        var rows = new List<CookieRow>();

        // 浏览器运行时会锁住数据库，复制一份再读
        string temp = Path.Combine(Path.GetTempPath(), "mny_browser_cookies_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (FileStream src = new(cookiesPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (FileStream dst = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                src.CopyTo(dst);
            }

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = temp,
                Mode = SqliteOpenMode.ReadOnly,
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            foreach (string host in targetHosts)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "SELECT host_key, name, value, encrypted_value, path, expires_utc, is_secure, is_httponly " +
                    "FROM cookies WHERE host_key LIKE $host";
                command.Parameters.AddWithValue("$host", "%" + host + "%");

                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string hostKey = reader.GetString(0);
                    if (MatchHost(hostKey, targetHosts) is null)
                    {
                        continue; // SQL 的 LIKE 是子串匹配，C# 这里精确过滤
                    }

                    string name = reader.GetString(1);
                    string value = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    byte[] encrypted = reader.IsDBNull(3) ? Array.Empty<byte>() : (byte[])reader.GetValue(3);
                    string path = reader.GetString(4);
                    long expires = reader.GetInt64(5);
                    bool secure = reader.GetInt64(6) != 0;
                    bool httpOnly = reader.GetInt64(7) != 0;

                    if (value.Length == 0 && encrypted.Length > 0)
                    {
                        if (TryDecrypt(encrypted, hostKey, key, out string plain, out bool v20))
                        {
                            value = plain;
                        }
                        else
                        {
                            if (v20)
                            {
                                appBound = true;
                            }

                            continue;
                        }
                    }

                    if (value.Length == 0)
                    {
                        continue;
                    }

                    rows.Add(new CookieRow(hostKey, name, value, path, secure, httpOnly, ToChromiumSeconds(expires)));
                }
            }
        }
        catch (Exception)
        {
            // 单个数据库读失败不影响其它浏览器/配置文件
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // 临时文件删不掉不影响结果
            }
        }

        return rows;
    }

    /// <summary>精确匹配：cookie 域名等于目标，或以 .目标 结尾。</summary>
    private static string? MatchHost(string cookieHost, List<string> targets)
    {
        string h = cookieHost.TrimStart('.');
        foreach (string target in targets)
        {
            if (h.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                h.EndsWith("." + target, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }
        }

        return null;
    }

    private static bool TryDecrypt(byte[] data, string host, byte[]? key, out string value, out bool appBound)
    {
        value = string.Empty;
        appBound = false;
        if (data.Length < 3)
        {
            return false;
        }

        string prefix = Encoding.ASCII.GetString(data, 0, 3);
        if (prefix == "v20")
        {
            appBound = true;
            return false;
        }

        if (prefix != "v10" && prefix != "v11")
        {
            return false;
        }

        try
        {
            if (key is null)
            {
                // 老版本直接 DPAPI 加密
                byte[] dpapi = data[3..];
                byte[] plain = Unprotect(dpapi);
                value = Encoding.UTF8.GetString(plain);
                return value.Length > 0;
            }

            byte[] nonce = data[3..15];
            byte[] tag = data[^16..];
            byte[] cipher = data[15..^16];
            byte[] output = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, output);

            // Chromium 新版本在明文前面拼了 32 字节的域名哈希
            byte[] hostHash = SHA256.HashData(Encoding.UTF8.GetBytes(host));
            int offset = 0;
            if (output.Length > 32 && output.AsSpan(0, 32).SequenceEqual(hostHash))
            {
                offset = 32;
            }

            value = Encoding.UTF8.GetString(output, offset, output.Length - offset);
            return value.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Chromium 时间单位是 1601 年起的微秒；WebView2 的 Expires 是同一基准的秒。</summary>
    private static double ToChromiumSeconds(long micros)
    {
        if (micros <= 0)
        {
            return DateTime.UtcNow.AddDays(7).ToFileTimeUtc() / 10_000_000.0;
        }

        return micros / 1_000_000.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("Crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static byte[] Unprotect(byte[] data)
    {
        var input = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, out DataBlob output))
            {
                throw new CryptographicException("DPAPI 解密失败");
            }

            try
            {
                byte[] result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }
}
