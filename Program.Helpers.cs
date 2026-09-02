using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace FlaxMcp.NavDaemon;

// ─── Shared helpers: I/O, utilities, response builders ───
internal static partial class Program
{
    /// <summary>Backend name for csharp/* atomics. Always "managed" —
    /// rg subprocess is not required for any csharp/* operation.</summary>
    private const string CsharpBackendName = "managed";

    /// <summary>Async bounded line read — never blocks a ThreadPool thread.
    /// Uses ReadAsync with true async/await; the old sync-over-async
    /// ReadBoundedLine caused ThreadPool starvation under concurrent bursts.</summary>
    private static async Task<string?> ReadBoundedLineAsync(Stream stream, int maxBytes)
    {
        using var buf = new MemoryStream(capacity: Math.Min(4096, maxBytes));
        var rb = new byte[4096];
        using var readTimeoutCts = new CancellationTokenSource(30_000);
        while (true)
        {
            int tr = Math.Min(rb.Length, maxBytes - (int)buf.Length);
            if (tr <= 0) return null; // overflow — no line terminator within maxBytes
            int got;
            try
            {
                got = await stream.ReadAsync(rb, 0, tr, readTimeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null; // timed out or cancelled
            }
            catch (IOException)
            {
                throw;
            }
            if (got <= 0) return buf.Length == 0 ? null : Encoding.UTF8.GetString(buf.ToArray());
            for (int i = 0; i < got; i++)
            {
                if (rb[i] == (byte)'\n')
                {
                    buf.Write(rb, 0, i);
                    long len = buf.Length;
                    if (len > 0) { buf.Position = len - 1; if (buf.ReadByte() == '\r') buf.SetLength(len - 1); }
                    return Encoding.UTF8.GetString(buf.ToArray());
                }
            }
            buf.Write(rb, 0, got);
        }
    }

    private static string RelOf(string repoRoot, string abs)
    {
        if (abs.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
            return abs.Substring(repoRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
        return abs;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "\u2026";

    private static string? _repoRootCached;
    private static readonly object _repoRootLock = new();
    private static string ResolveRepoRoot()
    {
        var explicitRoot = Volatile.Read(ref _explicitRepoRoot);
        if (!string.IsNullOrWhiteSpace(explicitRoot) && Directory.Exists(explicitRoot))
            return explicitRoot;
        if (Volatile.Read(ref _repoRootCached) != null) return _repoRootCached!;
        lock (_repoRootLock)
        {
            if (_repoRootCached != null) return _repoRootCached;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "flax-mcp.sln"))) { _repoRootCached = dir.FullName; return dir.FullName; }
                dir = dir.Parent;
            }
            _repoRootCached = Environment.CurrentDirectory;
            return _repoRootCached;
        }
    }

    private static JObject Err(string code, string message) => new()
    {
        ["ok"] = false,
        ["errorCode"] = code,
        ["errorMessage"] = message,
    };

