using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
namespace FlaxMcp.NavDaemon;

// ─── C# atomic dispatch table + all Csharp* handlers (managed backend) ───
//
// Every handler uses managed .NET file I/O + regex — no rg subprocess.
// Readiness never depends on rg. See Program.CsharpManaged.cs for the
// core search functions.
internal static partial class Program
{
    private static readonly Dictionary<string, Func<JObject, JObject>> _dispatch = new(StringComparer.Ordinal)
    {
        ["csharp/grep_symbol_context"] = CsharpGrepSymbolContext,
        ["csharp/find_references"] = CsharpFindReferencesAll,
        ["csharp/symbol_search"] = CsharpSymbolSearch,
        ["csharp/find_definition"] = CsharpFindDefinition,
        ["csharp/find_related_symbols"] = CsharpFindRelatedSymbols,
        ["csharp/list_plugin_tools"] = CsharpListPluginTools,
        ["csharp/find_implementations"] = CsharpFindImplementations,
        ["csharp/describe_symbol"] = CsharpDescribeSymbol,
        ["csharp/get_call_hierarchy"] = CsharpGetCallHierarchy,
        ["csharp/index_health"] = CsharpIndexHealth,
    };

    private static readonly Regex PluginToolRegex = new(
        @"public\s+static\s+ToolResult\s+\w+\(ToolContext",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PluginToolNameRegex = new(
        @"ToolResult\s+(\w+)\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ─── Content-type guard for csharp/* atomics ───
    // Keys whose values are C# symbol names (not file paths or config params)
    private static readonly HashSet<string> _csharpSearchKeys = new(StringComparer.Ordinal)
        { "symbolName", "query" };

    // File extensions that indicate non-C# script/data queries, not C# symbols
    private static readonly HashSet<string> _nonCsExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ps1", ".psm1", ".js", ".ts", ".json", ".yaml", ".yml" };

    /// <summary>Check if csharp/* args reference non-C# content. Returns terminal error or null.</summary>
    private static JObject? CheckCsharpContentType(JObject args)
    {
        foreach (var key in _csharpSearchKeys)
        {
            string? val = args[key]?.ToString();
            if (string.IsNullOrWhiteSpace(val)) continue;

            // Path separators → file path, not C# symbol
            if (val.Contains('\\') || val.Contains('/'))
            {
                string ext = Path.GetExtension(val);
                if (!string.IsNullOrEmpty(ext) && _nonCsExtensions.Contains(ext))
                    goto Found;
                // Even without a non-C# extension, a path separator in a csharp/*
                // query is strong evidence the caller wants file navigation.
                return new JObject
                {
                    ["ok"] = false,
                    ["errorCode"] = "unsupported_content_type",
                    ["errorMessage"] = $"csharp/ atomics accept C# symbols (types, methods, namespaces) only — use Glob, Read, or Grep for file-path queries. Detected path separator in '{key}': '{val}'",
                    ["suggestedTools"] = new JArray("Glob", "Read", "Grep"),
                    ["terminal"] = true,
                    ["retry"] = false,
                };
            }

            // Bare filename with non-C# extension (e.g. "deploy.ps1", "config.json")
            int dot = val.LastIndexOf('.');
            if (dot > 0 && dot < val.Length - 1)
            {
                string ext = val[dot..];
                // Only flag if no preceding dots (avoids FPs on namespaced symbols
                // like "System.Text.Json" which legitimately contain dots)
                bool hasPrecedingDots = val[..dot].Contains('.');
                if (!hasPrecedingDots && _nonCsExtensions.Contains(ext))
                    goto Found;
            }
        }
        return null;

    Found:
        return new JObject
        {
            ["ok"] = false,
            ["errorCode"] = "unsupported_content_type",
            ["errorMessage"] = "csharp/ atomics accept C# symbols only — use Glob, Read, or Grep for non-C# files. Detected non-C# script/file reference.",
            ["suggestedTools"] = new JArray("Glob", "Read", "Grep"),
            ["terminal"] = true,
            ["retry"] = false,
        };
    }

    /// <summary>Extract the search symbol from csharp/* args for error messages.</summary>
    private static string ExtractSearchSymbol(JObject args)
    {
        foreach (var key in _csharpSearchKeys)
        {
            string? val = args[key]?.ToString();
            if (!string.IsNullOrWhiteSpace(val)) return val.Length > 80 ? val[..77] + "…" : val;
        }
        return "(unknown)";
    }

    private readonly record struct SearchTerm(string Segment, bool SearchedSegment);

    /// <summary>
    /// Fully-qualified names (e.g. "FlaxMcp.Core.Tooling.ToolContext") and generic
    /// fully-qualified names (e.g. "System.Collections.Generic.List&lt;T&gt;") never match
    /// verbatim: the token index is per-identifier, and the literal dotted string or
    /// generic suffix does not appear on any declaration line. Agents paste both —
    /// that used to produce count:0 for symbols that exist. Search the last
    /// identifier segment instead ("ToolContext"; "List"); response fields keep the
    /// caller's original query. The <c>Segment</c> field is what is actually
    /// searched; consumers use the record to surface that the searched term differed.
    /// </summary>
    private static SearchTerm ResolveSearchTerm(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return new SearchTerm(symbol, false);
        int dot = symbol.LastIndexOf('.');
        if (dot < 0 || dot == symbol.Length - 1)
        {
            // No dotted namespace: only a bare generic suffix can be trimmed
            // ("List<T>" → "List"); otherwise the name stands as-is.
            int lt = symbol.IndexOf('<');
            if (lt > 0)
            {
                string stripped = symbol[..lt];
                return new SearchTerm(stripped, !string.Equals(stripped, symbol, StringComparison.Ordinal));
            }
            return new SearchTerm(symbol, false);
        }
        string segment = symbol[(dot + 1)..];
        // Strip a generic suffix (up to the first '<') BEFORE identifier
        // validation — "List<T>" must search "List". Keep the fallback that
        // returns the full symbol when the tail is not an identifier.
        int generic = segment.IndexOf('<');
        if (generic > 0) segment = segment[..generic];
        bool differs = !string.Equals(segment, symbol, StringComparison.Ordinal);
        return new SearchTerm(segment.Length > 0 && IsIdentifierSegment(segment) ? segment : symbol, differs);
    }

    private static string LastSymbolSegment(string symbol)
        => ResolveSearchTerm(symbol).Segment;

    private static bool IsIdentifierSegment(string segment)
    {
        if (!char.IsLetter(segment[0]) && segment[0] != '_') return false;
        foreach (char c in segment)
        {
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        }
        return true;
    }

    /// <summary>
    /// Attach index-scope metadata to count:0 responses so callers can distinguish
    /// "symbol does not exist" from "index does not cover this code". 2026-08-05:
    /// an agent concluded the daemon only indexes the Flax install / Source/Game
    /// and theorized a coverage gap instead of measuring it — the response itself
    /// now proves the scope (whole repo root, file + token counts, watcher state).
    ///
    /// 2026-08-19: the note hand-listed trees and silently omitted the game
    /// project (samples/game-project/Source/Game) — so agents read a count:0 as
    /// "game side is not indexed" and trusted the note's absolute claim. The
    /// note is now data-driven: indexScope.topLevelDirs enumerates the ACTUAL
    /// per-tree file counts from the live index, and the claim text is
    /// conditional on the searched tree being listed.
    /// </summary>
    private static void AttachCountZeroScope(JObject result)
    {
        try
        {
            string root = ResolveRepoRoot();
            CsSourceIndex index = GetSourceIndex(root);

            var byTree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (CsSourceFile file in index.Files)
            {
                string rel = Path.GetRelativePath(root, file.Path);
                int sep = rel.IndexOf(Path.DirectorySeparatorChar);
                if (sep < 0) continue;
                string top = rel.Substring(0, sep);
                byTree[top] = byTree.TryGetValue(top, out int n) ? n + 1 : 1;
            }

            var topLevelDirs = new JArray(
                byTree.OrderByDescending(kv => kv.Value).Select(kv =>
                    new JObject
                    {
                        ["dir"] = kv.Key,
                        ["csFileCount"] = kv.Value,
                    }));

            result["indexScope"] = new JObject
            {
                ["root"] = root,
                ["csFileCount"] = index.Files.Count,
                ["tokenCount"] = index.TokenCount,
                ["watcherActive"] = _sourceWatcherActive,
                ["topLevelDirs"] = topLevelDirs,
                ["note"] = "The index covers every .cs under the repository root (skips .git/.vs/bin/obj/node_modules at any depth). topLevelDirs lists the actual per-tree file counts — the game project (samples/game-project/Source/Game) is indexed like every other tree, so a count:0 there is a real \"does not exist\". A count:0 is definitive only when the tree you searched is listed in topLevelDirs. If it is not listed, the daemon is not covering that code — restart it (scripts/dev/daemon.ps1 restart) and re-check. Dotted/generic fully-qualified names are searched by their last identifier segment; the response's segmentSearched field records that rewrite.",
            };
        }
        catch (Exception ex)
        {
            SwallowedCatch.Record("NavDaemon.AttachCountZeroScope", ex);
        }
    }

    private static JObject DispatchAtomic(string name, JObject args)
    {
        // Source-backed results must observe edits. The managed source snapshot
        // validates file metadata on each query, but an outer response-cache hit
        // would bypass that validation entirely.
        bool sourceBacked = name.StartsWith("csharp/", StringComparison.Ordinal)
            || string.Equals(name, "plugin/catalog", StringComparison.Ordinal);

        // B5: reject unreasonably large arg values
        foreach (var kv in args)
        {
            if (kv.Value is JValue jv && jv.Value is string s && s.Length > 10000)
                return Err("arg_too_long", $"key '{kv.Key}' exceeds 10000 characters");
        }
        string k = name + "|" + string.Join(";", args.Properties().OrderBy(p => p.Name).Select(p => $"{p.Name}={p.Value}"));
        if (!sourceBacked)
        {
            lock (_lruLock)
            {
                if (_cacheStore.TryGetValue(k, out var entry))
                {
                    Interlocked.Increment(ref _cacheHits);
                    _lruOrder.Remove(entry.Node);
                    _lruOrder.AddFirst(entry.Node);
                    return (JObject)entry.Value.DeepClone();
                }
            }
        }
        Interlocked.Increment(ref _cacheMisses);

        // B5a: content-type guard for csharp/* atomics — reject non-C# queries
        if (name.StartsWith("csharp/"))
        {
            var cte = CheckCsharpContentType(args);
            if (cte != null) return cte;
        }

        JObject result;
        if (_dispatch.TryGetValue(name, out var handler))
            result = handler(args);
        else if (name.StartsWith("docs/")) result = DispatchDocAtomic(name, args);
        else if (name.StartsWith("flax_api/")) result = DispatchFlaxApiAtomic(name, args);
        else if (name.StartsWith("atlas/")) result = DispatchAtlasAtomic(name, args);
        else if (name.StartsWith("plugin/")) result = DispatchPluginAtomic(name, args);
        else if (name.StartsWith("receipt/")) result = DispatchReceiptAtomic(name, args);
        else
        {
            // Defensive fallback — should be unreachable when HandleOneRequestAsync
            // gates properly, but keep a structured terminal error if it surfaces.
            var defErr = Err("unsupported_atomic", $"'{name}' is not a supported atomic.");
            defErr["terminal"] = true;
            defErr["retryable"] = false;
            defErr["availableAtomics"] = new JArray(KnownAtomics);
            return defErr;
        }

        // B5b: terminal no-match signal for csharp/* atomics — count:0 is definitive
        if (name.StartsWith("csharp/") && (bool?)result["ok"] == true && result["count"] is JToken cnt && cnt.Value<int>() == 0)
        {
            string full = RawQuery(args);
            var term = ResolveSearchTerm(full);
            if (term.SearchedSegment)
            {
                result["segmentSearched"] = term.Segment;
                result["querySearchedAs"] = "last-segment";
            }
            var sym = ExtractSearchSymbol(args);
            result["terminal"] = true;
            result["retry"] = false;
            result["reason"] = term.SearchedSegment
                ? $"'{sym}' matched zero C# symbols — its last identifier segment '{term.Segment}' was what was actually searched, and that segment exists nowhere in the indexed workspace (indexScope.topLevelDirs lists the covered trees; the game project samples/game-project/Source/Game is included). Within those trees this is definitive — do NOT retry this flaxnav query with a variant spelling; use Glob, Read, or Grep for non-C# file/content search."
                : $"'{sym}' matched zero C# symbols in the indexed workspace (indexScope.topLevelDirs lists the covered trees; the game project samples/game-project/Source/Game is included). Within those trees this is definitive — do NOT retry this flaxnav query with a variant spelling; use Glob, Read, or Grep for non-C# file/content search.";
            AttachCountZeroScope(result);
        }
        else if (name.StartsWith("csharp/") && (bool?)result["ok"] == true)
        {
            var term = ResolveSearchTerm(RawQuery(args));
            if (term.SearchedSegment)
                result["segmentSearched"] = term.Segment;
        }

        if (!sourceBacked && (bool?)result["ok"] == true)
        {
            var clone = (JObject)result.DeepClone();
            lock (_lruLock)
            {
                if (!_cacheStore.ContainsKey(k))
                {
                    var node = new LinkedListNode<string>(k);
                    _cacheStore[k] = (clone, node);
                    _lruOrder.AddFirst(node);
                    while (_lruOrder.Count > CacheMax)
                    {
                        var last = _lruOrder.Last;
                        _lruOrder.RemoveLast();
                        _cacheStore.TryRemove(last!.Value, out _);
                    }
                }
            }
        }
        return result;
    }

    /// <summary>Untruncated query/symbolName from args (never display-truncated).</summary>
    private static string RawQuery(JObject args)
    {
        foreach (var key in _csharpSearchKeys)
        {
            string? val = args[key]?.ToString();
            if (!string.IsNullOrWhiteSpace(val)) return val;
        }
        return "";
    }

    // ── Managed-handler implementations ──────────────────────────────
    //
    // Every handler below uses .NET file I/O + regex. No rg subprocess.
    // Response shapes match the existing contract — callers see
    // the same {ok, count, results, ...} shape.

    private static JObject CsharpGrepSymbolContext(JObject args)
    {
        string symbol = args["symbolName"]?.ToString() ?? args["query"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(symbol)) return Err("empty_symbol_name", "'symbolName' or 'query' cannot be empty");
        int context = Math.Clamp((int?)args["context"] ?? 12, 1, 60);
        int maxResults = Math.Clamp((int?)args["maxResults"] ?? 20, 1, 100);
        string root = ResolveRepoRoot();
        string searchTerm = LastSymbolSegment(symbol);

        try
        {
            var hits = ManagedGrepFixed(root, searchTerm, context, maxResults);
            var results = HitsToResults(hits, root, "hit");
            foreach (JObject result in results)
                result.Remove("match");
            return new JObject { ["ok"] = true, ["root"] = root, ["symbol"] = symbol, ["count"] = results.Count, ["results"] = results };
        }
        catch (Exception ex)
        {
            return Err("search_failed", $"Managed search error: {ex.Message}");
        }
    }

    private static JObject CsharpFindReferencesAll(JObject args)
    {
        string symbol = args["symbolName"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(symbol)) return Err("empty_symbol_name", "'symbolName' cannot be empty");
        int maxResults = Math.Clamp((int?)args["maxResults"] ?? 500, 1, 500);
        bool compact = (bool?)args["compact"] == true;
        string root = ResolveRepoRoot();
        string searchTerm = LastSymbolSegment(symbol);

        try
        {
            var hits = ManagedFindAllReferences(root, searchTerm, maxResults);
            var results = new JArray();
            foreach (var h in hits)
            {
                if (compact)
                    results.Add(new JObject { ["file"] = RelOf(root, h.File), ["line"] = h.Line, ["kind"] = "reference" });
                else
                    results.Add(new JObject { ["file"] = RelOf(root, h.File), ["line"] = h.Line, ["kind"] = "reference", ["match"] = h.Match });
            }
            return new JObject
            {
                ["ok"] = true,
                ["symbol"] = symbol,
                ["count"] = results.Count,
                ["results"] = results,
                ["truncated"] = results.Count >= maxResults,
            };
        }
        catch (Exception ex)
        {
            return Err("search_failed", $"Managed search error: {ex.Message}");
        }
    }

    private static JObject CsharpSymbolSearch(JObject args)
    {
        string query = args["query"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(query)) return Err("invalid_arguments", "'query' required");
        string kind = ((string?)args["kind"] ?? "").Trim().ToLowerInvariant();
        int maxResults = Math.Clamp((int?)args["maxResults"] ?? 25, 1, 200);
        string root = ResolveRepoRoot();
        string searchTerm = LastSymbolSegment(query);
        string escaped = Regex.Escape(searchTerm);

        try
        {
            // Preserve the old declaration, method, and brace search behavior.
            string pattern = $"(class|struct|interface|enum|record|delegate|event)\\s+\\w*{escaped}\\w*|\\w*{escaped}\\w*\\s*\\(|\\w*{escaped}\\w*\\s*\\{{";
            // When a kind filter is active, apply it INSIDE the scan as a
            // lineFilter so only kind-matching hits count against the scan limit.
            // Previously the limit was consumed by any pattern hit — for a broad
            // query like {query:"Get", kind:"class"} the first 200 file-ordered
            // hits could all be non-class usages, so recall starved to zero even
            // when classes existed later in the corpus.
            string kindForMatch = kind == "intf" ? "interface" : kind;
            Func<string, bool>? kindFilter = kind.Length > 0 && kind != "any"
                ? line => MatchesSymbolKind(line, searchTerm, kindForMatch)
                : null;
            int scanLimit = maxResults * 20;
            // Route through the token index when it can answer for this query —
            // the common case, and the one that used to take minutes. Otherwise
            // fall back to the corpus scan, but hand it the query as a
            // prefilter: every alternative above requires that literal, so lines
            // without it cannot match and need not reach the (heavily
            // backtracking) regex.
            var hits = ManagedSubstringTokenSearch(root, searchTerm, pattern, scanLimit, kindFilter)
                ?? ManagedGrepRegex(root, pattern, 0, scanLimit, kindFilter, searchTerm);

            var results = new JArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in hits)
            {
                if (results.Count >= maxResults) break;
                string key = $"{h.File}:{h.Line}";
                if (!seen.Add(key)) continue;
                results.Add(new JObject { ["file"] = RelOf(root, h.File), ["line"] = h.Line, ["kind"] = "declaration", ["match"] = h.Match });
            }
            return new JObject
            {
                ["ok"] = true,
                ["query"] = query,
                ["kind"] = kind.Length > 0 ? kind : null,
                ["count"] = results.Count,
                ["truncated"] = results.Count >= maxResults,
                ["results"] = results,
            };
        }
        catch (Exception ex)
        {
            return Err("search_failed", $"Managed search error: {ex.Message}");
        }
    }

    private static JObject CsharpFindDefinition(JObject args)
    {
        string symbol = args["symbolName"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(symbol)) return Err("empty_symbol_name", "'symbolName' cannot be empty");
        int maxResults = Math.Clamp((int?)args["maxResults"] ?? 10, 1, 50);
        string root = ResolveRepoRoot();
        string searchTerm = LastSymbolSegment(symbol);
        string escaped = Regex.Escape(searchTerm);

        try
        {
            // Primary: declaration-only search with comment/string filtering
            string typePattern = $"(class|struct|interface|enum|record)\\s+{escaped}(?:[\\s<:({{]|$)";
            string methodPattern = $"^\\s*(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|unsafe|partial|readonly|ref)\\s+)*(?:[\\w<>\\[\\],.?]+\\s+)+{escaped}\\s*\\(";
            string pattern = $"(?:{typePattern})|(?:{methodPattern})";
            // Filter out comment and string lines — avoid false positives from
            // comments like "// class Foo" or string literals containing symbol names
            var declHits = ManagedGrepRegexIndexed(root, searchTerm, pattern, 0, maxResults, IsDeclarationLine);

            if (declHits.Count > 0)
            {
                var results = HitsToResults(declHits, root, "definition");
                return new JObject
                {
                    ["ok"] = true,
                    ["symbol"] = symbol,
                    ["count"] = results.Count,
                    ["results"] = results,
                };
            }

            // No declaration means no definition. Do not label arbitrary
            // references, comments, or string literals as definitions.
            return new JObject
            {
                ["ok"] = true,
                ["symbol"] = symbol,
                ["count"] = 0,
                ["results"] = new JArray(),
            };
        }
        catch (Exception ex)
        {
            return Err("search_failed", $"Managed search error: {ex.Message}");
        }
    }

    private static JObject CsharpFindRelatedSymbols(JObject args)
    {
        string symbol = args["symbolName"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(symbol)) return Err("empty_symbol_name", "'symbolName' cannot be empty");
        int maxResults = Math.Clamp((int?)args["maxResults"] ?? 50, 1, 500);
        string root = ResolveRepoRoot();
        string searchTerm = LastSymbolSegment(symbol);
        string escaped = Regex.Escape(searchTerm);

        try
        {
            string pattern = $":\\s*{escaped}\\b|,\\s*{escaped}\\b|<{escaped}>|{escaped}<";
            var hits = ManagedGrepRegexIndexed(root, searchTerm, pattern, 0, maxResults, line => !IsCommentOrString(line));
            var results = new JArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in hits)
            {
                if (results.Count >= maxResults) break;
                string key = $"{h.File}:{h.Line}";
                if (!seen.Add(key)) continue;
                results.Add(new JObject { ["file"] = RelOf(root, h.File), ["line"] = h.Line, ["kind"] = "reference", ["match"] = Truncate(h.Match, 80) });
            }
            if (results.Count == 0 && IsSimpleSymbol(searchTerm))
            {
                CsSourceIndex idx = GetSourceIndex(root);
                foreach (CsLocation loc in idx.LocationsOf(searchTerm))
                {
                    if (results.Count >= maxResults) break;
                    if (!idx.FilesByPath.TryGetValue(loc.File, out CsSourceFile? src)) continue;
                    string line = src.Lines[loc.Line - 1];
                    if (!LineContainsExactCodeToken(line, searchTerm)) continue;
                    string key = $"{loc.File}:{loc.Line}";
                    if (!seen.Add(key)) continue;
                    results.Add(new JObject { ["file"] = RelOf(root, loc.File), ["line"] = loc.Line, ["kind"] = "reference", ["match"] = Truncate(line.Trim(), 80) });
                }
            }
            return new JObject
            {
                ["ok"] = true,
                ["symbol"] = symbol,
                ["count"] = results.Count,
                ["truncated"] = results.Count >= maxResults,
                ["results"] = results,
            };
        }
        catch (Exception ex)
        {
            return Err("search_failed", $"Managed search error: {ex.Message}");
        }
    }

