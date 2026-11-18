using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace WinUI3Shell;

/// <summary>一帧调用栈。</summary>
internal sealed class GdbFrame
{
    public int Level { get; init; }

    public string Function { get; init; } = string.Empty;

    public string File { get; init; } = string.Empty;

    public int Line { get; init; }
}

/// <summary>一个局部变量或求值结果。</summary>
internal sealed class GdbVariable
{
    public string Name { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;
}

/// <summary>gdb 停下来时的信息（断点命中、单步结束、程序退出等）。</summary>
internal sealed class GdbStopEventArgs : EventArgs
{
    public string Reason { get; init; } = string.Empty;

    public string File { get; init; } = string.Empty;

    public int Line { get; init; }

    public string Function { get; init; } = string.Empty;
}

/// <summary>
/// 最小化的 gdb/MI 会话：只做 Dev-C++ 风格的断点 + 单步 + 变量/调用栈。
/// 命令用数字 token 前缀发送，响应按 token 匹配，避免多条命令互相串台。
/// </summary>
internal sealed class GdbSession : IDisposable
{
    private readonly Process _process;
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<MiRecord>> _pending = new();
    private int _nextToken;

    private GdbSession(Process process)
    {
        _process = process;
    }

    /// <summary>程序停下（断点命中/单步结束/退出）。</summary>
    public event EventHandler<GdbStopEventArgs>? Stopped;

    /// <summary>程序重新跑起来。</summary>
    public event EventHandler? Running;

    /// <summary>gdb 控制台输出（含被调试程序的 stdout）。</summary>
    public event EventHandler<string>? ConsoleOutput;

    /// <summary>gdb 进程退出。</summary>
    public event EventHandler? Exited;

    public bool IsAlive
    {
        get
        {
            try { return !_process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public static GdbSession? TryStart(string gdbPath, string exePath, string workingDirectory)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = gdbPath,
                Arguments = "-q --interpreter=mi2 --args " + Quote(exePath),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.GetEncoding(936),
            };

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var session = new GdbSession(process);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    session.HandleLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    session.ConsoleOutput?.Invoke(session, e.Data);
                }
            };
            process.Exited += (_, _) => session.Exited?.Invoke(session, EventArgs.Empty);

            if (!process.Start())
            {
                process.Dispose();
                return null;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return session;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task InitializeAsync()
    {
        await ExecuteAsync("-gdb-set pagination off", 5000);
        await ExecuteAsync("-gdb-set confirm off", 5000);
        // 让 -exec-run 立即返回，界面才不会卡住；老版本 gdb 不支持时忽略错误
        await ExecuteAsync("-gdb-set mi-async on", 5000);
    }

    public async Task<int> InsertBreakpointAsync(string filePath, int line)
    {
        MiRecord record = await ExecuteAsync("-break-insert " + QuoteMi($"{filePath}:{line}"));
        if (!record.Success || record.Fields is null ||
            !record.Fields.TryGetValue("bkpt", out MiNode? breakpoint))
        {
            return 0;
        }

        return ParseInt(breakpoint.GetText("number"));
    }

    public async Task DeleteBreakpointAsync(int number)
    {
        await ExecuteAsync("-break-delete " + number.ToString(CultureInfo.InvariantCulture));
    }

    public Task RunAsync(string arguments = "") =>
        ExecuteAsync("-exec-run" + (arguments.Length > 0 ? " " + arguments : string.Empty));

    public Task ContinueAsync() => ExecuteAsync("-exec-continue");

    public Task NextAsync() => ExecuteAsync("-exec-next");

    public Task StepAsync() => ExecuteAsync("-exec-step");

    public Task FinishAsync() => ExecuteAsync("-exec-finish");

    public Task InterruptAsync() => ExecuteAsync("-exec-interrupt", 5000);

    public Task SelectFrameAsync(int level) =>
        ExecuteAsync("-stack-select-frame " + level.ToString(CultureInfo.InvariantCulture), 5000);

    public async Task<List<GdbFrame>> GetFramesAsync()
    {
        var frames = new List<GdbFrame>();
        MiRecord record = await ExecuteAsync("-stack-list-frames");
        if (!record.Success || record.Fields is null ||
            !record.Fields.TryGetValue("stack", out MiNode? stack) || stack.Items is null)
        {
            return frames;
        }

        foreach (MiNode item in stack.Items)
        {
            MiNode? frame = item.Get("frame");
            if (frame?.Fields is null)
            {
                continue;
            }

            string function = frame.GetText("func");
            if (function.Length == 0)
            {
                function = "??";
            }

            frames.Add(new GdbFrame
            {
                Level = ParseInt(frame.GetText("level")),
                Function = function,
                File = frame.GetText("fullname") is { Length: > 0 } full ? full : frame.GetText("file"),
                Line = ParseInt(frame.GetText("line")),
            });
        }

        return frames;
    }

    public async Task<List<GdbVariable>> GetVariablesAsync()
    {
        var variables = new List<GdbVariable>();
        MiRecord record = await ExecuteAsync("-stack-list-variables --simple-values");
        if (!record.Success || record.Fields is null ||
            !record.Fields.TryGetValue("variables", out MiNode? list) || list.Items is null)
        {
            return variables;
        }

        foreach (MiNode item in list.Items)
        {
            if (item.Fields is null)
            {
                continue;
            }

            string name = item.GetText("name");
            if (name.Length == 0)
            {
                continue;
            }

            string value = item.GetText("value");
            if (value.Length == 0)
            {
                value = item.GetText("type");
            }

            variables.Add(new GdbVariable { Name = name, Value = value });
        }

        return variables;
    }

    public async Task<string> EvaluateAsync(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return string.Empty;
        }

        MiRecord record = await ExecuteAsync("-data-evaluate-expression " + QuoteMi(expression));
        return record.Success ? record.GetText("value") : "<无法求值>";
    }

