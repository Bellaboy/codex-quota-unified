using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace CodexStatusbar.Quota;

internal static class BatteryRenderer
{
    public static Bitmap Render(QuotaSnapshot quota, DateTimeOffset now, uint dpi, bool dark, bool stale)
    {
        var scale = (dpi == 0 ? 96 : dpi) / 96f;
        var bitmap = new Bitmap((int)Math.Round(BatteryLayout.WidthDip * scale),
            (int)Math.Round(BatteryLayout.HeightDip * scale), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        // 分层窗口的 alpha=0 区域会鼠标穿透；1/255 的底层让文字间隙也能接收悬停。
        g.Clear(Color.FromArgb(1, 0, 0, 0));
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var ink = dark ? Color.FromArgb(229, 234, 240) : Color.FromArgb(48, 59, 71);
        var readIsOld = stale || quota.ReadAt != DateTimeOffset.MinValue && now - quota.ReadAt > TimeSpan.FromMinutes(3);
        DrawBattery(g, quota.First, "5h", 0, now, readIsOld, ink, font, valueFont, format);
        DrawBattery(g, quota.Second, "周", 96, now, readIsOld, ink, font, valueFont, format);
        using var creditInk = new SolidBrush(ink);
        using var creditFormat = new StringFormat { Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(quota.ResetCreditText, font, creditInk, new RectangleF(191, 0, BatteryLayout.WidthDip - 195, 26), creditFormat);
        return bitmap;
    }

    private static void DrawBattery(Graphics g, QuotaWindow? window, string fallbackLabel, float x,
        DateTimeOffset now, bool stale, Color ink, Font font, Font valueFont, StringFormat format)
    {
        using var text = new SolidBrush(ink);
        var expired = window?.Expired(now) == true;
        var known = window is not null && !expired;
        var level = known ? window!.Remaining : (double?)null;
        var color = BatteryLayout.LevelColor(level);
        g.DrawString(window?.Label ?? fallbackLabel, font, text, new RectangleF(x, 0, 23, 26), format);
        var body = new RectangleF(x + 26, 3.5f, 55, 19);
        using var outline = Rounded(body, 4);
        using var pen = new Pen(stale ? Color.FromArgb(145, ink) : Color.FromArgb(190, ink), 1.1f);
        if (stale) pen.DashStyle = DashStyle.Dash;
        g.DrawPath(pen, outline);
        using var nub = new SolidBrush(Color.FromArgb(190, ink));
        g.FillRectangle(nub, body.Right + 1.7f, 9.5f, 2.5f, 7);
        var interior = RectangleF.Inflate(body, -2.4f, -2.4f);
        using var clip = Rounded(interior, 2);
        var saved = g.Save();
        g.SetClip(clip);
        using var fill = new SolidBrush(Color.FromArgb(stale ? 110 : 155, color));
        g.FillRectangle(fill, interior.X, interior.Y, BatteryLayout.FillWidth(level, interior.Width), interior.Height);
        g.Restore(saved);
        var value = known ? window!.Display(now) : "--";
        g.DrawString(value, valueFont, text, body, format);
        // Expired/failed data is visible even with mouse hover unavailable.
        if (stale || expired)
        {
            using var warning = new SolidBrush(Color.FromArgb(229, 157, 35));
            g.FillEllipse(warning, x + 86, 10, 4, 4);
        }
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void ExportSamples(string directory)
    {
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        QuotaSnapshot Sample(double first, double second) => new(new(300, 100 - first, now.AddHours(3)), new(10080, 100 - second, now.AddDays(4)), now, 2,
            new DateTimeOffset?[] { now.AddDays(1).AddMinutes(10), now.AddDays(8).AddMinutes(30) });
        using var sheet = new Bitmap(1030, 350);
        using var canvas = Graphics.FromImage(sheet);
        canvas.Clear(Color.White);
        using var title = new Font("Microsoft YaHei UI", 10);
        var samples = new[] {
            ("正常 · 透明背景", Sample(85, 95), false, false),
            ("低额度 · 橙色 / 红色", Sample(20, 10), false, false),
            ("深色模式", Sample(73, 94), true, false),
            ("数据未知 / 刷新失败", QuotaSnapshot.Empty, false, true),
            ("无可用卡", new QuotaSnapshot(null, null, now, 0), false, false)
        };
        for (var i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            if (s.Item3) canvas.FillRectangle(Brushes.DarkSlateGray, 0, i * 70, sheet.Width, 70);
            canvas.DrawString(s.Item1, title, s.Item3 ? Brushes.White : Brushes.DimGray, 15, i * 70 + 25);
            using var battery = Render(s.Item2, now, 144, s.Item3, s.Item4);
            battery.Save(Path.Combine(directory, $"battery-{i}.png"));
            canvas.DrawImageUnscaled(battery, 290, i * 70 + 15);
        }
        sheet.Save(Path.Combine(directory, "battery-preview.png"));
    }
}
