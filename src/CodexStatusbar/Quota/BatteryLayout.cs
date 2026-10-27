namespace CodexStatusbar.Quota;

internal static class BatteryLayout
{
    public const int WidthDip = 276;
    public const int HeightDip = 26;
    public static IntRect Place(IntRect help, IntRect host, uint dpi, int? captionLeft = null)
    {
        if (help.IsEmpty || host.IsEmpty) return default;
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        int S(int dip) => (int)Math.Round(dip * scale);
        var x = help.Right + S(12);
        var width = S(WidthDip);
        var limit = captionLeft ?? host.Right - S(146);
        if (x + width + S(12) > limit) return default;
        return new(x, help.Top + (help.Height - S(HeightDip)) / 2, width, S(HeightDip));
    }
    public static Color LevelColor(double? remaining) => remaining is null ? Color.FromArgb(126, 137, 150)
        : remaining <= 10 ? Color.FromArgb(225, 65, 71)
        : remaining <= 20 ? Color.FromArgb(229, 157, 35) : Color.FromArgb(38, 173, 105);
    public static float FillWidth(double? remaining, float interior) =>
        remaining is { } amount && double.IsFinite(amount) ? interior * (float)Math.Clamp(amount / 100, 0, 1) : 0;
}
