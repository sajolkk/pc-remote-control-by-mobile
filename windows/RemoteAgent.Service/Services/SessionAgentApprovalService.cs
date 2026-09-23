using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Ipc;
using RemoteAgent.Protocol;

namespace RemoteAgent.Service.Services;

/// <summary>
/// Asks the user to approve a pairing, by way of the session agent (§8.1).
/// </summary>
/// <remarks>
/// <para>A Windows service cannot display UI — interactive services have been disabled since
/// Vista, and Session 0 has no visible desktop. So the prompt is shown by the session agent,
/// which is the one process in this system that genuinely owns a desktop.</para>
///
/// <para><b>No agent means no approval.</b> If nobody is signed in, or the agent is restarting,
/// <see cref="CanPrompt"/> is false and pairing is refused. Treating "nobody available to
/// consent" as consent would reduce pairing to whoever holds the QR code, which is exactly the
/// property the approval step exists to prevent. Refusing is the only safe answer, and the
/// client is told plainly that someone must be signed in at the PC.</para>
/// </remarks>
public sealed class SessionAgentApprovalService : IPairingApprovalService
{
    private readonly SessionAgentRegistry _agents;
    private readonly ILogger<SessionAgentApprovalService> _logger;

    /// <summary>Creates the service.</summary>
    public SessionAgentApprovalService(SessionAgentRegistry agents, ILogger<SessionAgentApprovalService> logger)
    {
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool CanPrompt => _agents.IsConnected;

    /// <inheritdoc />
    public async Task<bool> RequestApprovalAsync(
        PairingApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ConnectedAgent? agent = _agents.ActiveAgent;
        if (agent is null)
        {
            _logger.LogWarning(
                "Cannot prompt for pairing approval: no session agent is connected. " +
                "Somebody must be signed in at the PC to approve a new device.");
            return false;
        }

        var payload = new
        {
            deviceName = request.DeviceName,
            platform = request.Platform,
            model = request.Model,
            remoteAddress = request.RemoteAddress,
            fingerprint = request.FingerprintShort,
            code = request.ComparisonCode,
        };

        // The IPC timeout is generous: the whole point is to wait for a human. The overall
        // deadline is enforced by the caller's cancellation token, which carries the
        // configured approval timeout.
        IpcResponse response = await agent.Connection.SendRequestAsync(
            new IpcRequest
            {
                Id = Guid.NewGuid().ToString("n"),
                Command = IpcCommands.ApprovePairing,
                Args = JsonSerializer.SerializeToElement(payload, ProtocolJson.Options),
            },
            TimeSpan.FromMinutes(10),
            cancellationToken).ConfigureAwait(false);

        if (!response.Ok)
        {
            _logger.LogWarning(
                "The session agent could not show the pairing prompt: {Code}",
                response.Error?.Code);
            return false;
        }

        bool approved = response.Data?["approved"]?.GetValue<bool>() ?? false;

        _logger.LogInformation(
            "Pairing request from {Address} was {Outcome} at the PC.",
            request.RemoteAddress,
            approved ? "approved" : "declined");

        return approved;
    }
}
