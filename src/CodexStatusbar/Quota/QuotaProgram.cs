namespace CodexStatusbar.Quota;

internal static class QuotaProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The inherited token strip has its own default-on startup policy. Keep it as source,
        // but do not expose that unrelated policy from this explicitly opt-in quota companion.
        if (args.Length >= 1 && args[0] == "--token-strip") return 2;
        if (args.Length >= 2 && args[0] == "--quota-test") return QuotaSelfTest.Run(args[1]);
        if (args.Length >= 1 && args[0] == "--self-test") return SelfTest.Run(args.Length >= 2 ? args[1] : null);
        if (args.Length >= 1 && args[0] == "--enable-startup") return 2;
        if (args.Length >= 1 && args[0] == "--disable-startup")
        {
            try { QuotaStartup.RemoveLegacyStartup(); return 0; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return 1; }
        }
        if (args.Length >= 2 && args[0] == "--startup-status")
        {
            File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new { command = QuotaStartup.Read(), enabled = QuotaStartup.IsOurCommand(QuotaStartup.Read(), Environment.ProcessPath!) }));
            return 0;
        }
        if (args.Length >= 1 && args[0] == "--stop")
        {
            if (EventWaitHandle.TryOpenExisting(QuotaBatteryApplication.StopEventName, out var stop)) { using (stop) stop.Set(); }
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--render-batteries") { BatteryRenderer.ExportSamples(args[1]); return 0; }
        if (args.Length >= 2 && args[0] == "--quota-probe")
        {
            try
            {
                var quota = new CodexQuotaClient().ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(quota, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            catch (Exception e) { File.WriteAllText(args[1], e.GetType().Name + ": " + e.Message); return 1; }
        }
        if (args.Length >= 2 && args[0] == "--dock-probe")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            var found = CodexWindowLocator.TryGetForegroundCodexTarget(out var target);
            File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new { found, handle = found ? target.HostWindow.Handle.ToInt64() : 0, bounds = found ? target.HostWindow.WindowBounds : default, dpi = found ? target.HostWindow.Dpi : 0, anchor = found ? HelpMenuAnchor.Measure(target.HostWindow) : null }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return found ? 0 : 1;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var singleton = new Mutex(false, "Local\\CodexQuotaUnified.SingleInstance");
        bool TryClaimOverlay()
        {
            try { return singleton.WaitOne(0); }
            catch (AbandonedMutexException) { return true; }
        }
        var ownsOverlay = TryClaimOverlay();
        var mcp = args.Contains("--mcp", StringComparer.Ordinal);
        if (!ownsOverlay && !mcp) return 0;
        using var context = new QuotaBatteryApplication(ownsOverlay,
            mcp ? new QuotaMcpServer(QuotaMcpServer.ReadLocalStatus) : null, TryClaimOverlay);
        Application.Run(context);
        GC.KeepAlive(singleton);
        return 0;
    }
}
