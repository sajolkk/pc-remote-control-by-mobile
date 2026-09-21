using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Security;
using RemoteAgent.Service.Network;

namespace RemoteAgent.Service.Handlers;

/// <summary>
/// Latency probe and liveness check.
/// </summary>
/// <remarks>
/// Available to any paired device before the handshake, because a client needs a way to
/// confirm the PC is answering before it commits to a full handshake, and because the mobile
/// app measures control-channel round-trip time with it.
/// </remarks>
public sealed class PingHandler : ICommandHandler
{
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public PingHandler(IClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public string Command => CommandNames.Ping;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken) =>
        Task.FromResult(CommandResult.Success(new { ts = _clock.UnixTimeMilliseconds }));
}

/// <summary>
/// Completes the connection handshake: negotiates the protocol version, issues a session
/// token, and tells the client what this PC can actually do (§1.4).
/// </summary>
/// <remarks>
/// <para>This is the command that makes one mobile build work against any PC. The response
/// carries the capability list and the permission set, and the client renders its UI from
/// those rather than from assumptions — so a PC with one monitor, no Store apps and no
/// Wake-on-LAN presents a smaller but entirely working interface (§0).</para>
///
/// <para>The token it issues is bound to this specific connection, which is why the handler
/// needs the session manager: a token handed out without that binding would be a bearer
/// credential usable from anywhere (§7.2).</para>
/// </remarks>
public sealed class HelloHandler : ICommandHandler
{
    private readonly ClientSessionManager _sessions;
    private readonly ISessionTokenService _tokens;
    private readonly IPairingStore _pairingStore;
    private readonly ICapabilityProvider _capabilities;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<HelloHandler> _logger;

    /// <summary>Creates the handler.</summary>
    public HelloHandler(
        ClientSessionManager sessions,
        ISessionTokenService tokens,
        IPairingStore pairingStore,
        ICapabilityProvider capabilities,
        IOptionsMonitor<AgentOptions> options,
        ILogger<HelloHandler> logger)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _pairingStore = pairingStore ?? throw new ArgumentNullException(nameof(pairingStore));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Command => CommandNames.Hello;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out HelloArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        int? agreed = ProtocolVersion.Negotiate(args.MinVersion, args.MaxVersion);
        if (agreed is null)
        {
            _logger.LogWarning(
                "Rejected a handshake from device {DeviceId}: it speaks protocol {Min}-{Max}, " +
                "this PC speaks {OurMin}-{OurMax}.",
                context.Caller.DeviceId,
                args.MinVersion,
                args.MaxVersion,
                ProtocolVersion.MinSupported,
                ProtocolVersion.Current);

            return CommandResult.Fail(
                ErrorCodes.VersionUnsupported,
                $"This PC speaks protocol versions {ProtocolVersion.MinSupported}-{ProtocolVersion.Current}. " +
                "Update the app or the PC agent.");
        }

        string? deviceId = context.Caller.DeviceId;
        if (deviceId is null)
        {
            return CommandResult.Fail(ErrorCodes.NotPaired, "This device is not paired with the PC.");
        }

        PairedDevice? device = await _pairingStore.FindByDeviceIdAsync(deviceId, cancellationToken)
            .ConfigureAwait(false);

        if (device is null)
        {
            // The pairing was revoked between the TLS handshake and this command.
            return CommandResult.Fail(ErrorCodes.NotPaired, "This device's pairing was revoked.");
        }

        // A device may rename itself at each handshake: the phone's name is the phone's to
        // choose, and keeping it current makes the Paired Devices page useful.
        if (!string.IsNullOrWhiteSpace(args.DeviceName) &&
            !string.Equals(args.DeviceName, device.DeviceName, StringComparison.Ordinal))
        {
            await _pairingStore.RenameAsync(deviceId, args.DeviceName, cancellationToken).ConfigureAwait(false);
            device = device with { DeviceName = args.DeviceName };
        }

        SessionToken token = _tokens.Issue(device.DeviceId, context.Caller.ConnectionId, device.Permissions);

