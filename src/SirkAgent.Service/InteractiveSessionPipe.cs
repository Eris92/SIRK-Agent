using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SirkAgent.Service;

internal static class InteractiveSessionPipe
{
    private sealed record SessionDescriptor(int sessionId, bool active, bool helperAvailable, int processId);

    private const string Prefix = "SIRK-Agent-Interactive-Session-";

    internal static string Name(int sessionId) => Prefix + sessionId;
    internal static string ActiveName() => Name(Resolve(null));

    internal static int Resolve(int? requested)
    {
        if (requested is >= 0 and <= 65535) return requested.Value;
        if (requested is not null) throw new InvalidDataException("Nieprawidłowy identyfikator sesji.");
        var active = WTSGetActiveConsoleSessionId();
        if (active == uint.MaxValue) throw new InvalidOperationException("Brak aktywnej sesji konsoli.");
        return checked((int)active);
    }

    internal static object[] Sessions()
    {
        var active = WTSGetActiveConsoleSessionId();
        return Process.GetProcessesByName("SirkAgent.Session")
            .Select(process =>
            {
                try
                {
                    return new SessionDescriptor(
                        process.SessionId,
                        active != uint.MaxValue && process.SessionId == (int)active,
                        true,
                        process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            })
            .GroupBy(item => item.sessionId)
            .Select(group => group.First())
            .OrderBy(item => item.sessionId)
            .Cast<object>()
            .ToArray();
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
