using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FlaxMcp.NavDaemon;

internal static partial class Program
{
    private static JObject DispatchFlaxApiAtomic(string atomic, JObject rawArgs)
    {
        try
        {
            FlaxApiIndex.EnsureBuilt();
            return atomic switch
            {
                "flax_api/lookup" => FlaxApiLookup(rawArgs),
                "flax_api/search" => FlaxApiSearch(rawArgs),
                "flax_api/members_of" => FlaxApiMembersOf(rawArgs),
                "flax_api/enum_values" => FlaxApiEnumValues(rawArgs),
                "flax_api/inheritance_chain" => FlaxApiInheritanceChain(rawArgs),
                _ => Err("unknown_atomic", atomic),
            };
        }
        catch (FileNotFoundException ex)
        {
            return Err("flax_api_xml_missing", ex.Message);
        }
        catch (Exception ex)
        {
            return Err("flax_api_atomic_threw", ex.Message);
        }
    }

    private static JObject FlaxApiLookup(JObject rawArgs)
    {
        string name = ((string?)rawArgs["name"] ?? "").Trim();
        if (name.Length == 0) return Err("invalid_arguments", "'name' required.");
        char? kindFilter = ParseKindFilter((string?)rawArgs["kind"]);
        string? nsFilter = NormalizeNs((string?)rawArgs["namespace"]);
        int maxResults = Math.Clamp((int?)rawArgs["maxResults"] ?? 10, 1, 50);
        string nl = name.ToLowerInvariant();
        var pq = new PriorityQueue<int, int>();
        var items = new List<(FlaxApiMember M, int Score)>();
        foreach (var m in FlaxApiIndex.Members)
        {
            if (kindFilter.HasValue && m.Kind != kindFilter.Value) continue;
            if (nsFilter != null && !m.Namespace.Equals(nsFilter, StringComparison.OrdinalIgnoreCase)) continue;
            int score;
            if (m.LowerFullName == nl) score = 100;
            else if (m.LowerSimpleName == nl) score = 80;
            else if (m.LowerFullName.EndsWith("." + nl, StringComparison.Ordinal)) score = 60;
            else if (m.LowerFullName.Contains(nl, StringComparison.Ordinal)) score = 20;
            else continue;
            if (m.IsRuntime) score += 5;
            items.Add((m, score));
        }
        items.Sort((a, b) => { int c = b.Score.CompareTo(a.Score); if (c != 0) return c; int ka = a.M.Kind == 'T' ? 0 : 1, kb = b.M.Kind == 'T' ? 0 : 1; c = ka.CompareTo(kb); if (c != 0) return c; return a.M.FullName.Length.CompareTo(b.M.FullName.Length); });
        bool truncated = items.Count > maxResults;
        var hits = new JArray();
        foreach (var s in items.Take(maxResults)) hits.Add(MemberToJson(s.M, full: true));
        return new JObject { ["ok"] = true, ["name"] = name, ["count"] = hits.Count, ["truncated"] = truncated, ["source"] = FlaxApiIndex.ResolvedFrom, ["hits"] = hits };
    }

