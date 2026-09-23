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

    /// <summary>Another code pairing is already waiting for the user at the PC.</summary>
    Busy,
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
    /// <param name="request">The pairing request. An empty token selects code pairing.</param>
    /// <param name="peerCertificateFingerprint">The certificate the peer presented on this connection.</param>
    /// <param name="remoteAddress">The peer's address, for display and rate limiting.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <param name="localCertificateFingerprint">
    /// This PC's own certificate fingerprint. Needed only for code pairing, whose code is derived
    /// from both fingerprints.
    /// </param>
    Task<PairingAttemptResult> TryPairAsync(
        PairRequestArgs request,
        string peerCertificateFingerprint,
        string remoteAddress,
        CancellationToken cancellationToken,
        string localCertificateFingerprint = "");
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
/// <para>
/// <b>Code pairing</b> is the second way in, for a phone that found the PC on the network
/// and has no QR code: the request carries no token. There is no secret to check, so the
/// human does all the work, and the dialog shows a six-digit <see cref="PairingCode"/> the
/// phone shows too. Matching codes prove both ends hold the keys this TLS connection was made
/// with, which is what the QR code's fingerprint otherwise proves. Because no token filters
/// requests before a person sees them, only one code prompt is shown at a time and an address
/// that was refused waits a minute before it can ask again. The owner can switch the path off
/// with <see cref="PairingOptions.AllowCodePairing"/>.
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

    private static readonly TimeSpan CodePairingCooldown = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, DateTimeOffset> _codeCooldownUntil = new(StringComparer.Ordinal);

    private PairingWindow? _window;
    private int _failedAttempts;
    private bool _codePromptActive;

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
        CancellationToken cancellationToken,
        string localCertificateFingerprint = "")
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(peerCertificateFingerprint))
        {
            // No client certificate means the TLS layer let through a connection it
            // should have refused. Refuse loudly rather than trusting the claim.
            _logger.LogError("Pairing attempt from {Address} had no peer certificate.", remoteAddress);
            return new PairingAttemptResult(PairingOutcome.FingerprintMismatch, null);
        }

        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            return Fail(PairingOutcome.InvalidRequest, remoteAddress, "missing device id");
        }

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return await TryPairWithCodeAsync(
                request,
                peerCertificateFingerprint,
                localCertificateFingerprint,
                remoteAddress,
                cancellationToken).ConfigureAwait(false);
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
        if (!ClaimMatchesPeer(request, peerCertificateFingerprint, remoteAddress))
        {
            return new PairingAttemptResult(PairingOutcome.FingerprintMismatch, null);
        }

        // --- Gate 4: does a human at the PC approve? ---
        if (_options.RequireLocalApproval)
        {
            PairingOutcome approval = await AskUserAsync(
                request,
                peerCertificateFingerprint,
                remoteAddress,
                comparisonCode: null,
                cancellationToken).ConfigureAwait(false);

            if (approval != PairingOutcome.Paired)
            {
                return new PairingAttemptResult(approval, null);
            }
        }

        // All gates passed. Burn the token by closing the window: a token is
        // single-use, so even a second legitimate device must be paired deliberately.
        PairedDevice device = await SaveAsync(request, peerCertificateFingerprint, remoteAddress, cancellationToken)
            .ConfigureAwait(false);
        CloseWindow();

        return new PairingAttemptResult(PairingOutcome.Paired, device);
    }

    /// <summary>
    /// Pairing without a QR code: the user compares a code on both screens and approves at the PC.
    /// </summary>
    private async Task<PairingAttemptResult> TryPairWithCodeAsync(
        PairRequestArgs request,
        string peerCertificateFingerprint,
        string localCertificateFingerprint,
        string remoteAddress,
        CancellationToken cancellationToken)
    {
        if (!_options.AllowCodePairing)
        {
            _logger.LogWarning(
                "Code pairing from {Address} refused: it is switched off in the configuration.",
                remoteAddress);
            return new PairingAttemptResult(PairingOutcome.Disabled, null);
        }

        if (string.IsNullOrEmpty(localCertificateFingerprint))
        {
            // Without this PC's fingerprint there is no code to compare, and approving without
            // one would be trusting whatever answered on the network.
            _logger.LogError("Code pairing from {Address} refused: this PC's fingerprint is unknown.", remoteAddress);
            return new PairingAttemptResult(PairingOutcome.Disabled, null);
        }

        if (!ClaimMatchesPeer(request, peerCertificateFingerprint, remoteAddress))
        {
            return new PairingAttemptResult(PairingOutcome.FingerprintMismatch, null);
        }

        lock (_sync)
        {
            DateTimeOffset now = _clock.UtcNow;

            if (_codeCooldownUntil.TryGetValue(remoteAddress, out DateTimeOffset until) && until > now)
            {
                _logger.LogWarning(
                    "Code pairing from {Address} refused: it was refused recently. Retry after {Until}.",
                    remoteAddress,
                    until);
                return new PairingAttemptResult(PairingOutcome.TooManyAttempts, null);
            }

            if (_codePromptActive)
            {
                _logger.LogWarning(
                    "Code pairing from {Address} refused: another request is already waiting at the PC.",
                    remoteAddress);
                return new PairingAttemptResult(PairingOutcome.Busy, null);
            }

            _codePromptActive = true;
        }

        try
        {
            string code = PairingCode.Compute(localCertificateFingerprint, peerCertificateFingerprint);

            PairingOutcome approval = await AskUserAsync(
                request,
                peerCertificateFingerprint,
                remoteAddress,
                code,
                cancellationToken).ConfigureAwait(false);

            if (approval != PairingOutcome.Paired)
            {
                lock (_sync)
                {
                    DateTimeOffset now = _clock.UtcNow;

                    foreach (string stale in _codeCooldownUntil.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToList())
                    {
                        _codeCooldownUntil.Remove(stale);
                    }

                    _codeCooldownUntil[remoteAddress] = now + CodePairingCooldown;
                }

                return new PairingAttemptResult(approval, null);
            }

            // A QR window that happens to be open is left alone: its token was not used.
            PairedDevice device = await SaveAsync(request, peerCertificateFingerprint, remoteAddress, cancellationToken)
                .ConfigureAwait(false);

            return new PairingAttemptResult(PairingOutcome.Paired, device);
        }
        finally
        {
            lock (_sync)
            {
                _codePromptActive = false;
            }
        }
    }

    private bool ClaimMatchesPeer(PairRequestArgs request, string peerCertificateFingerprint, string remoteAddress)
    {
        if (string.IsNullOrEmpty(request.ClientFingerprint) ||
            CertificateFingerprint.Equal(request.ClientFingerprint, peerCertificateFingerprint))
        {
            return true;
        }

        _logger.LogWarning(
            "Pairing request from {Address} claimed fingerprint {Claimed} but presented {Actual}. " +
            "Refusing: the request may have been relayed.",
            remoteAddress,
            CertificateFingerprint.ToShortForm(request.ClientFingerprint),
            CertificateFingerprint.ToShortForm(peerCertificateFingerprint));

        return false;
    }

    /// <summary>
    /// Asks the person at the PC. Returns <see cref="PairingOutcome.Paired"/> when they allow it.
    /// </summary>
    private async Task<PairingOutcome> AskUserAsync(
        PairRequestArgs request,
        string peerCertificateFingerprint,
        string remoteAddress,
        string? comparisonCode,
        CancellationToken cancellationToken)
    {
        if (!_approval.CanPrompt)
        {
            // No UI to ask. Refusing is the only safe answer: treating "nobody
            // available to consent" as consent would make a leaked QR code
            // sufficient for access.
            _logger.LogWarning(
                "Pairing request from {Address} refused: no interactive UI is available to approve it.",
                remoteAddress);
            return PairingOutcome.ApprovalTimeout;
        }

        var approvalRequest = new PairingApprovalRequest(
            SanitizeDisplayName(request.DeviceName),
            SanitizeDisplayName(request.Platform),
            SanitizeDisplayName(request.Model),
            remoteAddress,
            CertificateFingerprint.ToShortForm(peerCertificateFingerprint),
            comparisonCode is null ? null : PairingCode.ToDisplayForm(comparisonCode));

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
            return PairingOutcome.ApprovalTimeout;
        }

        if (!approved)
        {
            _logger.LogWarning("Pairing request from {Address} was refused by the user.", remoteAddress);
            return PairingOutcome.Rejected;
        }

        return PairingOutcome.Paired;
    }

    private async Task<PairedDevice> SaveAsync(
        PairRequestArgs request,
        string peerCertificateFingerprint,
        string remoteAddress,
        CancellationToken cancellationToken)
    {
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

        _logger.LogInformation(
            "Device paired. Device={DeviceId} Name={DeviceName} Fingerprint={Fingerprint} " +
            "Address={Address} Permissions={Permissions}",
            device.DeviceId,
            device.DeviceName,
            CertificateFingerprint.ToShortForm(device.CertificateFingerprint),
            remoteAddress,
            string.Join(",", PermissionSet.ToNames(device.Permissions)));

        return device;
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
        PairingOutcome.Busy => ErrorCodes.RateLimited,
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
        PairingOutcome.Busy => "Another device is waiting to be approved on the PC. Try again in a moment.",
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
