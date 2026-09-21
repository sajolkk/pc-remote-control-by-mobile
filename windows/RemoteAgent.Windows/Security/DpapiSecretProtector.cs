using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Configuration;

namespace RemoteAgent.Windows.Security;

/// <summary>
/// Protects secrets at rest with DPAPI.
/// </summary>
/// <remarks>
/// <para><b>Scope.</b> <see cref="DataProtectionScope.LocalMachine"/> for the service:
/// the identity key belongs to the machine, not to any user, and must be readable
/// after a reboot with nobody logged on. The consequence is that any local process
/// can ask DPAPI to unprotect the blob, so confidentiality rests on the file's ACL
/// rather than on the encryption alone — which is why
/// <see cref="HardenDirectory"/> exists and is called at startup.</para>
///
/// <para><b>Additional entropy</b> is mixed in so that a copy of the file alone is not
/// enough even for a process that can call DPAPI: an attacker also needs to know this
/// application's entropy value. That is obfuscation rather than a secret — it is in
/// the binary — and it is documented as such rather than presented as a security
/// boundary. The real boundary is the ACL.</para>
///
/// <para><b>Portability.</b> In portable mode the agent runs as an ordinary user rather than
/// SYSTEM. The protector still works, because DPAPI machine scope needs no privilege to
/// <em>use</em>, and the hardening grants the running account explicitly so the agent does not
/// lock itself out of its own data directory. Where the ACL cannot be rewritten at all, that is
/// logged rather than silently ignored.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Application-specific entropy. Not a secret (it ships in the binary); it only
    // ensures a blob from this application cannot be unprotected by accident.
    private static readonly byte[] Entropy =
        System.Text.Encoding.UTF8.GetBytes("PC-Remote/identity/v1");

    private readonly ILogger<DpapiSecretProtector> _logger;
    private readonly DataProtectionScope _scope;

    /// <summary>Creates the protector.</summary>
    /// <param name="logger">Diagnostic sink.</param>
    /// <param name="useMachineScope">
    /// Machine scope for the service; user scope is only appropriate for a
    /// user-owned portable instance whose data directory lives in the user profile.
    /// </param>
    public DpapiSecretProtector(ILogger<DpapiSecretProtector> logger, bool useMachineScope = true)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _scope = useMachineScope ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser;
    }

    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, Entropy, _scope);
    }

    /// <inheritdoc />
    public byte[] Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        return ProtectedData.Unprotect(protectedData, Entropy, _scope);
    }

    /// <summary>
    /// Restricts a data directory to SYSTEM, Administrators and the account the agent runs as,
    /// removing inherited access so that other local users cannot read the identity or pairing files.
    /// </summary>
    /// <remarks>
    /// Best-effort: an unelevated portable instance cannot rewrite ACLs, and failing
    /// to start over that would be worse than running with the directory's default
    /// protection. The outcome is always logged so it is visible in the UI's log view
    /// rather than silently assumed.
    /// </remarks>
    public bool HardenDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
            {
                directory.Create();
            }

            DirectorySecurity security = directory.GetAccessControl();

            // Stop inheriting, and drop the inherited entries rather than copying them:
            // copying would preserve exactly the broad Users access we are removing.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (FileSystemAccessRule existing in security
                         .GetAccessRules(true, false, typeof(SecurityIdentifier))
                         .Cast<FileSystemAccessRule>()
                         .ToArray())
            {
                security.RemoveAccessRuleSpecific(existing);
            }

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            Grant(security, system);
            Grant(security, administrators);

            // The identity this process actually runs as must keep access, or hardening locks the
            // agent out of its own data directory. As a service that is SYSTEM, already granted
            // above; in console or portable mode it is an ordinary user, and without this the very
            // next write fails with access denied. Adding the running identity is not a weakening:
            // the account already has whatever rights it needs to have started the agent at all.
            using (WindowsIdentity current = WindowsIdentity.GetCurrent())
            {
                if (current.User is { } currentUser && currentUser != system)
                {
                    Grant(security, currentUser);
                }
            }

            directory.SetAccessControl(security);

            _logger.LogInformation(
                "Data directory {Path} restricted to SYSTEM, Administrators and the account the agent " +
                "runs as. Inherited permissions were removed.",
                path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "Could not restrict permissions on {Path}: access denied. This is expected when running " +
                "unelevated (portable mode). The directory keeps its inherited permissions, so treat it as " +
                "readable by local users.",
                path);
            return false;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "Could not restrict permissions on {Path}.", path);
            return false;
        }
    }

    private static void Grant(DirectorySecurity security, SecurityIdentifier identity) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    /// <summary>
    /// Applies <see cref="HardenDirectory"/> to every directory the agent writes to.
    /// </summary>
    public void HardenAll(AgentPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        HardenDirectory(paths.Root);
    }
}
