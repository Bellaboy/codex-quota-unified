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

internal sealed record QuotaSnapshot(QuotaWindow? First, QuotaWindow? Second, DateTimeOffset ReadAt, int? ResetCredits,
    IReadOnlyList<DateTimeOffset?>? ResetCreditExpirations = null)
{
    public static QuotaSnapshot Empty { get; } = new(null, null, DateTimeOffset.MinValue, null);
    public string ResetCreditDisplay => ResetCredits is { } count ? count > 999 ? "999+" : count.ToString(CultureInfo.InvariantCulture) : "--";
    // 只有全部可用卡的日期已知时，才把其中一个日期标为“最早”，避免漏报的卡更早到期。
    public DateTimeOffset? EarliestCreditExpiration => ResetCredits is > 0
        && ResetCreditExpirations?.Count == ResetCredits && ResetCreditExpirations.All(x => x.HasValue)
        ? ResetCreditExpirations.Min() : null;
    internal static string CreditDate(DateTimeOffset date) => date.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    public string ResetCreditText => ResetCredits == 0 ? "重置卡 0 张"
        : $"重置卡 {ResetCreditDisplay} 张｜" + (EarliestCreditExpiration is { } expiry
            ? $"最早到期 {CreditDate(expiry)}" : "到期未知");
    public string ResetCreditTooltip
    {
        get
        {
            var lines = new List<string> { $"可用重置卡：{ResetCreditDisplay} 张" };
            if (ResetCredits == 0) return lines[0];
            var dates = (ResetCreditExpirations ?? Array.Empty<DateTimeOffset?>()).OrderBy(x => x ?? DateTimeOffset.MaxValue).ToArray();
            for (var i = 0; i < dates.Length; i++)
                lines.Add($"第 {i + 1} 张：" + (dates[i] is { } expiry ? $"{CreditDate(expiry)} 到期" : "到期未知"));
            if (ResetCredits is { } count && count > dates.Length) lines.Add($"另 {count - dates.Length} 张：到期未知");
            return string.Join("\n", lines) + "\n时间为北京时间";
        }
    }
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
        var creditData = Property(result, "rateLimitResetCredits");
        var credits = Property(creditData, "availableCount");
        var cards = Property(creditData, "credits");
        var expirations = new List<DateTimeOffset?>();
        if (cards.ValueKind == JsonValueKind.Array)
            foreach (var card in cards.EnumerateArray())
                if (Property(card, "status").ValueKind == JsonValueKind.String && Property(card, "status").GetString() == "available")
                    expirations.Add(ReadTimestamp(Property(card, "expiresAt")));
        return new(first, second, now, credits.TryInt(out var count) && count >= 0 ? count : null, expirations);
    }

    private static QuotaWindow? ReadWindow(JsonElement value)
    {
        var duration = Property(value, "windowDurationMins");
        var used = Property(value, "usedPercent");
        if (!duration.TryInt(out var minutes) || minutes <= 0 || used.ValueKind != JsonValueKind.Number
            || !used.TryGetDouble(out var percent) || !double.IsFinite(percent)) return null;
        return new(minutes, percent, ReadTimestamp(Property(value, "resetsAt")));
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement stamp)
    {
        if (stamp.ValueKind == JsonValueKind.Number && stamp.TryGetInt64(out var seconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { /* 缺失或无效的日期不能伪装成有效到期时间。 */ }
        }
        return null;
    }

    private static bool TryInt(this JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }
    private static JsonElement Property(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var found) ? found : default;
}
