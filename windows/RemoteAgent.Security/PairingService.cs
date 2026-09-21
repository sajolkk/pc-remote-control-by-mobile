using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Security;

/// <summary>The outcome of a pairing attempt.</summary>
public enum PairingOutcome
{
    /// <summary>Paired successfully.</summary>
    Paired,

    /// <summary>Pairing mode is not open.</summary>
    Disabled,

    /// <summary>The token was unknown, expired, or already used.</summary>
    TokenInvalid,

    /// <summary>The claimed fingerprint did not match the TLS peer's certificate.</summary>
    FingerprintMismatch,

    /// <summary>The user refused at the PC.</summary>
    Rejected,

    /// <summary>Nobody answered the prompt in time, or no UI was available to ask.</summary>
    ApprovalTimeout,

    /// <summary>Too many failed attempts; pairing mode was closed.</summary>
    TooManyAttempts,

    /// <summary>The request was malformed.</summary>
    InvalidRequest,
}

/// <summary>The result of a pairing attempt, with the record when it succeeded.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Device">The stored record, present only on success.</param>
public readonly record struct PairingAttemptResult(PairingOutcome Outcome, PairedDevice? Device)
{
    /// <summary>Whether pairing completed.</summary>
    public bool Succeeded => Outcome == PairingOutcome.Paired;
}

/// <summary>An open pairing window.</summary>
/// <param name="Token">The single-use token that goes into the QR code.</param>
/// <param name="OpenedAt">When the window opened.</param>
/// <param name="ExpiresAt">When it closes itself.</param>
public sealed record PairingWindow(string Token, DateTimeOffset OpenedAt, DateTimeOffset ExpiresAt);

/// <summary>Manages pairing (§8).</summary>
public interface IPairingService
{
    /// <summary>The open window, or null when pairing is closed.</summary>
    PairingWindow? CurrentWindow { get; }

    /// <summary>Whether pairing is currently possible.</summary>
    bool IsOpen { get; }

    /// <summary>Failed attempts against the current window.</summary>
    int FailedAttempts { get; }

    /// <summary>
    /// Opens a pairing window, generating a fresh single-use token and invalidating
    /// any previous one.
    /// </summary>
    PairingWindow OpenWindow();

    /// <summary>Closes the window and invalidates its token.</summary>
    void CloseWindow();

    /// <summary>Raised whenever the window opens or closes, so the UI can update.</summary>
    event EventHandler<PairingWindow?>? WindowChanged;

    /// <summary>Attempts to pair a device.</summary>
    Task<PairingAttemptResult> TryPairAsync(
        PairRequestArgs request,
        string peerCertificateFingerprint,
        string remoteAddress,
        CancellationToken cancellationToken);
}

/// <summary>
/// The pairing state machine (§8.1).
/// </summary>
/// <remarks>
/// Three independent things must all hold before a device is paired, and each one
/// alone is useless to an attacker:
/// <list type="number">
/// <item><b>Pairing mode is open.</b> It defaults to closed, is opened deliberately
/// by the user, and closes itself after a few minutes.</item>
/// <item><b>A valid single-use token.</b> 128 bits of CSPRNG output, delivered
/// out-of-band by QR code, burned on use, and limited to a handful of attempts
/// before the window slams shut.</item>
/// <item><b>A human approves at the PC</b>, seeing the device's claimed name, its
/// actual source address, and its certificate fingerprint.</item>
/// </list>
/// <para>
/// Ordering is deliberate: the token is verified <em>before</em> the user is
/// prompted, so an attacker cannot spam approval dialogs at someone in the hope of
/// a careless click — a wrong token never reaches a human at all.
/// </para>
/// <para>
/// The claimed fingerprint is also checked against the actual TLS peer certificate.
/// That closes the relay case, where a device forwards someone else's pairing
/// request: the attacker would have to possess the private key matching the
/// fingerprint it claims.
/// </para>
/// </remarks>
public sealed class PairingService : IPairingService
{
    private const int TokenBytes = 16; // 128 bits.
    private const int MaxNameLength = 64;

    private readonly IPairingStore _store;
    private readonly IPairingApprovalService _approval;
    private readonly IClock _clock;
    private readonly ILogger<PairingService> _logger;
    private readonly PairingOptions _options;
    private readonly object _sync = new();

    private PairingWindow? _window;
    private int _failedAttempts;

