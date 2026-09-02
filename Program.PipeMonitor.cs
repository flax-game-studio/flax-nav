using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
namespace FlaxMcp.NavDaemon;

// ─── Per-request pipe health monitor: cancels handler on disconnect ───
internal static partial class Program
{
    /// <summary>Starts a background loop that monitors pipe health.
    /// Cancels <paramref name="requestCts"/> when the client pipe disconnects,
    /// letting the handler bail early instead of hanging until timeout.
    /// B7: polls every 50ms (was 200ms) — detects client disconnect faster,
    /// freeing gate slots and reducing handler pile-up during bursts.</summary>
    private static void StartPipeMonitor(NamedPipeServerStream pipe, CancellationTokenSource requestCts)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!requestCts.IsCancellationRequested)
                {
                    await Task.Delay(50, requestCts.Token).ConfigureAwait(false);
                    if (!pipe.IsConnected) { requestCts.Cancel(); break; }
                }
            }
            catch (OperationCanceledException ex) { SwallowedCatch.Record("NavDaemon.StartPipeMonitor", ex); /* expected on cancellation */ }
            catch (Exception ex) { SwallowedCatch.Record("NavDaemon.PipeMonitor", ex); requestCts.Cancel(); }
        });
    }
}

