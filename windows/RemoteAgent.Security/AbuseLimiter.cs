using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;

namespace RemoteAgent.Security;

/// <summary>
/// Tracks failed authentication and pairing attempts per source address, and blocks
/// addresses that cross the threshold (§7.3).
/// </summary>
/// <remarks>
/// The address is used only to decide <em>how much work to do for a stranger</em> —
/// never to decide who someone is. That distinction matters: IP-based blocking is
/// trivially evaded on a LAN, so this is a cost-imposition measure against
/// brute-force and connection floods, not an authentication mechanism.
/// <para>
/// A sliding window is not used: a fixed decay is enough here and keeps the
/// structure small and allocation-free per attempt.
/// </para>
/// </remarks>
public sealed class AbuseLimiter
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly IClock _clock;
    private readonly ILogger<AbuseLimiter> _logger;
    private readonly int _maxFailures;
    private readonly TimeSpan _blockDuration;

    /// <summary>Creates the limiter.</summary>
    /// <param name="clock">Injected clock.</param>
    /// <param name="logger">Diagnostic sink.</param>
    /// <param name="maxFailures">Failures tolerated before a block.</param>
    /// <param name="blockMinutes">How long a block lasts.</param>
    public AbuseLimiter(IClock clock, ILogger<AbuseLimiter> logger, int maxFailures, int blockMinutes)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxFailures = Math.Clamp(maxFailures, 1, 100);
        _blockDuration = TimeSpan.FromMinutes(Math.Clamp(blockMinutes, 1, 1440));
    }

    /// <summary>Whether this address is currently blocked.</summary>
    public bool IsBlocked(string address)
    {
        if (string.IsNullOrEmpty(address) || !_entries.TryGetValue(address, out Entry? entry))
        {
            return false;
        }

        lock (entry)
        {
            if (entry.BlockedUntil is null)
            {
                return false;
            }

            if (entry.BlockedUntil <= _clock.UtcNow)
            {
                // Block expired: reset so the address starts from a clean slate
                // rather than being one failure away from another block forever.
                entry.BlockedUntil = null;
                entry.Failures = 0;
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Records a failure. Returns true when this failure triggered a block, so the
    /// caller can log that as a distinct security event.
    /// </summary>
    public bool RecordFailure(string address, string reason)
    {
        if (string.IsNullOrEmpty(address))
        {
            return false;
        }

        Entry entry = _entries.GetOrAdd(address, static _ => new Entry());
        lock (entry)
        {
            entry.Failures++;
            entry.LastFailureAt = _clock.UtcNow;

            if (entry.Failures < _maxFailures)
            {
                _logger.LogWarning(
                    "Authentication failure from {Address}: {Reason}. Failures={Failures}/{Max}",
                    address,
                    reason,
                    entry.Failures,
                    _maxFailures);
                return false;
            }

            entry.BlockedUntil = _clock.UtcNow.Add(_blockDuration);
            _logger.LogWarning(
                "Blocking {Address} for {Minutes} minute(s) after {Failures} failed attempts. Last reason: {Reason}",
                address,
                _blockDuration.TotalMinutes,
                entry.Failures,
                reason);
            return true;
        }
    }

    /// <summary>Clears an address's failure count after a success.</summary>
    public void RecordSuccess(string address)
    {
        if (!string.IsNullOrEmpty(address))
        {
            _entries.TryRemove(address, out _);
        }
    }

    /// <summary>Current failure count for an address, for diagnostics and the UI.</summary>
    public int GetFailureCount(string address) =>
        _entries.TryGetValue(address, out Entry? entry) ? entry.Failures : 0;

    /// <summary>Drops entries that have decayed, to bound memory.</summary>
    public int Purge()
    {
        DateTimeOffset cutoff = _clock.UtcNow - _blockDuration;
        int removed = 0;

        foreach (KeyValuePair<string, Entry> pair in _entries)
        {
            Entry entry = pair.Value;
            bool stale;
            lock (entry)
            {
                stale = entry.BlockedUntil is null && entry.LastFailureAt < cutoff;
            }

            if (stale && _entries.TryRemove(pair.Key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    private sealed class Entry
    {
        public int Failures { get; set; }

        public DateTimeOffset LastFailureAt { get; set; }

        public DateTimeOffset? BlockedUntil { get; set; }
    }
}

/// <summary>
/// A token-bucket rate limiter, one instance per connection (§7.3).
/// </summary>
/// <remarks>
/// Sized generously because the input path legitimately sends thousands of messages
/// a minute while someone is dragging the mouse. Its purpose is to bound the damage
/// from a runaway or malicious client, not to shape normal traffic.
/// </remarks>
public sealed class RequestRateLimiter
{
    private readonly double _permitsPerSecond;
    private readonly double _burstCapacity;
    private readonly IClock _clock;
    private readonly object _sync = new();

    private double _available;
    private DateTimeOffset _lastRefill;

    /// <summary>Creates a limiter.</summary>
    /// <param name="clock">Injected clock.</param>
    /// <param name="requestsPerMinute">Sustained rate.</param>
    public RequestRateLimiter(IClock clock, int requestsPerMinute)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _permitsPerSecond = Math.Max(1, requestsPerMinute) / 60.0;

        // One second of burst headroom, so a batch of input events at the start of a
        // gesture is never throttled.
        _burstCapacity = Math.Max(_permitsPerSecond, 50);
        _available = _burstCapacity;
        _lastRefill = clock.UtcNow;
    }

    /// <summary>Consumes one permit, or returns false when the budget is exhausted.</summary>
    public bool TryAcquire()
    {
        lock (_sync)
        {
            DateTimeOffset now = _clock.UtcNow;
            double elapsedSeconds = (now - _lastRefill).TotalSeconds;
            if (elapsedSeconds > 0)
            {
                _available = Math.Min(_burstCapacity, _available + (elapsedSeconds * _permitsPerSecond));
                _lastRefill = now;
            }

            if (_available < 1)
            {
                return false;
            }

            _available -= 1;
            return true;
        }
    }
}
