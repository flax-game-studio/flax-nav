using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
namespace FlaxMcp.NavDaemon;

// ─── Workspace warmup: docs + FlaxApi index building ───
internal static partial class Program
{
    private static void WarmWorkspaceWithTimeout(int timeoutMs)
    {
        if (_warmed) return;
        lock (_warmLock)
        {
            if (_warmed) return;
            using var cts = timeoutMs == Timeout.Infinite
                ? new CancellationTokenSource()
                : new CancellationTokenSource(timeoutMs);
            try
            {
                string root = ResolveRepoRoot();

                var docsTask = Task.Run(() =>
                {
                    DocsIndex.EnsureBuilt(root);
                }, cts.Token);

                var flaxTask = Task.Run(() =>
                {
                    FlaxApiIndex.EnsureBuilt();
                }, cts.Token);

                // 2026-08-03: warmup built the docs and FlaxApi indexes but not
                // the C# source index — the one csharp/find_definition,
                // csharp/find_references and csharp/symbol_search actually
                // query. So a freshly started daemon reported ready/warmed while
                // the first csharp/* call still paid the whole build (2.4s warm
                // OS cache, ~13s cold). Since the daemon is normally spawned by
                // the editor plugin the moment a session opens, building it here
                // means it is ready before the first real query arrives.
                var csharpTask = Task.Run(() =>
                {
                    GetSourceIndex(root);
                }, cts.Token);

                if (!docsTask.Wait(timeoutMs == Timeout.Infinite ? -1 : timeoutMs, cts.Token))
                {
                    DaemonLog.Warn($"docs index warmup timed out after {timeoutMs}ms (non-fatal)");
                }
                if (!flaxTask.Wait(timeoutMs == Timeout.Infinite ? -1 : timeoutMs, cts.Token))
                {
                    DaemonLog.Warn($"FlaxApi index warmup timed out after {timeoutMs}ms (non-fatal)");
                }
                if (!csharpTask.Wait(timeoutMs == Timeout.Infinite ? -1 : timeoutMs, cts.Token))
                {
                    DaemonLog.Warn($"C# source index warmup timed out after {timeoutMs}ms (non-fatal)");
                }
            }
            catch (OperationCanceledException)
            {
                DaemonLog.Warn($"warm workspace timed out after {timeoutMs}ms (non-fatal)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DaemonLog.Warn($"warm workspace failed: {ex.Message}");
            }
            _warmed = true;
            DaemonLog.Info("workspace warmed on demand");
        }
    }

    private static int Warm()
    {
        string root = ResolveRepoRoot();

        Console.WriteLine("Warming Roslyn...");
        try { CsharpSymbolSearch(new JObject { ["query"] = "IFlaxBridge", ["maxResults"] = 1 }); }
        catch (Exception ex) { DaemonLog.Warn($"Roslyn warm: {ex.Message}"); }

        Console.WriteLine("Warming docs index...");
        try { DocsIndex.EnsureBuilt(root); }
        catch (Exception ex) { DaemonLog.Warn($"docs warm: {ex.Message}"); }

        Console.WriteLine("Warming capability index...");
        try { DocsFindCapability(new JObject { ["query"] = "bridge", ["maxResults"] = 1 }); }
        catch (Exception ex) { DaemonLog.Warn($"capability warm: {ex.Message}"); }

        Console.WriteLine("Warming Flax API index...");
        try { FlaxApiIndex.EnsureBuilt(); }
        catch (Exception ex) { DaemonLog.Warn($"Flax API warm: {ex.Message}"); }

        Console.WriteLine("All 4 indexes warmed.");
        return 0;
    }
}

