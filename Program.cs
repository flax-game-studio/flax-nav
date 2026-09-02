using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FlaxMcp.NavDaemon;

internal static partial class Program
{
    private const string PipeName = "flaxmcp-nav";
    private const string Version = "2.2.1";
    private const int MaxRequestBytes = 1 * 1024 * 1024;

    private static readonly string[] KnownAtomics =
    {
        "__ping__",
        "__status__",
        "__health__",
        "__help__",
        "__warmup__",
        "__shutdown__",
        "__campaign_lock__",
        "__campaign_unlock__",
        "csharp/grep_symbol_context",
        "csharp/find_definition",
        "csharp/find_references",
        "csharp/symbol_search",
        "csharp/find_implementations",
        "csharp/get_call_hierarchy",
        "csharp/describe_symbol",
        "csharp/find_related_symbols",
        "csharp/list_plugin_tools",
        "csharp/index_health",
        "docs/find_section",
        "docs/find_doc",
        "docs/grep",
        "docs/find_capability",
        "docs/find_composes",
        "flax_api/lookup",
        "flax_api/search",
        "flax_api/members_of",
        "flax_api/enum_values",
        "flax_api/inheritance_chain",
        "atlas/diff_tree",
        "plugin/catalog",
        "receipt/search",
        "receipt/recent",
        "receipt/by_id",
    };

    private static readonly (string verb, string description)[] ControlVerbs =
    {
        ("__ping__", "Health check — returns pong + version + uptime"),
        ("__status__", "Daemon status — version, uptime, atomics, cache size"),
        ("__health__", "Detailed health report — cache stats, request/error counts"),
        ("__help__", "This help message — lists all control verbs and atomics"),
        ("__warmup__", "Eagerly warm docs + FlaxApi indices. Returns immediately; warmup runs in background. Prevents ~5s cold latency."),
        ("__shutdown__", "Shutdown the daemon (requires force=true; refused while campaign-locked)"),
        ("__campaign_lock__", "Refuse __shutdown__ until unlocked (requires ownerPid, reason)"),
        ("__campaign_unlock__", "Clear the campaign lock, allowing __shutdown__ again"),
    };

    // Campaign lock: set via `--campaign-lock --ownerPid <pid> --reason <text>` to keep
    // the daemon alive across a multi-step operator workflow. While locked, __shutdown__
    // is refused (graceful shutdown only — an OS-level Stop-Process still works, same as
    // any soft lock). Cleared via `--campaign-unlock`, or naturally on daemon restart
    // (in-memory only, never persisted).
    private static readonly object _campaignGate = new();
    private static volatile bool _campaignLocked;
    private static int? _campaignOwnerPid;
    private static string? _campaignReason;
    private static DateTime? _campaignLockedAtUtc;

    private static bool IsControlVerb(string name) =>
        _controlVerbs.Contains(name);

    private static readonly HashSet<string> _knownAtomics = new(KnownAtomics, StringComparer.Ordinal);
    private static readonly HashSet<string> _controlVerbs = new(
        ControlVerbs.Select(v => v.verb), StringComparer.Ordinal);

    private static bool IsKnownAtomic(string name) => _knownAtomics.Contains(name);
    private static readonly JsonConverter[] NoConverters = Array.Empty<JsonConverter>();
    private static readonly SemaphoreSlim _gate = new(8, 8);
    private static int _activeHandlers, _peakHandlers;
    private static long _totalServed;
    private static long _reqId;
    private static DateTime _startedUtc;

    // Per-request cancellation: when pipe disconnects, handlers bail early
    private static readonly AsyncLocal<CancellationToken> _requestCt = new();

    private static readonly ConcurrentDictionary<string, (JObject Value, LinkedListNode<string> Node)> _cacheStore = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> _lruOrder = new();
    private static readonly object _lruLock = new();
    private static long _cacheHits, _cacheMisses;
    private const int CacheMax = 512;

    private static volatile bool _warmed;
    private static readonly object _warmLock = new();
    private static volatile bool _rgAvailable;
    private static string? _explicitRepoRoot;
    // 2026-07-28: removed a dead `_tcpPort` field + `HoistTcpPort(args)` call
    // left over from the 2026-07-27 restructure commit (53c4cad4f) — the
    // field was assigned-but-never-read (CS0414, an error here since this
    // project treats warnings as errors) and `HoistTcpPort` was never
    // defined at all (CS0103), so the daemon's checked-in source has failed
    // to build from a clean checkout since that commit landed. No TCP
    // listener exists anywhere to consume a port, and nothing outside this
    // file (scripts, docs, opencode config) references `--tcp-port` — this
    // was a half-wired stub for a transport that was never implemented, not
    // a working feature this broke. If TCP transport is wanted later, it
    // needs its own real design (protocol framing, actual listener), not a
    // guessed reconstruction of this stub.

    /// <summary>Return whether an optional ripgrep executable is present on PATH.
    /// This is informational only; csharp/* atomics use the managed backend.</summary>
    private static bool ProbeRipgrep()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0)
                continue;

            if (File.Exists(Path.Combine(trimmed, "rg.exe")) || File.Exists(Path.Combine(trimmed, "rg")))
                return true;
        }
        return false;
    }

    /// <summary>Extract the optional repo-root override before verb dispatch.</summary>
    private static string[] HoistRepoRoot(string[] raw)
    {
        var remaining = new List<string>(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == "--repo-root" && i + 1 < raw.Length)
            {
                _explicitRepoRoot = raw[++i];
            }
            else
            {
                remaining.Add(raw[i]);
            }
        }
        return remaining.ToArray();
    }

    public static int Main(string[] args)
    {
        try
        {
            // Force UTF-8 output encoding for all Console.Out writes.
            // Without this, Windows defaults to OEM codepage (e.g. cp437) which
            // corrupts Unicode characters in JSON output — causing malformed JSON
            // on one-shot CLI calls. WriteStdoutUtf8 also bypasses buffered
            // Console.Out for JSON responses.
            Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            args = HoistRepoRoot(args);
            if (args.Length == 0) { PrintHelp(); return 1; }

            return args[0] switch
            {
                "--daemon" => RunDaemonEntry(),
                "--auto-start" => RunClient(args[1..], autoStart: true),
                "--status" => SendOneShot(new JObject { ["atomic"] = "__status__" }, false),
                "--health" => SendOneShot(new JObject { ["atomic"] = "__health__" }, false),
                "--shutdown" => SendOneShot(new JObject { ["atomic"] = "__shutdown__", ["force"] = true }, false),
                "--campaign-lock" => SendOneShot(BuildCampaignLockRequest(args[1..]), false),
                "--campaign-unlock" => SendOneShot(new JObject { ["atomic"] = "__campaign_unlock__" }, false),
                "--help" or "-h" => Help(),
                "--version" => Ver(),
                "--warm" => Warm(),
                _ => RunClient(args),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"flaxmcp-nav: {ex.Message}");
            return 99;
        }
    }

    // Parses the dash-flag style `--ownerPid <pid> --reason <text>` used by
    // `--campaign-lock` (distinct from the generic `key=value` atomic-arg
    // parser used for regular atomic dispatch).
    private static JObject BuildCampaignLockRequest(string[] rest)
    {
        var req = new JObject { ["atomic"] = "__campaign_lock__" };
        for (int i = 0; i < rest.Length - 1; i++)
        {
            if (rest[i] == "--ownerPid") req["ownerPid"] = rest[i + 1];
            else if (rest[i] == "--reason") req["reason"] = rest[i + 1];
        }
        return req;
    }
}
