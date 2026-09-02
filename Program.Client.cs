using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace FlaxMcp.NavDaemon;

// ─── Client-side: one-shot queries, daemon auto-start ───
internal static partial class Program
{
    // Cold index loads, managed source scans, and queued concurrent requests
    // can exceed the old 8s ceiling. Keep the one-shot client aligned with the
    // daemon's 55s request deadline and the proxy's 60s fetch timeout.
    private const int ClientReadTimeoutMs = 60_000;
    private static volatile int _spawnWaiters;
    private const int SpawnMaxWaitSec = 60;

    // How long to let an already-in-flight spawn finish before starting our own.
    // Short on purpose: this is pure latency on every cold start.
    private const int SpawnGraceSec = 3;

    // Second-chance connect window once the pipe is known to exist. Well inside
    // the 60s read timeout above, so a busy daemon still gets answered rather
    // than being declared dead.
    private const int BusyConnectTimeoutMs = 15_000;

    /// <summary>
    /// Whether the daemon's named pipe is present. Distinguishes "daemon is
    /// alive but every instance is busy" from "no daemon at all" — a connect
    /// timeout alone cannot tell those apart.
    /// </summary>
    private static bool PipeExists()
    {
        // Direct path query, not an enumeration of \\.\pipe\ — that listing walks
        // every named pipe on the machine and is far too slow to sit on the
        // connect path when several clients hit it at once.
        try { return File.Exists($@"\\.\pipe\{PipeName}"); }
        catch (Exception ex) { DaemonLog.Warn($"pipe probe failed: {ex.Message}"); return false; }
    }

    private static int RunClient(string[] args, bool autoStart = false)
    {
        if (args.Length == 0) { Console.Error.WriteLine("no atomic specified"); PrintHelp(); return 1; }
        string atomic = args[0];
        if (IsControlVerb(atomic))
        {
            var cvReq = new JObject { ["atomic"] = atomic, ["args"] = ParseClientArgs(args[1..]) };
            return SendOneShot(cvReq, autoStart);
        }
        if (!IsKnownAtomic(atomic))
        {
            var errResp = new JObject
            {
                ["ok"] = false,
                ["errorCode"] = "unsupported_atomic",
                ["errorMessage"] = $"'{atomic}' is not a supported atomic. Available atomics: {string.Join(", ", KnownAtomics)}",
                ["terminal"] = true,
                ["retryable"] = false,
                ["availableAtomics"] = new JArray(KnownAtomics),
                ["atomic"] = atomic,
            };
            WriteStdoutUtf8(errResp.ToString(Formatting.None, NoConverters));
            return 3;
        }
        JObject queryArgs = ParseClientArgs(args[1..]);
        var req = new JObject { ["atomic"] = atomic, ["args"] = queryArgs };
        return SendOneShot(req, autoStart);
    }

    private static int SendOneShot(JObject request, bool autoStart)
    {
        bool daemonAlive = false;
        try
        {
            // Sync-mode needed for ReadTimeout; we do synchronous I/O anyway.
            using var probe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            // 2000ms connect — gives the daemon server time to create a new pipe
            // instance during busy periods (Long atomics + 8-concurrent _gate).
            try { probe.Connect(2000); return WriteRead(probe, request); }
            catch (TimeoutException) { DaemonLog.Warn("first probe timed out"); }
        }
        catch (Exception pex) { DaemonLog.Warn($"probe failed: {pex.Message}"); }

        // 2026-08-03: a connect timeout used to end the call right here with
        // "daemon not running" (exit 10). But the timeout only says no pipe
        // instance came free inside the window — on a loaded machine the accept
        // pump routinely needs longer, and callers were told the daemon was down
        // while it was alive and answering every request it received. That was a
        // real failure mode for agents. Tell "busy" apart from "absent" by
        // whether the pipe exists, and give a live daemon a longer window.
        if (PipeExists())
        {
            daemonAlive = true;
            try
            {
                using var retry = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                retry.Connect(BusyConnectTimeoutMs);
                return WriteRead(retry, request);
            }
            catch (TimeoutException) { DaemonLog.Warn($"daemon busy; connect retry timed out after {BusyConnectTimeoutMs}ms"); }
            catch (Exception rex) { DaemonLog.Warn($"connect retry failed: {rex.Message}"); }
        }

        if (!autoStart)
        {
            Console.Error.WriteLine(daemonAlive
                ? $"daemon is running but did not accept a connection within {BusyConnectTimeoutMs}ms (busy)"
                : "daemon not running. Use --daemon");
            return 10;
        }

        if (!EnsureDaemonReady(out string err))
        {
            Console.Error.WriteLine($"flaxmcp-nav: {err}");
            return 13;
        }

        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { pipe.Connect(1000); } catch (TimeoutException) { Console.Error.WriteLine("daemon vanished"); return 13; }
        return WriteRead(pipe, request);
    }

