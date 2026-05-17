using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Orayo.Models;

namespace Orayo.Services;

public sealed class ServerLatencyService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public async Task<LatencyProbeResult> ProbeAsync(ServerEntry server, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(server.Host) || server.Port <= 0)
        {
            return LatencyProbeResult.Timeout();
        }

        try
        {
            using var tcpClient = new TcpClient();
            var startedAt = DateTimeOffset.UtcNow;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProbeTimeout);
            await tcpClient.ConnectAsync(server.Host, server.Port, timeoutCts.Token);
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            return LatencyProbeResult.Success((int)Math.Max(0, elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return LatencyProbeResult.Timeout();
        }
        catch
        {
            return LatencyProbeResult.Timeout();
        }
    }
}

public sealed record LatencyProbeResult(int? Milliseconds, bool TimedOut)
{
    public static LatencyProbeResult Success(int milliseconds) => new(milliseconds, false);

    public static LatencyProbeResult Timeout() => new(null, true);
}
