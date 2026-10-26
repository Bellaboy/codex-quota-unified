using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexStatusbar.Quota;

/// <summary>Only initialize and quota-read RPCs. Authentication stays inside the official executable.</summary>
internal sealed class CodexQuotaClient : IDisposable
{
    private readonly object _gate = new();
    private Process? _active;
    private bool _disposed;
    public void Dispose()
    {
        lock (_gate) { _disposed = true; StopActive(); }
    }
    private void StopActive()
    {
        try { if (_active is { HasExited: false }) _active.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
    public static string ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_BATTERY_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured) && configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(configured);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory.Trim('"'), "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(root))
        {
            var candidate = Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (candidate is not null) return candidate;
        }
        throw new FileNotFoundException("未找到 Codex，请安装或更新 Codex 后重试。");
    }

    internal static ProcessStartInfo CreateStartInfo(string executable)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath()
        };
        // A quota-only app-server must not recursively load this same MCP plugin.
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("plugins.\"codex-quota-unified@codex-unified-tools\".enabled=false");
        start.ArgumentList.Add("app-server");
        return start;
    }

    public async Task<QuotaSnapshot> ReadAsync(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        var start = CreateStartInfo(ResolveExecutable());
        Process started;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        started = Process.Start(start) ?? throw new IOException("无法启动 Codex 额度查询。");
        lock (_gate)
        {
            if (_disposed)
            {
                started.Kill(entireProcessTree: true);
                started.Dispose();
                cancellation.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(CodexQuotaClient));
            }
            _active = started;
        }
        using var process = started;
        // Drain without retaining or logging stderr; it can contain user-specific diagnostic text.
        var stderr = DrainAsync(process.StandardError, token);
        try
        {
            await SendAsync(process, new { id = 0, method = "initialize", @params = new {
                clientInfo = new { name = "codex_quota_battery", title = "Codex Quota Battery", version = "0.1.0" }
            } }, token);
            using (await ReadResponseAsync(process, 0, token)) { }
            await SendAsync(process, new { method = "initialized", @params = new { } }, token);
            await SendAsync(process, new { id = 1, method = "account/rateLimits/read" }, token);
            using var response = await ReadResponseAsync(process, 1, token);
            return QuotaParser.Parse(response.RootElement.GetProperty("result"), DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("额度查询超时，将自动重试。");
        }
        finally
        {
            lock (_gate) { StopActive(); _active = null; }
            try { await stderr; } catch (OperationCanceledException) { } catch (IOException) { }
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0) { }
    }
    private static async Task SendAsync(Process process, object message, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
    }
    private static async Task<JsonDocument> ReadResponseAsync(Process process, int id, CancellationToken token)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(token);
            if (line is null) throw new IOException("Codex 查询进程已退出，请确认已登录。");
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var responseId)
                || responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var number) || number != id)
            { doc.Dispose(); continue; }
            if (root.TryGetProperty("error", out _))
            {
                doc.Dispose();
                throw new IOException("Codex 未返回额度，请确认已用 ChatGPT 账户登录后刷新。");
            }
            if (!root.TryGetProperty("result", out _)) { doc.Dispose(); throw new IOException("Codex 额度响应格式发生变化。"); }
            return doc;
        }
    }
}
