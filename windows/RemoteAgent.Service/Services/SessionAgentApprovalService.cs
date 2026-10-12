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
        CancellationToken cancellationToken) =>
        (await RequestDecisionAsync(request, cancellationToken).ConfigureAwait(false)).Approved;

    /// <inheritdoc />
    /// <remarks>
    /// The dialog carries one extra, unticked checkbox: "give this device full control". Its state
    /// comes back beside the answer and is honoured only together with an explicit Allow — a
    /// response that says "full control" without "approved" is a refusal like any other.
    /// </remarks>
    public async Task<PairingApprovalDecision> RequestDecisionAsync(
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
            return PairingApprovalDecision.Declined;
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
            return PairingApprovalDecision.Declined;
        }

        bool approved = ReadFlag(response.Data, "approved");
        bool fullControl = approved && ReadFlag(response.Data, "fullControl");

        _logger.LogInformation(
            "Pairing request from {Address} was {Outcome} at the PC.",
            request.RemoteAddress,
            !approved ? "declined" : fullControl ? "approved with full control" : "approved");

        return new PairingApprovalDecision(approved, fullControl);
    }

    /// <summary>
    /// Reads a boolean from the agent's answer. Anything other than a literal <c>true</c> is false,
    /// so a missing field, a wrong type, or an older agent that never sends it all mean "no".
    /// </summary>
    private static bool ReadFlag(System.Text.Json.Nodes.JsonNode? data, string name) =>
        data is System.Text.Json.Nodes.JsonObject values &&
        values[name] is System.Text.Json.Nodes.JsonValue value &&
        value.TryGetValue(out bool flag) &&
        flag;
}
