// DocsIndex — lazy in-memory index of repo markdown files.
//
// Why this exists:
//   AI sessions in this repo burn 30-80 KB of context every time they
//   reach for `Get-ChildItem -Recurse -Filter *.md | Select-String "...".`
//   That's wrong twice: PowerShell-shell-spawn cost AND the dumped-text
//   token cost. Operator banned PowerShell explicitly.
//
//   This indexer scans the repo ONCE on first call (<5s for 1,217 md
//   files / 12 MB), then answers queries from RAM in 50-200ms.
//
// What it knows:
//   - Every .md file's absolute path + relative path + size.
//   - Every header line in every file (## text → file:line).
//   - Every file's lowercased body cached (for grep without re-reading
//     from disk).
//
// What it does NOT do:
//   - No semantic embeddings. Substring + header match only.
//   - No PDF / DOCX / HTML.
//   - No front-matter parsing.
// Freshness: FileSystemWatcher (debounced 800ms) keeps the index live;
// if the watcher buffer overflows or cannot be created, EnsureBuilt
// re-indexes on next docs/* demand once 60s staleness has elapsed.
//
// Scope: skips `bin/`, `obj/`, `external/`, `node_modules/`, `.git/`,
// any directory starting with `.opencode/index` or `.opencode/cache`.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FlaxMcp.NavDaemon;

internal sealed record DocHeader(
    string FilePath,
    string RelPath,
    int Line,
    int Depth,        // # = 1, ## = 2, ### = 3, etc.
    string Text       // header text WITHOUT leading hashes
);

internal sealed record DocFile(
    string FilePath,
    string RelPath,
    int LineCount,
    long SizeBytes,
    string LowerBody  // cached lowercased content for grep
);

internal static class DocsIndex
{
    private static readonly object _lock = new();
    private static volatile bool _built;
    private static List<DocHeader> _headers = new();
    private static List<DocFile> _files = new();
    private static string? _repoRoot;
    private static long _buildMs;
    private static DateTime? _builtAtUtc;

    private static readonly string[] SkipDirs = new[]
    {
        "bin", "obj", "external", "node_modules", ".git",
    };

    // ── Docs file watcher (2026-08-27 WAVE I1) ──
    private static FileSystemWatcher? _watcher;
    private static string? _watcherRoot;
    private static volatile bool _watcherActive;
    private static System.Threading.Timer? _debounceTimer;
    private static readonly object _debounceLock = new();
    private const int DocsDebounceMs = 800;
    private const int DocsStaleFallbackSeconds = 60;

    public static bool WatcherActive => _watcherActive;
    public static bool RequiresRestart => !_watcherActive && _built;

    /// <summary>
    /// Ensure the index is built. Idempotent; lock-protected; rebuilds
    /// only if not yet built OR stale when the watcher is down.
    /// </summary>
    public static void EnsureBuilt(string repoRoot)
    {
        if (_built && _repoRoot == repoRoot)
        {
            if (_watcherActive) return;
            if (_builtAtUtc.HasValue)
            {
                double age = (DateTime.UtcNow - _builtAtUtc.Value).TotalSeconds;
                if (age < DocsStaleFallbackSeconds) return;
            }
            else return;
        }
        lock (_lock)
        {
            if (_built && _repoRoot == repoRoot)
            {
                if (_watcherActive) return;
                if (_builtAtUtc.HasValue && (DateTime.UtcNow - _builtAtUtc.Value).TotalSeconds < DocsStaleFallbackSeconds)
                    return;
                if (!_builtAtUtc.HasValue) return;
            }
            Build(repoRoot);
            _built = true;
        }
    }

    private static void Build(string repoRoot)
    {
        var sw = Stopwatch.StartNew();
        var headers = new List<DocHeader>();
        var files = new List<DocFile>();

        if (!Directory.Exists(repoRoot))
            throw new DirectoryNotFoundException($"repo root not found: {repoRoot}");

        foreach (var path in EnumerateMarkdownFiles(repoRoot))
        {
            string relPath = Path.GetRelativePath(repoRoot, path);
            string raw;
            try { raw = File.ReadAllText(path); }
            catch (IOException ex) { DaemonLog.Warn($"docs index: skipped {relPath}: {ex.Message}"); continue; }
            catch (UnauthorizedAccessException ex) { DaemonLog.Warn($"docs index: skipped {relPath}: {ex.Message}"); continue; }

            var lines = raw.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                // Trim trailing \r without allocating a new array.
                if (line.Length > 0 && line[^1] == '\r') line = line[..^1];

                // Headers: leading hashes + space + text. ATX style.
                int depth = 0;
                while (depth < line.Length && line[depth] == '#') depth++;
                if (depth == 0 || depth > 6) continue;
                if (depth >= line.Length || line[depth] != ' ') continue;

                string headerText = line[(depth + 1)..].Trim();
                if (headerText.Length == 0) continue;

                headers.Add(new DocHeader(
                    FilePath: path,
                    RelPath: relPath,
                    Line: i + 1,
                    Depth: depth,
                    Text: headerText));
            }

            files.Add(new DocFile(
                FilePath: path,
                RelPath: relPath,
                LineCount: lines.Length,
                SizeBytes: new FileInfo(path).Length,
                LowerBody: raw.ToLowerInvariant()));
        }

