namespace RemoteAgent.Core.Abstractions;

/// <summary>
/// The current time, injectable so that token expiry, replay windows and pairing
/// timeouts can be tested without sleeping.
/// </summary>
public interface IClock
{
    /// <summary>Current UTC time.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Current UTC time as Unix milliseconds, the wire representation.</summary>
    long UnixTimeMilliseconds { get; }
}

/// <summary>The real system clock.</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public long UnixTimeMilliseconds => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
