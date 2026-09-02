using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace FlaxMcp.NavDaemon;

internal static partial class Program
{
    private static JObject DispatchDocAtomic(string atomic, JObject rawArgs)
    {
        try
        {
            string repoRoot = ResolveRepoRoot();
            DocsIndex.EnsureBuilt(repoRoot);
            return atomic switch
            {
                "docs/find_section" => DocsFindSection(rawArgs),
                "docs/find_doc" => DocsFindDoc(rawArgs),
                "docs/grep" => DocsGrep(rawArgs),
                "docs/find_capability" => DocsFindCapability(rawArgs),
                "docs/find_composes" => DocsFindComposes(rawArgs),
                _ => Err("unknown_atomic", atomic),
            };
        }
        catch (Exception ex)
        {
            return Err("doc_atomic_threw", ex.Message);
        }
    }

    private static JObject DocsFindSection(JObject rawArgs)
    {
        string query = ((string?)rawArgs["query"]) ?? "";
        if (string.IsNullOrEmpty(query)) return Err("invalid_arguments", "'query' required.");
        int depthMin = Math.Clamp((int?)rawArgs["depthMin"] ?? 1, 1, 6);
        int depthMax = Math.Clamp((int?)rawArgs["depthMax"] ?? 6, 1, 6);
        int maxResults = Math.Clamp((int?)rawArgs["maxResults"] ?? 50, 1, 500);
        var hits = new JArray();
        bool truncated = false;
        foreach (var h in DocsIndex.Headers)
        {
            if (h.Depth < depthMin || h.Depth > depthMax) continue;
            if (h.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (hits.Count >= maxResults) { truncated = true; break; }
            hits.Add(new JObject { ["file"] = h.RelPath, ["line"] = h.Line, ["kind"] = "section", ["depth"] = h.Depth, ["text"] = h.Text });
        }
        return new JObject { ["ok"] = true, ["query"] = query, ["count"] = hits.Count, ["truncated"] = truncated, ["hits"] = hits };
    }

    private static JObject DocsFindDoc(JObject rawArgs)
    {
        try
        {
            string query = ((string?)rawArgs["query"]) ?? "";
            if (string.IsNullOrEmpty(query)) return Err("invalid_arguments", "'query' required.");
            int maxResults = Math.Clamp((int?)rawArgs["maxResults"] ?? 30, 1, 200);
            string lower = query.ToLowerInvariant();
            var scored = new List<(DocFile File, int Count)>();
            foreach (var f in DocsIndex.Files)
            {
                int c = CountOccurrences(f.LowerBody, lower);
                if (c > 0) scored.Add((f, c));
            }
            scored.Sort((a, b) => b.Count.CompareTo(a.Count));
            var hits = new JArray();
            bool truncated = scored.Count > maxResults;
            foreach (var s in scored.Take(maxResults))
                hits.Add(new JObject { ["file"] = s.File.RelPath, ["kind"] = "doc", ["sizeBytes"] = s.File.SizeBytes, ["mentionCount"] = s.Count });
            return new JObject { ["ok"] = true, ["query"] = query, ["count"] = hits.Count, ["truncated"] = truncated, ["hits"] = hits };
        }
        catch (Exception ex)
        {
            return Err("find_doc_failed", ex.Message);
        }
    }

    private static JObject DocsGrep(JObject rawArgs)
    {
        string query = ((string?)rawArgs["query"]) ?? "";
        if (string.IsNullOrEmpty(query)) return Err("invalid_arguments", "'query' required.");
        string pathPrefix = ((string?)rawArgs["pathPrefix"] ?? "").Replace('\\', '/').ToLowerInvariant();
        int maxResults = Math.Clamp((int?)rawArgs["maxResults"] ?? 100, 1, 1000);
        int context = Math.Clamp((int?)rawArgs["context"] ?? 60, 1, 300);
        string q = query.ToLowerInvariant();
        var hits = new JArray();
        bool truncated = false;
        foreach (var f in DocsIndex.Files)
        {
            if (pathPrefix.Length > 0)
            {
                string rel = f.RelPath.Replace('\\', '/').ToLowerInvariant();
                if (!rel.StartsWith(pathPrefix, StringComparison.Ordinal)) continue;
            }
            int idx = 0;
            int currentLine = 1;
            while (true)
            {
                int next = f.LowerBody.IndexOf(q, idx, StringComparison.Ordinal);
                if (next < 0) break;
                if (hits.Count >= maxResults) { truncated = true; break; }
                for (int i = idx; i < next; i++)
                    if (f.LowerBody[i] == '\n') currentLine++;
                int from = Math.Max(0, next - context);
                int to = Math.Min(f.LowerBody.Length, next + q.Length + context);
                string snippet = f.LowerBody[from..to].Replace('\n', ' ').Replace('\r', ' ');
                hits.Add(new JObject { ["file"] = f.RelPath, ["line"] = currentLine, ["kind"] = "hit", ["snippet"] = snippet });
                idx = next + q.Length;
            }
            if (truncated) break;
        }
        return new JObject { ["ok"] = true, ["query"] = query, ["count"] = hits.Count, ["truncated"] = truncated, ["hits"] = hits };
    }

    private static JObject DocsFindCapability(JObject rawArgs)
    {
        // Community build: capability index not included (requires private CapabilityIndex scanner).
        // Returns empty result with explanatory note.
        return new JObject { ["ok"] = true, ["query"] = ((string?)rawArgs["query"] ?? (string?)rawArgs["symbol"] ?? "") ?? "", ["count"] = 0, ["truncated"] = false, ["hits"] = new JArray(), ["note"] = "capability index not included in community build — stub returns empty. See CapabilityIndex.cs vendoring note in README." };
    }


    private static JObject DocsFindComposes(JObject rawArgs)
    {
        // Community build: capability index not included (requires private CapabilityIndex scanner).
        return new JObject { ["ok"] = true, ["query"] = ((string?)rawArgs["query"] ?? (string?)rawArgs["symbol"] ?? "") ?? "", ["symbol"] = ((string?)rawArgs["query"] ?? (string?)rawArgs["symbol"] ?? "") ?? "", ["count"] = 0, ["truncated"] = false, ["composes"] = new JArray(), ["composedBy"] = new JArray(), ["note"] = "capability index not included in community build — stub returns empty." };
    }


    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0) return 0;
        int c = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { c++; i += needle.Length; }
        return c;
    }

}
