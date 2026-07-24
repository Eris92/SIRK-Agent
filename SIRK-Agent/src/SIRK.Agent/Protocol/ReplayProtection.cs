using System.Collections.Concurrent;

namespace Sirk.Agent.Protocol;

internal sealed class ReplayProtection
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> entries = new(StringComparer.Ordinal);
    private long operationCount;

    public bool TryRegister(string nonce, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        CleanupExpiredEntriesPeriodically(now);
        return entries.TryAdd(nonce, expiresAt);
    }

    private void CleanupExpiredEntriesPeriodically(DateTimeOffset now)
    {
        long current = Interlocked.Increment(ref operationCount);
        if (current % 256 != 0)
        {
            return;
        }

        foreach ((string nonce, DateTimeOffset expiresAt) in entries)
        {
            if (expiresAt <= now)
            {
                entries.TryRemove(nonce, out _);
            }
        }
    }
}
