using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace WinUI3Shell.Oj;

/// <summary>
/// 后台浏览器：所有 OJ 的搜索/题面抓取都在这一个 WebView2 里串行执行。
/// 用真实浏览器内核 + 伪装 UA，避免直接 HttpClient 被 OJ 判定为爬虫。
/// </summary>
public sealed class OjBrowser
{
    private readonly WebView2 _view;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource<bool>? _navigation;
    private bool _initialized;

    public OjBrowser(WebView2 view)
    {
        _view = view;
    }

    public string UserAgent { get; set; } = string.Empty;

    /// <summary>每次导航前注入到页面的公共脚本（__mny.htmlToMd 等工具）。</summary>
    public string PreludeScript { get; set; } = string.Empty;

    public bool IsInitialized => _initialized;

    public async Task EnsureAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _view.EnsureCoreWebView2Async();
        CoreWebView2 core = _view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        if (!string.IsNullOrEmpty(UserAgent))
        {
            core.Settings.UserAgent = UserAgent;
        }

        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationCompleted += OnNavigationCompleted;

        if (!string.IsNullOrEmpty(PreludeScript))
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PreludeScript);
        }

        _initialized = true;
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        _navigation?.TrySetResult(args.IsSuccess);
    }

    /// <summary>
    /// 导航到指定地址，等页面稳定后执行脚本，把返回值当 JSON 解析。
    /// 只能串行调用，内部用信号量排队。
    /// </summary>
    public async Task<JsonNode?> RunAsync(string url, string script, int settleMs, bool disableJs = false, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // WebView2 运行时缺失或初始化失败时，把原因带回给调用方，而不是静默返回空结果
            return ErrorNode(ex);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            CoreWebView2 core = _view.CoreWebView2;

            // 有的站点首屏数据只存在于服务端渲染的 HTML 里，页面框架水合后会销毁。
            // 这类站点关掉页面脚本，抓完再用宿主注入的脚本解析（宿主脚本不受此开关影响）。
            if (core.Settings.IsScriptEnabled == disableJs)
            {
                core.Settings.IsScriptEnabled = !disableJs;
            }

            var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _navigation = navigation;

            try
            {
                core.Navigate(url);
            }
            catch (Exception ex)
            {
                return ErrorNode(ex);
            }

            Task finished = await Task.WhenAny(navigation.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));
            _navigation = null;

            if (finished != navigation.Task)
            {
                // 导航超时：仍尝试读取当前页面，有时页面已经渲染只是没触发完成事件
                await Task.Delay(300, cancellationToken);
            }

            if (settleMs > 0)
            {
                await Task.Delay(settleMs, cancellationToken);
            }

            string raw;
            try
            {
                raw = await core.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                return ErrorNode(ex);
            }

            return ParseScriptResult(raw);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>把异常包装成调用方认识的 error 节点。</summary>
    private static JsonNode ErrorNode(Exception ex)
    {
        string message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
        if (message.Length > 160)
        {
            message = message[..160] + "…";
        }

        return new JsonObject { ["error"] = "WebView2 不可用：" + message };
    }

    /// <summary>ExecuteScriptAsync 的结果是 JSON 文本；脚本返回字符串时会被再包一层。</summary>
    public static JsonNode? ParseScriptResult(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return null;
        }

        try
        {
            JsonNode? node = JsonNode.Parse(raw);
            if (node is JsonValue value && value.TryGetValue(out string? inner) && !string.IsNullOrWhiteSpace(inner))
            {
                return JsonNode.Parse(inner);
            }

            return node;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>执行一段返回布尔值的脚本。</summary>
    public async Task<bool> EvaluateBooleanAsync(string script, CancellationToken cancellationToken = default)
    {
        await EnsureAsync();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string raw = await _view.CoreWebView2.ExecuteScriptAsync(script);
            return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>检查指定站点下是否存在登录 Cookie（任一命中即视为已登录）。</summary>
    public async Task<bool> HasCookieAsync(string url, IReadOnlyList<string> cookieNames, CancellationToken cancellationToken = default)
    {
        if (cookieNames.Count == 0)
        {
            return false;
        }

        await EnsureAsync();
        try
        {
            var cookies = await _view.CoreWebView2.CookieManager.GetCookiesAsync(url);
            foreach (CoreWebView2Cookie cookie in cookies)
            {
                foreach (string name in cookieNames)
                {
                    if (string.Equals(cookie.Name, name, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(cookie.Value))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }

    /// <summary>读出当前页面的 Cookie 字符串（调试/登录检测备用）。</summary>
    public async Task<string> GetCookieHeaderAsync(string url)
    {
        await EnsureAsync();
        try
        {
            var cookies = await _view.CoreWebView2.CookieManager.GetCookiesAsync(url);
            var parts = new List<string>(cookies.Count);
            foreach (CoreWebView2Cookie cookie in cookies)
            {
                parts.Add(cookie.Name + "=" + cookie.Value);
            }

            return string.Join("; ", parts);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