    private static JObject FlaxApiSearch(JObject rawArgs)
    {
        string query = ((string?)rawArgs["query"] ?? "").Trim();
        if (query.Length == 0) return Err("invalid_arguments", "'query' required.");
        char? kindFilter = ParseKindFilter((string?)rawArgs["kind"]);
        string? nsFilter = NormalizeNs((string?)rawArgs["namespace"]);
        int maxResults = Math.Clamp((int?)rawArgs["maxResults"] ?? 25, 1, 200);
        string q = query.ToLowerInvariant();
        string[] tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool multi = tokens.Length > 1;
        var items = new List<(FlaxApiMember M, int Score)>();
        foreach (var m in FlaxApiIndex.Members)
        {
            if (kindFilter.HasValue && m.Kind != kindFilter.Value) continue;
            if (nsFilter != null && !m.Namespace.Equals(nsFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (multi)
            {
                bool all = true;
                foreach (var t in tokens) { if (!m.LowerHaystack.Contains(t, StringComparison.Ordinal)) { all = false; break; } }
                if (!all) continue;
            }
            else if (!m.LowerHaystack.Contains(q, StringComparison.Ordinal)) continue;
            int score;
            if (multi)
            {
                int inName = 0, inFull = 0;
                foreach (var t in tokens) { if (m.LowerSimpleName.Contains(t, StringComparison.Ordinal)) inName++; else if (m.LowerFullName.Contains(t, StringComparison.Ordinal)) inFull++; }
                score = inName == tokens.Length ? 100 : inFull == tokens.Length ? 60 : 20 + inName * 10 + inFull * 5;
            }
            else
            {
                score = m.LowerSimpleName == q ? 100 : m.LowerSimpleName.Contains(q, StringComparison.Ordinal) ? 60 : m.LowerFullName.Contains(q, StringComparison.Ordinal) ? 40 : 10;
            }
            if (m.IsRuntime) score += 15;
            items.Add((m, score));
        }
        items.Sort((a, b) => { int c = b.Score.CompareTo(a.Score); if (c != 0) return c; int ka = a.M.Kind == 'T' ? 0 : 1, kb = b.M.Kind == 'T' ? 0 : 1; c = ka.CompareTo(kb); if (c != 0) return c; return string.CompareOrdinal(a.M.FullName, b.M.FullName); });
        bool truncated = items.Count > maxResults;
        var hits = new JArray();
        foreach (var s in items.Take(maxResults)) hits.Add(MemberToJson(s.M, full: false));
        return new JObject { ["ok"] = true, ["query"] = query, ["count"] = hits.Count, ["truncated"] = truncated, ["source"] = FlaxApiIndex.ResolvedFrom, ["hits"] = hits };
    }

    private static JObject FlaxApiMembersOf(JObject rawArgs)
    {
        string name = ((string?)rawArgs["name"] ?? "").Trim();
        if (name.Length == 0) return Err("invalid_arguments", "'name' required.");
        char? memberKind = ParseKindFilter((string?)rawArgs["kind"]);
        if (memberKind == 'T') memberKind = null;
        string? nsFilter = NormalizeNs((string?)rawArgs["namespace"]);
        int maxMembers = Math.Clamp((int?)rawArgs["maxMembers"] ?? 200, 1, 1000);
        string nl = name.ToLowerInvariant();
        FlaxApiMember? owner = FlaxApiIndex.FindType(name)
            ?? FlaxApiIndex.FindType("FlaxEngine." + name);
        int ownerScore = owner != null ? (owner.IsRuntime ? 105 : 100) : -1;
        if (owner == null)
        {
            foreach (var m in FlaxApiIndex.Members)
            {
                if (m.Kind != 'T') continue;
                if (nsFilter != null && !m.Namespace.Equals(nsFilter, StringComparison.OrdinalIgnoreCase)) continue;
                string fn = StripArity(m.FullName).ToLowerInvariant();
                string sn = StripArity(m.SimpleName).ToLowerInvariant();
                int s = fn == nl ? 100 : StripArity(fn) == nl ? 90 : sn == nl ? 80 : fn.EndsWith("." + nl, StringComparison.Ordinal) ? 60 : 0;
                if (s == 0) continue;
                if (m.IsRuntime) s += 5;
                if (s > ownerScore) { ownerScore = s; owner = m; }
            }
        }
        if (owner == null)
        {
            var cands = new JArray();
            foreach (var m in FlaxApiIndex.Members) { if (m.Kind != 'T') continue; if (StripArity(m.SimpleName).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { cands.Add(m.FullName); if (cands.Count >= 15) break; } }
            return new JObject { ["ok"] = true, ["found"] = false, ["name"] = name, ["source"] = FlaxApiIndex.ResolvedFrom, ["candidates"] = cands, ["note"] = "No type matched." };
        }
        string ownerNs = owner.Namespace;
        string ownerSn = StripArity(owner.SimpleName);
        var byOv = new Dictionary<string, List<FlaxApiMember>>(StringComparer.Ordinal);
        var order = new List<string>();
        int total = 0; bool truncated = false;
        foreach (var m in FlaxApiIndex.Members)
        {
            if (m.Kind == 'T') continue;
            if (!StripArity(m.DeclType).Equals(ownerSn, StringComparison.Ordinal)) continue;
            if (!m.Namespace.Equals(ownerNs, StringComparison.Ordinal)) continue;
            if (memberKind.HasValue && m.Kind != memberKind.Value) continue;
            string key = m.Kind + ":" + StripArity(m.SimpleName);
            if (!byOv.ContainsKey(key)) { byOv[key] = new List<FlaxApiMember>(); order.Add(key); }
            byOv[key].Add(m); total++;
            if (total >= maxMembers) { truncated = true; break; }
        }
        var sets = new JArray();
        foreach (var k in order)
        {
            var list = byOv[k];
            var first = list[0];
            var ovs = new JArray();
            foreach (var mv in list)
            {
                var ov = new JObject { ["signatureKey"] = mv.RawName, ["summary"] = mv.Summary };
                if (FlaxApiIndex.TryGetDeprecation(mv, out string ovDep))
                {
                    ov["deprecated"] = true;
                    if (ovDep.Length > 0) ov["deprecationHint"] = ovDep;
                }
                if (mv.Parameters.Length > 0) ov["parameters"] = mv.Parameters;
                if (mv.Returns.Length > 0) ov["returns"] = mv.Returns;
                int ga = GenericArity(mv.SimpleName);
                if (ga > 0) ov["genericArity"] = ga;
                ovs.Add(ov);
            }
            var so = new JObject { ["simpleName"] = StripArity(first.SimpleName), ["kind"] = KindWord(first.Kind), ["count"] = list.Count, ["overloads"] = ovs };
            sets.Add(so);
        }
        var typeObj = new JObject { ["kind"] = KindWord(owner.Kind), ["fullName"] = owner.FullName, ["summary"] = owner.Summary };
        int ar = GenericArity(owner.SimpleName);
        if (ar > 0) typeObj["genericArity"] = ar;
        return new JObject { ["ok"] = true, ["found"] = true, ["name"] = name, ["resolvedType"] = owner.FullName, ["namespace"] = ownerNs, ["count"] = total, ["truncated"] = truncated, ["source"] = FlaxApiIndex.ResolvedFrom, ["type"] = typeObj, ["overloadSets"] = sets };
    }

    private static JObject FlaxApiEnumValues(JObject rawArgs)
    {
        string name = ((string?)rawArgs["name"] ?? "").Trim();
        if (name.Length == 0) return Err("invalid_arguments", "'name' required.");
        string nl = name.ToLowerInvariant();
        FlaxApiMember? enumType = FlaxApiIndex.FindType(name)
            ?? FlaxApiIndex.FindType("FlaxEngine." + name);
        if (enumType == null || enumType.Kind != 'T')
        {
            foreach (var m in FlaxApiIndex.Members)
            {
                if (m.Kind != 'T') continue;
                string fn = StripArity(m.FullName).ToLowerInvariant();
                string sn = StripArity(m.SimpleName).ToLowerInvariant();
                if (fn == nl || sn == nl || fn.EndsWith("." + nl, StringComparison.Ordinal)) { enumType = m; break; }
            }
        }
        if (enumType == null)
        {
            var cands = new JArray();
            foreach (var m in FlaxApiIndex.Members)
            {
                if (m.Kind != 'T') continue;
                string sn = StripArity(m.SimpleName);
                string fn = m.FullName; // full name, not stripped for broader search
                if (sn.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || fn.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    cands.Add(m.FullName);
                    if (cands.Count >= 15) break;
                }
            }
            return new JObject { ["ok"] = true, ["found"] = false, ["name"] = name, ["candidates"] = cands, ["note"] = "No enum matched." };
        }
        var values = new JArray();
        foreach (var m in FlaxApiIndex.Members)
        {
            if (m.Kind != 'F') continue;
            if (!StripArity(m.DeclType).Equals(StripArity(enumType.SimpleName), StringComparison.Ordinal)) continue;
            if (!m.Namespace.Equals(enumType.Namespace, StringComparison.Ordinal)) continue;
            if (string.Equals(m.SimpleName, "value__", StringComparison.Ordinal)) continue;
            values.Add(new JObject { ["name"] = m.SimpleName, ["kind"] = "value", ["value"] = m.ConstantValue.Length > 0 ? m.ConstantValue : null, ["summary"] = m.Summary });
        }
        return new JObject { ["ok"] = true, ["found"] = true, ["enumType"] = enumType.FullName, ["name"] = name, ["count"] = values.Count, ["source"] = FlaxApiIndex.ResolvedFrom, ["values"] = values };
    }

    private static JObject FlaxApiInheritanceChain(JObject rawArgs)
    {
        string name = ((string?)rawArgs["name"] ?? "").Trim();
        if (name.Length == 0) return Err("invalid_arguments", "'name' required.");
        string nl = name.ToLowerInvariant();
        FlaxApiMember? typeEntry = FlaxApiIndex.FindType(name)
            ?? FlaxApiIndex.FindType("FlaxEngine." + name);
        // Fallback: linear scan for simple-name match
        if (typeEntry == null)
        {
            foreach (var m in FlaxApiIndex.Members)
            {
                if (m.Kind != 'T') continue;
                string fn = StripArity(m.FullName).ToLowerInvariant();
                string sn = StripArity(m.SimpleName).ToLowerInvariant();
                if (fn == nl || sn == nl || fn.EndsWith("." + nl, StringComparison.Ordinal)) { typeEntry = m; break; }
            }
        }
        if (typeEntry == null)
        {
            var cands = new JArray();
            foreach (var m in FlaxApiIndex.Members) { if (m.Kind != 'T') continue; string sn = StripArity(m.SimpleName); if (sn.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { cands.Add(m.FullName); if (cands.Count >= 15) break; } }
            return new JObject { ["ok"] = true, ["found"] = false, ["name"] = name, ["candidates"] = cands };
        }
        var chain = new List<string> { typeEntry.FullName };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { typeEntry.FullName };
        string? current = typeEntry.FullName;
        for (int i = 0; i < 20; i++)
        {
            string? parent = FindParentType(current);
            if (string.IsNullOrEmpty(parent) || !seen.Add(parent)) break;
            chain.Add(parent);
            current = parent;
            if (parent == "System.Object") break;
        }
        return new JObject { ["ok"] = true, ["found"] = true, ["name"] = name, ["resolvedType"] = typeEntry.FullName, ["chain"] = new JArray(chain.Select(c => (JToken)c)) };
    }

    private static string? FindParentType(string fullTypeName)
    {
        if (fullTypeName == "System.Object" || fullTypeName == "FlaxEngine.Object") return null;
        var m = FlaxApiIndex.FindType(fullTypeName);
        if (m == null) return null;
        string bt = m.BaseType;
        if (string.IsNullOrEmpty(bt)) return null;
        // Strip possible "T:" prefix from XML cref
        if (bt.Length > 2 && bt[1] == ':') bt = bt[2..];
        // O(1) lookup in type dictionary
        var parent = FlaxApiIndex.FindType(bt);
        if (parent != null) return parent.FullName;
        // Fallback: try FlaxEngine. prefix
        parent = FlaxApiIndex.FindType("FlaxEngine." + bt);
        if (parent != null) return parent.FullName;
        // Fallback: simple-name match (linear scan, rare)
        foreach (var m2 in FlaxApiIndex.Members)
            if (m2.Kind == 'T' && string.Equals(m2.SimpleName, bt, StringComparison.OrdinalIgnoreCase))
                return m2.FullName;
        return bt;
    }

    private static int GenericArity(string segment) { int t = segment.IndexOf('`'); if (t < 0 || t == segment.Length - 1) return 0; int n = 0; for (int i = t + 1; i < segment.Length && char.IsDigit(segment[i]); i++) n = n * 10 + (segment[i] - '0'); return n; }
    private static string StripArity(string s) { int t = s.IndexOf('`'); return t < 0 ? s : s[..t]; }
    private static string? NormalizeNs(string? ns) => ns?.Trim().ToLowerInvariant() switch { "engine" or "flaxengine" or "runtime" => "FlaxEngine", "editor" or "flaxeditor" => "FlaxEditor", { } x when x.Length > 0 => ns!.Trim(), _ => null };
    private static char? ParseKindFilter(string? kind) => kind?.Trim().ToLowerInvariant() switch { "type" or "t" or "class" or "struct" or "enum" or "interface" => 'T', "method" or "m" or "ctor" or "constructor" => 'M', "property" or "p" or "prop" => 'P', "field" or "f" => 'F', "event" or "e" => 'E', _ => null };
    private static string KindWord(char k) => k switch { 'T' => "type", 'M' => "method", 'P' => "property", 'F' => "field", 'E' => "event", _ => k.ToString() };
    private static JObject MemberToJson(FlaxApiMember m, bool full)
    {
        var o = new JObject { ["kind"] = KindWord(m.Kind), ["fullName"] = m.FullName, ["namespace"] = m.Namespace, ["declType"] = m.DeclType, ["simpleName"] = m.SimpleName, ["summary"] = m.Summary };
        // Deprecation-awareness (2026-08-18, steal #2 Codeturion/unreal-api-mcp):
        // ObsoleteAttribute from the DLL metadata — XML docs carry no
        // deprecation info. Message usually names the replacement ("Use X
        // instead"), which is the entire point.
        if (FlaxApiIndex.TryGetDeprecation(m, out string depMsg))
        {
            o["deprecated"] = true;
            if (depMsg.Length > 0) o["deprecationHint"] = depMsg;
        }
        if (full)
        {
            o["signatureKey"] = m.RawName;
            if (m.Parameters.Length > 0) o["parameters"] = m.Parameters;
            if (m.Returns.Length > 0) o["returns"] = m.Returns;
            if (m.Remarks.Length > 0) o["remarks"] = m.Remarks;
            int ga = GenericArity(m.SimpleName);
            if (ga > 0) o["genericArity"] = ga;
        }
        return o;
    }
}
