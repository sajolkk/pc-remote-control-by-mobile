using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Configuration;

namespace RemoteAgent.Security;

/// <summary>
/// The PC's cryptographic identity: one long-lived certificate and key pair.
/// </summary>
public sealed class DeviceIdentity
{
    internal DeviceIdentity(X509Certificate2 certificate, string fingerprint)
    {
        Certificate = certificate;
        Fingerprint = fingerprint;
    }

    /// <summary>The certificate presented to clients, with its private key attached.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The fingerprint clients pin (§7.1).</summary>
    public string Fingerprint { get; }

    /// <summary>Short colon-hex form for display.</summary>
    public string FingerprintShort => CertificateFingerprint.ToShortForm(Fingerprint);
}

/// <summary>
/// Loads or creates the PC's identity.
/// </summary>
public interface IIdentityStore
{
    /// <summary>
    /// Returns the existing identity, creating one on first run. Idempotent and
    /// safe to call from several places during startup.
    /// </summary>
    Task<DeviceIdentity> GetOrCreateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// File-backed identity store (§3, §7.1).
/// </summary>
/// <remarks>
/// <para><b>Algorithm.</b> ECDSA on P-256. This is not a preference but a
/// constraint: Windows SChannel — which backs <see cref="System.Net.Security.SslStream"/> —
/// does not support Ed25519 certificates for TLS, and the mobile side's BoringSSL
/// is likewise reliable with P-256. Choosing a curve neither TLS stack supports
/// would have forced a hand-rolled handshake, which is a far worse trade than using
/// a different well-studied curve.</para>
///
/// <para><b>Storage.</b> The certificate and key are exported as PKCS#12 under a
/// random 32-byte password, and the PFX together with that password is wrapped in a
/// single envelope encrypted by <see cref="ISecretProtector"/> (DPAPI at machine
/// scope). The password never exists outside the encrypted envelope, so there is no
/// secret to hardcode and none to leak into config, logs or source.</para>
///
/// <para><b>Key persistence.</b> SChannel will not perform server authentication
/// with an ephemeral key — the certificate object returned by
/// <c>CertificateRequest.CreateSelfSigned</c> fails the handshake with
/// "the platform does not support ephemeral keys". So the certificate must always be
/// round-tripped through PKCS#12 and re-imported into a key container, never used as
/// created. Which container is available depends on privilege, so the flags are
/// chosen at runtime by <see cref="LoadWithBestAvailableFlags"/> rather than
/// hardcoded: <c>MachineKeySet</c> is correct for the service but raises "Access
/// denied" for a non-elevated process, which would otherwise break portable mode
/// entirely (§0).</para>
///
/// <para><b>First run.</b> Nothing is provisioned ahead of time: on an arbitrary PC
/// the first start generates the key pair, writes the envelope, and is immediately
/// ready to pair (§0).</para>
/// </remarks>
public sealed class IdentityStore : IIdentityStore
{
    private const int EnvelopeVersion = 1;
    private const int PasswordBytes = 32;

    private readonly AgentPaths _paths;
    private readonly ISecretProtector _protector;
    private readonly ILogger<IdentityStore> _logger;
    private readonly int _lifetimeYears;
    private readonly string _subjectName;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private DeviceIdentity? _cached;

    /// <summary>Creates the store.</summary>
    /// <param name="paths">Resolved data directory.</param>
    /// <param name="protector">Machine-scoped secret protection.</param>
    /// <param name="logger">Diagnostic sink.</param>
    /// <param name="lifetimeYears">Certificate validity in years.</param>
    /// <param name="subjectName">
    /// Subject CN. Informational only, since validation is by pinned fingerprint
    /// rather than by name — but a meaningful value helps anyone inspecting the
    /// certificate later.
    /// </param>
    public IdentityStore(
        AgentPaths paths,
        ISecretProtector protector,
        ILogger<IdentityStore> logger,
        int lifetimeYears,
        string subjectName)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lifetimeYears = lifetimeYears;
        _subjectName = string.IsNullOrWhiteSpace(subjectName) ? "PC-Remote Agent" : subjectName;
    }

