using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Configuration;
using FileOptions = RemoteAgent.Core.Configuration.FileOptions;

namespace RemoteAgent.Core.Files;

/// <summary>
/// Combines the built-in default roots with the configured ones into the list file management
/// actually enforces (§7.3).
/// </summary>
/// <remarks>
/// <para><b>Defaults plus configuration, with configuration winning.</b> When
/// <see cref="FileOptions.UseDefaultRoots"/> is on, the platform's defaults — the user's Desktop,
/// Documents, Downloads, Pictures, Videos and Music, and every fixed drive — are the starting
/// point, and <see cref="FileOptions.AllowedRoots"/> adds to them. A configured entry for the same
/// directory replaces the default one, which is how an owner keeps <c>C:\</c> browsable but
/// read-only. Turning the defaults off leaves exactly the configured list, and an empty list then
/// means file management is unavailable.</para>
///
/// <para><b>No policy means no access.</b> The options come from a delegate that may return null —
/// the session agent asks the service for them, and the service may not have answered. A null is
/// treated as "nothing is allowed" rather than "use the defaults": the defaults are the whole PC,
/// and falling back to the whole PC because a question went unanswered would turn a narrowed
/// configuration into no restriction at all.</para>
///
/// <para>A configured path is expanded from environment variables in the process that serves the
/// files — the session agent, running as the signed-in user — so <c>%USERPROFILE%</c> means that
/// user's profile and not the service account's. An entry that does not canonicalise to a plain
/// drive path (a UNC share, a relative path, an unresolved variable) is ignored.</para>
/// </remarks>
public sealed class ConfiguredFileRoots : IFileRootProvider
{
    private readonly Func<FileOptions?> _options;
    private readonly IDefaultFileRoots _defaults;
    private readonly Func<string, string> _expand;

    /// <summary>Creates the provider.</summary>
    /// <param name="options">The file options in force, or null when they are not known.</param>
    /// <param name="defaults">The platform's built-in roots.</param>
    /// <param name="expandEnvironmentVariables">
    /// Expands <c>%NAME%</c> references. Defaults to <see cref="Environment.ExpandEnvironmentVariables"/>.
    /// </param>
    public ConfiguredFileRoots(
        Func<FileOptions?> options,
        IDefaultFileRoots defaults,
        Func<string, string>? expandEnvironmentVariables = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
        _expand = expandEnvironmentVariables ?? Environment.ExpandEnvironmentVariables;
    }

    /// <inheritdoc />
    public IReadOnlyList<FileRoot> GetRoots()
    {
        FileOptions? options = _options();
        if (options is null)
        {
            return [];
        }

        var roots = new List<FileRoot>();

        if (options.UseDefaultRoots)
        {
            foreach (FileRoot root in _defaults.GetDefaultRoots())
            {
                if (FilePathPolicy.TryCanonicalize(root.Path, out string canonical, out _))
                {
                    AddOrReplace(roots, root with { Path = canonical });
                }
            }
        }

        foreach (AllowedRoot configured in options.AllowedRoots ?? [])
        {
            if (configured is null || string.IsNullOrWhiteSpace(configured.Path))
            {
                continue;
            }

            string expanded = _expand(configured.Path.Trim());

            // A variable that did not expand is still in the text. Using it literally would create
            // a root named after the variable, relative to wherever the process happens to be.
            if (expanded.Contains('%') || !FilePathPolicy.TryCanonicalize(expanded, out string canonical, out _))
            {
                continue;
            }

            string label = string.IsNullOrWhiteSpace(configured.Alias)
                ? FilePathPolicy.GetName(canonical)
                : configured.Alias.Trim();

            AddOrReplace(roots, new FileRoot(label, canonical, configured.Write));
        }

        return roots;
    }

    /// <summary>
    /// Finds the allowed root that governs a canonical path: the most specific one containing it.
    /// </summary>
    /// <remarks>
    /// Most specific, because roots nest — Documents lies inside <c>C:\</c> — and the narrower
    /// entry is the one an owner wrote with that folder in mind. Its read-only flag is the one that
    /// applies.
    /// </remarks>
    public static FileRoot? FindGoverningRoot(string canonicalPath, IReadOnlyList<FileRoot> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        FileRoot? best = null;
        foreach (FileRoot root in roots)
        {
            if (FilePathPolicy.IsWithin(canonicalPath, root.Path) &&
                (best is null || root.Path.Length > best.Path.Length))
            {
                best = root;
            }
        }

        return best;
    }

    private static void AddOrReplace(List<FileRoot> roots, FileRoot root)
    {
        int existing = roots.FindIndex(r => string.Equals(r.Path, root.Path, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            roots[existing] = root;
        }
        else
        {
            roots.Add(root);
        }
    }
}
