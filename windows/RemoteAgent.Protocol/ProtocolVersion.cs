namespace RemoteAgent.Protocol;

/// <summary>
/// Wire protocol version negotiation.
/// </summary>
/// <remarks>
/// Both peers advertise the range they support. A connection proceeds at
/// <c>min(theirMax, ourMax)</c> provided that value is at least both minimums;
/// otherwise the connection is refused with <see cref="ErrorCodes.VersionUnsupported"/>.
/// Adding an optional field to an existing message does NOT bump the version —
/// unknown fields are ignored by design. Removing a field, changing a field's
/// meaning, or adding a required field DOES bump it.
/// </remarks>
public static class ProtocolVersion
{
    /// <summary>The version this build speaks and emits.</summary>
    public const int Current = 1;

    /// <summary>The oldest version this build can still serve.</summary>
    public const int MinSupported = 1;

    /// <summary>
    /// Resolves the version two peers should use, or <c>null</c> when their
    /// supported ranges do not overlap.
    /// </summary>
    public static int? Negotiate(int peerMin, int peerMax)
    {
        if (peerMin > peerMax)
        {
            return null;
        }

        int agreed = Math.Min(peerMax, Current);
        return agreed >= Math.Max(peerMin, MinSupported) ? agreed : null;
    }

    /// <summary>Whether a received envelope's version is one we can process.</summary>
    public static bool IsSupported(int version) => version >= MinSupported && version <= Current;
}
