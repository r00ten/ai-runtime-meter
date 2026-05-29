using System.Net;
using System.Net.Sockets;

namespace DerivityMeter.Infrastructure;

/// <summary>
/// Selects a free loopback port from a fixed, explicit candidate list.
/// Never scans broad ranges, never binds to 0.0.0.0, never inspects or modifies processes.
///
/// Allowed candidates:
///   OTEL HTTP : configured port (if > 0), 14318, 4318, 14319–14325
///   MCP TCP   : configured port (if > 0), 7891, 7892–7895
///
/// If no candidate is available, throws with a clear message telling the user how to configure a port.
/// </summary>
public static class LoopbackEndpointSelector
{
    private static readonly int[] OtelCandidates = { 14318, 4318, 14319, 14320, 14321, 14322, 14323, 14324, 14325 };
    private static readonly int[] McpCandidates  = { 7891, 7892, 7893, 7894, 7895 };

    public static int SelectOtelPort(int configured)
    {
        var candidates = configured > 0
            ? new[] { configured }.Concat(OtelCandidates)
            : OtelCandidates;

        return Select(candidates, "OTEL HTTP",
            "Set Otel.OtelHttpPort in %USERPROFILE%\\.derivity\\runtime-meter\\settings.json to a free port.");
    }

    public static int SelectMcpPort(int configured)
    {
        var candidates = configured > 0
            ? new[] { configured }.Concat(McpCandidates)
            : McpCandidates;

        return Select(candidates, "MCP TCP",
            "Set McpPort in %USERPROFILE%\\.derivity\\runtime-meter\\settings.json to a free port.");
    }

    private static int Select(IEnumerable<int> candidates, string label, string configHint)
    {
        foreach (var port in candidates)
        {
            if (IsAvailableOnLoopback(port)) return port;
        }

        throw new InvalidOperationException(
            $"No available loopback port found for {label}. " +
            $"Candidates tried: {string.Join(", ", candidates)}. " +
            configHint);
    }

    private static bool IsAvailableOnLoopback(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch { return false; }
    }
}
