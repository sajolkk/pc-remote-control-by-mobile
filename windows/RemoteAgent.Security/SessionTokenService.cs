using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Protocol;

namespace RemoteAgent.Security;

/// <summary>
/// A live session credential.
/// </summary>
/// <param name="Value">The opaque token string.</param>
/// <param name="DeviceId">The paired device it was issued to.</param>
/// <param name="ConnectionId">
/// The connection it is bound to. A token presented on any other connection is
/// rejected, which is what makes a captured token useless on its own (§7.2).
/// </param>
/// <param name="Permissions">Permission snapshot at issue time.</param>
/// <param name="IssuedAt">When it was issued.</param>
/// <param name="ExpiresAt">When it stops being valid.</param>
public sealed record SessionToken(
    string Value,
    string DeviceId,
    string ConnectionId,
    Permission Permissions,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

/// <summary>Issues and validates short-lived session tokens (§7.2).</summary>
public interface ISessionTokenService
{
    /// <summary>Issues a token bound to one device and one connection.</summary>
    SessionToken Issue(string deviceId, string connectionId, Permission permissions);

    /// <summary>
    /// Validates a token against the connection presenting it. Returns false with a
    /// reason for logging when invalid, expired, or bound elsewhere.
    /// </summary>
    bool TryValidate(string? token, string connectionId, out SessionToken? session, out string? failureReason);

    /// <summary>
    /// Replaces a connection's token, invalidating the previous one immediately so
    /// that a renewed session never leaves two usable credentials in circulation.
    /// </summary>
    SessionToken Renew(SessionToken current, Permission permissions);

    /// <summary>
    /// Invalidates every token for a device. Called the instant a pairing is revoked
    /// or permissions change (§8.3).
    /// </summary>
    int RevokeDevice(string deviceId);

    /// <summary>Invalidates the tokens for one connection, on disconnect.</summary>
    int RevokeConnection(string connectionId);

    /// <summary>Drops expired entries. Called periodically by the host.</summary>
    int PurgeExpired();
}

/// <summary>
/// In-memory token service.
/// </summary>
/// <remarks>
/// Tokens are deliberately not persisted: a service restart should invalidate every
/// session, because the sessions belonged to connections that no longer exist. That
/// also means there is no token material on disk to steal.
/// <para>
/// Token values are 32 bytes of CSPRNG output, so guessing one is not a practical
/// attack and no rate limiting is needed on validation itself. The value is used as
/// a dictionary key; that lookup is not constant time, but with 256 bits of entropy
/// there is no timing signal an attacker could exploit to narrow a search.
/// </para>
/// </remarks>
public sealed class SessionTokenService : ISessionTokenService
{
    private const int TokenBytes = 32;

    private readonly ConcurrentDictionary<string, SessionToken> _tokens = new(StringComparer.Ordinal);
    private readonly IClock _clock;
    private readonly ILogger<SessionTokenService> _logger;
    private readonly TimeSpan _lifetime;

    /// <summary>Creates the service.</summary>
    /// <param name="clock">Injected clock, so expiry is testable.</param>
    /// <param name="logger">Diagnostic sink.</param>
    /// <param name="lifetimeSeconds">Token lifetime.</param>
    public SessionTokenService(IClock clock, ILogger<SessionTokenService> logger, int lifetimeSeconds)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lifetime = TimeSpan.FromSeconds(Math.Clamp(lifetimeSeconds, 60, 3600));
    }

    /// <summary>Token lifetime in seconds, reported to clients so they renew in time.</summary>
    public int LifetimeSeconds => (int)_lifetime.TotalSeconds;

    /// <inheritdoc />
    public SessionToken Issue(string deviceId, string connectionId, Permission permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        DateTimeOffset now = _clock.UtcNow;
        var token = new SessionToken(
            Base64Url.GenerateToken(TokenBytes),
            deviceId,
            connectionId,
            permissions,
            now,
            now.Add(_lifetime));

        _tokens[token.Value] = token;

        // The token value itself is never logged (§7.5) — only a short opaque
        // reference derived from it, which is enough to correlate log lines.
        _logger.LogDebug(
            "Issued session token. Device={DeviceId} Connection={ConnectionId} Ref={TokenRef} ExpiresAt={ExpiresAt}",
            deviceId,
            connectionId,
            Reference(token.Value),
            token.ExpiresAt);

        return token;
    }

    /// <inheritdoc />
    public bool TryValidate(string? token, string connectionId, out SessionToken? session, out string? failureReason)
    {
        session = null;

        if (string.IsNullOrEmpty(token))
        {
            failureReason = "no token presented";
            return false;
        }

        if (!_tokens.TryGetValue(token, out SessionToken? found))
        {
            failureReason = "token unknown or already invalidated";
            return false;
        }

        if (found.ExpiresAt <= _clock.UtcNow)
        {
            _tokens.TryRemove(token, out _);
            failureReason = "token expired";
            return false;
        }

        if (!string.Equals(found.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            // A token valid for a different connection: either a client bug or an
            // attempt to reuse a captured credential. Logged as a security event.
            _logger.LogWarning(
                "Session token presented on the wrong connection. Device={DeviceId} " +
                "BoundTo={BoundConnectionId} PresentedOn={ConnectionId}",
                found.DeviceId,
                found.ConnectionId,
                connectionId);

            failureReason = "token is bound to a different connection";
            return false;
        }

        session = found;
        failureReason = null;
        return true;
    }

    /// <inheritdoc />
    public SessionToken Renew(SessionToken current, Permission permissions)
    {
        ArgumentNullException.ThrowIfNull(current);

        _tokens.TryRemove(current.Value, out _);
        return Issue(current.DeviceId, current.ConnectionId, permissions);
    }

    /// <inheritdoc />
    public int RevokeDevice(string deviceId)
    {
        int removed = RemoveWhere(t => string.Equals(t.DeviceId, deviceId, StringComparison.Ordinal));
        if (removed > 0)
        {
            _logger.LogInformation("Invalidated {Count} session token(s) for device {DeviceId}.", removed, deviceId);
        }

        return removed;
    }

    /// <inheritdoc />
    public int RevokeConnection(string connectionId) =>
        RemoveWhere(t => string.Equals(t.ConnectionId, connectionId, StringComparison.Ordinal));

    /// <inheritdoc />
    public int PurgeExpired()
    {
        DateTimeOffset now = _clock.UtcNow;
        return RemoveWhere(t => t.ExpiresAt <= now);
    }

    private int RemoveWhere(Func<SessionToken, bool> predicate)
    {
        int removed = 0;
        foreach (KeyValuePair<string, SessionToken> entry in _tokens)
        {
            if (predicate(entry.Value) && _tokens.TryRemove(entry.Key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// A short, non-reversible reference to a token, safe for logs. Truncating a
    /// hash rather than the token itself means a log line can never be replayed.
    /// </summary>
    private static string Reference(string token)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token));
        return Convert.ToHexString(hash.AsSpan(0, 4));
    }
}
