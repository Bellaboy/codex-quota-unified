using System.Globalization;
using System.Text.Json;

namespace CodexStatusbar.Quota;

internal sealed record QuotaWindow(int Minutes, double UsedPercent, DateTimeOffset? ResetsAt)
{
    public double Remaining => Math.Clamp(100 - UsedPercent, 0, 100);
    public string Label => Minutes == 10080 ? "周" : Minutes % 60 == 0 ? $"{Minutes / 60}h" : $"{Minutes}m";
    public bool Expired(DateTimeOffset now) => ResetsAt is { } reset && reset <= now;
    public string Display(DateTimeOffset now) => Expired(now) ? "--" : $"{Math.Floor(Remaining).ToString(CultureInfo.InvariantCulture)}%";
}

internal sealed record QuotaSnapshot(QuotaWindow? First, QuotaWindow? Second, DateTimeOffset ReadAt, int? ResetCredits)
{
    public static QuotaSnapshot Empty { get; } = new(null, null, DateTimeOffset.MinValue, null);
    public string ResetCreditDisplay => ResetCredits is { } count ? count > 999 ? "999+" : count.ToString(CultureInfo.InvariantCulture) : "--";
}

internal static class QuotaParser
{
    public static QuotaSnapshot Parse(JsonElement result, DateTimeOffset now)
    {
        var bucket = Property(result, "rateLimits");
        var map = Property(result, "rateLimitsByLimitId");
        if (map.ValueKind == JsonValueKind.Object)
        {
            var account = Property(map, "codex");
            // An explicit multi-bucket map is authoritative. Do not substitute another model pool.
            bucket = account.ValueKind == JsonValueKind.Object ? account : default;
        }
        var windows = new[] { ReadWindow(Property(bucket, "primary")), ReadWindow(Property(bucket, "secondary")) }
            .OfType<QuotaWindow>().OrderBy(w => w.Minutes).ToArray();
        var first = windows.FirstOrDefault(w => w.Minutes != 10080);
        var second = windows.FirstOrDefault(w => w.Minutes == 10080)
            ?? windows.FirstOrDefault(w => !ReferenceEquals(w, first));
        var credits = Property(Property(result, "rateLimitResetCredits"), "availableCount");
        return new(first, second, now, credits.TryInt(out var count) && count >= 0 ? count : null);
    }

    private static QuotaWindow? ReadWindow(JsonElement value)
    {
        var duration = Property(value, "windowDurationMins");
        var used = Property(value, "usedPercent");
        if (!duration.TryInt(out var minutes) || minutes <= 0 || used.ValueKind != JsonValueKind.Number
            || !used.TryGetDouble(out var percent) || !double.IsFinite(percent)) return null;
        DateTimeOffset? reset = null;
        var stamp = Property(value, "resetsAt");
        if (stamp.ValueKind == JsonValueKind.Number && stamp.TryGetInt64(out var seconds))
        {
            try { reset = DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { /* Preserve usable percentage without an invalid timestamp. */ }
        }
        return new(minutes, percent, reset);
    }

    private static bool TryInt(this JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }
    private static JsonElement Property(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var found) ? found : default;
}
