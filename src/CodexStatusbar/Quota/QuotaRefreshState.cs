namespace CodexStatusbar.Quota;

internal sealed class QuotaRefreshState
{
    public QuotaSnapshot Snapshot { get; private set; } = QuotaSnapshot.Empty;
    public string? Error { get; private set; }
    public void Succeed(QuotaSnapshot snapshot) { Snapshot = snapshot; Error = null; }
    public void Fail(string error) { Error = error; }
    public bool Stale(DateTimeOffset now) => Error is not null
        || Snapshot.ReadAt != DateTimeOffset.MinValue && now - Snapshot.ReadAt > TimeSpan.FromMinutes(3)
        || Snapshot.First?.Expired(now) == true || Snapshot.Second?.Expired(now) == true;
}
