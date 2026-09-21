using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Security;
using RemoteAgent.Service.Handlers;
using RemoteAgent.Service.Network;

namespace RemoteAgent.Integration.Tests;

/// <summary>
/// Stands up a real listener on a free port, backed by temporary storage.
/// </summary>
/// <remarks>
/// <para>Composed by hand rather than by booting the whole host, so a test can reach in and change
/// one thing — open pairing, decline approval, revoke a device — and observe the effect through the
/// wire protocol. The parts that are actually under test are the real implementations: the identity
/// store, the pairing store, the pairing service, the token service, the authorization gate, the
/// dispatcher and the TLS listener.</para>
///
/// <para>Only two things are substituted. The secret protector, because DPAPI ties a blob to the
/// machine and a test should not leave machine-scoped state behind; and the approval service,
/// because the real one puts a dialog on a human's screen. The approval fake is scriptable so the
/// decline path is tested as thoroughly as the approve path.</para>
/// </remarks>
internal sealed class TestHarness : IAsyncDisposable
{
    private readonly string _dataDirectory;
    private readonly List<IAsyncDisposable> _disposables = [];

    private TestHarness(string dataDirectory, AgentOptions options)
    {
        _dataDirectory = dataDirectory;
        Options = options;
        Paths = CreatePaths(dataDirectory);
    }

    internal AgentOptions Options { get; }

    internal AgentPaths Paths { get; }

    internal JsonPairingStore PairingStore { get; private set; } = null!;

    internal IPairingService PairingService { get; private set; } = null!;

    internal ScriptedApprovalService Approval { get; private set; } = null!;

    internal ControlChannelListener Listener { get; private set; } = null!;

    internal ClientSessionManager Sessions { get; private set; } = null!;

    internal DeviceIdentity Identity { get; private set; } = null!;

    internal int Port => Listener.BoundPort;

    /// <summary>Builds and starts a harness.</summary>
    internal static async Task<TestHarness> StartAsync(Action<AgentOptions>? configure = null)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "pcremote-tests",
            Guid.NewGuid().ToString("n"));

        var options = new AgentOptions();

        // Port 0 is not usable here because the listener walks a range from its configured start, so
        // a high random port is chosen to avoid colliding with a real agent or another test.
        options.Network.ControlPort = Random.Shared.Next(49_000, 58_000);
        options.Network.ControlPortFallbackRange = 40;
        options.Discovery.Enabled = false;
        options.Device.Id = "pc-under-test";
        options.Device.Name = "PC Under Test";

        configure?.Invoke(options);

        var harness = new TestHarness(directory, options);
        await harness.StartInternalAsync();
        return harness;
    }

    private async Task StartInternalAsync()
    {
        Paths.EnsureCreated();

        var clock = new SystemClock();
        var protector = new PassthroughSecretProtector();

        var identityStore = new IdentityStore(
            Paths,
            protector,
            NullLogger<IdentityStore>.Instance,
            lifetimeYears: 1,
            subjectName: "PC-Remote Test");

        Identity = await identityStore.GetOrCreateAsync(CancellationToken.None);

        PairingStore = new JsonPairingStore(Paths, clock, NullLogger<JsonPairingStore>.Instance);
        Approval = new ScriptedApprovalService();

        PairingService = new PairingService(
            PairingStore,
            Approval,
            clock,
            NullLogger<PairingService>.Instance,
            Options.Pairing);

        var tokens = new SessionTokenService(
            clock,
            NullLogger<SessionTokenService>.Instance,
            Options.Security.SessionTokenTtlSeconds);

        var abuseLimiter = new AbuseLimiter(
            clock,
            NullLogger<AbuseLimiter>.Instance,
            Options.Security.MaxAuthFailuresPerAddress,
            Options.Security.AuthBlockMinutes);

        Sessions = new ClientSessionManager(NullLogger<ClientSessionManager>.Instance);
        var endpointStatus = new ListenerEndpointStatus();
        var monitor = new StaticOptionsMonitor<AgentOptions>(Options);
        var capabilities = new FixedCapabilityProvider([CapabilityNames.PowerShutdown]);

        // The handler set is deliberately the security-relevant one: pairing, handshake, token
        // renewal and permission reporting. Adding the power handlers would mean a test could shut
        // the developer's machine down.
        ICommandHandler[] handlers =
        [
            new PingHandler(clock),
            new HelloHandler(
                Sessions,
                tokens,
                PairingStore,
                capabilities,
                monitor,
                NullLogger<HelloHandler>.Instance),
            new SessionRenewHandler(Sessions, tokens, PairingStore, monitor),
            new PermissionsListHandler(),
            new PairRequestHandler(PairingService, Sessions, NullLogger<PairRequestHandler>.Instance),
        ];

        var registry = new CommandRegistry(AgentRole.Service, handlers);

        var dispatcher = new CommandDispatcher(
            registry,
            new AuthorizationGate(),
            NullLogger<CommandDispatcher>.Instance,
            sessionBridge: null);

        PairingIdentity.Initialize(Options.Device.Id, Options.Device.Name, Identity.Fingerprint);

        Listener = new ControlChannelListener(
            identityStore,
            PairingStore,
            tokens,
            dispatcher,
            Sessions,
            endpointStatus,
            abuseLimiter,
            clock,
            monitor,
            NullLogger<ControlChannelListener>.Instance,
            NullLoggerFactory.Instance);

        await Listener.StartAsync(CancellationToken.None);

        _disposables.Add(Listener);
        _disposables.Add(Sessions);
    }

    /// <summary>Opens pairing and returns the token that would go into the QR code.</summary>
    internal string OpenPairing()
    {
        Options.Pairing.AllowPairing = true;
        return PairingService.OpenWindow().Token;
    }

    /// <summary>
    /// Resolves the data directory without going through the environment, so tests never disturb a
    /// real installation's configuration.
    /// </summary>
    private static AgentPaths CreatePaths(string directory)
    {
        string? previous = Environment.GetEnvironmentVariable(AgentPaths.DataDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(AgentPaths.DataDirectoryVariable, directory);
            return AgentPaths.Resolve();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentPaths.DataDirectoryVariable, previous);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable disposable in _disposables)
        {
            try
            {
                await disposable.DisposeAsync();
            }
            catch (Exception)
            {
                // Test teardown: a component that is already gone is not a failure.
            }
        }

        Identity?.Certificate.Dispose();

        try
        {
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held open by a closing socket; the temp directory is disposable anyway.
        }
    }
}