    private static JObject? TryParseJObject(string json)
    {
        try { return JObject.Parse(json); }
        catch (JsonReaderException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static JObject ExceptionErr(string code, string message, Exception ex) => new()
    {
        ["ok"] = false,
        ["errorCode"] = code,
        ["errorMessage"] = message,
        ["data"] = new JObject
        {
            ["exceptionType"] = ex.GetType().FullName ?? "Unknown",
            ["stackTrace"] = ex.ToString(),
        },
    };

    // ─── Per-response framing / serialization (B8) ───
    // Each call builds the full JSON byte array with a trailing newline in a
    // dedicated StringWriter, then writes it atomically to the pipe via a single
    // Write call.  This eliminates:
    //   1. StreamWriter encoding buffers (partial writes on disposal)
    //   2. Any Newtonsoft internal state from cross-call reuse
    //   3. NoConverters empty-array edge case in JObject.ToString

    /// <summary>Serialize a JObject to a complete UTF-8 byte array with trailing newline,
    /// using a per-call StringWriter+JsonTextWriter for complete isolation.</summary>
    private static byte[] SerializeResponseToBytes(JObject response)
    {
        using var sw = new StringWriter(CultureInfo.InvariantCulture);
        using var writer = new JsonTextWriter(sw) { Formatting = Formatting.None };
        response.WriteTo(writer);
        writer.Flush();
        // Encode to UTF-8 bytes WITHOUT BOM, append newline for delimiter
        int utf8Len = Encoding.UTF8.GetByteCount(sw.ToString());
        byte[] buf = new byte[utf8Len + 1];
        Encoding.UTF8.GetBytes(sw.ToString(), 0, sw.ToString().Length, buf, 0);
        buf[utf8Len] = (byte)'\n';
        return buf;
    }

    /// <summary>Write a JSON response to the pipe in a single atomic Write call.
    /// Bypasses StreamWriter entirely — no encoding buffer or partial-frame risk.</summary>
    private static void PipeWriteJson(NamedPipeServerStream pipe, JObject response)
    {
        byte[] bytes = SerializeResponseToBytes(response);
        pipe.Write(bytes, 0, bytes.Length);
        pipe.Flush();
    }

    // ─── Response builders ───

    private static JObject BuildPing() => new()
    {
        ["ok"] = true, ["pong"] = true, ["version"] = Version,
        ["warming"] = !_warmed,
        ["ready"] = _warmed, // readiness no longer depends on rg — managed backend always works
        ["csharpBackend"] = CsharpBackendName,
        ["rgAvailable"] = _rgAvailable,
        ["uptimeSeconds"] = Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1),
    };

    // Computed once — identifies which build this daemon is serving, so
    // daemon.ps1 can detect "rebuilt on disk but old code still running".
    private static readonly string _buildStamp = BuildStampOf(AppContext.BaseDirectory);

    private static JObject BuildStatus() => new()
    {
        ["ok"] = true,
        ["daemonVersion"] = Version,
        ["daemon"] = "flaxmcp-nav",
        ["version"] = Version,
        ["buildStamp"] = _buildStamp,
        ["baseDir"] = AppContext.BaseDirectory,
        ["ready"] = _warmed, // readiness no longer depends on rg — managed backend always works
        ["csharpBackend"] = CsharpBackendName,
        ["rgAvailable"] = _rgAvailable,
        ["uptime"] = Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1),
        ["uptimeSeconds"] = Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1),
        ["pipe"] = $@"\\.\pipe\{PipeName}",
        ["atomics"] = new JArray(KnownAtomics),
        ["cacheSize"] = _cacheStore.Count,
        ["warmed"] = _warmed,
        ["docsWarm"] = DocsIndex.RepoRoot != null,
        ["flaxApiWarm"] = FlaxApiIndex.Built,
        ["campaignLocked"] = _campaignLocked,
        ["campaignOwnerPid"] = _campaignOwnerPid,
        ["campaignReason"] = _campaignReason,
        ["warmNote"] = _warmed
            ? null
            : (JToken)"First docs/flax_api/csharp query pays cold-build latency (~5s). Call __warmup__ to pre-warm.",
    };

    private static JObject BuildHelp()
    {
        var atomicsWithDesc = new JArray();
        foreach (var a in KnownAtomics)
            atomicsWithDesc.Add(new JObject { ["atomic"] = a, ["description"] = GetAtomicDescription(a) });
        return new JObject
        {
            ["ok"] = true,
            ["help"] = "flaxmcp-nav daemon control verbs and atomics",
            ["controlVerbs"] = new JArray(
                ControlVerbs.Select(v => new JObject { ["verb"] = v.verb, ["description"] = v.description })),
            ["atomics"] = atomicsWithDesc,
        };
    }

    private static string GetAtomicDescription(string atomic) => atomic switch
    {
        "docs/find_section" => "Find a section in docs by query",
        "docs/find_doc" => "Find a doc page by name",
        "docs/grep" => "Search doc content by regex",
        "docs/find_capability" => "Find reusable capabilities from the structured CapabilityIndex (tag/symbol/graph, not header text)",
        "docs/find_composes" => "Find CapabilityIndex composes graph edges for a symbol (forward + reverse)",
        "flax_api/lookup" => "Look up Flax Engine API signature by name",
        "flax_api/search" => "Search Flax Engine API by keyword",
        "flax_api/members_of" => "List all members of a Flax Engine type",
        "flax_api/enum_values" => "List values of a Flax Engine enum",
        "flax_api/inheritance_chain" => "Show inheritance chain for a Flax Engine type",
        "atlas/diff_tree" => "Show diff tree between editor and nav indexes",
        _ when atomic.StartsWith("csharp/") => "C# semantic navigation — " + atomic.Split('/')[1].Replace('_', ' ') + " (managed backend)",
        _ => atomic,
    };

    private static JObject BuildHealth()
    {
        var now = DateTime.UtcNow;
        // Peek only (no forced build) — __health__ must stay a passive check.
        var sourceIndexSnapshot = Volatile.Read(ref _sourceIndex);
        return new JObject
        {
            ["ok"] = true,
            ["warming"] = !_warmed,
            ["ready"] = _warmed, // readiness no longer depends on rg — managed backend always works
            ["csharpBackend"] = CsharpBackendName,
            ["rgAvailable"] = _rgAvailable,
            ["daemon"] = "flaxmcp-nav",
            ["version"] = Version,
            ["buildStamp"] = _buildStamp,
            ["baseDir"] = AppContext.BaseDirectory,
            ["uptimeSeconds"] = Math.Round((now - _startedUtc).TotalSeconds, 1),
            ["atomics"] = new JArray(KnownAtomics),
            ["concurrency"] = new JObject
            {
                ["maxConcurrency"] = 8,
                ["activeHandlers"] = Volatile.Read(ref _activeHandlers),
                ["peakHandlers"] = Volatile.Read(ref _peakHandlers),
                ["totalServed"] = Interlocked.Read(ref _totalServed),
            },
            ["cache"] = new JObject
            {
                ["entries"] = _cacheStore.Count,
                ["maxEntries"] = CacheMax,
                ["hits"] = Interlocked.Read(ref _cacheHits),
                ["misses"] = Interlocked.Read(ref _cacheMisses),
            },
            ["docsIndex"] = new JObject
            {
                ["built"] = DocsIndex.RepoRoot != null,
                ["fileCount"] = DocsIndex.FileCount,
                ["headerCount"] = DocsIndex.HeaderCount,
                ["buildMillis"] = DocsIndex.BuildMillis,
                ["builtAtUtc"] = DocsIndex.BuiltAtUtc?.ToString("o"),
                ["builtSecondsAgo"] = DocsIndex.BuiltAtUtc.HasValue ? Math.Round((now - DocsIndex.BuiltAtUtc.Value).TotalSeconds, 1) : null,
                ["watcherActive"] = DocsIndex.WatcherActive,
                ["requiresRestart"] = DocsIndex.RequiresRestart,
            },
            ["flaxApiIndex"] = new JObject
            {
                ["built"] = FlaxApiIndex.Built,
                ["memberCount"] = FlaxApiIndex.MemberCount,
                ["buildMillis"] = FlaxApiIndex.BuildMillis,
            },
            // 2026-07-28: __health__ previously reported docsIndex and
            // flaxApiIndex sizes but nothing at all about the C# managed
            // source index — the thing csharp/find_definition,
            // csharp/find_references, and csharp/symbol_search actually
            // query. csharp/index_health's own "targetsIndexed" field was
            // already (honestly, per its own code comment) a FILE count
            // (3,543 in this repo), not the token-level symbol index size —
            // there was no visibility anywhere into SymbolLocations.Count,
            // the actual in-memory index that answers "did this build with
            // real content."
            ["csharpSourceIndex"] = new JObject
            {
                ["built"] = sourceIndexSnapshot != null,
                ["fileCount"] = sourceIndexSnapshot?.Files.Count ?? 0,
                ["symbolCount"] = sourceIndexSnapshot?.TokenCount ?? 0,
            },
        };
    }

    internal static void InvalidateDocsCache()
    {
        lock (_lruLock)
        {
            var toRemove = new System.Collections.Generic.List<string>();
            foreach (var key in _cacheStore.Keys) if (key.StartsWith("docs/", StringComparison.Ordinal)) toRemove.Add(key);
            foreach (string key in toRemove) if (_cacheStore.TryRemove(key, out var entry)) _lruOrder.Remove(entry.Node);
            if (toRemove.Count > 0) DaemonLog.Info($"docs cache invalidated ({toRemove.Count} entries) after index rebuild");
        }
    }

    // ─── CLI arg parsing ───

    private static JObject ParseClientArgs(string[] tail)
    {
        var args = new JObject();
        for (int i = 0; i < tail.Length; i++)
        {
            string tok = tail[i];
            if (tok == "--json-args" && i + 1 < tail.Length)
            {
                var p = TryParseJObject(tail[++i]);
                if (p == null) throw new ArgumentException("--json-args: malformed JSON");
                foreach (var pr in p.Properties()) args[pr.Name] = pr.Value;
                continue;
            }
            int eq = tok.IndexOf('=');
            if (eq <= 0) continue;
            args[tok[..eq]] = CoerceArgValue(tok[(eq + 1)..]);
        }
        return args;
    }

    private static JToken CoerceArgValue(string raw)
    {
        if (raw == "true" || raw == "false") return JToken.FromObject(raw == "true");
        if (int.TryParse(raw, out int i)) return JToken.FromObject(i);
        if ((raw.StartsWith('{') || raw.StartsWith('['))) { try { return JToken.Parse(raw); } catch (Exception jex) { DaemonLog.Warn($"coerce json: {jex.Message}"); } }
        return JToken.FromObject(raw);
    }

    // ─── CLI convenience ───

    private static int Help() { PrintHelp(); return 0; }
    private static int Ver() { Console.WriteLine(Version); return 0; }

    private static void PrintHelp()
    {
        Console.WriteLine($"flaxmcp-nav v{Version} — persistent navigation helper");
        Console.WriteLine();
        Console.WriteLine("USAGE:");
        Console.WriteLine("  flaxmcp-nav --daemon                        # start daemon");
        Console.WriteLine("  flaxmcp-nav --repo-root <dir>               # override repository root");
        Console.WriteLine("  flaxmcp-nav --auto-start <atomic> key=val   # spawn daemon if not running");
        Console.WriteLine("  flaxmcp-nav --status                        # daemon version + uptime");
        Console.WriteLine("  flaxmcp-nav --health                        # warm-state report");
        Console.WriteLine("  flaxmcp-nav --warm                          # warm all 4 indexes in-process (no daemon needed)");
        Console.WriteLine("  flaxmcp-nav --shutdown                      # stop daemon");
        Console.WriteLine("  flaxmcp-nav <atomic> key=val ...            # one-shot query");
        Console.WriteLine();
        Console.WriteLine("ATOMICS:");
        foreach (var a in KnownAtomics) Console.WriteLine($"  {a}");
        Console.WriteLine();
        Console.WriteLine("EXAMPLES:");
        Console.WriteLine("  flaxmcp-nav csharp/symbol_search query=Bridge kind=class");
        Console.WriteLine("  flaxmcp-nav csharp/find_definition symbolName=ToolResult");
        Console.WriteLine("  flaxmcp-nav csharp/find_related_symbols symbolName=IFlaxBridge");
        Console.WriteLine("  flaxmcp-nav csharp/list_plugin_tools namespace=Sample");
        Console.WriteLine("  flaxmcp-nav docs/find_section query=flax");
        Console.WriteLine("  flaxmcp-nav flax_api/lookup name=Physics.RayCast");
        Console.WriteLine("  flaxmcp-nav atlas/diff_tree");
        Console.WriteLine();
        Console.WriteLine("OUTPUT: one JSON line to stdout. Errors to stderr.");
    }
}

