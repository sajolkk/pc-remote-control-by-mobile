using System.Runtime.Versioning;
using System.Threading;

namespace RemoteAgent.Session.Ui;

/// <summary>
/// Lets the Start-menu shortcut bring up the running agent's pairing window.
/// </summary>
/// <remarks>
/// <para>The shortcut starts <c>RemoteAgent.Session.exe</c> by hand, which has no spawn token and
/// so must never become an agent itself (see <see cref="Program"/>). Instead it sets this event and
/// exits, and the real agent, which is listening, reacts exactly as if its tray icon had been
/// double-clicked.</para>
///
/// <para>The event lives in the <c>Local\</c> namespace, which Windows keeps per logon session, so
/// a shortcut can only reach the agent on its own desktop. Setting it grants nothing that clicking
/// the tray icon does not: it opens pairing, and pairing still needs approval on this screen.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ShowRequestSignal
{
    private const string EventName = @"Local\PCRemote.Session.ShowRequest";

    /// <summary>Creates (or opens) the event the running agent waits on.</summary>
    public static EventWaitHandle CreateListener() =>
        new(initialState: false, EventResetMode.AutoReset, EventName);

    /// <summary>
    /// Signals the running agent in this session. Returns false when no agent is listening.
    /// </summary>
    public static bool TrySignal()
    {
        if (!EventWaitHandle.TryOpenExisting(EventName, out EventWaitHandle? handle))
        {
            return false;
        }

        using (handle)
        {
            return handle.Set();
        }
    }
}