        if (_sessions.TryGet(context.Caller.ConnectionId, out ClientSession session))
        {
            session.OnAuthenticated(token, device);
        }
        else
        {
            // The connection vanished mid-handshake. Issuing a token nobody can use would
            // leave a live credential with no owner, so it is withdrawn immediately.
            _tokens.RevokeConnection(context.Caller.ConnectionId);
            return CommandResult.Fail(
                ErrorCodes.Internal,
                "The connection was closed during the handshake.",
                retryable: true);
        }

        AgentOptions options = _options.CurrentValue;
        IReadOnlyList<string> capabilities = await _capabilities.GetCapabilitiesAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Handshake complete. Device={DeviceId} Name={DeviceName} Platform={Platform} " +
            "AppVersion={AppVersion} Protocol=v{Version} Permissions={Permissions}",
            device.DeviceId,
            device.DeviceName,
            args.Platform,
            args.AppVersion,
            agreed.Value,
            string.Join(",", PermissionSet.ToNames(device.Permissions)));

        return CommandResult.Success(new HelloResult
        {
            Version = agreed.Value,
            DeviceId = options.Device.Id,
            DeviceName = options.Device.Name,
            Token = token.Value,
            TokenTtlSeconds = options.Security.SessionTokenTtlSeconds,
            Permissions = PermissionSet.ToNames(device.Permissions),
            Capabilities = capabilities.ToArray(),
            KeepaliveTimeoutSeconds = options.Network.KeepaliveTimeoutSeconds,
            MaxClockSkewSeconds = options.Security.MaxClockSkewSeconds,
        });
    }
}

/// <summary>
/// Replaces the session token before it expires.
/// </summary>
/// <remarks>
/// Renewal also re-sends the permission set, so a grant or revocation made at the PC reaches
/// the client within one token lifetime even if no other traffic is flowing. The previous
/// token is invalidated immediately: two simultaneously valid tokens would widen the window
/// in which a captured one is useful.
/// </remarks>
public sealed class SessionRenewHandler : ICommandHandler
{
    private readonly ClientSessionManager _sessions;
    private readonly ISessionTokenService _tokens;
    private readonly IPairingStore _pairingStore;
    private readonly IOptionsMonitor<AgentOptions> _options;

    /// <summary>Creates the handler.</summary>
    public SessionRenewHandler(
        ClientSessionManager sessions,
        ISessionTokenService tokens,
        IPairingStore pairingStore,
        IOptionsMonitor<AgentOptions> options)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _pairingStore = pairingStore ?? throw new ArgumentNullException(nameof(pairingStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string Command => CommandNames.SessionRenew;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        string? deviceId = context.Caller.DeviceId;
        if (deviceId is null)
        {
            return CommandResult.Fail(ErrorCodes.NotPaired, "This device is not paired.");
        }

        PairedDevice? device = await _pairingStore.FindByDeviceIdAsync(deviceId, cancellationToken)
            .ConfigureAwait(false);

        if (device is null)
        {
            return CommandResult.Fail(ErrorCodes.NotPaired, "This device's pairing was revoked.");
        }

        SessionToken token = _tokens.Issue(device.DeviceId, context.Caller.ConnectionId, device.Permissions);

        if (_sessions.TryGet(context.Caller.ConnectionId, out ClientSession session))
        {
            session.OnAuthenticated(token, device);
        }

        return CommandResult.Success(new SessionRenewResult
        {
            Token = token.Value,
            TokenTtlSeconds = _options.CurrentValue.Security.SessionTokenTtlSeconds,
            Permissions = PermissionSet.ToNames(device.Permissions),
        });
    }
}

/// <summary>
/// Reports which permission groups this device holds, and which exist.
/// </summary>
/// <remarks>
/// Both halves matter: the client shows what it can do, and also what it <em>could</em> do if
/// the user granted more — which turns a mysteriously missing button into an explainable one.
/// </remarks>
public sealed class PermissionsListHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.PermissionsList;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken) =>
        Task.FromResult(CommandResult.Success(new PermissionsResult
        {
            Granted = PermissionSet.ToNames(context.Caller.Permissions),
            Available = PermissionSet.AllNames.ToArray(),
        }));
}
