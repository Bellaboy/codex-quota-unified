using System.Text.Json;

namespace CodexStatusbar.Quota;

internal sealed class QuotaMcpServer(Func<string> readStatus)
{
    private int _closed;
    private int _initialized;
    public bool ConnectionClosed => Volatile.Read(ref _closed) != 0;
    public bool Initialized => Volatile.Read(ref _initialized) != 0;

    internal object? Handle(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return null;
        var method = message.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (method == "notifications/initialized") { Interlocked.Exchange(ref _initialized, 1); return null; }
        if (!message.TryGetProperty("id", out var originalId)) return null;
        var id = originalId.Clone();
        object Success(object result) => new { jsonrpc = "2.0", id, result };
        object Error(int code, string text) => new { jsonrpc = "2.0", id, error = new { code, message = text } };
        switch (method)
        {
            case "initialize":
                var requested = message.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.String
                    ? version.GetString() : null;
                var protocol = requested is "2025-11-25" or "2025-06-18" or "2025-03-26" or "2024-11-05"
                    ? requested : "2025-03-26";
                return Success(new { protocolVersion = protocol, capabilities = new { tools = new { } },
                    serverInfo = new { name = "codex-quota-unified", version = "1.0.6" } });
            case "ping": return Success(new { });
            case "tools/list":
                return Success(new { tools = new[] { new {
                    name = "get_quota_status", title = "Codex 剩余额度与重置卡库存",
                    description = "读取统一插件的30秒额度快照、顶栏位置和自动续跑状态；本工具只读。",
                    inputSchema = new { type = "object", properties = new { }, additionalProperties = false },
                    annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = false }
                } } });
            case "tools/call":
                if (!message.TryGetProperty("params", out var call) || call.ValueKind != JsonValueKind.Object
                    || !call.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != "get_quota_status")
                    return Error(-32602, "Unknown tool");
                return Success(new { content = new[] { new { type = "text", text = readStatus() } }, isError = false });
            case "resources/list": return Success(new { resources = Array.Empty<object>() });
            case "prompts/list": return Success(new { prompts = Array.Empty<object>() });
            default: return Error(-32601, "Method not found");
        }
    }

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await input.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                object? response;
                try { using var doc = JsonDocument.Parse(line); response = Handle(doc.RootElement); }
                catch (JsonException) { response = new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } }; }
                if (response is null) continue;
                await output.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { /* Codex owns this pipe. Closing it ends the plugin. */ }
        finally { Interlocked.Exchange(ref _closed, 1); }
    }

    public static string ReadLocalStatus()
    {
        try { return File.ReadAllText(Path.Combine(QuotaBatteryApplication.DataDirectory, "status.json")); }
        catch (IOException) { return "{\"status\":\"connecting\"}"; }
        catch (UnauthorizedAccessException) { return "{\"status\":\"unavailable\"}"; }
    }
}
