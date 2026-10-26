namespace CodexStatusbar.Quota;

internal sealed class HostLifetime(DateTimeOffset startedAt)
{
    private DateTimeOffset? _lastPresent;
    public bool ShouldExit(bool hostPresent, bool connectionClosed, DateTimeOffset now)
    {
        if (connectionClosed) return true;
        if (hostPresent) { _lastPresent = now; return false; }
        return _lastPresent is { } seen ? now - seen >= TimeSpan.FromSeconds(2)
            : now - startedAt >= TimeSpan.FromSeconds(8);
    }
}