    public async Task<MiRecord> ExecuteAsync(string command, int timeoutMs = 20000)
    {
        int token;
        var completion = new TaskCompletionSource<MiRecord>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (!IsAlive)
            {
                return MiRecord.Failure("gdb 进程已退出");
            }

            token = ++_nextToken;
            _pending[token] = completion;
        }

        try
        {
            await _process.StandardInput.WriteLineAsync(token.ToString(CultureInfo.InvariantCulture) + command);
            await _process.StandardInput.FlushAsync();

            Task finished = await Task.WhenAny(
                completion.Task,
                Task.Delay(timeoutMs));
            if (finished == completion.Task)
            {
                return await completion.Task;
            }

            return MiRecord.Failure("gdb 响应超时");
        }
        catch (Exception ex)
        {
            return MiRecord.Failure(ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(token);
            }
        }
    }

    private void HandleLine(string line)
    {
        if (line.Length == 0)
        {
            return;
        }

        int index = 0;
        while (index < line.Length && char.IsDigit(line[index]))
        {
            index += 1;
        }

        int token = 0;
        if (index > 0)
        {
            int.TryParse(line.AsSpan(0, index), out token);
        }

        string body = line[index..];
        if (body.Length == 0)
        {
            return;
        }

        char kind = body[0];
        string head;
        string payload;
        int comma = body.IndexOf(',');
        if (comma < 0)
        {
            head = body[1..];
            payload = string.Empty;
        }
        else
        {
            head = body[1..comma];
            payload = body[(comma + 1)..];
        }

        switch (kind)
        {
            case '~':
            case '@':
            {
                string text = Unescape(body[1..]);
                if (text.Length > 0)
                {
                    ConsoleOutput?.Invoke(this, text.TrimEnd('\r', '\n'));
                }
                break;
            }

            case '^':
            {
                MiRecord record = new(head != "error", head, ParseFields(payload));
                if (token > 0)
                {
                    TaskCompletionSource<MiRecord>? completion = null;
                    lock (_gate)
                    {
                        if (_pending.TryGetValue(token, out completion))
                        {
                            _pending.Remove(token);
                        }
                    }

                    completion?.TrySetResult(record);
                }
                else if (head == "error")
                {
                    string message = record.GetText("msg");
                    if (message.Length > 0)
                    {
                        ConsoleOutput?.Invoke(this, message);
                    }
                }

                break;
            }

            case '*':
                if (head == "stopped")
                {
                    Stopped?.Invoke(this, ParseStop(ParseFields(payload)));
                }
                else if (head == "running")
                {
                    Running?.Invoke(this, EventArgs.Empty);
                }

                break;
        }
    }

    private static GdbStopEventArgs ParseStop(Dictionary<string, MiNode> fields)
    {
        var info = new GdbStopEventArgs
        {
            Reason = GetText(fields, "reason"),
        };

        if (fields.TryGetValue("frame", out MiNode? frame) && frame.Fields is not null)
        {
            string file = frame.GetText("fullname");
            if (file.Length == 0)
            {
                file = frame.GetText("file");
            }

            info = new GdbStopEventArgs
            {
                Reason = info.Reason,
                File = file,
                Line = ParseInt(frame.GetText("line")),
                Function = frame.GetText("func"),
            };
        }

        return info;
    }

    private static string GetText(Dictionary<string, MiNode> fields, string key) =>
        fields.TryGetValue(key, out MiNode? node) ? node.Text : string.Empty;