/// <summary>
/// Stores secrets unencrypted, for tests only.
/// </summary>
/// <remarks>
/// DPAPI binds a blob to the machine and, at machine scope, leaves state that a test run should not
/// create. Since the test's identity file lives in a temp directory that is deleted afterwards,
/// there is nothing worth protecting — and passing bytes through makes a failure in the identity
/// store visible rather than hidden behind a decryption error.
/// </remarks>
internal sealed class PassthroughSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => (byte[])plaintext.Clone();

    public byte[] Unprotect(byte[] protectedData) => (byte[])protectedData.Clone();
}

/// <summary>An approval service a test can script, standing in for the human at the PC.</summary>
internal sealed class ScriptedApprovalService : IPairingApprovalService
{
    /// <summary>Whether a prompt is possible at all. False simulates "nobody signed in".</summary>
    public bool CanPrompt { get; set; } = true;

    /// <summary>What the simulated human answers.</summary>
    public bool ApproveNext { get; set; } = true;

    /// <summary>Set to simulate nobody answering the prompt.</summary>
    public bool NeverAnswer { get; set; }

    /// <summary>How many times approval was requested.</summary>
    public int PromptCount { get; private set; }

    /// <summary>The request the caller was shown, for asserting on what the user would see.</summary>
    public PairingApprovalRequest? LastRequest { get; private set; }

    /// <inheritdoc />
    public async Task<bool> RequestApprovalAsync(
        PairingApprovalRequest request,
        CancellationToken cancellationToken)
    {
        PromptCount++;
        LastRequest = request;

        if (NeverAnswer)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return ApproveNext;
    }
}

/// <summary>A capability provider returning a fixed list.</summary>
internal sealed class FixedCapabilityProvider(IReadOnlyList<string> capabilities) : ICapabilityProvider
{
    public Task<IReadOnlyList<string>> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(capabilities);
}

/// <summary>An options monitor over a single mutable instance, so tests can change settings live.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

    private sealed class NullDisposable : IDisposable
    {
        internal static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
