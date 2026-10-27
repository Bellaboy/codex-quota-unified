using System.Text.Json;

namespace CodexStatusbar.Quota;

internal static class QuotaSelfTest
{
    public static int Run(string path)
    {
        var results = new List<object>();
        var failed = 0;
        void Test(string name, Action action)
        {
            try { action(); results.Add(new { name, passed = true }); }
            catch (Exception e) { failed++; results.Add(new { name, passed = false, reason = e.Message }); }
        }
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        Test("startup owner does not register a connecting tray icon", () =>
        {
            using var app = new QuotaBatteryApplication();
            try { Assert(!app.TrayVisible, "startup registered a tray icon before the first quota result"); }
            finally { app.ExitThread(); }
        });
        Test("standby connection does not register a tray icon", () =>
        {
            using var app = new QuotaBatteryApplication(renderOverlay: false);
            try { Assert(!app.TrayVisible, "standby registered a tray icon"); }
            finally { app.ExitThread(); }
        });
        Test("MCP initialization must finish before owner starts background work", () =>
        {
            var server = new QuotaMcpServer(() => "{}");
            using var init = JsonDocument.Parse("""{"id":1,"method":"initialize"}""");
            server.Handle(init.RootElement);
            Assert(!server.Initialized, "initialize response alone marked the client ready");
            using var ready = JsonDocument.Parse("""{"method":"notifications/initialized"}""");
            Assert(server.Handle(ready.RootElement) is null && server.Initialized, "initialized notification was ignored");
        });
        Test("account change clears cached quota before reuse", () =>
        {
            var state = new QuotaRefreshState();
            state.Succeed(new(new(300, 10, now.AddHours(1)), new(10080, 10, now.AddDays(1)), now, 2));
            state.AccountChanged();
            Assert(state.Snapshot.First is null && state.Snapshot.Second is null && state.Stale(now), "old account quota survived");
        });
        Test("120 moves reuse a single rendered surface", () =>
        {
            using var strip = new BatteryStripForm();
            strip.SetSurface(BatteryRenderer.Render(QuotaSnapshot.Empty, now, 96, false, false), Array.Empty<QuotaHoverRow>());
            for (var i = 0; i < 120; i++) strip.PositionAt(new(200 + i, 200, BatteryLayout.WidthDip, 26));
            Assert(strip.NativeMoves == 120 && strip.SurfaceRenders == 1, "movement regenerated the surface");
        });
        QuotaSnapshot Parse(string json) { using var doc = JsonDocument.Parse(json); return QuotaParser.Parse(doc.RootElement, now); }

        Test("prefers account codex bucket over model bucket and legacy", () =>
        {
            var s = Parse("""{"rateLimits":{"primary":{"usedPercent":99,"windowDurationMins":300}},"rateLimitsByLimitId":{"model":{"primary":{"usedPercent":90,"windowDurationMins":300}},"codex":{"primary":{"usedPercent":15,"windowDurationMins":300},"secondary":{"usedPercent":5,"windowDurationMins":10080}}}}""");
            Assert(s.First?.Remaining == 85 && s.Second?.Remaining == 95, "wrong pool or remaining percentage");
        });
        Test("reversed window order is normalized", () =>
        {
            var s = Parse("""{"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":10080},"secondary":{"usedPercent":20,"windowDurationMins":300}}}""");
            Assert(s.First?.Minutes == 300 && s.Second?.Minutes == 10080, "reversed windows");
        });
        Test("weekly-only account leaves first battery unknown", () =>
        {
            var s = Parse("""{"rateLimits":{"primary":{"usedPercent":5,"windowDurationMins":10080},"secondary":null}}""");
            Assert(s.First is null && s.Second?.Remaining == 95, "invented first window");
        });
        Test("model-only map never claims account quota", () =>
        {
            var s = Parse("""{"rateLimitsByLimitId":{"other":{"primary":{"usedPercent":2,"windowDurationMins":300}}}}""");
            Assert(s.First is null && s.Second is null, "model quota passed as account quota");
        });
        Test("missing percentage is not zero usage", () =>
        {
            var s = Parse("""{"rateLimits":{"primary":{"windowDurationMins":300}}}""");
            Assert(s.First is null, "missing value became 100%");
        });
        Test("out-of-range percentages clamp and expired reset is unknown", () =>
        {
            var s = Parse("""{"rateLimits":{"primary":{"usedPercent":120,"windowDurationMins":300,"resetsAt":1799999999},"secondary":{"usedPercent":-5,"windowDurationMins":10080}}}""");
            Assert(s.First?.Remaining == 0 && s.First.Display(now) == "--", "expired quota falsely reset");
            Assert(s.Second?.Remaining == 100, "percentage unclamped");
        });
        Test("invalid reset timestamp is ignored without losing usable quota", () =>
        {
            var s = Parse("""{"rateLimits":{"primary":{"usedPercent":15,"windowDurationMins":300,"resetsAt":9223372036854775807}}}""");
            Assert(s.First?.Remaining == 85 && s.First.ResetsAt is null, "invalid reset crashed parser");
        });
        Test("unknown window duration retains real label", () =>
        {
            var s = Parse("""{"rateLimits":{"primary":{"usedPercent":7,"windowDurationMins":60}}}""");
            Assert(s.First?.Label == "1h" && s.First.Remaining == 93, "invented five-hour window");
        });
        Test("reset credit count is optional and authoritative", () =>
        {
            var s = Parse("""{"rateLimitResetCredits":{"availableCount":3,"credits":[]}}""");
            Assert(s.ResetCredits == 3, "wrong credit count");
        });
        Test("null or malformed fields are unknown", () =>
        {
            Assert(Parse("null").First is null, "null response");
            Assert(Parse("""{"rateLimits":{"primary":{"usedPercent":"abc","windowDurationMins":300},"secondary":{"usedPercent":2,"windowDurationMins":-1}}}""").First is null, "invalid numbers accepted");
        });

        Test("dock after Help and vertically center", () =>
        {
            var r = BatteryLayout.Place(new(270, 0, 40, 36), new(0, 0, 880, 1000), 96, 740);
            Assert(r.Left == 322 && r.Top == 5 && r.Width == 276 && r.Height == 26, "wrong dock position or reset stock missing");
        });
        Test("DPI scales menu gap and battery size", () =>
        {
            var r = BatteryLayout.Place(new(405, 0, 60, 54), new(0, 0, 1320, 1500), 144, 1110);
            Assert(r.Left == 483 && r.Width == 414 && r.Height == 39, "wrong 150% scaling");
        });
        Test("narrow host hides without covering window controls", () =>
        {
            Assert(BatteryLayout.Place(new(270, 0, 40, 36), new(0, 0, 500, 1000), 96, 370).IsEmpty, "overlaps caption controls");
        });
        Test("low quota thresholds and unknown are visually distinct", () =>
        {
            Assert(BatteryLayout.LevelColor(21) != BatteryLayout.LevelColor(20), "20% needs orange");
            Assert(BatteryLayout.LevelColor(11) != BatteryLayout.LevelColor(10), "10% needs red");
            Assert(BatteryLayout.LevelColor(null) != BatteryLayout.LevelColor(100), "unknown shown as full");
        });
        Test("battery fills proportionally with no fake minimum sliver", () =>
        {
            Assert(BatteryLayout.FillWidth(85, 50) == 42.5f && BatteryLayout.FillWidth(0, 50) == 0
                && BatteryLayout.FillWidth(null, 50) == 0, "incorrect fill width");
        });

        Test("failure preserves last successful numbers and timestamp", () =>
        {
            var state = new QuotaRefreshState();
            var snapshot = new QuotaSnapshot(new(300, 15, now.AddHours(1)), null, now, null);
            state.Succeed(snapshot);
            state.Fail("offline");
            Assert(state.Snapshot == snapshot && state.Error == "offline" && state.Stale(now), "failure lost numbers or stale marker");
        });
        Test("successful refresh clears failure and stale state", () =>
        {
            var state = new QuotaRefreshState();
            state.Fail("offline");
            state.Succeed(new(null, null, now, null));
            Assert(state.Error is null && !state.Stale(now), "success retained error");
        });
        Test("old or expired snapshot becomes stale without inventing allowance", () =>
        {
            var state = new QuotaRefreshState();
            state.Succeed(new(new(300, 100, now.AddSeconds(1)), null, now, null));
            Assert(state.Stale(now.AddSeconds(2)) && state.Snapshot.First!.Display(now.AddSeconds(2)) == "--", "expired data looks fresh");
            state.Succeed(new(null, null, now, null));
            Assert(state.Stale(now.AddMinutes(4)), "old snapshot looks fresh");
        });

        Test("startup quotes paths with spaces and waits for Codex", () =>
        {
            Assert(QuotaStartup.BuildCommand(@"C:\Tools and Apps\电池.exe") == "\"C:\\Tools and Apps\\电池.exe\" --watch-codex", "unsafe startup path");
            Assert(QuotaStartup.IsOurCommand("\"C:\\Tools and Apps\\电池.exe\" --watch-codex", @"C:\Tools and Apps\电池.exe"), "own startup command not recognized");
            Assert(!QuotaStartup.IsOurCommand("\"C:\\Other.exe\" --watch-codex", @"C:\Tools and Apps\电池.exe"), "unrelated startup command accepted");
        });
        Test("render has a nearly invisible continuous hover hit surface", () =>
        {
            using var bitmap = BatteryRenderer.Render(new(new(300, 15, now.AddHours(1)), new(10080, 5, now.AddDays(1)), now, 1), now, 144, false, false);
            Assert(bitmap.GetPixel(0, 0).A == 1 && bitmap.GetPixel(bitmap.Width - 1, 0).A == 1, "hover hit backing is missing or opaque");
            Assert(bitmap.Width == 414, "inventory width absent");
        });
        Test("zero reset cards is distinct from unknown inventory", () =>
        {
            Assert(new QuotaSnapshot(null, null, now, 0).ResetCreditDisplay == "0", "zero hidden");
            Assert(QuotaSnapshot.Empty.ResetCreditDisplay == "--", "unknown shown as zero");
            Assert(new QuotaSnapshot(null, null, now, 3).ResetCreditDisplay == "3", "wrong available count");
        });
        Test("available card dates exclude used cards and show Beijing minutes", () =>
        {
            var earlier = new DateTimeOffset(2026, 10, 29, 0, 10, 59, TimeSpan.Zero);
            var later = earlier.AddDays(8);
            var snapshot = Parse(JsonSerializer.Serialize(new { rateLimitResetCredits = new { availableCount = 2,
                credits = new[] { new { status = "used", expiresAt = earlier.AddDays(-1).ToUnixTimeSeconds() },
                    new { status = "available", expiresAt = later.ToUnixTimeSeconds() },
                    new { status = "available", expiresAt = earlier.ToUnixTimeSeconds() } } } }));
            Assert(snapshot.ResetCreditText == "重置卡 2 张", "wrong earliest date, timezone, or minute format");
            Assert(snapshot.HoverRows[2] == new QuotaHoverRow("重置卡 1 到期", "10-29 08:10", true)
                && snapshot.HoverRows[3] == new QuotaHoverRow("重置卡 2 到期", "11-06 08:10"), "tooltip lost card dates or sorting");
        });
        Test("missing card dates never claim a known earliest expiration", () =>
        {
            var snapshot = Parse("""{"rateLimitResetCredits":{"availableCount":2,"credits":[{"status":"available","expiresAt":1793232659},{"status":"available"}]}}""");
            Assert(snapshot.ResetCreditText == "重置卡 2 张" && snapshot.HoverRows[3].Value == "--", "missing expiration was invented");
        });
        Test("partial inventory keeps authoritative count with unknown expiration", () =>
        {
            var snapshot = Parse("""{"rateLimitResetCredits":{"availableCount":2,"credits":[{"status":"available","expiresAt":1793232659}]}}""");
            Assert(snapshot.HoverRows.Count == 4 && snapshot.HoverRows[3].Value == "--", "partial inventory mislabeled earliest date");
        });
        Test("zero cards omit expiration and invalid dates stay unknown", () =>
        {
            Assert(new QuotaSnapshot(null, null, now, 0).ResetCreditText == "重置卡 0 张", "zero-card UI claims expiration");
            var snapshot = Parse("""{"rateLimitResetCredits":{"availableCount":1,"credits":[{"status":"available","expiresAt":9223372036854775807}]}}""");
            Assert(snapshot.ResetCreditText == "重置卡 1 张" && snapshot.HoverRows[2].Value == "--", "invalid timestamp became an expiration");
        });
        Test("hover includes only quota refresh and card expiration times", () =>
        {
            var snapshot = new QuotaSnapshot(new(300, 10, now.AddHours(1)), new(10080, 20, now.AddDays(1)), now, 1,
                new DateTimeOffset?[] { now.AddDays(2) });
            Assert(snapshot.HoverRows.Count == 3 && snapshot.HoverRows[0].Label == "5h 刷新"
                && snapshot.HoverRows[1].Label == "周额度刷新", "unexpected tooltip rows");
            foreach (var row in snapshot.HoverRows)
                Assert(row.Value.Length == 11 && !row.Value.Contains("%"), "tooltip contains more than minute-precision dates");
            Assert(!snapshot.ResetCreditTooltip.Contains("最近") && !snapshot.ResetCreditTooltip.Contains("北京时间")
                && !snapshot.ResetCreditTooltip.Contains("每30秒") && !snapshot.ResetCreditTooltip.Contains("可用重置卡"), "extra tooltip prose retained");
        });
        Test("rounded hover card scales in light and dark mode", () =>
        {
            var rows = new QuotaSnapshot(null, null, now, 1, new DateTimeOffset?[] { now.AddDays(1) }).HoverRows;
            using var light = QuotaHoverCard.Render(rows, false, 96);
            using var dark = QuotaHoverCard.Render(rows, true, 144);
            Assert(light.Width == 280 && dark.Width == 420 && light.GetPixel(0, 0).A == 0, "card size or rounded corner incorrect");
            Assert(light.GetPixel(20, 10).ToArgb() != dark.GetPixel(30, 15).ToArgb(), "theme colors identical");
        });
        Test("credit stock change affects rendered pixels", () =>
        {
            using var zero = BatteryRenderer.Render(new(null, null, now, 0), now, 96, false, false);
            using var three = BatteryRenderer.Render(new(null, null, now, 3), now, 96, false, false);
            var difference = false;
            for (var x = 237; x < 272; x++) for (var y = 4; y < 23; y++)
                difference |= zero.GetPixel(x, y) != three.GetPixel(x, y);
            Assert(difference, "stock number not drawn");
        });

        Test("host lifetime exits after initial no-Codex grace", () =>
        {
            var lifetime = new HostLifetime(now);
            Assert(!lifetime.ShouldExit(false, false, now.AddSeconds(1)), "exits before desktop is initialized");
            Assert(lifetime.ShouldExit(false, false, now.AddSeconds(10)), "resident without Codex");
        });
        Test("last Codex window closes: process exits after short recreation grace", () =>
        {
            var lifetime = new HostLifetime(now);
            Assert(!lifetime.ShouldExit(true, false, now), "exits with desktop present");
            Assert(!lifetime.ShouldExit(false, false, now.AddSeconds(1)), "window-recreation grace absent");
            Assert(lifetime.ShouldExit(false, false, now.AddSeconds(3)), "resident after closing Codex");
        });
        Test("minimized or background Codex is still a live host", () =>
        {
            var lifetime = new HostLifetime(now);
            Assert(!lifetime.ShouldExit(true, false, now.AddHours(1)), "closed on minimize/background");
        });
        Test("window recreation or another Codex window cancels shutdown", () =>
        {
            var lifetime = new HostLifetime(now);
            lifetime.ShouldExit(true, false, now);
            lifetime.ShouldExit(false, false, now.AddSeconds(1));
            Assert(!lifetime.ShouldExit(true, false, now.AddSeconds(2)), "recreated host was ignored");
        });
        Test("Codex-owned stdio EOF ends plugin immediately", () =>
        {
            Assert(new HostLifetime(now).ShouldExit(true, true, now), "pipe closed but plugin stayed resident");
        });
        JsonElement Mcp(string request)
        {
            using var doc = JsonDocument.Parse(request);
            return JsonSerializer.SerializeToElement(new QuotaMcpServer(() => "{\"resetCredits\":1}").Handle(doc.RootElement));
        }
        Test("MCP initializes with numeric response id and compatible protocol", () =>
        {
            var response = Mcp("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""");
            Assert(response.GetProperty("id").GetInt32() == 1 && response.GetProperty("result").GetProperty("protocolVersion").GetString() == "2025-03-26", "bad MCP handshake");
        });
        Test("MCP tools are read-only and retain string ids", () =>
        {
            var response = Mcp("""{"jsonrpc":"2.0","id":"list","method":"tools/list"}""");
            var tool = response.GetProperty("result").GetProperty("tools")[0];
            Assert(response.GetProperty("id").GetString() == "list" && tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), "unsafe tool declaration");
        });
        Test("MCP status tool returns current snapshot only", () =>
        {
            var response = Mcp("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_quota_status","arguments":{}}}""");
            Assert(response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() == "{\"resetCredits\":1}", "wrong status tool");
        });
        Test("MCP unknown methods are rejected, notifications produce no reply", () =>
        {
            Assert(Mcp("""{"id":4,"method":"execute_command"}""").GetProperty("error").GetProperty("code").GetInt32() == -32601, "unknown method accepted");
            Assert(Mcp("""{"method":"notifications/initialized"}""").ValueKind == JsonValueKind.Null, "notification replied");
        });
        Test("quota query suppresses its own plugin to prevent recursive child launch", () =>
        {
            var start = CodexQuotaClient.CreateStartInfo(@"C:\Codex\codex.exe");
            Assert(start.ArgumentList.Contains("plugins.\"codex-quota-unified@codex-unified-tools\".enabled=false"), "own plugin not disabled in child query");
            Assert(start.ArgumentList.Contains("app-server") && !start.ArgumentList.Contains("exec") && start.CreateNoWindow, "unsafe query launch");
        });
        Test("MCP malformed tool name is a protocol error and does not crash", () =>
        {
            Assert(Mcp("""{"id":5,"method":"tools/call","params":{"name":12}}""").GetProperty("error").GetProperty("code").GetInt32() == -32602, "malformed call accepted");
        });
        Test("real window classifier accepts minimized desktop for lifetime", () =>
        {
            var window = new WindowCandidateFacts(new IntPtr(1), 1, true, true, true, IntPtr.Zero, 0,
                new(-32000, -32000, 160, 28), "Chrome_WidgetWin_1");
            Assert(CodexWindowClassifier.IsLiveDesktopWindow(window), "minimized window killed plugin");
            Assert(CodexWindowClassifier.IsLiveDesktopWindow(window with { IsMinimized = false, Bounds = new(0, 0, 300, 200) }), "small desktop killed plugin");
            Assert(!CodexWindowClassifier.IsLiveDesktopWindow(window with { IsCodexProcess = false }), "other app accepted");
            Assert(!CodexWindowClassifier.IsLiveDesktopWindow(window with { IsVisible = false }), "hidden helper accepted");
        });

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { total = results.Count, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}