    /// <inheritdoc />
    public async Task<DeviceIdentity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            _cached = await LoadAsync(cancellationToken).ConfigureAwait(false) ??
                      await CreateAsync(cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<DeviceIdentity?> LoadAsync(CancellationToken cancellationToken)
    {
        string file = _paths.IdentityFile;
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            byte[] envelopeBytes = _protector.Unprotect(
                await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false));

            IdentityEnvelope? envelope = JsonSerializer.Deserialize<IdentityEnvelope>(envelopeBytes);
            if (envelope is null || envelope.Version != EnvelopeVersion ||
                string.IsNullOrEmpty(envelope.Pfx) || string.IsNullOrEmpty(envelope.Password))
            {
                _logger.LogWarning("Identity envelope is malformed. A new identity will be generated.");
                return null;
            }

            byte[] pfx = Convert.FromBase64String(envelope.Pfx);
            string password = envelope.Password;

            X509Certificate2 certificate = LoadWithBestAvailableFlags(pfx, password);

            CryptographicOperations.ZeroMemory(pfx);

            if (certificate.NotAfter < DateTime.Now)
            {
                _logger.LogWarning(
                    "The identity certificate expired on {NotAfter}. A new identity will be generated, " +
                    "which means paired devices must pair again.",
                    certificate.NotAfter);
                certificate.Dispose();
                return null;
            }

            string fingerprint = CertificateFingerprint.Compute(certificate);
            _logger.LogInformation(
                "Loaded device identity. Fingerprint={Fingerprint} NotAfter={NotAfter}",
                CertificateFingerprint.ToShortForm(fingerprint),
                certificate.NotAfter);

            return new DeviceIdentity(certificate, fingerprint);
        }
        catch (CryptographicException ex)
        {
            // Typically means the file was copied from another machine, so the DPAPI
            // blob cannot be opened here. Regenerating is correct and safe: paired
            // devices will see a fingerprint mismatch and refuse to connect, which
            // is the loud failure we want rather than a silent downgrade (§8.4).
            _logger.LogWarning(
                ex,
                "The stored identity could not be decrypted on this machine. A new identity will be generated.");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The stored identity could not be parsed. A new identity will be generated.");
            return null;
        }
    }

    private async Task<DeviceIdentity> CreateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Generating a new device identity (ECDSA P-256, {Years} year lifetime).", _lifetimeYears);

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={EscapeSubject(_subjectName)}"),
            key,
            HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, 0, critical: true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement,
                critical: true));

        // Both EKUs, because the same identity authenticates this machine as a TLS
        // server to phones and could authenticate it as a client in future
        // peer-to-peer use.
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [
                    new Oid("1.3.6.1.5.5.7.3.1", "serverAuth"),
                    new Oid("1.3.6.1.5.5.7.3.2", "clientAuth"),
                ],
                critical: false));

        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        // A SAN is included for hygiene when someone inspects the certificate, but
        // it is never validated: identity is the pinned fingerprint, not the name,
        // which is exactly why this system works on a machine whose hostname and IP
        // change (§0, §6.4).
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(SanitizeDnsName(Environment.MachineName));
        request.CertificateExtensions.Add(sanBuilder.Build());

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        DateTimeOffset notAfter = notBefore.AddYears(Math.Clamp(_lifetimeYears, 1, 30));

        using X509Certificate2 generated = request.CreateSelfSigned(notBefore, notAfter);

        string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(PasswordBytes));
        byte[] pfx = generated.Export(X509ContentType.Pkcs12, password);

        var envelope = new IdentityEnvelope
        {
            Version = EnvelopeVersion,
            Pfx = Convert.ToBase64String(pfx),
            Password = password,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(envelope);
        byte[] protectedBytes = _protector.Protect(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        CryptographicOperations.ZeroMemory(pfx);

        Directory.CreateDirectory(Path.GetDirectoryName(_paths.IdentityFile)!);
        await WriteAtomicAsync(_paths.IdentityFile, protectedBytes, cancellationToken).ConfigureAwait(false);

        // Re-load through the normal path so the running process uses a key in the
        // machine key set, matching what every later start will use.
        DeviceIdentity? loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded is not null)
        {
            _logger.LogInformation(
                "New device identity created. Fingerprint={Fingerprint}",
                loaded.FingerprintShort);
            return loaded;
        }

        // Re-loading failed, which would mean the just-written envelope is unreadable.
        // Fall back to a fresh import of the same material so the agent still starts,
        // and say plainly that it will not survive a restart.
        _logger.LogError(
            "The newly created identity could not be read back from disk. Continuing with an in-memory " +
            "identity for this run; it will not survive a restart, and paired devices will need to pair again.");

        X509Certificate2 fallback = LoadWithBestAvailableFlags(
            generated.Export(X509ContentType.Pkcs12, password),
            password);

        return new DeviceIdentity(fallback, CertificateFingerprint.Compute(fallback));
    }

    /// <summary>
    /// Imports a PKCS#12 blob into whichever key container this process can actually
    /// use, preferring the machine store.
    /// </summary>
    /// <remarks>
    /// The order matters and was established by measurement on Windows 10 22H2:
    /// <list type="number">
    /// <item><c>MachineKeySet</c> — correct for the service. The key belongs to the
    /// machine, not to a user, so it is readable after a reboot with nobody logged
    /// on. Raises <see cref="CryptographicException"/> "Access denied" unless the
    /// process is elevated or running as a service account.</item>
    /// <item>default flags — a user-scoped container, which is what makes portable
    /// and console modes work for an ordinary user with no elevation.</item>
    /// </list>
    /// <c>EphemeralKeySet</c> is deliberately absent: it happens to work for server
    /// authentication on this build, but Microsoft documents it as unsupported for
    /// <see cref="System.Net.Security.SslStream"/>, and relying on an undocumented
    /// behaviour for the one code path that must never fail is a poor trade.
    /// </remarks>
    private X509Certificate2 LoadWithBestAvailableFlags(byte[] pfx, string password)
    {
        (string Name, X509KeyStorageFlags Flags)[] strategies =
        [
            ("machine key set", X509KeyStorageFlags.MachineKeySet),
            ("user key set", X509KeyStorageFlags.DefaultKeySet),
        ];

        CryptographicException? lastFailure = null;

        foreach ((string name, X509KeyStorageFlags flags) in strategies)
        {
            try
            {
                X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(pfx, password, flags);
                _logger.LogDebug("Identity key loaded into the {Strategy}.", name);
                return certificate;
            }
            catch (CryptographicException ex)
            {
                lastFailure = ex;
                _logger.LogDebug(
                    "Could not load the identity key into the {Strategy}: {Reason}. Trying the next option.",
                    name,
                    ex.Message);
            }
        }

        throw new CryptographicException(
            "The identity key could not be loaded into any available key container.",
            lastFailure);
    }

    /// <summary>
    /// Writes to a temporary file and moves it into place, so a crash or power loss
    /// mid-write cannot leave a truncated identity that would orphan every pairing.
    /// </summary>
    private static async Task WriteAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken)
    {
        string temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, contents, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private static string EscapeSubject(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')
            {
                builder.Append(c);
            }
        }

        string result = builder.ToString().Trim();
        return result.Length == 0 ? "PC-Remote Agent" : result;
    }

    private static string SanitizeDnsName(string machineName)
    {
        var builder = new StringBuilder(machineName.Length);
        foreach (char c in machineName)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-')
            {
                builder.Append(c);
            }
        }

        string result = builder.ToString();
        return result.Length == 0 ? "pc-remote" : result;
    }

    private sealed class IdentityEnvelope
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("pfx")]
        public string Pfx { get; init; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; init; } = string.Empty;

        [JsonPropertyName("createdUtc")]
        public DateTimeOffset CreatedUtc { get; init; }
    }
}
