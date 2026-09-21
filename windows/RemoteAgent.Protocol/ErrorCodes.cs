namespace RemoteAgent.Protocol;

/// <summary>
/// Stable error codes carried in <see cref="ProtocolError.Code"/>.
/// </summary>
/// <remarks>
/// These strings are part of the wire contract: the mobile app branches on them
/// to decide whether to retry, re-pair, or show a specific explanation. They are
/// never changed once shipped, and the human-readable message beside them is
/// free to change at any time.
/// </remarks>
public static class ErrorCodes
{
    // --- Transport and framing ---

    /// <summary>The frame could not be parsed, or exceeded the size limit.</summary>
    public const string MalformedFrame = "E_MALFORMED_FRAME";

    /// <summary>The peer's protocol version range does not overlap ours.</summary>
    public const string VersionUnsupported = "E_VERSION_UNSUPPORTED";

    /// <summary>Sequence number was not strictly increasing, or the timestamp was outside the accepted window.</summary>
    public const string ReplayDetected = "E_REPLAY_DETECTED";

    /// <summary>Too many requests, or too many connection attempts from this source.</summary>
    public const string RateLimited = "E_RATE_LIMITED";

    // --- Authentication and authorization ---

    /// <summary>The presented certificate is not in the pairing store, or the pairing was revoked.</summary>
    public const string NotPaired = "E_NOT_PAIRED";

    /// <summary>No valid session token, or the token is bound to a different connection.</summary>
    public const string Unauthenticated = "E_UNAUTHENTICATED";

    /// <summary>Authenticated, but this device lacks the permission group the command requires.</summary>
    public const string PermissionDenied = "E_PERMISSION_DENIED";

    // --- Pairing ---

    /// <summary>Pairing mode is not currently enabled on the PC.</summary>
    public const string PairingDisabled = "E_PAIRING_DISABLED";

    /// <summary>The pairing token is unknown, expired, or already used.</summary>
    public const string PairingTokenInvalid = "E_PAIRING_TOKEN_INVALID";

    /// <summary>The user declined the pairing request at the PC.</summary>
    public const string PairingRejected = "E_PAIRING_REJECTED";

    /// <summary>Nobody answered the approval prompt before it timed out.</summary>
    public const string PairingTimeout = "E_PAIRING_TIMEOUT";

    // --- Dispatch ---

    /// <summary>No command with that name exists in the registry.</summary>
    public const string UnknownCommand = "E_UNKNOWN_COMMAND";

    /// <summary>Arguments were missing, malformed, or failed validation.</summary>
    public const string InvalidArguments = "E_INVALID_ARGUMENTS";

    /// <summary>The command is known but this PC cannot perform it (capability absent).</summary>
    public const string NotSupported = "E_NOT_SUPPORTED";

    /// <summary>The command was disabled by local policy or configuration.</summary>
    public const string PolicyDenied = "E_POLICY_DENIED";

    // --- Session and execution ---

    /// <summary>
    /// No interactive session agent is available: nobody is logged in, the
    /// workstation is at the logon screen, or the agent is restarting.
    /// Session-scoped commands (input, capture, apps, clipboard, files) return this.
    /// </summary>
    public const string SessionUnavailable = "E_SESSION_UNAVAILABLE";

    /// <summary>The desktop is locked, so the requested operation is blocked by Windows (§12.1).</summary>
    public const string DesktopLocked = "E_DESKTOP_LOCKED";

    /// <summary>The secure desktop (UAC prompt / logon UI) currently owns the display.</summary>
    public const string SecureDesktopActive = "E_SECURE_DESKTOP";

    /// <summary>The target application, monitor, file or device no longer exists.</summary>
    public const string NotFound = "E_NOT_FOUND";

    /// <summary>The command took longer than its deadline.</summary>
    public const string Timeout = "E_TIMEOUT";

    /// <summary>A shared resource is at its limit, e.g. the maximum number of screen streams. Retryable.</summary>
    public const string Busy = "E_BUSY";

    /// <summary>Windows refused the operation (access denied, elevation required).</summary>
    public const string AccessDenied = "E_ACCESS_DENIED";

    /// <summary>An unexpected failure. The detail goes to the PC's log, not to the client.</summary>
    public const string Internal = "E_INTERNAL";
}
