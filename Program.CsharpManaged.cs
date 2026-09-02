using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace FlaxMcp.NavDaemon;

// Managed C# source search backend. All source navigation stays inside the
// daemon process, so PATH tools cannot make csharp/* unavailable.
//
// Architecture: immutable CsSourceIndex built once per root, atomically swapped
// via Volatile.Write. Queries read the snapshot lock-free — no global gate
// serializes concurrent queries after warm. Background rebuild triggers on
// file watcher invalidation with a bounded TTL fallback.
internal static partial class Program
{
    private static readonly HashSet<string> SourceSkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "node_modules",
        "obj",
    };
    private static readonly string[] SourceDeclarationKeywords = { "class ", "struct ", "interface ", "enum ", "record " };

    private readonly record struct CsHit(string File, int Line, string Match, JArray? Context = null);
    private readonly record struct CsLocation(string File, int Line);

    /// <summary>
    /// One indexed source file. <see cref="Tokens"/> maps each identifier token
    /// in the file to the ascending, distinct line numbers (1-based) it occurs
    /// on. Holding the postings per file — rather than in one global list — is
    /// what lets a single changed file be evicted and re-added without touching
    /// the other 3,754.
    /// </summary>
    private sealed record CsSourceFile(
        string Path,
        string[] Lines,
        IReadOnlyDictionary<string, int[]> Tokens,
        long Length,
        DateTime LastWriteUtc);

    /// <summary>Directory-scan metadata for one file — enough to tell whether
    /// the indexed copy is still current without reading it.</summary>
    private readonly record struct CsFileStamp(string Path, long Length, DateTime LastWriteUtc);

    /// <summary>Immutable source index — read lock-free, atomically swapped on update.</summary>
    private sealed class CsSourceIndex
    {
        public readonly string Root;
        public readonly IReadOnlyList<CsSourceFile> Files;
        public readonly IReadOnlyDictionary<string, CsSourceFile> FilesByPath;

        // token -> the files containing it, ascending by path. Arrays are
        // immutable and shared between index generations; every mutation in
        // With() returns a fresh array, so untouched tokens cost nothing.
        //
        // Sorted at rest rather than per query: hot symbols like Program or
        // CancellationToken live in thousands of files, and sorting those paths
        // on every lookup cost ~465ms/call. Build order already visits files in
        // sorted order, so the arrays come out sorted for free.
        private readonly Dictionary<string, string[]> _tokenFiles;

        public int TokenCount => _tokenFiles.Count;

        // Lazily-built, generation-scoped arrays for substring token scans.
        // symbol_search and grep_symbol_context test every token for a fragment;
        // doing that with OrdinalIgnoreCase costs ~50-100ms per call across 91k
        // tokens. Pre-lowercasing once lets the scan use ordinal comparison,
        // which is vectorized. Built on first use so an incremental update does
        // not pay for it unless a fragment query actually follows.
        private string[]? _tokenNames;
        private string[]? _tokenNamesLower;

        private void EnsureTokenScanArrays()
        {
            if (Volatile.Read(ref _tokenNamesLower) != null) return;

            var names = new string[_tokenFiles.Count];
            var lower = new string[_tokenFiles.Count];
            int at = 0;
            foreach (string token in _tokenFiles.Keys)
            {
                names[at] = token;
                lower[at] = token.ToLowerInvariant();
                at++;
            }
            // Publish names first: a reader that sees the lowered array must
            // also see the aligned original-casing array.
            Volatile.Write(ref _tokenNames, names);
            Volatile.Write(ref _tokenNamesLower, lower);
        }

        /// <summary>Every indexed token containing <paramref name="fragment"/>.</summary>
        public IEnumerable<string> TokensContaining(string fragment)
        {
            EnsureTokenScanArrays();
            string[] names = _tokenNames!;
            string[] lower = Volatile.Read(ref _tokenNamesLower)!;
            string needle = fragment.ToLowerInvariant();

            for (int i = 0; i < lower.Length; i++)
            {
                if (lower[i].Contains(needle, StringComparison.Ordinal))
                    yield return names[i];
            }
        }

        public CsSourceIndex(string root, IReadOnlyList<CsSourceFile> files)
            : this(root, files, BuildLookups(files, out var tokenFiles), tokenFiles)
        {
        }

        private CsSourceIndex(
            string root,
            IReadOnlyList<CsSourceFile> files,
            Dictionary<string, CsSourceFile> filesByPath,
            Dictionary<string, string[]> tokenFiles)
        {
            Root = root;
            Files = files;
            FilesByPath = filesByPath;
            _tokenFiles = tokenFiles;
        }

        private static Dictionary<string, CsSourceFile> BuildLookups(
            IReadOnlyList<CsSourceFile> files,
            out Dictionary<string, string[]> tokenFiles)
        {
            var filesByPath = new Dictionary<string, CsSourceFile>(files.Count, StringComparer.OrdinalIgnoreCase);
            // Pre-sized: this repo interns ~91k distinct tokens, and growing from
            // empty rehashes the whole table a dozen-plus times during the build.
            var owners = new Dictionary<string, List<string>>(128 * 1024, StringComparer.OrdinalIgnoreCase);
            foreach (CsSourceFile file in files)
            {
                filesByPath[file.Path] = file;
                foreach (string token in file.Tokens.Keys)
                {
                    if (!owners.TryGetValue(token, out var paths))
                        owners[token] = paths = new List<string>();
                    paths.Add(file.Path);
                }
            }

            // `files` arrives sorted by path, so each list is already ascending.
            tokenFiles = new Dictionary<string, string[]>(owners.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in owners)
                tokenFiles[pair.Key] = pair.Value.ToArray();
            return filesByPath;
        }

        /// <summary>Locations of an exact token, ordered by file then line.</summary>
        public IEnumerable<CsLocation> LocationsOf(string token)
        {
            if (!_tokenFiles.TryGetValue(token, out string[]? owners))
                yield break;

            foreach (string path in owners)
            {
                if (!FilesByPath.TryGetValue(path, out CsSourceFile? file)) continue;
                if (!file.Tokens.TryGetValue(token, out int[]? lines)) continue;
                foreach (int line in lines)
                    yield return new CsLocation(path, line);
            }
        }

        private static string[] WithoutOwner(string[] owners, string path)
        {
            int at = Array.BinarySearch(owners, path, StringComparer.OrdinalIgnoreCase);
            if (at < 0) return owners;
            var next = new string[owners.Length - 1];
            Array.Copy(owners, 0, next, 0, at);
            Array.Copy(owners, at + 1, next, at, owners.Length - at - 1);
            return next;
        }

        private static string[] WithOwner(string[] owners, string path)
        {
            int at = Array.BinarySearch(owners, path, StringComparer.OrdinalIgnoreCase);
            if (at >= 0) return owners;
            at = ~at;
            var next = new string[owners.Length + 1];
            Array.Copy(owners, 0, next, 0, at);
            next[at] = path;
            Array.Copy(owners, at, next, at + 1, owners.Length - at);
            return next;
        }

        /// <summary>
        /// Derive a new index with <paramref name="upserts"/> replaced/added and
        /// <paramref name="removals"/> dropped, sharing every untouched file and
        /// token set with this one. Cost scales with the size of the change, not
        /// the size of the repository.
        /// </summary>
        public CsSourceIndex With(IReadOnlyList<CsSourceFile> upserts, IReadOnlyList<string> removals)
        {
            var filesByPath = new Dictionary<string, CsSourceFile>(FilesByPath, StringComparer.OrdinalIgnoreCase);
            var tokenFiles = new Dictionary<string, string[]>(_tokenFiles, StringComparer.OrdinalIgnoreCase);

            void Retract(string path)
            {
                if (!filesByPath.TryGetValue(path, out CsSourceFile? previous)) return;
                // Remove the *indexed* spelling, not the caller's. A watcher event
                // can report a different casing than the enumeration stored, and
                // the owner arrays hold the enumeration's.
                foreach (string token in previous.Tokens.Keys)
                {
                    if (!tokenFiles.TryGetValue(token, out string[]? owners)) continue;
                    string[] next = WithoutOwner(owners, previous.Path);
                    if (next.Length == 0) tokenFiles.Remove(token);
                    else tokenFiles[token] = next;
                }
                filesByPath.Remove(previous.Path);
            }

            foreach (string path in removals) Retract(path);
            foreach (CsSourceFile file in upserts) Retract(file.Path);

            foreach (CsSourceFile file in upserts)
            {
                filesByPath[file.Path] = file;
                foreach (string token in file.Tokens.Keys)
                {
                    tokenFiles[token] = tokenFiles.TryGetValue(token, out string[]? owners)
                        ? WithOwner(owners, file.Path)
                        : new[] { file.Path };
                }
            }

            var files = new List<CsSourceFile>(filesByPath.Values);
            files.Sort(static (left, right) =>
                string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
            return new CsSourceIndex(Root, files, filesByPath, tokenFiles);
        }
    }

    // Lock-free snapshot: queries read via Volatile.Read, rebuilds swap via Volatile.Write.
    // No global gate serializes concurrent queries after the index is built.
    private static CsSourceIndex? _sourceIndex;
    private static long _sourceIndexBuiltAtUtcMs; // Environment.TickCount64 when index was built
    private static readonly object _sourceIndexBuildLock = new(); // serialize builds, not reads
    private static FileSystemWatcher? _sourceWatcher;
    private static string? _sourceWatcherRoot;
    private static volatile bool _sourceWatcherActive;

    // Files the watcher reported as changed since the last snapshot update. Used
    // as a set; the value is ignored.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _dirtyPaths =
        new(StringComparer.OrdinalIgnoreCase);

    // Set when the watcher drops events (buffer overflow) — the dirty set can no
    // longer be trusted to describe every change, so the next query rebuilds.
    private static volatile bool _sourceIndexNeedsFullRebuild;

    // 2026-08-03: this TTL used to apply unconditionally, so any query landing
    // more than 5s after the previous one rebuilt the whole index — 3,754 files,
    // 871k lines, 91k tokens, ~20s measured on this repo. Agents call nav
    // sporadically, so in practice almost every call paid a full rebuild, on
    // every csharp/* atomic. Measured back-to-back: 20.9s, 1.8s, 1.3s, then
    // 21.5s again after a 7s idle gap.
    //
    // The TTL was only ever meant as the fallback for when the watcher cannot
    // be created (see EnsureSourceWatcher) — when the watcher IS live it
    // already invalidates on real .cs changes, so re-deriving the index on a
    // timer is pure waste. Trust the watcher when it is active, and keep a long
    // outer bound purely as a self-healing net in case it silently stops.
    //
    // With incremental updates the watched bound is a pure belt-and-braces
    // rebuild, and it is not cheap (~19.6s, holding the build lock), so keep it
    // rare rather than merely long.
    private const int SourceIndexTtlMs = 5000;             // no watcher: bounded staleness
    private const int SourceIndexWatchedTtlMs = 1_800_000; // watcher live: safety net only

    private static int SourceIndexTtl => _sourceWatcherActive ? SourceIndexWatchedTtlMs : SourceIndexTtlMs;

    /// <summary>
    /// Every indexable .cs file under <paramref name="root"/>.
    ///
    /// 2026-08-03: this was the single largest rebuild phase — 10.6s of ~19.6s.
    /// Two causes: GetFiles/GetDirectories materialize arrays per directory, and
    /// `new DirectoryInfo(child).Attributes` re-stat'd every directory that the
    /// scan had already returned attributes for. Enumerate* streams and its
    /// DirectoryInfo carries those attributes, so the second stat is gone.
    ///
    /// The rest is I/O latency against a cold metadata cache, which overlaps
    /// well, so the walk runs level-synchronous BFS in parallel. Levels keep
    /// termination trivial (no outstanding-work bookkeeping) and this tree is
    /// wide where it is deep, which is where the parallelism pays.
    /// </summary>
    private static List<CsFileStamp> EnumerateCsFiles(string root)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"C# source root does not exist: {root}");

        var found = new System.Collections.Concurrent.ConcurrentBag<CsFileStamp>();
        var level = new List<DirectoryInfo> { new(root) };

        while (level.Count > 0)
        {
            var next = new System.Collections.Concurrent.ConcurrentBag<DirectoryInfo>();
            Parallel.ForEach(level, directory =>
            {
                try
                {
                    // Length/LastWriteTimeUtc come from the directory scan that
                    // already happened — reading them here costs no extra syscall.
                    foreach (FileInfo file in directory.EnumerateFiles("*.cs", SearchOption.TopDirectoryOnly))
                        found.Add(new CsFileStamp(file.FullName, file.Length, file.LastWriteTimeUtc));
                }
                catch (IOException ex)
                {
                    DaemonLog.Warn($"managed C# search skipped {directory.FullName}: {ex.Message}");
                    return;
                }
                catch (UnauthorizedAccessException ex)
                {
                    DaemonLog.Warn($"managed C# search skipped {directory.FullName}: {ex.Message}");
                    return;
                }

                try
                {
                    foreach (DirectoryInfo child in directory.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
                    {
                        if (SourceSkipDirectories.Contains(child.Name))
                            continue;
                        if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;
                        next.Add(child);
                    }
                }
                catch (IOException ex)
                {
                    DaemonLog.Warn($"managed C# search skipped child directories of {directory.FullName}: {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    DaemonLog.Warn($"managed C# search skipped child directories of {directory.FullName}: {ex.Message}");
                }
            });

            level = new List<DirectoryInfo>(next);
        }

        return new List<CsFileStamp>(found);
    }

    /// <summary>Read and tokenize one file into an indexable snapshot.</summary>
    private static CsSourceFile LoadSourceFile(string path)
    {
        var info = new FileInfo(path);
        return LoadSourceFile(new CsFileStamp(
            path,
            info.Exists ? info.Length : 0,
            info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue));
    }

    private static CsSourceFile LoadSourceFile(CsFileStamp stamp)
    {
        string path = stamp.Path;
        string[] lines = ReadSourceLines(path);
        var tokens = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex];
            int cursor = 0;
            while (cursor < line.Length)
            {
                while (cursor < line.Length && !IsIdentifierStart(line[cursor]))
                    cursor++;
                int start = cursor;
                while (cursor < line.Length && IsIdentifierPart(line[cursor]))
                    cursor++;
                if (cursor <= start)
                    continue;

                string token = line[start..cursor];
                if (!tokens.TryGetValue(token, out var occurrences))
                    tokens[token] = occurrences = new List<int>();
                // A token repeated on one line is one location, not several. The
                // old flat index appended per occurrence, so find_references
                // reported such lines twice and inflated `count` (verified live:
                // `_repoRootCached` returned count=8 for 6 distinct lines).
                if (occurrences.Count == 0 || occurrences[^1] != lineIndex + 1)
                    occurrences.Add(lineIndex + 1);
            }
        }

        var frozen = new Dictionary<string, int[]>(tokens.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in tokens)
            frozen[pair.Key] = pair.Value.ToArray();
        return new CsSourceFile(path, lines, frozen, stamp.Length, stamp.LastWriteUtc);
    }

    private static string[] ReadSourceLines(string file)
    {
        try
        {
            return File.ReadAllLines(file);
        }
        catch (IOException ex)
        {
            DaemonLog.Warn($"managed C# search skipped {file}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            DaemonLog.Warn($"managed C# search skipped {file}: {ex.Message}");
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Get the source index for the given root. Fast path: return existing
    /// immutable index if within TTL (zero I/O). Slow path: rebuild index
    /// under lock (only one thread builds, others wait) then atomically swap.
    /// TTL prevents O(n) file enumeration on every concurrent request while
    /// still detecting changes within 5 seconds.
    /// </summary>
    private static CsSourceIndex GetSourceIndex(string root)
    {
        var current = Volatile.Read(ref _sourceIndex);
        long now = Environment.TickCount64;
        long builtAt = Interlocked.Read(ref _sourceIndexBuiltAtUtcMs);
        if (IsUsable(current, root, now) && _dirtyPaths.IsEmpty && !_sourceIndexNeedsFullRebuild)
            return current!;

        // Slow path: update under lock (only one thread does), then atomically
        // swap. Other threads wait here rather than duplicating the work.
        lock (_sourceIndexBuildLock)
        {
            current = Volatile.Read(ref _sourceIndex);
            if (IsUsable(current, root, Environment.TickCount64)
                && _dirtyPaths.IsEmpty
                && !_sourceIndexNeedsFullRebuild)
            {
                return current!;
            }

            EnsureSourceWatcher(root);

            CsSourceIndex next;
            bool haveSnapshot = current != null
                && string.Equals(current.Root, root, StringComparison.OrdinalIgnoreCase);

            if (IsUsable(current, root, Environment.TickCount64) && !_sourceIndexNeedsFullRebuild)
            {
                // Known, bounded set of changed files — patch the snapshot.
                next = ApplyDirtyPaths(current!, root);
            }
            else if (haveSnapshot)
            {
                // Either the watcher dropped events (we hold a snapshot but no
                // longer know what changed) or the safety-net interval expired.
                // Both used to mean a blind full rebuild. The existing snapshot
                // is still mostly correct, so diff it against the directory scan
                // and reload only what actually differs.
                _sourceIndexNeedsFullRebuild = false;
                _dirtyPaths.Clear();
                next = ResyncSourceIndex(current!, root);
            }
            else
            {
                // Nothing usable to diff against: first build, or a new root.
                _sourceIndexNeedsFullRebuild = false;
                _dirtyPaths.Clear();
                next = BuildSourceIndex(root);
            }

            Volatile.Write(ref _sourceIndex, next);
            Interlocked.Exchange(ref _sourceIndexBuiltAtUtcMs, Environment.TickCount64);
            return next;
        }

        static bool IsUsable(CsSourceIndex? index, string root, long now) =>
            index != null
            && string.Equals(index.Root, root, StringComparison.OrdinalIgnoreCase)
            && (now - Interlocked.Read(ref _sourceIndexBuiltAtUtcMs)) < SourceIndexTtl;
    }

    /// <summary>
    /// Reconcile a snapshot we can no longer prove current against what is
    /// actually on disk, using the directory scan's own size/mtime metadata.
    ///
    /// 2026-08-03: reached when the watcher overflows its buffer ("Too many
    /// changes at once" — one historical daemon log had 495 of them, and a
    /// solution build triggers them readily) or when the safety-net interval
    /// lapses. Previously either meant discarding a correct 3,755-file index and
    /// rebuilding from nothing. The walk is the cheap part (~200ms warm); only
    /// files whose length or write time actually moved are re-read.
    /// </summary>
    private static CsSourceIndex ResyncSourceIndex(CsSourceIndex current, string root)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        List<CsFileStamp> onDisk = EnumerateCsFiles(root);

        var upserts = new List<CsSourceFile>();
        var present = new HashSet<string>(onDisk.Count, StringComparer.OrdinalIgnoreCase);
        foreach (CsFileStamp stamp in onDisk)
        {
            present.Add(stamp.Path);
            if (current.FilesByPath.TryGetValue(stamp.Path, out CsSourceFile? indexed)
                && indexed.Length == stamp.Length
                && indexed.LastWriteUtc == stamp.LastWriteUtc)
            {
                continue;
            }
            upserts.Add(LoadSourceFile(stamp));
        }

        var removals = new List<string>();
        foreach (CsSourceFile indexed in current.Files)
        {
            if (!present.Contains(indexed.Path))
                removals.Add(indexed.Path);
        }

        timer.Stop();
        if (upserts.Count == 0 && removals.Count == 0)
        {
            DaemonLog.Info($"managed C# index: resync found no changes ({timer.ElapsedMilliseconds}ms)");
            return current;
        }

        DaemonLog.Info(
            $"managed C# index: resync reloaded {upserts.Count} and dropped {removals.Count} "
            + $"of {onDisk.Count} files in {timer.ElapsedMilliseconds}ms");
        return current.With(upserts, removals);
    }

    /// <summary>
    /// Fold the watcher's pending paths into the current snapshot. Each changed
    /// file costs one read + re-tokenize (~0.5ms measured) plus set bookkeeping,
    /// against ~19.6s to rebuild all 3,754 files.
    /// </summary>
    private static CsSourceIndex ApplyDirtyPaths(CsSourceIndex current, string root)
    {
        var pending = new List<string>(_dirtyPaths.Count);
        foreach (string path in _dirtyPaths.Keys)
        {
            if (_dirtyPaths.TryRemove(path, out _))
                pending.Add(path);
        }

        var upserts = new List<CsSourceFile>();
        var removals = new List<string>();
        foreach (string path in pending)
        {
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsIndexedSourcePath(path)) continue;
            if (!IsWithinRoot(root, path)) continue;

            if (File.Exists(path))
                upserts.Add(LoadSourceFile(path));
            else if (current.FilesByPath.ContainsKey(path))
                removals.Add(path);
        }

        if (upserts.Count == 0 && removals.Count == 0)
            return current;

        DaemonLog.Warn($"managed C# index: incremental update (+{upserts.Count}/-{removals.Count} files)");
        return current.With(upserts, removals);
    }

    private static void EnsureSourceWatcher(string root)
    {
        if (string.Equals(_sourceWatcherRoot, root, StringComparison.OrdinalIgnoreCase)
            && _sourceWatcher != null)
            return;

        _sourceWatcher?.Dispose();
        _sourceWatcher = null;
        _sourceWatcherActive = false;
        _sourceWatcherRoot = root;
        try
        {
            var watcher = new FileSystemWatcher(root, "*.cs")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                // 2026-07-28: default InternalBufferSize is 8KB. FileSystemWatcher's
                // "*.cs" Filter is applied AFTER Windows (ReadDirectoryChangesW)
                // queues raw change events for the ENTIRE watched subtree — there's
                // no way to exclude bin/obj/.git/node_modules from what the OS
                // buffers, only from what EnumerateCsFiles later reads. Any build in
                // this repo (which touches hundreds of files under bin/obj across
                // a dozen+ plugins) floods that 8KB buffer long before the .cs
                // filter gets a chance to discard the irrelevant events, throwing
                // InternalBufferOverflowException ("Too many changes at once") —
                // confirmed live: 495 occurrences in one daemon log, each one
                // nulling the entire C# index via SourceWatcherError below, forcing
                // a full 3,500+ file rebuild on the next query (observed
                // csharp/find_definition calls up to 16s instead of the documented
                // 200-500ms). 64KB is the practical max Windows honors and won't
                // eliminate overflows during a full solution build, but
                // meaningfully raises the burst size before one occurs.
                InternalBufferSize = 65536,
            };
            watcher.Changed += InvalidateSourceIndex;
            watcher.Created += InvalidateSourceIndex;
            watcher.Deleted += InvalidateSourceIndex;
            watcher.Renamed += InvalidateSourceIndex;
            watcher.Error += SourceWatcherError;
            watcher.EnableRaisingEvents = true;
            _sourceWatcher = watcher;
            // Live watcher: invalidation is now event-driven, so GetSourceIndex
            // can stop rebuilding on the short timer.
            _sourceWatcherActive = true;
        }
        catch (Exception ex)
        {
            // TTL validation remains as a safe fallback when the watcher cannot
            // be created, such as a network or restricted filesystem root.
            DaemonLog.Warn($"managed C# source watcher unavailable for {root}: {ex.Message}");
        }
    }

    /// <summary>
    /// True when a changed path is one the index actually contains — i.e. it
    /// survives the same directory skip-list <see cref="EnumerateCsFiles"/>
    /// applies. The watcher's "*.cs" filter is evaluated by Windows over the
    /// whole subtree, so bin/obj/.git/node_modules churn arrives here too.
    /// </summary>
    private static bool IsIndexedSourcePath(string fullPath)
    {
        foreach (string segment in fullPath.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (SourceSkipDirectories.Contains(segment))
                return false;
        }
        return true;
    }

    // 2026-08-03: this used to drop the whole index for ANY *.cs event under the
    // root — including the generated files a build writes into obj/ (AssemblyInfo,
    // GlobalUsings.g.cs, ...). EnumerateCsFiles skips those directories, so none
    // of it is in the index: every build invalidated a 3,754-file index over
    // files it did not contain, then paid a ~20s rebuild on the next query.
    //
    // Now it records which indexed files changed instead of discarding
    // everything, so the next query patches those files (~0.5ms each) rather
    // than re-reading and re-tokenizing the repository.
    private static void InvalidateSourceIndex(object? sender, FileSystemEventArgs args)
    {
        if (args is RenamedEventArgs renamed && IsIndexedSourcePath(renamed.OldFullPath))
            _dirtyPaths[renamed.OldFullPath] = 0;
        if (IsIndexedSourcePath(args.FullPath))
            _dirtyPaths[args.FullPath] = 0;
    }

    private static void SourceWatcherError(object? sender, ErrorEventArgs args)
    {
        // Buffer overflow means events were dropped — the dirty set is no longer
        // a complete description of what changed, so only a full rebuild is safe.
        _sourceIndexNeedsFullRebuild = true;
        Interlocked.Exchange(ref _sourceIndexBuiltAtUtcMs, 0);
        DaemonLog.Warn($"managed C# source watcher error: {args.GetException().Message}");
    }

    /// <summary>Build a new immutable source index — no locks, no shared state.</summary>
    private static CsSourceIndex BuildSourceIndex(string root)
    {
        var walked = System.Diagnostics.Stopwatch.StartNew();
        List<CsFileStamp> paths = EnumerateCsFiles(root);
        paths.Sort(static (left, right) =>
            string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
        walked.Stop();

        // Reading and tokenizing are per-file and independent, so overlap them.
        // Results land by index, preserving the sorted order callers rely on for
        // deterministic result ordering.
        var loaded = System.Diagnostics.Stopwatch.StartNew();
        var files = new CsSourceFile[paths.Count];
        Parallel.For(0, paths.Count, i => files[i] = LoadSourceFile(paths[i]));
        loaded.Stop();

        var inverted = System.Diagnostics.Stopwatch.StartNew();
        var index = new CsSourceIndex(root, files);
        inverted.Stop();

        // A full rebuild is the daemon's one expensive operation and it holds the
        // build lock; log its phases so a regression is diagnosable from the log
        // rather than by bisecting builds.
        DaemonLog.Info(
            $"managed C# index: full build of {paths.Count} files in "
            + $"{walked.ElapsedMilliseconds + loaded.ElapsedMilliseconds + inverted.ElapsedMilliseconds}ms "
            + $"(walk {walked.ElapsedMilliseconds}ms, load {loaded.ElapsedMilliseconds}ms, "
            + $"invert {inverted.ElapsedMilliseconds}ms)");
        return index;
    }

    // ─── Regex cache: avoid per-call Regex allocation ───

    private static readonly Dictionary<string, Regex> _regexCache = new(StringComparer.Ordinal);
    private static readonly object _regexCacheLock = new();

    private static Regex CreateSearchRegex(string pattern)
    {
        lock (_regexCacheLock)
        {
            if (_regexCache.TryGetValue(pattern, out var cached))
                return cached;
        }
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        lock (_regexCacheLock)
        {
            // Double-check after acquiring lock
            if (!_regexCache.TryAdd(pattern, regex))
                return _regexCache[pattern];
        }
        return regex;
    }

    private static bool IsIdentifierStart(char value) => char.IsLetter(value) || value == '_';

    private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static bool IsSimpleSymbol(string value)
    {
        if (value.Length == 0 || !IsIdentifierStart(value[0]))
            return false;
        for (int i = 1; i < value.Length; i++)
        {
            if (!IsIdentifierPart(value[i]))
                return false;
        }
        return true;
    }

    /// <summary>Pre-compiled regex for ExtractNamespace — avoid per-call allocation.</summary>
    private static readonly Regex NamespaceRegex = new(@"namespace\s+([\w.]+)", RegexOptions.Compiled);

    private static JArray BuildContext(string[] lines, int hitIndex, int context)
    {
        var result = new JArray();
        int first = Math.Max(0, hitIndex - context);
        int last = Math.Min(lines.Length - 1, hitIndex + context);
        for (int i = first; i <= last; i++)
        {
            result.Add(new JObject
            {
                ["line"] = i + 1,
                ["text"] = lines[i],
            });
        }
        return result;
    }

    /// <summary>
    /// Every (file, line) where some indexed identifier token contains
    /// <paramref name="fragment"/>, in file-then-line order with duplicates
    /// removed. Returns null when the index cannot answer the question (the
    /// fragment is not a valid symbol start, e.g. digit-initial) OR when the
    /// fragment is so common that the candidate union is enormous — in that
    /// case the caller must fall back to the corpus scan with a
    /// <c>requiredSubstring</c> prefilter, which outruns materializing and
    /// sorting hundreds of thousands of locations into a list.
    ///
    /// Soundness: the tokenizer emits every maximal identifier-character run
    /// that begins with a letter or underscore. So when <paramref name="fragment"/>
    /// itself begins with a letter or underscore (<see cref="IsSimpleSymbol"/>), any
    /// occurrence of it inside such a run necessarily lies inside an emitted
    /// token — the index sees it. Fragments that start with a digit are
    /// rejected precisely because they could hide in a digit-initial run (the
    /// "3F" of "3Foo") that the tokenizer never emits.
    /// </summary>
    private static List<CsLocation>? IndexedFragmentCandidates(CsSourceIndex index, string fragment)
    {
        if (!IsSimpleSymbol(fragment))
            return null;

        const int FragmentLocationCap = 50_000;
        var candidates = new List<CsLocation>();
        foreach (string token in index.TokensContaining(fragment))
        {
            if (candidates.Count >= FragmentLocationCap)
                return null;
            foreach (CsLocation location in index.LocationsOf(token))
            {
                if (candidates.Count >= FragmentLocationCap)
                    return null;
                candidates.Add(location);
            }
        }

        // The union loses the index's file-then-line traversal order, and callers
        // cap results by position — restore it so output stays deterministic and
        // identical to what a whole-corpus scan produced.
        candidates.Sort(static (left, right) =>
        {
            int byFile = string.Compare(left.File, right.File, StringComparison.OrdinalIgnoreCase);
            return byFile != 0 ? byFile : left.Line.CompareTo(right.Line);
        });

        // Several distinct tokens on one line can contain the fragment; dedupe
        // the now-adjacent repeats rather than paying for a hash set.
        int kept = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (kept > 0 && candidates[kept - 1] == candidates[i]) continue;
            candidates[kept++] = candidates[i];
        }
        candidates.RemoveRange(kept, candidates.Count - kept);
        return candidates;
    }

    private static List<CsHit> ManagedGrepFixed(string root, string needle, int context, int maxResults)
    {
        CsSourceIndex index = GetSourceIndex(root);
        var hits = new List<CsHit>();

        List<CsLocation>? candidates = IndexedFragmentCandidates(index, needle);
        if (candidates != null)
        {
            foreach (CsLocation location in candidates)
            {
                if (hits.Count >= maxResults)
                    break;
                if (!index.FilesByPath.TryGetValue(location.File, out CsSourceFile? source))
                    continue;
                string[] lines = source.Lines;
                hits.Add(new CsHit(
                    location.File,
                    location.Line,
                    Truncate(lines[location.Line - 1].Trim(), 120),
                    context > 0 ? BuildContext(lines, location.Line - 1, context) : null));
            }
            return hits;
        }

        foreach (CsSourceFile source in index.Files)
        {
            if (hits.Count >= maxResults)
                break;

            string[] lines = source.Lines;
            for (int i = 0; i < lines.Length && hits.Count < maxResults; i++)
            {
                if (!lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase))
                    continue;
                hits.Add(new CsHit(
                    source.Path,
                    i + 1,
                    Truncate(lines[i].Trim(), 120),
                    context > 0 ? BuildContext(lines, i, context) : null));
            }
        }
        return hits;
    }

    /// <param name="requiredSubstring">
    /// Optional literal that every alternative of <paramref name="pattern"/> is
    /// known to require. When supplied, a vectorized ordinal search rejects
    /// non-matching lines before the regex engine runs — the regex is only asked
    /// about lines that can possibly match. Pass null when the pattern has no
    /// such mandatory literal; a wrong value here silently drops real hits.
    /// </param>
    private static List<CsHit> ManagedGrepRegex(
        string root,
        string pattern,
        int context,
        int maxResults,
        Func<string, bool>? lineFilter = null,
        string? requiredSubstring = null)
    {
        var hits = new List<CsHit>();
        Regex regex = CreateSearchRegex(pattern);
        foreach (CsSourceFile source in GetSourceIndex(root).Files)
        {
            if (hits.Count >= maxResults)
                break;

            string[] lines = source.Lines;
            for (int i = 0; i < lines.Length && hits.Count < maxResults; i++)
            {
                if (requiredSubstring != null
                    && !lines[i].Contains(requiredSubstring, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!regex.IsMatch(lines[i]) || (lineFilter != null && !lineFilter(lines[i])))
                    continue;
                hits.Add(new CsHit(
                    source.Path,
                    i + 1,
                    Truncate(lines[i].Trim(), 120),
                    context > 0 ? BuildContext(lines, i, context) : null));
            }
        }
        return hits;
    }

    private static List<CsHit> ManagedGrepRegexIndexed(
        string root,
        string symbol,
        string pattern,
        int context,
        int maxResults,
        Func<string, bool>? lineFilter = null)
    {
        if (!IsSimpleSymbol(symbol))
            return ManagedGrepRegex(root, pattern, context, maxResults, lineFilter);

        CsSourceIndex index = GetSourceIndex(root);
        var hits = new List<CsHit>();

        Regex regex = CreateSearchRegex(pattern);
        foreach (CsLocation location in index.LocationsOf(symbol))
        {
            if (hits.Count >= maxResults)
                break;
            if (!index.FilesByPath.TryGetValue(location.File, out CsSourceFile? source))
                continue;
            string line = source.Lines[location.Line - 1];
            if (!regex.IsMatch(line) || (lineFilter != null && !lineFilter(line)))
                continue;
            hits.Add(new CsHit(
                location.File,
                location.Line,
                Truncate(line.Trim(), 120),
                context > 0 ? BuildContext(source.Lines, location.Line - 1, context) : null));
        }
        return hits;
    }

    /// <summary>
    /// Substring symbol search, accelerated by the token index.
    ///
    /// 2026-08-03: csharp/symbol_search matches <c>\w*query\w*</c> — a substring
    /// of an identifier — so it cannot use ManagedGrepRegexIndexed, which looks
    /// up whole tokens. It therefore fell through to a full-corpus
    /// ManagedGrepRegex: ~870k lines against a pattern whose three alternatives
    /// each begin with an unanchored <c>\w*</c>. With no literal prefix to
    /// anchor on, the engine retries at every position on every line and
    /// backtracks quadratically to *reject* the overwhelming majority that hold
    /// no match at all. Measured 60-255s per call against this repo, which is
    /// past most callers' timeouts. It was the only csharp/* atomic left doing
    /// an unindexed scan — every other one already goes through the index,
    /// which is why symbol_search alone was slow.
    ///
    /// All three alternatives require an identifier token containing `query` on
    /// the matched line, so the candidate lines are exactly the union of the
    /// locations of index tokens containing `query` — see
    /// <see cref="IndexedFragmentCandidates"/>. That hands the regex a few
    /// hundred lines instead of 870k, and yields the same hits in the same
    /// order. Returns null when the index cannot answer for this query, leaving
    /// the caller to scan.
    /// </summary>
    private static List<CsHit>? ManagedSubstringTokenSearch(
        string root,
        string query,
        string pattern,
        int maxResults,
        Func<string, bool>? lineFilter = null)
    {
        CsSourceIndex index = GetSourceIndex(root);
        List<CsLocation>? candidates = IndexedFragmentCandidates(index, query);
        if (candidates == null)
            return null;

        Regex regex = CreateSearchRegex(pattern);
        var hits = new List<CsHit>();
        foreach (CsLocation location in candidates)
        {
            if (hits.Count >= maxResults)
                break;
            if (!index.FilesByPath.TryGetValue(location.File, out CsSourceFile? source))
                continue;
            string line = source.Lines[location.Line - 1];
            if (!regex.IsMatch(line) || (lineFilter != null && !lineFilter(line)))
                continue;
            hits.Add(new CsHit(location.File, location.Line, Truncate(line.Trim(), 120)));
        }
        return hits;
    }

    // 2026-07-28: neither branch used to filter comment/string-literal lines,
    // so "find all references" for e.g. symbol Foo counted a `// Foo does X`
    // comment or a `"Foo failed"` log string as a real reference alongside
    // actual code usages, indistinguishably. That's the wrong default for the
    // primary use case (impact analysis before a rename/change) — it inflates
    // the count and mixes noise into results with no way to tell them apart.
    // IsCommentOrString already existed and was used elsewhere (declaration/
    // kind matching) but never wired into this path. Skip by default; comment
    // mentions were never actionable "references" for this atomic's purpose.
    private static List<CsHit> ManagedFindAllReferences(string root, string symbol, int maxResults)
    {
        if (IsSimpleSymbol(symbol))
        {
            CsSourceIndex index = GetSourceIndex(root);
            var indexed = new List<CsHit>();
            foreach (CsLocation location in index.LocationsOf(symbol))
            {
                if (indexed.Count >= maxResults)
                    break;
                if (!index.FilesByPath.TryGetValue(location.File, out CsSourceFile? source))
                    continue;
                string line = source.Lines[location.Line - 1];
                if (IsCommentOrString(line))
                    continue;
                indexed.Add(new CsHit(location.File, location.Line, Truncate(line.Trim(), 120)));
            }
            return indexed;
        }

        var hits = new List<CsHit>();
        foreach (CsSourceFile source in GetSourceIndex(root).Files)
        {
            if (hits.Count >= maxResults)
                break;

            string[] lines = source.Lines;
            for (int i = 0; i < lines.Length && hits.Count < maxResults; i++)
            {
                if (!lines[i].Contains(symbol, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsCommentOrString(lines[i]))
                    continue;
                hits.Add(new CsHit(source.Path, i + 1, Truncate(lines[i].Trim(), 120)));
            }
        }
        return hits;
    }

    private static bool LineContainsExactCodeToken(string line, string token)
    {
        if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(token)) return false;
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("[", StringComparison.Ordinal)) return false;
        if (trimmed.StartsWith("using ", StringComparison.Ordinal) && trimmed.TrimEnd().EndsWith(";", StringComparison.Ordinal)) return false;
        if (line.IndexOf(token, StringComparison.Ordinal) < 0) return false;
        int n = line.Length;
        var codeChars = new char[n];
        for (int i = 0; i < n; i++) codeChars[i] = line[i];
        bool inString = false; bool isVerbatim = false;
        for (int i = 0; i < n; )
        {
            char c = line[i];
            if (!inString)
            {
                if (c == '"')
                {
                    bool isVerb = false;
                    if (i > 0 && line[i-1] == '@') isVerb = true;
                    if (i >= 2 && line[i-2] == '$' && line[i-1] == '@') isVerb = true;
                    inString = true; isVerbatim = isVerb;
                    codeChars[i] = ' ';
                    i++;
                    continue;
                }
                if (c == '\'')
                {
                    codeChars[i] = ' ';
                    i++;
                    while (i < n)
                    {
                        codeChars[i] = ' ';
                        if (line[i] == '\\' && i+1 < n) { i+=2; continue; }
                        if (line[i] == '\'') { i++; break; }
                        i++;
                    }
                    continue;
                }
                if (c == '/' && i+1 < n && line[i+1] == '/')
                {
                    for (int k=i; k<n; k++) codeChars[k]=' ';
                    break;
                }
                if (c == '/' && i+1 < n && line[i+1] == '*')
                {
                    for (int k=i; k<n; k++) codeChars[k]=' ';
                    break;
                }
                i++;
            }
            else
            {
                codeChars[i]=' ';
                if (isVerbatim)
                {
                    if (c == '"' && i+1 < n && line[i+1] == '"') { codeChars[i+1]=' '; i+=2; continue; }
                    if (c == '"') { inString=false; }
                }
                else
                {
                    if (c == '\\' && i+1 < n) { codeChars[i+1]=' '; i+=2; continue; }
                    if (c == '"') { inString=false; }
                }
                i++;
            }
        }
        string codeOnly = new string(codeChars);
        int pos = 0;
        while (true)
        {
            int idx = codeOnly.IndexOf(token, pos, StringComparison.Ordinal);
            if (idx < 0) return false;
            bool leftOk = idx==0 || !IsIdentifierPart(codeOnly[idx-1]);
            bool rightOk = idx+token.Length==n || !IsIdentifierPart(codeOnly[idx+token.Length]);
            if (leftOk && rightOk) return true;
            pos = idx + token.Length;
        }
    }

    private static JArray HitsToResults(List<CsHit> hits, string root, string kind)
    {
        var results = new JArray();
        foreach (CsHit hit in hits)
        {
            var result = new JObject
            {
                ["file"] = RelOf(root, hit.File),
                ["line"] = hit.Line,
                ["kind"] = kind,
                ["match"] = hit.Match,
            };
            if (hit.Context != null)
                result["context"] = hit.Context.DeepClone();
            results.Add(result);
        }
        return results;
    }

    /// <summary>Check if a line is a comment or string literal — skip for definition/declaration searches.</summary>
    private static bool IsCommentOrString(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith("///", StringComparison.Ordinal)
            || trimmed.StartsWith("[", StringComparison.Ordinal) // attributes like [Tool(...)]
            || trimmed.StartsWith("\"", StringComparison.Ordinal)
            || trimmed.StartsWith("@\"", StringComparison.Ordinal)
            || trimmed.StartsWith("$\"", StringComparison.Ordinal)
            || trimmed.StartsWith("$@\"", StringComparison.Ordinal);
    }

    private static bool IsDeclarationLine(string line)
    {
        if (IsCommentOrString(line))
            return false;

        int quote = line.IndexOf('"');
        if (quote < 0)
            return true;

        foreach (string keyword in SourceDeclarationKeywords)
        {
            int keywordIndex = line.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (keywordIndex > quote)
                return false;
        }
        return true;
    }

    private static bool MatchesSymbolKind(string line, string query, string kind)
    {
        if (kind.Length == 0 || kind == "any")
            return true;

        // Skip comment and string lines for all kind filters
        if (IsCommentOrString(line))
            return false;

        if (kind is "class" or "interface" or "struct" or "enum" or "record" or "delegate")
        {
            string pattern = $"\\b{Regex.Escape(kind)}\\s+\\w*{Regex.Escape(query)}\\w*\\b";
            return CreateSearchRegex(pattern).IsMatch(line);
        }

        if (kind == "method")
        {
            const string modifiers = "public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|unsafe|partial|readonly|ref";
            string pattern = $"^\\s*(?:(?:{modifiers})\\s+)*(?:[\\w<>\\[\\],.?]+\\s+)+\\w*{Regex.Escape(query)}\\w*\\s*\\(";
            return CreateSearchRegex(pattern).IsMatch(line);
        }

        if (kind == "property")
            return line.Contains("get;", StringComparison.OrdinalIgnoreCase)
                || line.Contains("set;", StringComparison.OrdinalIgnoreCase)
                || line.Contains("init;", StringComparison.OrdinalIgnoreCase);

        return true;
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || string.Equals(relative, "..", StringComparison.Ordinal))
            return false;
        return !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Indexed files under <paramref name="directory"/>.
    ///
    /// 2026-08-03: callers pass subdirectories here (plugin/catalog iterates one
    /// plugin dir at a time, csharp/list_plugin_tools passes `target`). Because
    /// there is a single global snapshot keyed by root, each such call used to
    /// re-root it — discarding the whole-repo index, building one for the
    /// subtree, and leaving the snapshot rooted there so the next csharp/*
    /// query paid another full repo rebuild. One plugin/catalog call did that
    /// once per plugin directory. Serve subtrees by filtering the repo index
    /// instead; it is already sorted by path, so the filter is a linear scan and
    /// the ordering callers expect is preserved.
    /// </summary>
    private static IReadOnlyList<CsSourceFile> GetSourceFiles(string directory)
    {
        string repoRoot = ResolveRepoRoot();
        string full = Path.GetFullPath(directory);

        // Outside the repo (or is the repo) — index it directly as before.
        if (!IsWithinRoot(repoRoot, full))
            return GetSourceIndex(full).Files;

        string prefix = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
        var matches = new List<CsSourceFile>();
        foreach (CsSourceFile file in GetSourceIndex(repoRoot).Files)
        {
            if (file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                matches.Add(file);
        }
        return matches;
    }
}
