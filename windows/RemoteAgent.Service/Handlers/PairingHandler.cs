using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Commands;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Security;
using RemoteAgent.Service.Network;

namespace RemoteAgent.Service.Handlers;

/// <summary>
/// The only command reachable from an unpaired connection (§8.1).
/// </summary>
/// <remarks>
/// <para>Everything protective about pairing lives in <see cref="PairingService"/>; this
/// handler's job is to supply the one thing only the transport layer knows — the certificate
/// the peer actually presented — and to translate the outcome into a wire response.</para>
///
/// <para>That fingerprint is the important input. The request also <em>claims</em> a
/// fingerprint, and the two must match: a device that relays someone else's pairing request
/// cannot also present their private key, so the mismatch is what closes the relay attack.</para>
///
/// <para>Note what this handler does not do: it never decides whether pairing is allowed, never
/// compares the token, and never skips the approval prompt. Keeping those decisions in one
/// service means they can be reasoned about — and tested — in one place.</para>
/// </remarks>
public sealed class PairRequestHandler : ICommandHandler
{
    private readonly IPairingService _pairing;
    private readonly ClientSessionManager _sessions;
    private readonly ILogger<PairRequestHandler> _logger;

    /// <summary>Creates the handler.</summary>
    public PairRequestHandler(
        IPairingService pairing,
        ClientSessionManager sessions,
        ILogger<PairRequestHandler> logger)
    {
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Command => CommandNames.PairRequest;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out PairRequestArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        if (!_sessions.TryGet(context.Caller.ConnectionId, out ClientSession session))
        {
            return CommandResult.Fail(
                ErrorCodes.Internal,
                "The connection was closed during pairing.",
                retryable: true);
        }

        if (session.Device is not null)
        {
            // Already paired on this connection. Harmless, but worth answering clearly rather
            // than burning the pairing token on a no-op.
            return CommandResult.Fail(
                ErrorCodes.InvalidArguments,
                "This device is already paired with the PC.");
        }

        PairingAttemptResult result = await _pairing
            .TryPairAsync(args, session.PeerFingerprint, session.RemoteAddress, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded || result.Device is null)
        {
            return CommandResult.Fail(
                PairingService.ToErrorCode(result.Outcome),
                PairingService.ToMessage(result.Outcome));
        }

        _logger.LogInformation(
            "Pairing completed for device {DeviceId} on connection {ConnectionId}. " +
            "The client must now reconnect to authenticate.",
            result.Device.DeviceId,
            context.Caller.ConnectionId);

        return CommandResult.Success(new PairResult
        {
            DeviceId = PairingIdentity.DeviceId,
            DeviceName = PairingIdentity.DeviceName,
            CertificateFingerprint = PairingIdentity.Fingerprint,
            Permissions = PermissionSet.ToNames(result.Device.Permissions),
            ProtocolVersion = ProtocolVersion.Current,
        });
    }
}

/// <summary>
/// The PC's own identity as advertised during pairing.
/// </summary>
/// <remarks>
/// Populated once at startup rather than read per request: the device id and fingerprint are
/// fixed for the process lifetime, and threading them through every handler constructor added
/// noise without adding clarity. Set by the host during initialization.
/// </remarks>
public static class PairingIdentity
{
    /// <summary>This PC's device id.</summary>
    public static string DeviceId { get; private set; } = string.Empty;

    /// <summary>This PC's display name.</summary>
    public static string DeviceName { get; private set; } = string.Empty;

    /// <summary>This PC's certificate fingerprint, which clients pin.</summary>
    public static string Fingerprint { get; private set; } = string.Empty;

    /// <summary>Called once by the host after the identity is loaded.</summary>
    public static void Initialize(string deviceId, string deviceName, string fingerprint)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        Fingerprint = fingerprint;
    }
}