        sw.Stop();
        _repoRoot = repoRoot;
        _builtAtUtc = DateTime.UtcNow;
        _headers = headers;
        _files = files;
        _buildMs = sw.ElapsedMilliseconds;
        EnsureWatcher(repoRoot);
    }

    private static void EnsureWatcher(string repoRoot)
    {
        if (string.Equals(_watcherRoot, repoRoot, StringComparison.OrdinalIgnoreCase) && _watcher != null) return;
        try { _watcher?.Dispose(); } catch (Exception ex) { DaemonLog.Warn($"docs watcher dispose: {ex.Message}"); }
        _watcher = null; _watcherActive = false; _watcherRoot = repoRoot;
        try
        {
            var watcher = new FileSystemWatcher(repoRoot, "*.md") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName, InternalBufferSize = 65536 };
            watcher.Changed += OnDocsFsEvent; watcher.Created += OnDocsFsEvent; watcher.Deleted += OnDocsFsEvent; watcher.Renamed += OnDocsFsEvent; watcher.Error += OnDocsWatcherError; watcher.EnableRaisingEvents = true;
            _watcher = watcher; _watcherActive = true; DaemonLog.Info($"docs watcher active for {repoRoot} (debounce {DocsDebounceMs}ms)");
        }
        catch (Exception ex) { DaemonLog.Warn($"docs watcher unavailable for {repoRoot}: {ex.Message}"); _watcherActive = false; }
    }

    private static void OnDocsFsEvent(object? sender, FileSystemEventArgs e)
    {
        try { if (!IsRelevantDocsPath(e.FullPath)) return; if (e is RenamedEventArgs re && IsRelevantDocsPath(re.OldFullPath)) { } else if (!IsRelevantDocsPath(e.FullPath)) return; ScheduleDebouncedRebuild(); } catch (Exception ex) { DaemonLog.Warn($"docs watcher event: {ex.Message}"); }
    }

    private static void OnDocsWatcherError(object? sender, ErrorEventArgs e) { _watcherActive = false; DaemonLog.Warn($"docs watcher error: {e.GetException().Message} \u2014 fallback to on-demand"); }

    private static void ScheduleDebouncedRebuild()
    {
        lock (_debounceLock)
        {
            try { _debounceTimer?.Dispose(); } catch (Exception ex) { DaemonLog.Warn($"docs debounce dispose: {ex.Message}"); }
            _debounceTimer = new System.Threading.Timer(_ => { try { string? root = _repoRoot; if (root == null) return; lock (_lock) { Build(root); _built = true; } Program.InvalidateDocsCache(); DaemonLog.Info($"docs index: debounced rebuild ({FileCount} files, {HeaderCount} headers, {BuildMillis}ms)"); } catch (Exception ex) { DaemonLog.Warn($"docs debounced rebuild failed: {ex.Message}"); } }, null, DocsDebounceMs, System.Threading.Timeout.Infinite);
        }
    }

    private static bool IsRelevantDocsPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false; if (!fullPath.ToLowerInvariant().EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
        string repoRoot = _repoRoot ?? ""; string rel; try { rel = string.IsNullOrEmpty(repoRoot) ? fullPath : Path.GetRelativePath(repoRoot, fullPath); } catch { rel = fullPath; } rel = rel.Replace('\\', '/'); string[] segs = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in segs) { if (Array.IndexOf(SkipDirs, seg) >= 0) return false; if (string.Equals(seg, ".cache", StringComparison.OrdinalIgnoreCase)) return false; }
        if (rel.StartsWith(".opencode/cache", StringComparison.OrdinalIgnoreCase)) return false; if (rel.StartsWith(".opencode/index", StringComparison.OrdinalIgnoreCase)) return false; return true;
    }

    private static IEnumerable<string> EnumerateMarkdownFiles(string repoRoot)
    {
        var stack = new Stack<string>();
        stack.Push(repoRoot);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch (Exception dirEx) { DaemonLog.Warn($"DocsIndex.EnumerateMarkdownFiles subdirs ({dir}): {dirEx.Message}"); continue; }

            foreach (var sub in subDirs)
            {
                string name = Path.GetFileName(sub);
                if (string.IsNullOrEmpty(name)) continue;
                if (Array.IndexOf(SkipDirs, name) >= 0) continue;
                string relSub = Path.GetRelativePath(repoRoot, sub).Replace('\\', '/');
                if (relSub.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(s => string.Equals(s, ".cache", StringComparison.OrdinalIgnoreCase))) continue;
                if (relSub.Equals(".opencode/cache", StringComparison.OrdinalIgnoreCase) || relSub.StartsWith(".opencode/cache/", StringComparison.OrdinalIgnoreCase)) continue;
                if (relSub.Equals(".opencode/index", StringComparison.OrdinalIgnoreCase) || relSub.StartsWith(".opencode/index/", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(sub);
            }

            string[] files;
            try { files = Directory.GetFiles(dir, "*.md"); }
            catch (Exception fileEx) { DaemonLog.Warn($"DocsIndex.EnumerateMarkdownFiles files ({dir}): {fileEx.Message}"); continue; }
            foreach (var f in files) yield return f;
        }
    }

    public static IReadOnlyList<DocHeader> Headers => _headers;
    public static IReadOnlyList<DocFile> Files => _files;
    public static int FileCount => _files.Count;
    public static long BuildMillis => _buildMs;
    public static DateTime? BuiltAtUtc => _builtAtUtc;
    public static int HeaderCount => _headers.Count;
    public static string? RepoRoot => _repoRoot;
}
