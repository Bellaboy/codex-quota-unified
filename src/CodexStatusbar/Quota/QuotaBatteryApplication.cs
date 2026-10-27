using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexStatusbar.Quota;

internal sealed class QuotaBatteryApplication : ApplicationContext
{
    internal const int QuotaPollingSeconds = 30;
    private readonly RecoveryCompanion _recovery = new();
    internal const string StopEventName = "Local\\CodexQuotaUnified.Stop";
    private readonly EventWaitHandle _stop = new(false, EventResetMode.ManualReset, StopEventName);
    private readonly HostLifetime _lifetime = new(DateTimeOffset.UtcNow);
    private readonly QuotaMcpServer? _mcp;
    private bool _renderOverlay;
    private readonly Func<bool>? _tryClaimOverlay;
    private bool _hostPresent;
    private DateTimeOffset _nextClaim;
    // 仅观察官方认证文件的修改时间，不读取、保存或发布认证内容。
    private readonly string _authPath = Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "auth.json");
    private DateTime _authStamp;
    internal static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaUnified");
    private readonly BatteryStripForm _strip = new();
    private readonly HostWindowSampler _sampler = new();
    private readonly CodexThemeSource _theme = new();
    private readonly QuotaRefreshState _state = new();
    private readonly CodexQuotaClient _client = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly ToolStripMenuItem _refresh;
    private CodexWindowTarget? _target;
    private DateTimeOffset _nextDiscover, _nextRead, _nextStatus, _nextPaint;
    private IntPtr _lastForeground;
    private bool _reading, _disposed;
    private string? _renderKey;
    private IntRect _bounds;
    private string _anchorSource = "waiting-for-codex";
    internal bool TrayVisible => false;

    public QuotaBatteryApplication(bool renderOverlay = true, QuotaMcpServer? mcp = null, Func<bool>? tryClaimOverlay = null)
    {
        _renderOverlay = renderOverlay;
        _mcp = mcp;
        _tryClaimOverlay = tryClaimOverlay;
        _authStamp = File.GetLastWriteTimeUtc(_authPath);
        Directory.CreateDirectory(DataDirectory);
        RecoveryCompanion.PublishCurrentRuntime();
        _ = _strip.Handle; // Install the WinForms synchronization context before async requests.
        var menu = new ContextMenuStrip();
        menu.Items.Add("Codex 额度与恢复").Enabled = false;
        _refresh = new ToolStripMenuItem("立即刷新", null, (_, _) => { _nextRead = DateTimeOffset.MinValue; });
        menu.Items.Add(_refresh);
        menu.Items.Add("随 Codex 启动和退出").Enabled = false;
        _strip.ContextMenuStrip = menu;
        _strip.DoubleClick += (_, _) => _nextRead = DateTimeOffset.MinValue;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        if (_mcp is not null) _ = Task.Run(async () =>
        {
            // MCP 直接持有独立的 UTF-8 管道流，避免使用 Console 的同步文本封装。
            using var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
            using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            await _mcp.RunAsync(input, output, _shutdown.Token);
        });
    }

    private void Tick()
    {
        if (_disposed) return;
        if (_stop.WaitOne(0)) { ExitThread(); return; }
        var now = DateTimeOffset.UtcNow;
        var foreground = GetAncestor(GetForegroundWindow(), 2);
        if (foreground != _lastForeground || now >= _nextDiscover)
        {
            var stamp = File.GetLastWriteTimeUtc(_authPath);
            if (stamp != _authStamp)
            {
                _authStamp = stamp;
                _state.AccountChanged();
                _nextRead = _nextStatus = DateTimeOffset.MinValue;
                _renderKey = null;
            }
            _lastForeground = foreground;
            _nextDiscover = now.AddSeconds(1);
            if (CodexWindowLocator.TryGetForegroundCodexTarget(out var current))
            {
                if (_target?.HostWindow.Handle != current.HostWindow.Handle)
                {
                    _sampler.Bind(current.HostWindow.Handle, OnHostEvent);
                    _renderKey = null;
                }
                _target = current;
            }
            else if (_target is not null && !IsWindow(_target.HostWindow.Handle))
            {
                _target = null;
                _sampler.Unbind();
            }
            if (_target is null && CodexWindowLocator.TryFindAnyCodexTarget(out var existing))
            {
                _target = existing;
                _sampler.Bind(existing.HostWindow.Handle, OnHostEvent);
            }
            _hostPresent = _target is not null && IsWindow(_target.HostWindow.Handle) && IsWindowVisible(_target.HostWindow.Handle)
                || CodexWindowLocator.HasLiveCodexWindow();
        }
        if (_lifetime.ShouldExit(_hostPresent, _mcp?.ConnectionClosed == true, now))
        { ExitThread(); return; }
        if (!_renderOverlay && _tryClaimOverlay is not null && now >= _nextClaim)
        {
            _nextClaim = now.AddSeconds(1);
            if (_tryClaimOverlay())
            {
                _renderOverlay = true;
                _renderKey = null;
                _nextRead = DateTimeOffset.MinValue;
                _nextStatus = DateTimeOffset.MinValue;
            }
        }
        if (!_renderOverlay) return;
        if (_mcp is { Initialized: false }) return;
        _recovery.EnsureRunning();
        if (_target is not null && now >= _nextRead && !_reading)
        {
            _nextRead = now.AddSeconds(QuotaPollingSeconds);
            _ = RefreshAsync();
        }
        if (_target is null || foreground != _target.HostWindow.Handle || IsIconic(_target.HostWindow.Handle))
        {
            _strip.Hide();
            _bounds = default;
        }
        else
        {
            if (_sampler.TrySample(out _)) _sampler.Commit();
            if (_sampler.TryBuildHostInfo(out var host))
            {
                var anchor = HelpMenuAnchor.Measure(host);
                _anchorSource = anchor.Source;
                _bounds = BatteryLayout.Place(anchor.Help, host.WindowBounds, host.Dpi, host.CaptionButtonBounds?.Left);
                if (_bounds.IsEmpty) _strip.Hide();
                else
                {
                    _strip.Attach(host.Handle);
                    // Refresh tooltip countdown once a second; repaint pixels only when their key changes.
                    if (now >= _nextPaint || !_strip.Visible)
                    {
                        _nextPaint = now.AddSeconds(1);
                        var dark = _theme.Current == CodexThemeKind.Dark || _theme.Current == CodexThemeKind.Unknown
                            && (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0;
                        var stale = _state.Stale(now);
                        var key = $"{_state.Snapshot.First?.Display(now)}|{_state.Snapshot.Second?.Display(now)}|{_state.Snapshot.ResetCreditText}|{host.Dpi}|{dark}|{stale}";
                        if (key != _renderKey)
                        {
                            _strip.SetSurface(BatteryRenderer.Render(_state.Snapshot, now, host.Dpi, dark, stale), _state.Snapshot.HoverRows, dark, host.Dpi);
                            _renderKey = key;
                        }
                        else _strip.UpdateTooltip(_state.Snapshot.HoverRows);
                    }
                    _strip.PositionAt(_bounds);
                }
            }
        }
        if (now >= _nextStatus)
        {
            _nextStatus = now.AddSeconds(5);
            WriteStatus(now);
        }
    }

    private long _eventPositionWrites;
    private void OnHostEvent(HostWindowEventKind kind)
    {
        // 回调就在 UI 线程。只采样原生几何并平移已有合成面，不排队、不读额度、不重绘像素。
        if (_disposed || _target is null || kind is not (HostWindowEventKind.LocationChange
            or HostWindowEventKind.MovesizeEnd or HostWindowEventKind.MinimizeEnd)) return;
        if (GetAncestor(GetForegroundWindow(), 2) != _target.HostWindow.Handle || IsIconic(_target.HostWindow.Handle)) return;
        if (!_sampler.TrySample(out _)) return;
        _sampler.Commit();
        if (!_sampler.TryBuildHostInfo(out var host)) return;
        var anchor = HelpMenuAnchor.Measure(host);
        var bounds = BatteryLayout.Place(anchor.Help, host.WindowBounds, host.Dpi, host.CaptionButtonBounds?.Left);
        if (bounds.IsEmpty) { _strip.Hide(); return; }
        _bounds = bounds;
        _strip.PositionAt(bounds);
        _eventPositionWrites++;
    }

    private async Task RefreshAsync()
    {
        _reading = true;
        _refresh.Enabled = false;
        var authStamp = File.GetLastWriteTimeUtc(_authPath);
        try
        {
            var quota = await Task.Run(() => _client.ReadAsync(_shutdown.Token));
            if (_disposed) return;
            if (File.GetLastWriteTimeUtc(_authPath) != authStamp)
            {
                _state.AccountChanged();
                _nextRead = DateTimeOffset.MinValue;
                return;
            }
            _state.Succeed(quota);

        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or TimeoutException or System.ComponentModel.Win32Exception or JsonException or UnauthorizedAccessException)
        {
            if (!_disposed) _state.Fail(e is TimeoutException ? "查询超时，等待自动重试" : "暂时无法读取额度，请确认 Codex 已登录并检查网络");
        }
        finally
        {
            _reading = false;
            if (!_disposed)
            {
                _refresh.Enabled = true;
                _renderKey = null;
                _nextPaint = DateTimeOffset.MinValue;
                _nextStatus = DateTimeOffset.MinValue;
                var now = DateTimeOffset.UtcNow;

            }
        }
    }

    private void WriteStatus(DateTimeOffset now)
    {
        try
        {
            var snapshot = _state.Snapshot;
            var status = new { processId = Environment.ProcessId, visible = _strip.Visible, bounds = _bounds,
                anchorSource = _anchorSource, lastUpdate = snapshot.ReadAt, error = _state.Error, stale = _state.Stale(now),
                first = snapshot.First?.Display(now) ?? "--", second = snapshot.Second?.Display(now) ?? "--",
                resetCredits = snapshot.ResetCredits, dpi = _sampler.Dpi, polling = _reading, nextRead = _nextRead,
                resetCreditText = snapshot.ResetCreditText, resetCreditExpirations = snapshot.ResetCreditExpirations,
                resetCreditTooltip = snapshot.ResetCreditTooltip,
                quotaPollingSeconds = QuotaPollingSeconds,
                updatedUtc = snapshot.ReadAt,
                shortRemaining = snapshot.First is { Minutes: 300 } first && !first.Expired(now) ? (double?)first.Remaining : null,
                weekRemaining = snapshot.Second is { Minutes: 10080 } second && !second.Expired(now) ? (double?)second.Remaining : null,
                lowQuotaAlertsEnabled = false,
                trayVisible = false,
                recoveryProcessId = _recovery.ProcessId, recovery = RecoveryCompanion.ReadStatus(),
                native = _strip.InspectNativeState(), lifecycle = "codex-owned", mcpHosted = _mcp is not null,
                displayStrategy = "mcp-owned-attached-toolwindow", version = "1.0.7", statusUpdatedUtc = now,
                positioning = new { strategy = "native-window-events", eventWrites = _eventPositionWrites,
                    hookEvents = _sampler.Events?.EventCount, hookErrors = _sampler.Events?.ErrorCount,
                    surfaceRenders = _strip.SurfaceRenders, nativeMoves = _strip.NativeMoves } };
            File.WriteAllText(Path.Combine(DataDirectory, "status.tmp"), JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(Path.Combine(DataDirectory, "status.tmp"), Path.Combine(DataDirectory, "status.json"), overwrite: true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    protected override void ExitThreadCore()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _client.Dispose();
        _recovery.Dispose();
        _timer.Stop();
        _timer.Dispose();
        _sampler.Dispose();
        _strip.ContextMenuStrip?.Dispose();
        _strip.Dispose();
        _stop.Dispose();
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
}
