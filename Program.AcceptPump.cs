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

// ─── Daemon lifecycle: RunDaemon, accept pump, per-request handler ───
internal static partial class Program
{
    private static int RunDaemon()
    {
        _startedUtc = DateTime.UtcNow;
        _rgAvailable = ProbeRipgrep();
        if (!_rgAvailable)
            DaemonLog.Info("ripgrep (rg) not found on PATH — managed C# search backend will be used (no degradation)");
        DaemonLog.Info($"flaxmcp-nav v{Version} starting");

        // B7: ensure ThreadPool can handle burst concurrency without starvation.
        // When all _gate slots are occupied and handlers block on I/O, the accept
        // pump's IOCP continuations need ready threads. Default min = CPU count is
        // too low for 4-agent bursts (16+ concurrent connections). 32 workers + 32
        // IOCP ensures the pump never starves.
        ThreadPool.SetMinThreads(32, 32);

        using var instanceMutex = new Mutex(initiallyOwned: false, @"Global\flaxmcp-nav-daemon", out _);
        bool owns = false;
        try { owns = instanceMutex.WaitOne(2000); } catch (AbandonedMutexException) { owns = true; }
        if (!owns) { DaemonLog.Info("another instance running; exiting"); return 0; }

        using var cts = new CancellationTokenSource();
        var shutdownEvent = new ManualResetEventSlim(false);
        // Wire shutdownEvent to cts so any cts.Cancel() (Ctrl+C, __shutdown__, etc.)
        // wakes the main thread — not just the Ctrl+C path that explicitly calls Set().
        cts.Token.Register(() => shutdownEvent.Set());
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    // Non-blocking warmup: daemon accepts connections immediately,
    // indexes build in background with timeout. First docs/flax_api query
    // pays cold latency if timeout fires. FlaxApi failure (missing XML) does
    // NOT block _warmed - flax_api/* dispatch returns clear flax_api_xml_missing
    // error; C# and docs queries still work.
    _ = Task.Run(() =>
    {
        try { WarmWorkspaceWithTimeout(60_000); }
        catch (Exception ex) { SwallowedCatch.Record("NavDaemon.StartupWarmup", ex); }
    });

        // Start async accept pump. It runs on the ThreadPool and uses IOCP
        // for WaitForConnectionAsync — no thread is blocked while waiting for
        // the next client. This means slow atomics (2.6-4s) cannot prevent
        // new connections from being accepted concurrently.
        var acceptTask = Task.Run(() => AcceptPumpAsync(cts.Token, cts));

        // Block main thread until shutdown signal (Ctrl+C or __shutdown__)
        shutdownEvent.Wait();

        cts.Cancel();

        try { acceptTask.GetAwaiter().GetResult(); }
        catch (OperationCanceledException ex) { SwallowedCatch.Record("NavDaemon.RunDaemon.cancel", ex); }
        catch (AggregateException ae) when (ae.InnerException is OperationCanceledException) { SwallowedCatch.Record("NavDaemon.RunDaemon.aggregateCancel", ae); }

        DaemonLog.Info($"stopped after {(DateTime.UtcNow - _startedUtc).TotalSeconds:F1}s");
        return 0;
    }

    // 2026-08-03: the pump used to keep exactly ONE outstanding pipe instance —
    // create an instance, await a connection, dispatch, loop. A client can only
    // connect while an instance is actually pending, so concurrent callers
    // serialized behind each re-arm. On a loaded machine that round trip takes
    // long enough that clients hit their 2s connect timeout and reported the
    // daemon as "not running" while it was healthy. Several agents navigating at
    // once is the normal case here, so keep a pool of instances pre-posted.
    private const int AcceptorCount = 16;

    private static int _acceptAnnounced;