    private static bool EnsureDaemonReady(out string error)
    {
        error = "";
        if (PingDaemon(500)) return true;
        Interlocked.Increment(ref _spawnWaiters);
        try
        {
            // 2026-08-03: this used to sleep out the WHOLE SpawnMaxWaitSec
            // window polling for a daemon before it would spawn one itself — so
            // a genuinely cold call (nobody else spawning, which is the common
            // case since each CLI invocation is its own process) cost a flat 60s
            // of nothing before the daemon was even started. Measured: 64.2s for
            // `--auto-start __ping__` against a stopped daemon.
            //
            // Give a concurrent spawner a short head start instead, then spawn.
            // A redundant spawn is harmless: the daemon takes a global mutex and
            // a second instance exits immediately ("another instance running").
            var grace = DateTime.UtcNow.AddSeconds(SpawnGraceSec);
            while (DateTime.UtcNow < grace)
            {
                if (PingDaemon(500)) return true;
                Thread.Sleep(250);
            }
            SpawnDaemon();
            var ready = DateTime.UtcNow.AddSeconds(SpawnMaxWaitSec);
            while (DateTime.UtcNow < ready) { if (PingDaemon(1000)) return true; Thread.Sleep(1500); }
            error = "daemon didn't become ready"; return false;
        }
        finally { Interlocked.Decrement(ref _spawnWaiters); }
    }

