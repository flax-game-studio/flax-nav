// Program.AtlasAtomics.cs — the atlas/* completeness family (partial of Program).
//
// THE GAP (F5, DEEP-AUDIT-8 L7): nothing proved the atlas COVERS the
// surface. Live-proven Day-1: `demo-multiplayer` + `demo-rhythm` are
// 0-mention across the entire docs/ecosystem/atlas/, and 00-ATLAS.md
// claims "8 GameSide libs" while disk has 7. An atlas that silently
// drifts out of completeness mis-routes every AI that trusts it as the
// whole-ecosystem gestalt.
//
// This atomic walks the REAL tree (plugins/, GameSide/Game.Shared.*, demos/,
// external/ vendored dirs) and reports, for each unit, whether its name is
// mentioned ANYWHERE in the atlas markdown — plus count-claim drift between an
// atlas assertion ("N GameSide libs") and the disk count. Pure filesystem +
// markdown scan; no Roslyn, no kernel pipeline. It is the engine behind
// scripts/check-atlas-completeness.ps1 (the standing gate).
//
// Honesty: it reports candidates for human review (an atlas may deliberately
// roll a unit under an umbrella section); the `mentioned` flag is the literal
// "does the unit's directory name appear in any atlas .md" question, never a
// claim that the coverage is semantically adequate. The gate treats UNMENTIONED
// as the failure — a unit with 0 literal mentions cannot be covered.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace FlaxMcp.NavDaemon;

internal static partial class Program
{
    /// <summary>
    /// Dispatch entry for the atlas/* atomic family. Called from
    /// HandleOneRequest's short-circuit branch (parallel to docs/* + flax_api/*).
    /// </summary>
    private static JObject DispatchAtlasAtomic(string atomic, JObject rawArgs)
    {
        try
        {
            return atomic switch
            {
                "atlas/diff_tree" => AtlasDiffTree(rawArgs),
                _ => Err("unknown_atomic", atomic),
            };
        }
        catch (Exception ex)
        {
            return Err("atlas_atomic_threw", ex.Message);
        }
    }

