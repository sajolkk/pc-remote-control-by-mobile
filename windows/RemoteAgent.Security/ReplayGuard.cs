namespace RemoteAgent.Security;

/// <summary>
/// Why a request was rejected by the replay guard.
/// </summary>
public enum ReplayVerdict
{
    /// <summary>The request is fresh and in order.</summary>
    Accepted,

    /// <summary>The sequence number repeated or went backwards: a replay.</summary>
    SequenceRepeated,

    /// <summary>The sequence number skipped ahead: a frame was injected or dropped.</summary>
    SequenceGap,

    /// <summary>The timestamp is too far from the PC's clock.</summary>
    TimestampOutOfWindow,
}

/// <summary>
/// Per-connection replay and injection defence (§6.2).
/// </summary>
/// <remarks>
/// TLS already guarantees ordering, integrity and freshness within a connection, so
/// this is defence in depth rather than the primary protection. It earns its place
/// by catching two things TLS alone does not make obvious:
/// <list type="bullet">
/// <item>an application-layer bug or a proxy that duplicates or reorders messages;</item>
/// <item>a captured request replayed on a <em>new</em> connection — the sequence
/// restarts there, so the token binding in <see cref="SessionTokenService"/> is what
/// stops that, and this guard makes the two mechanisms independent.</item>
/// </list>
/// <para>
/// Sequence numbers must increase by exactly one. Exact increments are enforceable
/// precisely because the transport is ordered and reliable, and they turn a silent
/// gap into an immediate, loud failure.
/// </para>
/// <para>
/// Instances are not thread-safe by design: one guard belongs to one connection,
/// which processes its requests sequentially.
/// </para>
/// </remarks>
public sealed class ReplayGuard
{
    private readonly long _maxSkewMs;
    private long? _lastSequence;

    /// <summary>Creates a guard for one connection.</summary>
    /// <param name="maxClockSkewSeconds">Accepted difference between the peer's clock and ours.</param>
    public ReplayGuard(int maxClockSkewSeconds)
    {
        _maxSkewMs = Math.Clamp(maxClockSkewSeconds, 5, 300) * 1000L;
    }

    /// <summary>The highest sequence number accepted so far, or null before the first request.</summary>
    public long? LastSequence => _lastSequence;

    /// <summary>
    /// Evaluates one request, updating state only when it is accepted.
    /// </summary>
    /// <param name="sequence">The request's sequence number.</param>
    /// <param name="timestampMs">The request's timestamp, Unix milliseconds.</param>
    /// <param name="nowMs">The PC's current time, Unix milliseconds.</param>
    public ReplayVerdict Evaluate(long sequence, long timestampMs, long nowMs)
    {
        long skew = Math.Abs(nowMs - timestampMs);
        if (skew > _maxSkewMs)
        {
            return ReplayVerdict.TimestampOutOfWindow;
        }

        if (_lastSequence is null)
        {
            // The first request of a session may start at any positive number: a
            // client is free to keep a global counter across reconnects. Only the
            // progression within this connection is constrained.
            if (sequence < 1)
            {
                return ReplayVerdict.SequenceRepeated;
            }

            _lastSequence = sequence;
            return ReplayVerdict.Accepted;
        }

        if (sequence <= _lastSequence.Value)
        {
            return ReplayVerdict.SequenceRepeated;
        }

        if (sequence != _lastSequence.Value + 1)
        {
            return ReplayVerdict.SequenceGap;
        }

        _lastSequence = sequence;
        return ReplayVerdict.Accepted;
    }

    /// <summary>A displayable reason for a rejection, for logs and error messages.</summary>
    public static string Describe(ReplayVerdict verdict) => verdict switch
    {
        ReplayVerdict.Accepted => "accepted",
        ReplayVerdict.SequenceRepeated => "the request sequence number repeated or went backwards",
        ReplayVerdict.SequenceGap => "the request sequence number skipped a value",
        ReplayVerdict.TimestampOutOfWindow =>
            "the request timestamp is outside the accepted window; check the device's clock",
        _ => "rejected",
    };
}