    private static JObject CsharpListPluginTools(JObject args)
    {
        string target = ((string?)args["target"] ?? "").Trim();
        string ns = ((string?)args["namespace"] ?? "").Trim();
        int maxResults = Math.Clamp((int?)args["maxResults"] ?? 100, 1, 500);
        string root = ResolveRepoRoot();

        try
        {
            string searchDir = target.Length > 0
                ? Path.GetFullPath(Path.Combine(root, target))
                : Path.Combine(root, "plugins");
            if (!Directory.Exists(searchDir))
                return Err("target_not_found", $"Directory not found: {searchDir}");

            // Path traversal guard: resolved searchDir must stay within root
            if (!IsWithinRoot(root, searchDir))
                return Err("path_traversal", $"target resolves outside repository root: {searchDir}");

            var tools = new JArray();
            int count = 0;

            foreach (CsSourceFile source in GetSourceFiles(searchDir))
            {
                if (count >= maxResults) break;
                string file = source.Path;

                try
                {
                    string[] lines = source.Lines;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (count >= maxResults) break;
                        if (!PluginToolRegex.IsMatch(lines[i])) continue;

                        if (ns.Length > 0 && !lines[i].Contains(ns, StringComparison.OrdinalIgnoreCase))
                        {
                            string? nsFromFile = ExtractNamespace(file);
                            if (nsFromFile == null || !nsFromFile.Contains(ns, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }

                        var m = PluginToolNameRegex.Match(lines[i]);
                        string toolName = m.Success ? m.Groups[1].Value : "?";
                        tools.Add(new JObject { ["file"] = RelOf(root, file), ["line"] = i + 1, ["kind"] = "tool", ["toolName"] = toolName, ["match"] = lines[i].Trim() });
                        count++;
                    }
                }
                catch (IOException ex) { SwallowedCatch.Record("NavDaemon.CsharpListPluginTools.io", ex); /* skip unreadable */ }
                catch (UnauthorizedAccessException ex) { SwallowedCatch.Record("NavDaemon.CsharpListPluginTools.auth", ex); /* skip inaccessible */ }
            }

            return new JObject
            {
                ["ok"] = true,
                ["searchDir"] = searchDir,
                ["count"] = tools.Count,
                ["truncated"] = count >= maxResults,
                ["tools"] = tools,
            };
        }
        catch (Exception ex)
        {
            return Err("search_failed", $"Managed search error: {ex.Message}");
        }
    }

    private static JObject CsharpFindImplementations(JObject args)
    {
        string symbol = args["symbolName"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(symbol)) return Err("empty_symbol_name", "'symbolName' cannot be empty");
        int maxResults = Math.Clamp((int?)args["maxResults"] ?? 20, 1, 100);
        string root = ResolveRepoRoot();
        string searchTerm = LastSymbolSegment(symbol);
        string escaped = Regex.Escape(searchTerm);

        try
        {
            string pattern = $":\\s*{escaped}\\b|<{escaped}>|{escaped}<";
            var hits = ManagedGrepRegexIndexed(root, searchTerm, pattern, 0, maxResults, line => !IsCommentOrString(line));
            var results = HitsToResults(hits, root, "implementation");
            return new JObject
            {
                ["ok"] = true,
                ["symbol"] = symbol,
                ["count"] = results.Count,
                ["truncated"] = results.Count >= maxResults,
                ["results"] = results,
            };
        }
        catch (Exception ex)
        {
            return Err("search_failed", $"Managed search error: {ex.Message}");
        }
    }

    private static JObject CsharpDescribeSymbol(JObject args)
    {
        string symbol = args["symbolName"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(symbol)) return Err("empty_symbol_name", "'symbolName' cannot be empty");
        int maxResults = Math.Clamp((int?)args["maxResults"] ?? 100, 1, 100);
        string root = ResolveRepoRoot();
        string searchTerm = LastSymbolSegment(symbol);
        string escaped = Regex.Escape(searchTerm);

        try
        {
            string pattern = $"(class|struct|interface|enum|record)\\s+{escaped}(?:[\\s<:({{]|$)";
            var hits = ManagedGrepRegexIndexed(root, searchTerm, pattern, 0, maxResults);
            var results = HitsToResults(hits, root, "declaration");
            return new JObject
            {
                ["ok"] = true,
                ["symbol"] = symbol,
                ["count"] = results.Count,
                ["truncated"] = results.Count >= maxResults,
                ["results"] = results,
            };
        }
        catch (Exception ex)
        {
            return Err("search_failed", $"Managed search error: {ex.Message}");
        }
    }

    private static JObject CsharpGetCallHierarchy(JObject args)
    {
        string symbol = args["symbolName"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(symbol)) return Err("empty_symbol_name", "'symbolName' cannot be empty");
        int maxResults = Math.Clamp((int?)args["maxResults"] ?? 50, 1, 200);
        string root = ResolveRepoRoot();
        string searchTerm = LastSymbolSegment(symbol);

        try
        {
            var calls = new JArray();
            var defs = new JArray();
            var typeDefPattern = CreateSearchRegex($@"^\s*(?:(?:public|private|protected|internal|static|sealed|abstract|partial)\s+)*(?:class|struct|interface|enum|record)\s+.*\b{Regex.Escape(searchTerm)}\b");
            var methodDefPattern = CreateSearchRegex($@"^\s*(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|unsafe|partial|readonly|ref)\s+)+(?:[\w<>\[\],.?]+\s+)+{Regex.Escape(searchTerm)}\s*\(");
            if (IsSimpleSymbol(searchTerm))
            {
                CsSourceIndex idx2 = GetSourceIndex(root);
                foreach (CsLocation loc in idx2.LocationsOf(searchTerm))
                {
                    if (calls.Count + defs.Count >= maxResults) break;
                    if (!idx2.FilesByPath.TryGetValue(loc.File, out CsSourceFile? src2)) continue;
                    string line = src2.Lines[loc.Line - 1];
                    if (!LineContainsExactCodeToken(line, searchTerm)) continue;
                    bool isDef = typeDefPattern.IsMatch(line) || methodDefPattern.IsMatch(line);
                    var target = isDef ? defs : calls;
                    target.Add(new JObject { ["file"] = RelOf(root, loc.File), ["line"] = loc.Line, ["kind"] = isDef ? "definition" : "call", ["match"] = Truncate(line.Trim(), 120) });
                }
            }
            else
            {
                var hits = ManagedFindAllReferences(root, searchTerm, maxResults * 2);
                foreach (var h in hits)
                {
                    if (calls.Count + defs.Count >= maxResults) break;
                    if (!LineContainsExactCodeToken(h.Match, searchTerm)) continue;
                    bool isDef = typeDefPattern.IsMatch(h.Match) || methodDefPattern.IsMatch(h.Match);
                    var target = isDef ? defs : calls;
                    target.Add(new JObject { ["file"] = RelOf(root, h.File), ["line"] = h.Line, ["kind"] = isDef ? "definition" : "call", ["match"] = h.Match });
                }
            }
            return new JObject { ["ok"] = true, ["symbol"] = symbol, ["count"] = calls.Count + defs.Count, ["definitions"] = defs, ["calls"] = calls };
        }
        catch (Exception ex)
        {
            return Err("search_failed", $"Managed search error: {ex.Message}");
        }
    }

    private static JObject CsharpIndexHealth(JObject args)
    {
        var health = BuildHealth();
        string root = ResolveRepoRoot();
        var index = GetSourceIndex(root);
        int sourceFileCount = index.Files.Count;
        return new JObject
        {
            ["ok"] = true,
            // Kept for existing studio/doctor consumers. This is a live source
            // file count, not a claim that C# symbols are persistently indexed.
            ["targetsIndexed"] = sourceFileCount,
            ["sourceFileCount"] = sourceFileCount,
            // 2026-07-28: the actual token-level index size — every unique
            // identifier-like token mapped to its (file, line) occurrences.
            // This is what was actually missing to answer "is this repo
            // really indexed" — sourceFileCount alone (3,543) understates it;
            // this repo's real count is tens of thousands of unique tokens.
            ["symbolCount"] = index.TokenCount,
            ["sourceSearch"] = new JObject
            {
                ["available"] = true,
                ["backend"] = CsharpBackendName,
                ["live"] = true,
            },
            ["snapshotFreshness"] = $"uptime: {health["uptimeSeconds"]}s, cache: {health["cache"]?["entries"]} entries",
            ["daemon"] = "flaxmcp-nav",
            ["version"] = Version,
            ["uptimeSeconds"] = health["uptimeSeconds"],
            ["cache"] = health["cache"],
            ["concurrency"] = health["concurrency"],
            ["docsIndex"] = health["docsIndex"],
            ["flaxApiIndex"] = health["flaxApiIndex"],
        };
    }

    private static string? ExtractNamespace(string filePath)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            var m = NamespaceRegex.Match(text);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception ex) { SwallowedCatch.Record("NavDaemon.ExtractNamespace", ex); return null; }
    }
}

