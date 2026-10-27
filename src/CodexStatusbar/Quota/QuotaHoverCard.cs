using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace CodexStatusbar.Quota;

/// <summary>额度条的非激活悬停卡片；只绘制刷新/到期时间，浅深色跟随现有额度条。</summary>
internal sealed class QuotaHoverCard : Form
{
    private Bitmap? _surface;
    private IReadOnlyList<QuotaHoverRow> _rows = Array.Empty<QuotaHoverRow>();
    private bool _dark;
    private uint _dpi = 96;
    internal const int WidthDip = 280;

    public QuotaHoverCard()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        Text = "额度刷新与重置卡到期时间";
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000080; p.ClassStyle |= 0x00020000; return p; }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; }
        base.WndProc(ref message);
    }

    public void Present(Form anchor, IReadOnlyList<QuotaHoverRow> rows, bool dark, uint dpi)
    {
        if (rows.Count == 0) return;
        if (!_rows.SequenceEqual(rows) || _dark != dark || _dpi != dpi || _surface is null)
        {
            _rows = rows.ToArray(); _dark = dark; _dpi = dpi;
            _surface?.Dispose();
            _surface = Render(rows, dark, dpi);
            Size = _surface.Size;
            using var rounded = Rounded(new RectangleF(0, 0, Width, Height), 12 * dpi / 96f);
            var old = Region; Region = new Region(rounded); old?.Dispose();
        }
        var work = Screen.FromRectangle(anchor.Bounds).WorkingArea;
        var gap = (int)Math.Round(8 * dpi / 96d);
        var x = Math.Clamp(anchor.Left, work.Left, Math.Max(work.Left, work.Right - Width));
        var y = anchor.Bottom + gap;
        if (y + Height > work.Bottom) y = Math.Max(work.Top, anchor.Top - Height - gap);
        Location = new Point(x, y);
        if (!Visible) Show(anchor);
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (_surface is not null) e.Graphics.DrawImageUnscaled(_surface, 0, 0);
    }
    internal static Bitmap Render(IReadOnlyList<QuotaHoverRow> rows, bool dark, uint dpi)
    {
        var scale = (dpi == 0 ? 96 : dpi) / 96f;
        var height = 24 + rows.Count * 28 + rows.Count(x => x.SeparatorBefore) * 8;
        var bitmap = new Bitmap((int)Math.Round(WidthDip * scale), (int)Math.Round(height * scale), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent); g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var shape = Rounded(new RectangleF(.5f, .5f, WidthDip - 1, height - 1), 12);
        using var background = new SolidBrush(dark ? Color.FromArgb(38, 38, 38) : Color.FromArgb(250, 250, 250));
        using var border = new Pen(dark ? Color.FromArgb(65, 65, 65) : Color.FromArgb(222, 222, 222));
        using var label = new SolidBrush(dark ? Color.FromArgb(166, 166, 166) : Color.FromArgb(112, 112, 112));
        using var value = new SolidBrush(dark ? Color.FromArgb(238, 238, 238) : Color.FromArgb(35, 35, 35));
        using var font = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
        using var left = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.FillPath(background, shape); g.DrawPath(border, shape);
        var y = 12f;
        foreach (var row in rows)
        {
            if (row.SeparatorBefore) { g.DrawLine(border, 16, y + 3, WidthDip - 16, y + 3); y += 8; }
            g.DrawString(row.Label, font, label, new RectangleF(16, y, 118, 28), left);
            g.DrawString(row.Value, font, value, new RectangleF(138, y, WidthDip - 154, 28), right);
            y += 28;
        }
        return bitmap;
    }
    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath(); var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90); path.AddArc(rect.Right-d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right-d, rect.Bottom-d, d, d, 0, 90); path.AddArc(rect.X, rect.Bottom-d, d, d, 90, 90); path.CloseFigure();
        return path;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _surface?.Dispose();
        base.Dispose(disposing);
    }
}