    private static bool PingDaemon(int ms)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { pipe.Connect(ms); } catch (TimeoutException) { return false; }
            string? line = SendAndRecv(pipe, new JObject { ["atomic"] = "__ping__" }.ToString(Formatting.None, NoConverters));
            if (line == null) return false;
            var r = TryParseJObject(line);
            return r != null && (bool?)r["ok"] == true && (bool?)r["pong"] == true;
        }
        catch (Exception pex) { DaemonLog.Warn($"ping: {pex.Message}"); return false; }
    }

    private static void SpawnDaemon()
    {
        try
        {
            // SECURITY trust-boundary: self-respawn of flaxmcp-nav.exe
            // (Environment.ProcessPath = this same verified binary; the daemon
            // re-exec is intentional and the child is us). No SHA-256 pin needed
            // — the executable we are already running. UseShellExecute=true is
            // required for a detached hidden console; the child takes a global
            // mutex so a redundant spawn exits immediately.
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                ArgumentList = { "--daemon" },
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex) { DaemonLog.Warn($"spawn failed: {ex.Message}"); }
    }

    private static int WriteRead(NamedPipeClientStream pipe, JObject request)
    {
        string json = request.ToString(Formatting.None, NoConverters);
        string? line;
        try { line = SendAndRecv(pipe, json); }
        catch (IOException) { Console.Error.WriteLine("pipe broken"); return 14; }
        if (line == null) { Console.Error.WriteLine("no response"); return 12; }
        line = CapOutput(line);
        var parsed = TryParseJObject(line);
        if (parsed == null)
        {
            DaemonLog.Warn("write read parse: malformed JSON");
            try
            {
                WriteStdoutUtf8(new JObject
                {
                    ["ok"] = false,
                    ["errorCode"] = "malformed_response",
                    ["errorMessage"] = "The nav daemon returned malformed JSON.",
                    ["retryable"] = true,
                }.ToString(Formatting.None, NoConverters));
            }
            catch (IOException) { DaemonLog.Warn("stdout write failed"); }
            return 15;
        }
        // Write raw UTF-8 bytes directly to stdout — bypasses Console.Out
        // encoding which may default to OEM codepage on Windows, corrupting
        // Unicode characters in JSON output (e.g. box-drawing ─ → â"€ garble).
        // Write raw bytes to the underlying stdout stream. Do not route this
        // through a StreamWriter: its default UTF-8 encoder can emit a BOM,
        // which Windows PowerShell may transcode into visible prefix bytes.
        try
        {
            WriteStdoutUtf8(line);
        }
        catch (IOException) { DaemonLog.Warn("stdout write failed"); }
        return (bool?)parsed["ok"] == true ? 0 : 3;
    }

    private static void WriteStdoutUtf8(string line)
    {
        byte[] utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(line + "\n");
        Console.OpenStandardOutput().Write(utf8, 0, utf8.Length);
    }

    private static string? SendAndRecv(NamedPipeClientStream pipe, string json)
    {
        using (var w = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true }) w.WriteLine(json);
        // Use a timeout task instead of pipe.ReadTimeout (which throws on
        // async pipe streams and is unreliable on sync-mode pipe streams).
        using var r = new StreamReader(pipe, leaveOpen: true);
        var readTask = Task.Run(() => r.ReadLine());
        if (!readTask.Wait(TimeSpan.FromMilliseconds(ClientReadTimeoutMs)))
        {
            DaemonLog.Warn($"client read timed out after {ClientReadTimeoutMs}ms");
            return null;
        }
        return readTask.GetAwaiter().GetResult();
    }

    private static string CapOutput(string line)
    {
        int cap = 32_000;
        if (line.Length <= cap) return line;
        var root = TryParseJObject(line);
        if (root == null)
        {
            DaemonLog.Warn("cap parse: malformed JSON");
            return new JObject
            {
                ["ok"] = false,
                ["errorCode"] = "malformed_response",
                ["errorMessage"] = "The nav daemon returned malformed JSON.",
                ["retryable"] = true,
            }.ToString(Formatting.None, NoConverters);
        }
        try
        {
            string atomic = root["atomic"]?.ToString() ?? "?";
            string[] candidateKeys = { "results", "hits", "tools", "values", "unmapped", "units", "countDrift" };
            JArray? arr = null;
            string foundKey = candidateKeys[0];
            foreach (var k in candidateKeys)
            {
                if (root[k] is JArray a) { arr = a; foundKey = k; break; }
            }
            if (arr == null)
            {
                root["truncated"] = true;
                root["returnedCount"] = 0;
                return root.ToString(Formatting.None, NoConverters);
            }
            int originalCount = arr.Count;
            var indexed = arr.Cast<JObject>().Select((jt, i) => (
                Item: (JToken)jt,
                Index: i,
                JsonLen: jt.ToString(Formatting.None, NoConverters).Length
            )).ToList();
            var sortedBySizeDesc = indexed.OrderByDescending(x => x.JsonLen).ToList();
            int bytesToRemove = line.Length - cap;
            int totalRemoved = 0;
            foreach (var largest in sortedBySizeDesc)
            {
                if (totalRemoved >= bytesToRemove) break;
                arr.Remove(largest.Item);
                totalRemoved += largest.JsonLen;
            }
            int returnedCount = arr.Count;
            root[foundKey] = arr;
            root["truncated"] = true;
            root["originalCount"] = originalCount;
            root["returnedCount"] = returnedCount;
            return root.ToString(Formatting.None, NoConverters);
        }
        catch (Exception cex)
        {
            DaemonLog.Warn($"cap truncation: {cex.Message}");
            return new JObject
            {
                ["ok"] = false,
                ["errorCode"] = "response_truncation_failed",
                ["errorMessage"] = "The nav daemon response exceeded the output cap and could not be safely truncated.",
                ["retryable"] = false,
            }.ToString(Formatting.None, NoConverters);
        }
    }
}

