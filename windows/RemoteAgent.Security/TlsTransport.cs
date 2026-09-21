using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace RemoteAgent.Security;

/// <summary>
/// Why a TLS session was refused after the handshake completed.
/// </summary>
public enum TlsRejectionReason
{
    /// <summary>The session is acceptable.</summary>
    None,

    /// <summary>The peer presented no client certificate.</summary>
    NoClientCertificate,

    /// <summary>The negotiated protocol version is below the floor.</summary>
    ProtocolTooOld,

    /// <summary>The negotiated cipher suite is not in the allowlist.</summary>
    WeakCipherSuite,

    /// <summary>The peer certificate is expired or not yet valid.</summary>
    CertificateNotTimeValid,
}

/// <summary>
/// TLS configuration and post-handshake enforcement for the control channel (§7.1).
/// </summary>
/// <remarks>
/// <para><b>Protocol floor.</b> <c>Tls12 | Tls13</c> is requested. Measured behaviour
/// on Windows 10 22H2: TLS 1.3 alone fails the handshake because SChannel there has
/// no 1.3 support, while the pair negotiates 1.2 successfully and would negotiate 1.3
/// on Windows 11. Naming both is therefore what makes one binary correct on every
/// supported Windows version (§0), and naming them explicitly rather than passing
/// <c>SslProtocols.None</c> keeps TLS 1.0 and 1.1 unreachable even on a machine whose
/// OS policy still enables them.</para>
///
/// <para><b>Certificate validation is intentionally disabled</b> at the TLS layer and
/// replaced by fingerprint pinning one level up. There is no CA in this system, so
/// chain validation has nothing to validate against; hostname validation is equally
/// meaningless because the PC's name and address change (§6.4). Returning <c>true</c>
/// from the callback is safe <em>only</em> because the caller then requires the peer's
/// fingerprint to match a stored pairing record before any command is accepted. That
/// check is not optional, and the connection handler performs it before the caller is
/// given any identity beyond "unpaired".</para>
///
/// <para><b>Cipher suites are enforced after the handshake, not before.</b>
/// <see cref="CipherSuitesPolicy"/> throws <see cref="PlatformNotSupportedException"/>
/// on Windows, so a code-level allowlist is simply unavailable on the target platform.
/// Rather than pretend otherwise, the session is inspected once it is established and
/// dropped if the negotiated suite is not forward-secret and AEAD. That produces the
/// same outcome — no weak session ever carries a command — by a route that actually
/// works on Windows.</para>
/// </remarks>
public static class TlsTransport
{
    /// <summary>The protocol versions offered. Nothing below TLS 1.2 is ever reachable.</summary>
    public const SslProtocols AllowedProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>
    /// Cipher suites accepted once negotiated: ECDHE key exchange for forward secrecy,
    /// AEAD for integrity, and no CBC or RSA key transport.
    /// </summary>
    private static readonly HashSet<TlsCipherSuite> AllowedCipherSuites =
    [
        // TLS 1.3
        TlsCipherSuite.TLS_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,

        // TLS 1.2 with an ECDSA certificate, which is what our identity always is.
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
    ];

    /// <summary>
    /// Builds the server-side options for one accepted connection.
    /// </summary>
    /// <param name="serverCertificate">The PC's identity certificate, with private key.</param>
    public static SslServerAuthenticationOptions CreateServerOptions(X509Certificate2 serverCertificate)
    {
        ArgumentNullException.ThrowIfNull(serverCertificate);

        return new SslServerAuthenticationOptions
        {
            ServerCertificate = serverCertificate,

            // Mutual authentication: a client with no certificate cannot be matched to
            // a pairing record, so it is refused before any command is parsed.
            ClientCertificateRequired = true,

            EnabledSslProtocols = AllowedProtocols,

            // See the class remarks: pinning replaces chain validation entirely.
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,

            // No CA means no revocation lists to consult. Revocation in this system is
            // performed by deleting the pairing record, which is immediate and local.
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,

            EncryptionPolicy = EncryptionPolicy.RequireEncryption,

            AllowRenegotiation = false,
        };
    }

    /// <summary>
    /// Inspects an established session and reports whether it is fit to carry commands.
    /// </summary>
    /// <remarks>
    /// Called immediately after <c>AuthenticateAsServerAsync</c> and before the peer is
    /// given any identity. A rejection here closes the connection without dispatching
    /// anything.
    /// </remarks>
    public static TlsRejectionReason Validate(SslStream stream, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.RemoteCertificate is null)
        {
            return TlsRejectionReason.NoClientCertificate;
        }

        if (stream.SslProtocol is not (SslProtocols.Tls12 or SslProtocols.Tls13))
        {
            return TlsRejectionReason.ProtocolTooOld;
        }

        if (!AllowedCipherSuites.Contains(stream.NegotiatedCipherSuite))
        {
            return TlsRejectionReason.WeakCipherSuite;
        }

        using var peer = new X509Certificate2(stream.RemoteCertificate);
        if (now < peer.NotBefore.ToUniversalTime() || now > peer.NotAfter.ToUniversalTime())
        {
            // Expiry is still checked even though the chain is not: a pinned
            // fingerprint says "this is the device we paired with", and the validity
            // window says "this credential is still meant to be in use". Both are
            // cheap, and honouring the certificate's own stated lifetime avoids a
            // pairing that silently lives forever.
            return TlsRejectionReason.CertificateNotTimeValid;
        }

        return TlsRejectionReason.None;
    }

    /// <summary>Extracts the peer's pinning fingerprint from an established session.</summary>
    public static string? GetPeerFingerprint(SslStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.RemoteCertificate is null)
        {
            return null;
        }

        using var peer = new X509Certificate2(stream.RemoteCertificate);
        return CertificateFingerprint.Compute(peer);
    }

    /// <summary>A displayable explanation of a rejection, for logs.</summary>
    public static string Describe(TlsRejectionReason reason) => reason switch
    {
        TlsRejectionReason.None => "acceptable",
        TlsRejectionReason.NoClientCertificate => "the client presented no certificate",
        TlsRejectionReason.ProtocolTooOld => "the negotiated TLS version is below 1.2",
        TlsRejectionReason.WeakCipherSuite => "the negotiated cipher suite is not forward-secret and AEAD",
        TlsRejectionReason.CertificateNotTimeValid => "the client certificate is expired or not yet valid",
        _ => "rejected",
    };

    /// <summary>
    /// Formats the negotiated parameters for <c>device.info</c> and the Windows UI, so
    /// the user can see what their connection actually agreed on.
    /// </summary>
    public static string DescribeSession(SslStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return $"{stream.SslProtocol} / {stream.NegotiatedCipherSuite}";
    }
}
