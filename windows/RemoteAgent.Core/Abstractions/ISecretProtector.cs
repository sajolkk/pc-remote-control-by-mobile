namespace RemoteAgent.Core.Abstractions;

/// <summary>
/// Protects secrets at rest.
/// </summary>
/// <remarks>
/// Abstracted so that the security layer stays platform-neutral and testable: the
/// real implementation is DPAPI at machine scope, which lives in the Windows layer
/// (§13 rule 1). Machine scope is correct here because the protected material
/// belongs to the service account, not to any interactive user, and must be
/// readable after a reboot with nobody logged on.
/// </remarks>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/> for this machine.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>
    /// Decrypts data produced by <see cref="Protect"/> on this machine.
    /// </summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The blob was produced on a different machine, or has been tampered with.
    /// Callers treat this as "identity unusable, regenerate" rather than crashing.
    /// </exception>
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>
/// Asks the interactive user to approve or refuse a pairing request (§8.1).
/// </summary>
/// <remarks>
/// Implemented by the tray UI and reached over IPC, because a Windows service
/// cannot display UI (§12.2). If no UI is available to ask, the implementation must
/// refuse rather than assume consent — that is the difference between a pairing
/// flow that requires a human and one that a leaked QR code defeats.
/// </remarks>
public interface IPairingApprovalService
{
    /// <summary>Whether a UI is currently available to prompt the user.</summary>
    bool CanPrompt { get; }

    /// <summary>
    /// Prompts for approval, showing the requesting device's name, address and
    /// certificate fingerprint. Returns false on refusal, timeout, or no UI.
    /// </summary>
    Task<bool> RequestApprovalAsync(PairingApprovalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Prompts for approval and also reports what the person chose to grant.
    /// </summary>
    /// <remarks>
    /// The default implementation asks the yes/no question and grants nothing beyond the default
    /// permissions, so an implementation with no way to ask for more — and every test double
    /// written before this existed — keeps behaving exactly as it did. Only an implementation whose
    /// prompt genuinely offers the choice overrides this.
    /// </remarks>
    async Task<PairingApprovalDecision> RequestDecisionAsync(
        PairingApprovalRequest request,
        CancellationToken cancellationToken) =>
        await RequestApprovalAsync(request, cancellationToken).ConfigureAwait(false)
            ? PairingApprovalDecision.ApprovedWithDefaults
            : PairingApprovalDecision.Declined;
}

/// <summary>
/// What the person at the PC answered to a pairing prompt.
/// </summary>
/// <param name="Approved">Whether they allowed the device to pair.</param>
/// <param name="GrantFullControl">
/// Whether they also ticked "give this device full control". Meaningless unless
/// <paramref name="Approved"/> is true, and ignored in that case.
/// </param>
public readonly record struct PairingApprovalDecision(bool Approved, bool GrantFullControl)
{
    /// <summary>Refused, or nobody answered.</summary>
    public static PairingApprovalDecision Declined { get; } = new(false, false);

    /// <summary>Allowed, with the default permissions only.</summary>
    public static PairingApprovalDecision ApprovedWithDefaults { get; } = new(true, false);

    /// <summary>Allowed, with every permission group.</summary>
    public static PairingApprovalDecision ApprovedWithFullControl { get; } = new(true, true);
}

/// <summary>
/// What the user is shown before approving a pairing.
/// </summary>
/// <param name="DeviceName">Name the device claims.</param>
/// <param name="Platform">Platform the device claims.</param>
/// <param name="Model">Model the device claims.</param>
/// <param name="RemoteAddress">Address the request came from, which the device cannot forge.</param>
/// <param name="FingerprintShort">
/// Colon-hex prefix of the device's certificate fingerprint, so a cautious user can
/// compare it against what their phone displays.
/// </param>
/// <param name="ComparisonCode">
/// For code pairing, the six-digit code the phone also shows; the user must check they match.
/// Null for QR pairing.
/// </param>
public sealed record PairingApprovalRequest(
    string DeviceName,
    string Platform,
    string Model,
    string RemoteAddress,
    string FingerprintShort,
    string? ComparisonCode = null);