    // ===== atlas/diff_tree =====
    //
    // Args:
    //   unitKind (string, optional)  filter the units scanned:
    //                                  all (default) | plugin | gameside | demo | external
    //   maxResults (int, optional)   cap on units returned (default 200, max 1000).
    //
    // Returns:
    //   { ok, atlasFiles, source,
    //     totals:{ scanned, mentioned, unmapped },
    //     unmapped:[{ kind, name, file }],
    //     countDrift:[{ claim, atlasSays, diskHas, file, line }],
    //     units:[{ kind, name, file, mentioned }] }   (units omitted unless verbose)
    private static JObject AtlasDiffTree(JObject rawArgs)
    {
        string repoRoot = ResolveRepoRoot();
        string atlasDir = Path.Combine(repoRoot, "docs", "knowledge", "ecosystem");
        if (!Directory.Exists(atlasDir))
            return Err("atlas_dir_missing",
                $"atlas dir not found at {atlasDir}. Expected docs/knowledge/ecosystem/.");

        string kindFilter = ((string?)rawArgs["unitKind"] ?? "all").Trim().ToLowerInvariant();
        int maxResults = (int?)rawArgs["maxResults"] ?? 200;
        if (maxResults <= 0) maxResults = 200;
        if (maxResults > 1000) maxResults = 1000;
        bool verbose = (bool?)rawArgs["verbose"] ?? false;

        // 1. Read every atlas .md once into one lowercased haystack (the
        //    "is this unit mentioned anywhere in the atlas" corpus).
        var atlasFiles = Directory.GetFiles(atlasDir, "*.md", SearchOption.AllDirectories);
        var atlasTexts = new List<(string Rel, string Lower)>();
        var combined = new System.Text.StringBuilder();
        foreach (var f in atlasFiles)
        {
            string body;
            try { body = File.ReadAllText(f); } catch (IOException ex) { DaemonLog.Warn($"atlas build: skipped unreadable {f}: {ex.Message}"); continue; } catch (UnauthorizedAccessException ex) { DaemonLog.Warn($"atlas build: skipped inaccessible {f}: {ex.Message}"); continue; }
            string lower = body.ToLowerInvariant();
            atlasTexts.Add((RelOf(repoRoot, f), lower));
            combined.Append(lower).Append('\n');
        }
        string haystack = combined.ToString();

        // 2. Enumerate the real tree units.
        var units = new List<(string Kind, string Name, string Rel)>();
        if (kindFilter is "all" or "plugin")
            AddDirUnits(units, "plugin", Path.Combine(repoRoot, "plugins"), repoRoot);
        if (kindFilter is "all" or "gameside")
            AddDirUnits(units, "gameside", Path.Combine(repoRoot, "GameSide"), repoRoot,
                        namePrefix: "Game.Shared.");
        if (kindFilter is "all" or "demo")
            AddDirUnits(units, "demo", Path.Combine(repoRoot, "demos"), repoRoot);
        if (kindFilter is "all" or "external")
            AddDirUnits(units, "external", Path.Combine(repoRoot, "external"), repoRoot);

        // 3. For each unit, is its directory name mentioned in the atlas corpus?
        var unmapped = new JArray();
        var allUnits = new JArray();
        int mentioned = 0;
        foreach (var u in units)
        {
            string needle = u.Name.ToLowerInvariant();
            bool isMentioned = haystack.Contains(needle, StringComparison.Ordinal);
            if (isMentioned) mentioned++;
            else if (unmapped.Count < maxResults)
                unmapped.Add(new JObject
                {
                    ["kind"] = u.Kind,
                    ["name"] = u.Name,
                    ["file"] = u.Rel,
                });
            if (verbose && allUnits.Count < maxResults)
                allUnits.Add(new JObject
                {
                    ["kind"] = u.Kind,
                    ["name"] = u.Name,
                    ["file"] = u.Rel,
                    ["mentioned"] = isMentioned,
                });
        }

        // 4. Count-claim drift: scan the atlas for "N <thing>" claims that name a
        //    countable tree dimension and compare to disk. Conservative: only the
        //    well-known dimensions (plugins, GameSide libs, demos) so a stray
        //    number in prose isn't flagged.
        var countDrift = AtlasCountDrift(atlasTexts, repoRoot);

        return new JObject
        {
            ["ok"] = true,
            ["atlasFiles"] = atlasFiles.Length,
            ["source"] = RelOf(repoRoot, atlasDir),
            ["totals"] = new JObject
            {
                ["scanned"] = units.Count,
                ["mentioned"] = mentioned,
                ["unmapped"] = units.Count - mentioned,
            },
            ["unmapped"] = unmapped,
            ["countDrift"] = countDrift,
            ["units"] = verbose ? allUnits : new JArray(),
            ["note"] = "unmapped = the unit's directory name has 0 literal mentions in any atlas " +
                             ".md → it cannot be covered. An atlas may roll a unit under an umbrella " +
                             "section; verify before assuming. countDrift compares an atlas count CLAIM " +
                             "to the disk count for the well-known dimensions only.",
        };
    }

    /// <summary>
    /// Add immediate child directories of <paramref name="parent"/> as units.
    /// For GameSide, only dirs matching the Game.Shared.* prefix count.
    /// </summary>
    private static void AddDirUnits(
        List<(string Kind, string Name, string Rel)> units,
        string kind, string parent, string repoRoot, string? namePrefix = null)
    {
        if (!Directory.Exists(parent)) return;
        foreach (var d in Directory.GetDirectories(parent))
        {
            string name = Path.GetFileName(d);
            if (namePrefix != null && !name.StartsWith(namePrefix, StringComparison.Ordinal))
                continue;
            // Skip noise dirs that aren't real units.
            if (name is "bin" or "obj" or ".git" or ".vs") continue;
            units.Add((kind, name, RelOf(repoRoot, d)));
        }
    }

