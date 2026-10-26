using System.Diagnostics;
using System.Text.Json;

namespace CodexStatusbar.Quota;

/// <summary>
/// 只由顶栏所有者启动恢复进程；输入为官方当前调用环境和安装器写入的 Python 路径。
/// 输出在独立的 CodexQuotaUnified 数据目录；不启动模型、不修改审批设置。
/// </summary>
internal sealed class RecoveryCompanion : IDisposable
{
    private Process? _worker;
    private readonly object _gate = new();
    private Task? _starting;
    private bool _disposed;
    private DateTimeOffset _nextCheck;
    public int? ProcessId => _worker is { HasExited: false } ? _worker.Id : null;

    internal static void PublishCurrentRuntime()
    {
        var pipe = Environment.GetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH");
        var caller = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
        if (string.IsNullOrEmpty(pipe) || string.IsNullOrEmpty(caller)) return;
        // 仅使用当前官方调用传入的身份；不枚举管道，不从其他会话推断身份。
        var value = new { pipe, callerThreadId = caller, node = Environment.GetEnvironmentVariable("CODEX_MCP_NODE_PATH") };
        var path = Path.Combine(QuotaBatteryApplication.DataDirectory, "bridge-runtime.json");
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, path, overwrite: true);
    }

    public void EnsureRunning()
    {
        if (DateTimeOffset.UtcNow < _nextCheck) return;
        _nextCheck = DateTimeOffset.UtcNow.AddSeconds(5);
        if (_starting is { IsCompleted: false }) return;
        if (_worker is { HasExited: false }) return;
        _worker?.Dispose();
        _worker = null;
        // 子进程创建不能阻塞 WinForms 消息循环，否则 MCP EOF 和退出事件无法处理。
        _starting = Task.Run(StartWorker);
    }

    private void StartWorker()
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "runtime.json")));
            var python = runtime.RootElement.GetProperty("python").GetString()!;
            var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = System.Text.Encoding.UTF8, StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8, WorkingDirectory = root };
            start.ArgumentList.Add(Path.Combine(root, "recovery", "recovery_monitor.py"));
            start.ArgumentList.Add("--parent");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            var worker = Process.Start(start) ?? throw new IOException("无法启动恢复监控。");
            lock (_gate)
            {
                if (_disposed) { worker.Kill(entireProcessTree: true); worker.Dispose(); return; }
                _worker = worker;
            }
            _ = worker.StandardOutput.ReadToEndAsync();
            var errors = worker.StandardError.ReadToEndAsync();
            _ = errors.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion && !string.IsNullOrWhiteSpace(task.Result))
                    File.WriteAllText(Path.Combine(QuotaBatteryApplication.DataDirectory, "recovery-error.log"), task.Result);
            }, TaskScheduler.Default);
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or JsonException)
        {
            File.WriteAllText(Path.Combine(QuotaBatteryApplication.DataDirectory, "recovery-status.json"),
                JsonSerializer.Serialize(new { status = "start_error", reason = exception.Message, updatedUtc = DateTimeOffset.UtcNow }));
        }
    }

    public static JsonElement ReadStatus()
    {
        var path = Path.Combine(QuotaBatteryApplication.DataDirectory, "recovery-status.json");
        using var doc = File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path))
            : JsonDocument.Parse("{\"status\":\"starting\"}");
        return doc.RootElement.Clone();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_worker is { HasExited: false }) _worker.Kill(entireProcessTree: true);
            _worker?.Dispose();
            _worker = null;
        }
    }
}