    private static int ParseInt(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static string Quote(string value) => "\"" + value + "\"";

    /// <summary>把字符串转成 MI 的 C 风格转义形式。</summary>
    private static string QuoteMi(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (char c in value)
        {
            builder.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i += 1)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                builder.Append(c);
                continue;
            }

            i += 1;
            builder.Append(value[i] switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '\\' => '\\',
                '"' => '"',
                _ => value[i],
            });
        }

        return builder.ToString();
    }

    private static Dictionary<string, MiNode> ParseFields(string text)
    {
        var fields = new Dictionary<string, MiNode>(StringComparer.Ordinal);
        int index = 0;

        while (index < text.Length)
        {
            while (index < text.Length && (text[index] == ',' || text[index] == ' '))
            {
                index += 1;
            }

            int nameStart = index;
            while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '-' or '_'))
            {
                index += 1;
            }

            if (index >= text.Length || text[index] != '=')
            {
                break;
            }

            string name = text[nameStart..index];
            index += 1;
            MiNode value = ParseValue(text, ref index);
            fields[name] = value;

            while (index < text.Length && text[index] == ',')
            {
                index += 1;
                break;
            }
        }

        return fields;
    }

    private static MiNode ParseValue(string text, ref int index)
    {
        while (index < text.Length && text[index] == ' ')
        {
            index += 1;
        }

        if (index >= text.Length)
        {
            return new MiNode(string.Empty);
        }

        char c = text[index];
        if (c == '"')
        {
            index += 1;
            var builder = new StringBuilder();
            while (index < text.Length)
            {
                char ch = text[index];
                if (ch == '\\' && index + 1 < text.Length)
                {
                    index += 1;
                    builder.Append(text[index] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        '"' => '"',
                        _ => text[index],
                    });
                    index += 1;
                }
                else if (ch == '"')
                {
                    index += 1;
                    break;
                }
                else
                {
                    builder.Append(ch);
                    index += 1;
                }
            }

            return new MiNode(builder.ToString());
        }

        if (c == '{')
        {
            index += 1;
            var fields = new Dictionary<string, MiNode>(StringComparer.Ordinal);
            while (index < text.Length && text[index] != '}')
            {
                int nameStart = index;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '-' or '_'))
                {
                    index += 1;
                }

                if (index >= text.Length || text[index] != '=')
                {
                    break;
                }

                string name = text[nameStart..index];
                index += 1;
                fields[name] = ParseValue(text, ref index);

                while (index < text.Length && text[index] is ',' or ' ')
                {
                    index += 1;
                }
            }

            if (index < text.Length && text[index] == '}')
            {
                index += 1;
            }

            return MiNode.Tuple(fields);
        }

        if (c == '[')
        {
            index += 1;
            var items = new List<MiNode>();
            while (index < text.Length && text[index] != ']')
            {
                if (text[index] is ',' or ' ')
                {
                    index += 1;
                    continue;
                }

                int nameStart = index;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '-' or '_'))
                {
                    index += 1;
                }

                if (nameStart != index && index < text.Length && text[index] == '=')
                {
                    string name = text[nameStart..index];
                    index += 1;
                    MiNode inner = ParseValue(text, ref index);
                    items.Add(MiNode.Tuple(new Dictionary<string, MiNode>(StringComparer.Ordinal)
                    {
                        [name] = inner,
                    }));
                }
                else
                {
                    index = nameStart;
                    items.Add(ParseValue(text, ref index));
                }
            }

            if (index < text.Length && text[index] == ']')
            {
                index += 1;
            }

            return MiNode.List(items);
        }

        int valueStart = index;
        while (index < text.Length && text[index] is not (',' or '}' or ']'))
        {
            index += 1;
        }

        return new MiNode(text[valueStart..index].Trim());
    }

    public void Dispose()
    {
        try
        {
            if (!IsAlive)
            {
                return;
            }

            _process.StandardInput.WriteLine("-gdb-exit");
            _process.StandardInput.Flush();
            if (!_process.WaitForExit(1000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 忽略清理异常
        }
        finally
        {
            try { _process.Dispose(); }
            catch (Exception) { }
        }
    }

    /// <summary>MI 结果记录（^done / ^error 及其字段）。</summary>
    internal sealed class MiRecord
    {
        public MiRecord(bool success, string name, Dictionary<string, MiNode> fields)
        {
            Success = success;
            Name = name;
            Fields = fields;
        }

        public bool Success { get; }

        public string Name { get; }

        public Dictionary<string, MiNode>? Fields { get; }

        public static MiRecord Failure(string message) => new(false, "error", new Dictionary<string, MiNode>
        {
            ["msg"] = new MiNode(message),
        });

        public string GetText(string key) =>
            Fields is not null && Fields.TryGetValue(key, out MiNode? node) ? node.Text : string.Empty;
    }

    /// <summary>MI 值：纯文本、tuple 或 list。</summary>
    internal sealed class MiNode
    {
        public MiNode(string text)
        {
            Text = text;
        }

        public string Text { get; }

        public Dictionary<string, MiNode>? Fields { get; private init; }

        public List<MiNode>? Items { get; private init; }

        public static MiNode Tuple(Dictionary<string, MiNode> fields) =>
            new(string.Empty) { Fields = fields };

        public static MiNode List(List<MiNode> items) =>
            new(string.Empty) { Items = items };

        public MiNode? Get(string key) =>
            Fields is not null && Fields.TryGetValue(key, out MiNode? node) ? node : null;

        public string GetText(string key) => Get(key)?.Text ?? string.Empty;
    }
}