    /// <summary>
    /// Detect REPO-WIDE atlas count-claim drift for the well-known countable
    /// dimensions. Matches global inventory phrases like "27 plugins",
    /// "8 GameSide libs", "5 demos" and compares them to the live disk count.
    /// Slice-local prose such as "Owns 6 plugins" is intentionally ignored.
    /// </summary>
    private static JArray AtlasCountDrift(List<(string Rel, string Lower)> atlasTexts, string repoRoot)
    {
        var drift = new JArray();

        int diskPlugins = CountDirs(Path.Combine(repoRoot, "plugins"));
        int diskGameSide = CountDirs(Path.Combine(repoRoot, "GameSide"), "Game.Shared.");
        int diskDemos = CountDirs(Path.Combine(repoRoot, "demos"));

        // (regex, dimension label, disk count). The regex captures the claimed
        // number immediately before the dimension noun.
        var dims = new (Regex Rx, string Label, int Disk)[]
        {
            (new Regex(@"(\d+)\s+plugins\b", RegexOptions.IgnoreCase), "plugins", diskPlugins),
            (new Regex(@"(\d+)\s+gameside\s+(?:lib|libs|libraries)\b", RegexOptions.IgnoreCase), "GameSide libs", diskGameSide),
            (new Regex(@"(\d+)\s+game\.shared\.\*\s+(?:lib|libs|libraries)\b", RegexOptions.IgnoreCase), "GameSide libs", diskGameSide),
            (new Regex(@"(\d+)\s+demos\b", RegexOptions.IgnoreCase), "demos", diskDemos),
        };

        foreach (var (rel, lower) in atlasTexts)
        {
            // Walk line-by-line so we can report file:line of the stale claim.
            string[] lines = lower.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (var dim in dims)
                {
                    var m = dim.Rx.Match(lines[i]);
                    if (!m.Success) continue;
                    if (!LooksRepoWideCountClaim(dim.Label, rel, lines[i])) continue;
                    if (!int.TryParse(m.Groups[1].Value, out int claimed)) continue;
                    if (claimed == dim.Disk) continue;
                    drift.Add(new JObject
                    {
                        ["claim"] = dim.Label,
                        ["atlasSays"] = claimed,
                        ["diskHas"] = dim.Disk,
                        ["file"] = rel,
                        ["line"] = i + 1,
                    });
                }
            }
        }
        return drift;
    }

    private static bool LooksRepoWideCountClaim(string label, string rel, string line)
    {
        string file = Path.GetFileName(rel).ToLowerInvariant();

        string[] cues =
        {
            "roster",
            "live counts",
            "live count",
            "repo-wide",
            "whole repo",
            "entire repo",
            "across the repo",
            "all plugins",
            "all demos",
            "all gameside",
            "plugin roster",
            "demo roster",
            "gameside roster",
        };

        bool hasCue = cues.Any(cue => line.Contains(cue, StringComparison.Ordinal));
        if (!hasCue && file is "00-atlas.md" or "perfection-final.md")
        {
            hasCue = line.Contains("as of", StringComparison.Ordinal) ||
                     line.Contains("current", StringComparison.Ordinal) ||
                     line.Contains("today", StringComparison.Ordinal);
        }
        if (!hasCue) return false;

        return label switch
        {
            "plugins" => true,
            "GameSide libs" => line.Contains("gameside", StringComparison.Ordinal) ||
                               line.Contains("game.shared", StringComparison.Ordinal),
            "demos" => true,
            _ => false,
        };
    }

    private static int CountDirs(string parent, string? namePrefix = null)
    {
        if (!Directory.Exists(parent)) return 0;
        int n = 0;
        foreach (var d in Directory.GetDirectories(parent))
        {
            string name = Path.GetFileName(d);
            if (namePrefix != null && !name.StartsWith(namePrefix, StringComparison.Ordinal)) continue;
            if (name is "bin" or "obj" or ".git" or ".vs") continue;
            n++;
        }
        return n;
    }

    // ===== plugin/catalog atomic =====

    private static JObject DispatchPluginAtomic(string atomic, JObject args)
    {
        try
        {
            return atomic switch
            {
                "plugin/catalog" => PluginCatalog(args),
                _ => Err("unknown_atomic", atomic),
            };
        }
        catch (Exception ex)
        {
            return Err("plugin_atomic_threw", ex.Message);
        }
    }

    private static JObject PluginCatalog(JObject args)
    {
        string repoRoot = ResolveRepoRoot();
        string pluginsDir = Path.Combine(repoRoot, "plugins");
        if (!Directory.Exists(pluginsDir))
            return Err("plugins_dir_missing", $"plugins dir not found at {pluginsDir}");

        string categoryFilter = ((string?)args["category"] ?? "").Trim().ToLowerInvariant();
        string capabilityFilter = ((string?)args["capability"] ?? "").Trim().ToLowerInvariant();
        string format = ((string?)args["format"] ?? "full").Trim().ToLowerInvariant();
        bool summaryMode = format == "summary";

        var categories = new JArray();
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FlaxMcp.Plugins", "FlaxMcp.Plugins.Core", "bin", "obj" };

        foreach (var pluginDir in Directory.GetDirectories(pluginsDir))
        {
            string categoryName = Path.GetFileName(pluginDir);
            if (skipDirs.Contains(categoryName) || categoryName.StartsWith('.')) continue;

            string pluginJsonPath = Path.Combine(pluginDir, "flaxmcp-plugin.json");
            if (!File.Exists(pluginJsonPath)) continue;

            string manifestRaw;
            try { manifestRaw = File.ReadAllText(pluginJsonPath); }
            catch (IOException ex) { DaemonLog.Warn($"plugin catalog: skipped unreadable {pluginJsonPath}: {ex.Message}"); continue; }
            var manifest = TryParseJObject(manifestRaw);
            if (manifest == null) { DaemonLog.Warn($"plugin catalog: malformed JSON {pluginJsonPath}"); continue; }

            string cap = ((string?)manifest["capability"] ?? categoryName).ToLowerInvariant();
            string desc = (string?)manifest["description"] ?? "";
            string version = (string?)manifest["version"] ?? "1.0.0";

            if (categoryFilter.Length > 0 && categoryName != categoryFilter) continue;
            if (capabilityFilter.Length > 0 && !cap.Contains(capabilityFilter, StringComparison.OrdinalIgnoreCase)) continue;

            var assemblies = manifest["assemblies"] as JArray ?? new JArray();

            var moduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asm in assemblies)
            {
                string asmName = asm.ToString();
                var parts = asmName.Split('.');
                if (parts.Length >= 3 && parts[0] == "FlaxMcp")
                    moduleNames.Add(parts[2]);
            }
            if (moduleNames.Count == 0) continue;

            string asmCap = "";
            foreach (var asm in assemblies)
            {
                string an = asm.ToString();
                var parts = an.Split('.');
                if (parts.Length >= 2 && parts[0] == "FlaxMcp" && !string.IsNullOrEmpty(parts[1]))
                { asmCap = parts[1]; break; }
            }
            if (string.IsNullOrEmpty(asmCap))
                asmCap = char.ToUpperInvariant(cap[0]) + cap[1..];

            var modCsCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var modTestCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var modToolCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var modMegaCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var modSubActions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var modKeyPaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var mn in moduleNames) { modCsCount[mn] = 0; modTestCount[mn] = 0; modToolCount[mn] = 0; modMegaCount[mn] = 0; modSubActions[mn] = 0; modKeyPaths[mn] = new List<string>(); }

            string coreBase = Path.Combine(pluginDir, "src", "Core");
            string liveBase = Path.Combine(pluginDir, "src", "Live");
            foreach (var mn in moduleNames)
            {
                var paths = new List<string>();
                string coreDir = Path.Combine(coreBase, $"FlaxMcp.{asmCap}.{mn}.Core");
                string liveDir = Path.Combine(liveBase, $"FlaxMcp.{asmCap}.{mn}.Live");
                string coreDirBare = Path.Combine(coreBase, $"FlaxMcp.{asmCap}.{mn}");
                string liveDirBare = Path.Combine(liveBase, $"FlaxMcp.{asmCap}.{mn}");
                if (Directory.Exists(coreDir)) paths.Add(coreDir);
                else if (Directory.Exists(coreDirBare)) paths.Add(coreDirBare);
                if (Directory.Exists(liveDir)) paths.Add(liveDir);
                else if (Directory.Exists(liveDirBare)) paths.Add(liveDirBare);
                int csCount = 0;
                foreach (var d in paths) { try { csCount += Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories).Length; } catch (DirectoryNotFoundException ex) { SwallowedCatch.Record("NavDaemon.PluginCatalog.csCount", ex); } catch (IOException ex) { DaemonLog.Warn($"plugin catalog: io error scanning {d}: {ex.Message}"); } }
                modCsCount[mn] = csCount;
                foreach (var d in paths) { try { modKeyPaths[mn].Add(RelOf(repoRoot, d)); } catch (Exception ex) when (ex is not OutOfMemoryException) { DaemonLog.Warn($"plugin catalog: skip key path {d}: {ex.Message}"); } }
            }

            string testsBase = Path.Combine(pluginDir, "tests");
            foreach (var mn in moduleNames)
            {
                string testDir = Path.Combine(testsBase, $"FlaxMcp.{asmCap}.{mn}.Tests");
                if (Directory.Exists(testDir)) { try { modTestCount[mn] = Directory.GetFiles(testDir, "*.cs", SearchOption.AllDirectories).Length; } catch (DirectoryNotFoundException ex) { SwallowedCatch.Record("NavDaemon.PluginCatalog.testCount", ex); } catch (IOException ex) { DaemonLog.Warn($"plugin catalog: io error test dir {testDir}: {ex.Message}"); } }
            }

            try
            {
                // Managed [Tool]/[MegaTool] attribute scanning — replaces rg subprocess.
                // Counts per-module like the old rg pipeline but uses file I/O + string
                // containment check (faster than regex for exact attribute names).
                int totalTools = 0, totalMegas = 0;
                foreach (CsSourceFile source in GetSourceFiles(pluginDir))
                {
                    string csFile = source.Path;
                    string norm = csFile.Replace('/', '\\');

                    string pfx = $"FlaxMcp.{asmCap}."; int idx = norm.IndexOf(pfx, StringComparison.Ordinal);
                    if (idx < 0) continue; string after = norm[(idx + pfx.Length)..]; int dot = after.IndexOf('.');
                    if (dot <= 0) continue; string mn = after[..dot];
                    if (!moduleNames.Contains(mn)) continue;

                    try
                    {
                        foreach (string line in source.Lines)
                        {
                            if (line.Contains("[Tool]", StringComparison.Ordinal))
                                { if (modToolCount.ContainsKey(mn)) modToolCount[mn]++; totalTools++; }
                            else if (line.Contains("[MegaTool]", StringComparison.Ordinal))
                                { if (modMegaCount.ContainsKey(mn)) modMegaCount[mn]++; totalMegas++; }
                        }
                    }
                    catch (IOException ex) { SwallowedCatch.Record("NavDaemon.PluginCatalog.readFile", ex); /* skip unreadable */ }
                    catch (UnauthorizedAccessException ex) { SwallowedCatch.Record("NavDaemon.PluginCatalog.readFileAuth", ex); /* skip inaccessible */ }
                }
                if (totalTools == 0 && totalMegas == 0)
                    DaemonLog.Info($"plugin catalog: no [Tool]/[MegaTool] found in {pluginDir} (expected for non-tool plugins)");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { DaemonLog.Warn($"plugin catalog: managed scan failed for {pluginDir}: {ex.Message}"); }

            foreach (var mn in moduleNames)
            {
                string resDir = Path.Combine(pluginDir, "Resources", mn, "Contracts");
                if (Directory.Exists(resDir))
                {
                    try
                    {
                        foreach (var cf in Directory.GetFiles(resDir, "manage_*.contract.json", SearchOption.TopDirectoryOnly))
                        {
                            string ctText;
                            try { ctText = File.ReadAllText(cf); }
                            catch (IOException ex) { DaemonLog.Warn($"plugin catalog: skipped contract {cf}: {ex.Message}"); continue; }
                            var ct = TryParseJObject(ctText);
                            if (ct == null) { DaemonLog.Warn($"plugin catalog: malformed {cf}"); continue; }
                            var actions = ct["requiredArgsByAction"] as JObject;
                            if (actions != null)
                            {
                                var seen = new HashSet<string>(StringComparer.Ordinal);
                                foreach (var prop in actions.Properties())
                                {
                                    if (prop.Value is JArray arr)
                                        foreach (var a in arr) { string an = a.ToString(); if (!string.IsNullOrEmpty(an) && seen.Add(an)) modSubActions[mn]++; }
                                }
                            }
                        }
                    }
                    catch (IOException ex) { DaemonLog.Warn($"plugin catalog: io error reading contract dir {resDir}: {ex.Message}"); }
                    catch (UnauthorizedAccessException ex) { DaemonLog.Warn($"plugin catalog: inaccessible contract dir {resDir}: {ex.Message}"); }
                }
            }

            int categoryContracts = 0;
            try { foreach (var f in Directory.GetFiles(pluginDir, "manage_*.contract.json", SearchOption.AllDirectories)) categoryContracts++; } catch (DirectoryNotFoundException ex) { SwallowedCatch.Record("NavDaemon.PluginCatalog.contracts", ex); } catch (IOException ex) { DaemonLog.Warn($"plugin catalog: io error scanning {pluginDir} for contracts: {ex.Message}"); }

            var modules = new JArray();
            int tCs = 0, tTs = 0, tTools = 0, tMegas = 0, tSubs = 0;
            foreach (var mn in moduleNames.OrderBy(m => m))
            {
                modules.Add(new JObject { ["name"] = mn, ["csCount"] = modCsCount.GetValueOrDefault(mn), ["testCount"] = modTestCount.GetValueOrDefault(mn), ["atomicCount"] = modToolCount.GetValueOrDefault(mn), ["megaCount"] = modMegaCount.GetValueOrDefault(mn), ["subActions"] = modSubActions.GetValueOrDefault(mn), ["keyPaths"] = new JArray(modKeyPaths.GetValueOrDefault(mn) ?? new List<string>()) });
                tCs += modCsCount.GetValueOrDefault(mn); tTs += modTestCount.GetValueOrDefault(mn); tTools += modToolCount.GetValueOrDefault(mn); tMegas += modMegaCount.GetValueOrDefault(mn); tSubs += modSubActions.GetValueOrDefault(mn);
            }

            if (summaryMode)
            {
                categories.Add(new JObject { ["name"] = categoryName, ["capability"] = cap, ["version"] = version, ["purpose"] = desc, ["modules"] = moduleNames.Count, ["csCount"] = tCs, ["testCount"] = tTs, ["atomicCount"] = tTools, ["megaCount"] = tMegas, ["subActions"] = tSubs, ["contracts"] = categoryContracts });
            }
            else
            {
                var ba = manifest["bridges"] as JArray ?? new JArray();
                var da = manifest["dependencies"] as JArray ?? new JArray();
                var aa = new JArray(assemblies.Select(a => a.ToString()));
                string owner = (string?)manifest["owner"] ?? "";
                categories.Add(new JObject { ["name"] = categoryName, ["purpose"] = desc, ["capability"] = cap, ["version"] = version, ["modules"] = modules, ["assemblies"] = aa, ["bridges"] = ba, ["dependencies"] = da, ["owner"] = owner, ["contracts"] = categoryContracts });
            }
        }

        return new JObject { ["ok"] = true, ["categories"] = categories, ["totalCategories"] = categories.Count };
    }
}

