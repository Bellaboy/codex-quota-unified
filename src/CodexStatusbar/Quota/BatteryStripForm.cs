using System.Runtime.InteropServices;

namespace CodexStatusbar.Quota;

/// <summary>Adapted from upstream TokenStripForm's premultiplied ARGB layered-window compositor.</summary>
internal sealed class BatteryStripForm : Form
{
    private readonly QuotaHoverCard _hoverCard = new();
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 300 };
    private IReadOnlyList<QuotaHoverRow> _hoverRows = Array.Empty<QuotaHoverRow>();
    private bool _hoverDark;
    private uint _hoverDpi = 96;
    private Point _hoverPoint;
    private Bitmap? _surface;
    internal long SurfaceRenders { get; private set; }
    internal long NativeMoves { get; private set; }
    internal long TooltipPopups { get; private set; }
    public BatteryStripForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Text = "Codex 额度电池条";
        AccessibleName = Text;
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (!Visible || !ClientRectangle.Contains(PointToClient(Cursor.Position))) return;
            var alreadyVisible = _hoverCard.Visible;
            _hoverCard.Present(this, _hoverRows, _hoverDark, _hoverDpi);
            if (!alreadyVisible && _hoverCard.Visible) TooltipPopups++;
        };
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000; return p; }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; }
        base.WndProc(ref message);
    }
    public void Attach(IntPtr host)
    {
        if (GetWindowLongPtr(Handle, -8) != host) SetWindowLongPtr(Handle, -8, host);
    }
    public void SetSurface(Bitmap surface, IReadOnlyList<QuotaHoverRow> rows, bool dark = false, uint dpi = 96)
    {
        SurfaceRenders++;
        var old = _surface; _surface = surface; old?.Dispose();
        _hoverDark = dark; _hoverDpi = dpi;
        UpdateTooltip(rows);
        if (IsHandleCreated) Blit();
    }
    public void UpdateTooltip(IReadOnlyList<QuotaHoverRow> rows)
    {
        if (!_hoverRows.SequenceEqual(rows)) _hoverRows = rows.ToArray();
        if (_hoverCard.Visible) _hoverCard.Present(this, _hoverRows, _hoverDark, _hoverDpi);
    }
    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e); _hoverPoint = PointToClient(Cursor.Position); _hoverTimer.Start();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_hoverCard.Visible && _hoverPoint != e.Location)
        { _hoverPoint = e.Location; _hoverTimer.Stop(); _hoverTimer.Start(); }
    }
    private void DismissHover() { _hoverTimer?.Stop(); _hoverCard?.Hide(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); DismissHover(); }
    protected override void OnMouseDown(MouseEventArgs e) { DismissHover(); base.OnMouseDown(e); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) DismissHover(); }
    protected override void OnLocationChanged(EventArgs e) { DismissHover(); base.OnLocationChanged(e); }
    public void PositionAt(IntRect rect)
    {
        var moved = Bounds != rect.ToRectangle();
        var resized = Width != rect.Width || Height != rect.Height;
        var showing = !Visible;
        if (showing) Show();
        // 跟随 MCP 所属 Codex owner 的层级；平移只复用已有表面，不抢其他应用的置顶顺序。
        if (moved && !SetWindowPos(Handle, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height, 0x0014))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (moved) NativeMoves++;
        if (showing || resized) Blit();
    }
    public object InspectNativeState()
    {
        GetWindowRect(Handle, out var rect);
        var inkPoint = new NativePoint { X = Left, Y = Top };
        var foundInk = false;
        if (_surface is not null)
        {
            for (var y = 0; y < _surface.Height && !foundInk; y++)
                for (var x = 0; x < _surface.Width; x++)
                    if (_surface.GetPixel(x, y).A >= 240)
                    { inkPoint = new NativePoint { X = Left + x, Y = Top + y }; foundInk = true; break; }
        }
        return new {
            visible = IsWindowVisible(Handle),
            actualBounds = new IntRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
            owner = GetWindowLongPtr(Handle, -8).ToInt64(),
            tooltipStyle = "codex-rounded-card", tooltipVisible = _hoverCard.Visible, tooltipPopups = TooltipPopups,
            renderedInkAtExpectedPosition = foundInk && WindowFromPoint(inkPoint) == Handle
        };
    }
    protected override void OnPaint(PaintEventArgs e) { /* Pixels are committed through UpdateLayeredWindow. */ }
    private void Blit()
    {
        if (_surface is null || !IsHandleCreated) return;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = _surface.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memory, bitmap);
        try
        {
            var destination = new NativePoint { X = Left, Y = Top };
            var origin = new NativePoint();
            var size = new NativeSize { Width = _surface.Width, Height = _surface.Height };
            var blend = new Blend { Alpha = 255, Format = 1 };
            if (!UpdateLayeredWindow(Handle, screen, ref destination, ref size, memory, ref origin, 0, ref blend, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _hoverTimer.Dispose(); _hoverCard.Dispose(); _surface?.Dispose(); }
        base.Dispose(disposing);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dc, ref NativePoint pt, ref NativeSize size, IntPtr src, ref NativePoint origin, int key, ref Blend blend, int flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
}