    /// <summary>Creates the service.</summary>
    public PairingService(
        IPairingStore store,
        IPairingApprovalService approval,
        IClock clock,
        ILogger<PairingService> logger,
        PairingOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _approval = approval ?? throw new ArgumentNullException(nameof(approval));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public event EventHandler<PairingWindow?>? WindowChanged;

    /// <inheritdoc />
    public PairingWindow? CurrentWindow
    {
        get
        {
            lock (_sync)
            {
                return IsWindowLive() ? _window : null;
            }
        }
    }

    /// <inheritdoc />
    public bool IsOpen => CurrentWindow is not null;

    /// <inheritdoc />
    public int FailedAttempts
    {
        get
        {
            lock (_sync)
            {
                return _failedAttempts;
            }
        }
    }

    /// <inheritdoc />
    public PairingWindow OpenWindow()
    {
        PairingWindow window;
        lock (_sync)
        {
            DateTimeOffset now = _clock.UtcNow;
            window = new PairingWindow(
                Base64Url.GenerateToken(TokenBytes),
                now,
                now.AddMinutes(Math.Clamp(_options.WindowMinutes, 1, 60)));

            _window = window;
            _failedAttempts = 0;
        }

        // The token is never logged: it is a live credential for the duration of the
        // window, and a log file is exactly the wrong place for it (§7.5).
        _logger.LogInformation(
            "Pairing window opened, closing at {ExpiresAt}. Approval required: {RequireApproval}",
            window.ExpiresAt,
            _options.RequireLocalApproval);

        WindowChanged?.Invoke(this, window);
        return window;
    }

    /// <inheritdoc />
    public void CloseWindow()
    {
        bool wasOpen;
        lock (_sync)
        {
            wasOpen = _window is not null;
            _window = null;
            _failedAttempts = 0;
        }

        if (wasOpen)
        {
            _logger.LogInformation("Pairing window closed.");
            WindowChanged?.Invoke(this, null);
        }
    }

    /// <inheritdoc />
    public async Task<PairingAttemptResult> TryPairAsync(
        PairRequestArgs request,
        string peerCertificateFingerprint,
        string remoteAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(peerCertificateFingerprint))
        {
            // No client certificate means the TLS layer let through a connection it
            // should have refused. Refuse loudly rather than trusting the claim.
            _logger.LogError("Pairing attempt from {Address} had no peer certificate.", remoteAddress);
            return new PairingAttemptResult(PairingOutcome.FingerprintMismatch, null);
        }

        if (string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.Token))
        {
            return Fail(PairingOutcome.InvalidRequest, remoteAddress, "missing device id or token");
        }

        // --- Gate 1: is pairing open at all? ---
        string expectedToken;
        lock (_sync)
        {
            if (!_options.AllowPairing || !IsWindowLive())
            {
                _logger.LogWarning(
                    "Pairing attempt from {Address} refused: pairing mode is not open.",
                    remoteAddress);
                return new PairingAttemptResult(PairingOutcome.Disabled, null);
            }

            if (_failedAttempts >= Math.Clamp(_options.MaxAttempts, 1, 20))
            {
                _logger.LogWarning(
                    "Pairing attempt from {Address} refused: attempt limit already reached.",
                    remoteAddress);
                return new PairingAttemptResult(PairingOutcome.TooManyAttempts, null);
            }

            expectedToken = _window!.Token;
        }

        // --- Gate 2: is the token the one we issued? ---
        // Compared in constant time: the token IS the secret here, so a timing
        // side channel would let an attacker narrow it byte by byte.
        if (!FixedTimeStringEquals(request.Token, expectedToken))
        {
            bool exhausted;
            lock (_sync)
            {
                _failedAttempts++;
                exhausted = _failedAttempts >= Math.Clamp(_options.MaxAttempts, 1, 20);
            }

            _logger.LogWarning(
                "Pairing token mismatch from {Address}. Attempt {Attempt} of {Max}.",
                remoteAddress,
                FailedAttempts,
                _options.MaxAttempts);

            if (exhausted)
            {
                _logger.LogWarning("Closing the pairing window after too many failed tokens.");
                CloseWindow();
                return new PairingAttemptResult(PairingOutcome.TooManyAttempts, null);
            }

            return new PairingAttemptResult(PairingOutcome.TokenInvalid, null);
        }

        // --- Gate 3: does the claimed identity match the TLS peer? ---
        if (!string.IsNullOrEmpty(request.ClientFingerprint) &&
            !CertificateFingerprint.Equal(request.ClientFingerprint, peerCertificateFingerprint))
        {
            _logger.LogWarning(
                "Pairing request from {Address} claimed fingerprint {Claimed} but presented {Actual}. " +
                "Refusing: the request may have been relayed.",
                remoteAddress,
                CertificateFingerprint.ToShortForm(request.ClientFingerprint),
                CertificateFingerprint.ToShortForm(peerCertificateFingerprint));

            return new PairingAttemptResult(PairingOutcome.FingerprintMismatch, null);
        }