    /// <summary>Async accept pump — a pool of always-pending pipe instances so
    /// concurrent clients connect without waiting for a re-arm. Each connected
    /// client is dispatched to a background Task.Run (HandleOneRequest).</summary>
    private static async Task AcceptPumpAsync(CancellationToken ct, CancellationTokenSource cts)
    {
        var acceptors = new Task[AcceptorCount];
        for (int i = 0; i < AcceptorCount; i++)
            acceptors[i] = Task.Run(() => AcceptLoopAsync(ct, cts), CancellationToken.None);
        await Task.WhenAll(acceptors).ConfigureAwait(false);
    }

    private static async Task AcceptLoopAsync(CancellationToken ct, CancellationTokenSource cts)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);

                if (Interlocked.Exchange(ref _acceptAnnounced, 1) == 0)
                    DaemonLog.Info($"listening on \\\\.\\pipe\\{PipeName} ({AcceptorCount} acceptors)");

                await pipe.WaitForConnectionAsync(ct);

                long id = Interlocked.Increment(ref _reqId);
                var p = pipe;
                // B7: use Task.Run for the handler but the handler itself
                // is async-bound — _gate.WaitAsync never blocks a ThreadPool
                // thread, so the accept pump's IOCP resumption is never
                // starved, even under 4-agent bursts.
                _ = Task.Run(async () =>
                {
                    try { await HandleOneRequestAsync(p, cts, id).ConfigureAwait(false); }
                    catch (Exception ex) { SwallowedCatch.Record("NavDaemon.HandleOneRequest", ex); }
                });
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                DaemonLog.Error($"accept pump: {ex.Message}");
                pipe?.Dispose();
                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(200, ct); } catch (Exception retryEx) { SwallowedCatch.Record("NavDaemon.AcceptPumpRetry", retryEx); break; }
            }
        }
    }

    private static async Task HandleOneRequestAsync(NamedPipeServerStream pipe, CancellationTokenSource cts, long id)
    {
        Interlocked.Increment(ref _activeHandlers);
        int peak;
        while (_activeHandlers > (peak = Volatile.Read(ref _peakHandlers)))
            Interlocked.CompareExchange(ref _peakHandlers, _activeHandlers, peak);
        string atomic = "<unknown>", outcome = "err";
        Stopwatch? sw = null;
        try
        {
            sw = Stopwatch.StartNew();
            string? line = await ReadBoundedLineAsync(pipe, MaxRequestBytes).ConfigureAwait(false);
            if (line == null) { outcome = "empty"; return; }
            var req = TryParseJObject(line);
            if (req == null) { WriteErr(pipe, "bad_json", "malformed JSON"); outcome = "bad_json"; return; }
            string name = req["atomic"]?.ToString() ?? "";
            // B5: reject non-object args with a clean error
            var argsToken = req["args"];
            if (argsToken != null && argsToken.Type != JTokenType.Object && argsToken.Type != JTokenType.Null)
            {
                WriteErr(pipe, "invalid_args_type", "args must be a JSON object, got " + argsToken.Type);
                outcome = "invalid_args";
                return;
            }
            var args = req["args"] as JObject ?? new JObject();
            atomic = name;

            JObject response;
            // Control verbs short-circuit — handled early, before any atomic logic
            if (IsControlVerb(name))
            {
                switch (name)
                {
                    case "__ping__": response = BuildPing(); outcome = "ok"; break;
                    case "__status__": response = BuildStatus(); outcome = "ok"; break;
                    case "__help__": response = BuildHelp(); outcome = "ok"; break;
                    case "__warmup__":
                        if (!_warmed)
                        {
                            _ = Task.Run(() =>
                            {
                                try { WarmWorkspaceWithTimeout(60_000); }
                                catch (Exception ex) { SwallowedCatch.Record("NavDaemon.WarmupTrigger", ex); }
                            });
                        }
                        response = new JObject
                        {
                            ["ok"] = true,
                            ["warmed"] = _warmed,
                            ["note"] = _warmed
                                ? "Already warm."
                                : "Warmup triggered in background. Next docs/flax_api query will be fast.",
                        };
                        outcome = "ok";
                        break;
                    case "__health__": response = BuildHealth(); outcome = "ok"; break;
                    case "__shutdown__":
                        if (_campaignLocked)
                        {
                            response = Err("shutdown_refused_campaign_locked",
                                $"Campaign lock held by pid={_campaignOwnerPid?.ToString() ?? "?"} ({_campaignReason}) since {_campaignLockedAtUtc:O}. Call __campaign_unlock__ first, or stop the process at the OS level (Stop-Process).");
                            outcome = "shutdown_refused_campaign_locked";
                        }
                        else if ((bool?)req["force"] != true)
                        {
                            response = Err("shutdown_refused", "Pass force=true");
                            outcome = "shutdown_refused";
                        }
                        else
                        {
                            response = new JObject { ["ok"] = true, ["shutdown"] = true, ["uptimeSeconds"] = Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1) };
                            outcome = "shutdown";
                            cts.Cancel();
                        }
                        break;
                    case "__campaign_lock__":
                        lock (_campaignGate)
                        {
                            _campaignLocked = true;
                            _campaignOwnerPid = (int?)req["ownerPid"];
                            _campaignReason = (string?)req["reason"] ?? "(no reason given)";
                            _campaignLockedAtUtc = DateTime.UtcNow;
                        }
                        response = new JObject
                        {
                            ["ok"] = true,
                            ["campaignLocked"] = true,
                            ["ownerPid"] = _campaignOwnerPid,
                            ["reason"] = _campaignReason,
                        };
                        outcome = "ok";
                        break;
                    case "__campaign_unlock__":
                        lock (_campaignGate)
                        {
                            _campaignLocked = false;
                            _campaignOwnerPid = null;
                            _campaignReason = null;
                            _campaignLockedAtUtc = null;
                        }
                        response = new JObject { ["ok"] = true, ["campaignLocked"] = false };
                        outcome = "ok";
                        break;
                    default: response = Err("unknown_control_verb", name); outcome = "err"; break;
                }
                response["atomic"] = name;
                try { PipeWriteJson(pipe, response); }
                catch (IOException ioex) { DaemonLog.Warn($"req#{id} pipe broken (client disconnected — likely execFile timeout): {ioex.Message}"); outcome = "pipe_broken"; }
                sw.Stop();
                DaemonLog.Info($"req#{id} {outcome} '{atomic}' {sw.ElapsedMilliseconds}ms");
                return;
            }
            if (!IsKnownAtomic(name))
            {
                if (!_warmed)
                {
                    // Fire warmup on background thread - don't block a handler slot
                    _ = Task.Run(() =>
                    {
                        try { WarmWorkspaceWithTimeout(30_000); }
                        catch (Exception ex) { SwallowedCatch.Record("NavDaemon.WarmupOnUnknown", ex); }
                    });
                    response = Err("warming", $"Indexes not yet built. Retry in a few seconds.");
                    response["terminal"] = false;
                    response["retryable"] = true;
                    response["warming"] = true;
                    response["atomic"] = name;
                    try { PipeWriteJson(pipe, response); }
                    catch (IOException ioex) { DaemonLog.Warn($"req#{id} pipe broken: {ioex.Message}"); }
                    sw.Stop();
                    DaemonLog.Info($"req#{id} warming '{atomic}' {sw.ElapsedMilliseconds}ms");
                    outcome = "warming";
                    return;
                }
                // Post-warmup: unknown atomic is terminal — no retry
                response = Err("unsupported_atomic",
                    $"'{name}' is not a supported atomic. Available atomics: {string.Join(", ", KnownAtomics)}");
                response["terminal"] = true;
                response["retryable"] = false;
                response["availableAtomics"] = new JArray(KnownAtomics);
                response["atomic"] = name;
                try { PipeWriteJson(pipe, response); }
                catch (IOException ioex) { DaemonLog.Warn($"req#{id} pipe broken: {ioex.Message}"); }
                sw.Stop();
                DaemonLog.Info($"req#{id} unsupported_atomic '{atomic}' {sw.ElapsedMilliseconds}ms");
                outcome = "unsupported_atomic";
                return;
            }

            // B7: start pipe monitor and wire up request cancellation BEFORE
            // waiting on the gate. This way, if the client disconnects while
            // all gate slots are busy, the gate wait is cancelled and the slot
            // freed immediately — instead of blocking a handler until the
            // 2000ms client probe timeout.
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            _requestCt.Value = requestCts.Token;
            // Server-side deadline on EVERY request — a request may never hold a
            // handler slot forever. Clients give up at 60s (plugin/CLI read),
            // so the 55s default only reclaims slots that are
            // already dead to their caller. Explicit timeoutMs can shorten or
            // extend within [1s, 120s].
            long? reqTimeoutMs = (long?)req["timeoutMs"];
            requestCts.CancelAfter((int)Math.Clamp(reqTimeoutMs ?? 55_000, 1000, 120_000));
            StartPipeMonitor(pipe, requestCts);
            // Async wait — does NOT block a ThreadPool thread while waiting
            // for a gate slot. Combined with ThreadPool.SetMinThreads(32,32)
            // at startup, this prevents the accept pump's IOCP continuations
            // from being starved during 4-agent bursts.
            try { await _gate.WaitAsync(requestCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                response = Err(requestCts.IsCancellationRequested && !cts.IsCancellationRequested
                    ? "client_disconnected" : "shutdown_in_progress",
                    requestCts.IsCancellationRequested && !cts.IsCancellationRequested
                        ? "Request cancelled while waiting for a handler slot (client disconnected or request deadline exceeded) — slot freed"
                        : "Daemon is shutting down");
                response["atomic"] = name;
                try { PipeWriteJson(pipe, response); }
                catch (IOException ioex) { DaemonLog.Warn($"req#{id} pipe broken: {ioex.Message}"); }
                outcome = "shutdown_cancelled";
                sw.Stop();
                DaemonLog.Info($"req#{id} {outcome} '{atomic}' {sw.ElapsedMilliseconds}ms");
                return;
            }

            try
            {
                Interlocked.Increment(ref _totalServed);
                response = DispatchAtomic(name, args);
            }
            catch (Exception ex)
            {
                response = ExceptionErr("atomic_handler_failed", $"Handler threw: {ex.Message}", ex);
                outcome = "handler_exception";
            }
            finally { _gate.Release(); }
            if (outcome != "handler_exception")
                outcome = (bool?)response["ok"] == true ? "ok" : "err";
            response["atomic"] = name;
                try { PipeWriteJson(pipe, response); }
            catch (IOException ioex) { DaemonLog.Warn($"req#{id} pipe broken (client disconnected — likely execFile timeout): {ioex.Message}"); outcome = "pipe_broken"; }
            sw.Stop();
            DaemonLog.Info($"req#{id} {outcome} '{atomic}' {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            DaemonLog.Error($"req#{id} handler threw: {ex.Message}");
            try
            {
                var err = ExceptionErr("handler_failed", $"Unhandled exception: {ex.Message}", ex);
                err["atomic"] = atomic;
                PipeWriteJson(pipe, err);
            }
            catch (Exception pipeEx) { DaemonLog.Warn($"handler write err: {pipeEx.Message}"); /* last-resort — pipe may be broken */ }
        }
        finally { Interlocked.Decrement(ref _activeHandlers); pipe.Dispose(); }
    }

    private static void WriteErr(NamedPipeServerStream pipe, string code, string msg)
    {
        try { PipeWriteJson(pipe, Err(code, msg)); } catch (Exception wex) { DaemonLog.Warn($"write err: {wex.Message}"); }
    }
}

