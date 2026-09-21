using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RemoteAgent.Protocol;
using RemoteAgent.Security;

namespace RemoteAgent.Integration.Tests;

/// <summary>
/// A minimal client that speaks the real protocol, standing in for the mobile app.
/// </summary>
/// <remarks>
/// Also serves as the reference for the Flutter client: everything the phone has to do is here, in
/// order — generate a device identity once, present it as a TLS client certificate, pin the PC's
/// certificate fingerprint, frame requests with a monotonic sequence number and a timestamp, submit
/// a pairing token, then reconnect and handshake to obtain a session token.
/// <para>
/// The certificate is generated per instance, which is exactly what a freshly installed app does.
/// Two clients therefore have different identities and cannot use each other's pairing — a property
/// the tests rely on.
/// </para>
/// </remarks>
internal sealed class TestClient : IAsyncDisposable
{
    private readonly X509Certificate2 _certificate;
    private readonly string _expectedServerFingerprint;

    private TcpClient? _tcp;
    private SslStream? _tls;
    private long _sequence;

    private TestClient(X509Certificate2 certificate, string expectedServerFingerprint)
    {
        _certificate = certificate;
        _expectedServerFingerprint = expectedServerFingerprint;
        Fingerprint = CertificateFingerprint.Compute(certificate);
    }

    /// <summary>This client's certificate fingerprint, its identity to the PC.</summary>
    internal string Fingerprint { get; }

    /// <summary>The session token issued by a successful handshake.</summary>
    internal string? Token { get; private set; }

    /// <summary>The TLS protocol that was negotiated, for reporting in tests.</summary>
    internal string NegotiatedProtocol { get; private set; } = string.Empty;

    /// <summary>The cipher suite that was negotiated.</summary>
    internal string NegotiatedCipher { get; private set; } = string.Empty;

    /// <summary>Creates a client with a fresh identity, as a newly installed app would have.</summary>
    internal static TestClient Create(string expectedServerFingerprint, string commonName = "test-phone")
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={commonName}"),
            key,
            HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement,
                true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2", "clientAuth")], false));

        using X509Certificate2 generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));

        // Round-tripped through PKCS#12 for the same reason the agent does it: a certificate whose
        // key is ephemeral cannot be used by SChannel.
        string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        X509Certificate2 usable = X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pkcs12, password),
            password,
            X509KeyStorageFlags.DefaultKeySet);

        return new TestClient(usable, expectedServerFingerprint);
    }

    /// <summary>
    /// Connects and completes the TLS handshake, pinning the PC's certificate.
    /// </summary>
    /// <remarks>
    /// The validation callback returns true and the fingerprint is then compared explicitly, which
    /// mirrors what the mobile app must do: there is no CA, so chain validation has nothing to check,
    /// and the pin is the entire basis of trust.
    /// </remarks>
    internal async Task ConnectAsync(int port, CancellationToken cancellationToken = default)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync("127.0.0.1", port, cancellationToken);

        _tls = new SslStream(_tcp.GetStream(), leaveInnerStreamOpen: false);

        await _tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "pc-remote",
                ClientCertificates = new X509CertificateCollection { _certificate },
                EnabledSslProtocols = TlsTransport.AllowedProtocols,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            },
            cancellationToken);

        NegotiatedProtocol = _tls.SslProtocol.ToString();
        NegotiatedCipher = _tls.NegotiatedCipherSuite.ToString();

        if (_expectedServerFingerprint.Length > 0)
        {
            using var server = new X509Certificate2(_tls.RemoteCertificate!);
            string actual = CertificateFingerprint.Compute(server);

            if (!CertificateFingerprint.Equal(actual, _expectedServerFingerprint))
            {
                throw new InvalidOperationException(
                    "The PC's certificate fingerprint does not match the pinned value.");
            }
        }
    }

    /// <summary>Sends a command and returns the raw response.</summary>
    internal async Task<ResponseEnvelope> SendAsync(
        string command,
        object? args = null,
        bool includeToken = true,
        long? overrideSequence = null,
        long? overrideTimestampMs = null,
        CancellationToken cancellationToken = default)
    {
        if (_tls is null)
        {
            throw new InvalidOperationException("Connect first.");
        }

        long sequence = overrideSequence ?? Interlocked.Increment(ref _sequence);

        var request = new RequestEnvelope
        {
            Id = Guid.NewGuid().ToString("n")[..8],
            TimestampMs = overrideTimestampMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sequence = sequence,
            Command = command,
            Args = args is null
                ? null
                : JsonSerializer.SerializeToElement(args, ProtocolJson.Options),
            Token = includeToken ? Token : null,
        };

        await FrameCodec.WriteMessageAsync(_tls, FrameType.Request, request, cancellationToken);

        Frame? frame = await FrameCodec.ReadAsync(_tls, cancellationToken);
        if (frame is null)
        {
            throw new IOException("The PC closed the connection without responding.");
        }

        // Events can arrive interleaved with responses; skip them to find the answer.
        while (frame.Value.Type == FrameType.Event)
        {
            frame = await FrameCodec.ReadAsync(_tls, cancellationToken);
            if (frame is null)
            {
                throw new IOException("The PC closed the connection without responding.");
            }
        }

        if (!ProtocolJson.TryDeserialize(frame.Value.Payload.Span, out ResponseEnvelope? response) ||
            response is null)
        {
            throw new InvalidDataException("The PC's response could not be parsed.");
        }

        return response;
    }

    /// <summary>Deserializes a successful response's payload.</summary>
    internal static T Payload<T>(ResponseEnvelope response)
    {
        if (!response.Ok || response.Data is null)
        {
            throw new InvalidOperationException(
                $"Expected a successful response, got {response.Error?.Code}: {response.Error?.Message}");
        }

        return response.Data.Deserialize<T>(ProtocolJson.Options)
               ?? throw new InvalidDataException("The payload could not be read.");
    }

    /// <summary>Completes the handshake and stores the session token.</summary>
    internal async Task<ResponseEnvelope> HelloAsync(CancellationToken cancellationToken = default)
    {
        ResponseEnvelope response = await SendAsync(
            CommandNames.Hello,
            new
            {
                minVersion = ProtocolVersion.MinSupported,
                maxVersion = ProtocolVersion.Current,
                deviceName = "Test Phone",
                platform = "test",
                appVersion = "0.2.0",
            },
            includeToken: false,
            cancellationToken: cancellationToken);

        if (response.Ok)
        {
            Token = Payload<Protocol.Messages.HelloResult>(response).Token;
        }

        return response;
    }

    /// <summary>Submits a pairing token.</summary>
    internal Task<ResponseEnvelope> PairAsync(
        string pairingToken,
        string deviceId = "test-phone-1",
        CancellationToken cancellationToken = default) =>
        SendAsync(
            CommandNames.PairRequest,
            new
            {
                deviceId,
                deviceName = "Test Phone",
                platform = "test",
                model = "Harness",
                token = pairingToken,
                clientFingerprint = Fingerprint,
            },
            includeToken: false,
            cancellationToken: cancellationToken);

    /// <summary>Closes the connection, leaving the identity intact for a reconnect.</summary>
    internal async Task DisconnectAsync()
    {
        if (_tls is not null)
        {
            await _tls.DisposeAsync();
            _tls = null;
        }

        _tcp?.Dispose();
        _tcp = null;
        _sequence = 0;
        Token = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _certificate.Dispose();
    }
}