        // --- Gate 4: does a human at the PC approve? ---
        if (_options.RequireLocalApproval)
        {
            if (!_approval.CanPrompt)
            {
                // No UI to ask. Refusing is the only safe answer: treating "nobody
                // available to consent" as consent would make a leaked QR code
                // sufficient for access.
                _logger.LogWarning(
                    "Pairing request from {Address} refused: no interactive UI is available to approve it.",
                    remoteAddress);
                return new PairingAttemptResult(PairingOutcome.ApprovalTimeout, null);
            }

            var approvalRequest = new PairingApprovalRequest(
                SanitizeDisplayName(request.DeviceName),
                SanitizeDisplayName(request.Platform),
                SanitizeDisplayName(request.Model),
                remoteAddress,
                CertificateFingerprint.ToShortForm(peerCertificateFingerprint));

            using var approvalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            approvalTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.ApprovalTimeoutSeconds, 10, 600)));

            bool approved;
            try
            {
                approved = await _approval.RequestApprovalAsync(approvalRequest, approvalTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Pairing approval from {Address} timed out.", remoteAddress);
                return new PairingAttemptResult(PairingOutcome.ApprovalTimeout, null);
            }

            if (!approved)
            {
                _logger.LogWarning("Pairing request from {Address} was refused by the user.", remoteAddress);
                return new PairingAttemptResult(PairingOutcome.Rejected, null);
            }
        }

        // All gates passed. Burn the token by closing the window: a token is
        // single-use, so even a second legitimate device must be paired deliberately.
        var device = new PairedDevice
        {
            DeviceId = request.DeviceId,
            DeviceName = SanitizeDisplayName(request.DeviceName) is { Length: > 0 } name ? name : "Mobile device",
            Platform = SanitizeDisplayName(request.Platform),
            Model = SanitizeDisplayName(request.Model),
            CertificateFingerprint = peerCertificateFingerprint,
            PairedAt = _clock.UtcNow,
            Permissions = PermissionSet.Default,
        };

        await _store.SaveAsync(device, cancellationToken).ConfigureAwait(false);
        CloseWindow();

        _logger.LogInformation(
            "Device paired. Device={DeviceId} Name={DeviceName} Fingerprint={Fingerprint} " +
            "Address={Address} Permissions={Permissions}",
            device.DeviceId,
            device.DeviceName,
            CertificateFingerprint.ToShortForm(device.CertificateFingerprint),
            remoteAddress,
            string.Join(",", PermissionSet.ToNames(device.Permissions)));

        return new PairingAttemptResult(PairingOutcome.Paired, device);
    }

    /// <summary>Maps an outcome to the wire error code the client receives.</summary>
    public static string ToErrorCode(PairingOutcome outcome) => outcome switch
    {
        PairingOutcome.Disabled => ErrorCodes.PairingDisabled,
        PairingOutcome.TokenInvalid => ErrorCodes.PairingTokenInvalid,
        PairingOutcome.TooManyAttempts => ErrorCodes.RateLimited,
        PairingOutcome.Rejected => ErrorCodes.PairingRejected,
        PairingOutcome.ApprovalTimeout => ErrorCodes.PairingTimeout,
        PairingOutcome.FingerprintMismatch => ErrorCodes.PairingTokenInvalid,
        PairingOutcome.InvalidRequest => ErrorCodes.InvalidArguments,
        _ => ErrorCodes.Internal,
    };

    /// <summary>A displayable explanation for the client.</summary>
    public static string ToMessage(PairingOutcome outcome) => outcome switch
    {
        PairingOutcome.Disabled => "Pairing is not enabled on the PC. Turn on 'Allow pairing' and try again.",
        PairingOutcome.TokenInvalid => "The pairing code is invalid or has expired. Generate a new QR code.",
        PairingOutcome.TooManyAttempts => "Too many failed pairing attempts. Generate a new QR code on the PC.",
        PairingOutcome.Rejected => "The request was declined on the PC.",
        PairingOutcome.ApprovalTimeout => "Nobody approved the request on the PC in time.",
        PairingOutcome.FingerprintMismatch =>
            "The pairing request did not match this device's certificate and was refused.",
        PairingOutcome.InvalidRequest => "The pairing request was incomplete.",
        _ => "Pairing failed.",
    };

    private bool IsWindowLive() => _window is not null && _window.ExpiresAt > _clock.UtcNow;

    private PairingAttemptResult Fail(PairingOutcome outcome, string address, string reason)
    {
        _logger.LogWarning("Pairing attempt from {Address} refused: {Reason}.", address, reason);
        return new PairingAttemptResult(outcome, null);
    }

    /// <summary>
    /// Constant-time comparison of two token strings, tolerating different lengths
    /// without leaking which one is longer through an early return.
    /// </summary>
    private static bool FixedTimeStringEquals(string a, string b)
    {
        byte[] left = System.Text.Encoding.UTF8.GetBytes(a);
        byte[] right = System.Text.Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    /// <summary>
    /// Makes a device-supplied display string safe to show and to log: control
    /// characters removed so it cannot forge log lines or corrupt the dialog, and a
    /// length cap so it cannot push the fingerprint off the screen.
    /// </summary>
    private static string SanitizeDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            trimmed = trimmed[..MaxNameLength];
        }

        return new string(Array.FindAll(trimmed.ToCharArray(), static c => !char.IsControl(c)));
    }
}
