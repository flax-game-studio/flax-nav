using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
namespace FlaxMcp.NavDaemon;

// ─── Shadow-copy launch: never serve from the build output directory ───
//
// A daemon serving from tools/flaxmcp-nav/bin/ holds OS file locks on
// flaxmcp-nav.exe/.dll and every dependency, so ANY rebuild of the daemon
// (dotnet build flaxmcp-nav.csproj / tools.slnf) fails with MSB3027 until the
// operator kills the process. Instead, `--daemon` copies the build output to
// a per-build shadow directory under %TEMP% and re-execs from there:
//   - bin/ stays writable at all times → builds never blocked by the daemon;
//   - after a rebuild the running daemon keeps serving the OLD build until
//     restarted — `daemon.ps1 start`/`warm` compare `--status`.buildStamp
//     against the on-disk dll and restart automatically on mismatch.
// Opt-out: FLAXMCP_NAV_NO_SHADOW=1 (tests use it so teardown Kill() reaches
// the actual serving process, not a launcher that already exited).
internal static partial class Program
{
    private static int RunDaemonEntry()
    {
        if (Environment.GetEnvironmentVariable("FLAXMCP_NAV_SHADOWED") == "1" ||
            Environment.GetEnvironmentVariable("FLAXMCP_NAV_NO_SHADOW") == "1")
            return RunDaemon();
        try
        {
            return ReexecFromShadow();
        }
        catch (Exception ex)
        {
            DaemonLog.Warn($"shadow-copy launch failed ({ex.Message}); serving from build output — rebuilds will be blocked while this daemon runs");
            return RunDaemon();
        }
    }

    /// <summary>Identity of a build output: last-write ticks + size of the
    /// main assembly, hex-encoded. File.Copy preserves timestamps, so a shadow
    /// copy reports the SAME stamp as the bin directory it was copied from.</summary>
    private static string BuildStampOf(string dir)
    {
        var fi = new FileInfo(Path.Combine(dir, "flaxmcp-nav.dll"));
        if (!fi.Exists) fi = new FileInfo(Environment.ProcessPath!);
        return $"{fi.LastWriteTimeUtc.Ticks:x}-{fi.Length:x}";
    }

    private static int ReexecFromShadow()
    {
        string src = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string exeName = Path.GetFileName(Environment.ProcessPath!);
        string stamp = BuildStampOf(src);
        string shadowRoot = Path.Combine(Path.GetTempPath(), "flaxmcp-nav", "shadow");
        string shadowDir = Path.Combine(shadowRoot, stamp);
        string marker = Path.Combine(shadowDir, ".copy-complete");

        // Serialize concurrent launchers copying into the same stamp dir.
        using (var copyMutex = new Mutex(initiallyOwned: false, @"Global\flaxmcp-nav-shadow-copy"))
        {
            bool owns = false;
            try { owns = copyMutex.WaitOne(30_000); } catch (AbandonedMutexException) { owns = true; }
            try
            {
                if (!File.Exists(marker))
                {
                    CopyTree(src, shadowDir);
                    File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
                }
            }
            finally { if (owns) copyMutex.ReleaseMutex(); }
        }
        CleanStaleShadows(shadowRoot, keep: stamp);

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(shadowDir, exeName),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // SECURITY trust-boundary: self re-exec of flaxmcp-nav.exe from the
        // shadow copy (same verified binary we are running; shadow dir is a
        // copy of our own build output, never operator secrets). No SHA-256 pin
        // needed — the executable is us. Env is intentionally inherited (the
        // child needs the parent's repo-root/pipe state; the trust boundary is
        // that both processes are the same self-authored binary).
        psi.ArgumentList.Add("--daemon");
        // The shadow dir is outside the repo, so the child cannot discover the
        // repo root by walking up from its own location — pass it explicitly.
        psi.ArgumentList.Add("--repo-root");
        psi.ArgumentList.Add(ResolveRepoRoot());
        psi.Environment["FLAXMCP_NAV_SHADOWED"] = "1";
        var child = Process.Start(psi);
        if (child == null)
        {
            DaemonLog.Warn("shadow re-exec: Process.Start returned null; serving from build output");
            return RunDaemon();
        }
        DaemonLog.Info($"shadow re-exec pid={child.Id} stamp={stamp} dir={shadowDir}");
        return 0;
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)), overwrite: true);
    }

    /// <summary>Delete shadow dirs from older builds. A dir whose main dll is
    /// still mapped by a running daemon can't be opened exclusively — skip it
    /// entirely rather than half-deleting files out from under that process
    /// (its sqlite native dll loads lazily and may not be mapped yet).</summary>
    private static void CleanStaleShadows(string shadowRoot, string keep)
    {
        try
        {
            foreach (string dir in Directory.GetDirectories(shadowRoot))
            {
                if (string.Equals(Path.GetFileName(dir), keep, StringComparison.OrdinalIgnoreCase))
                    continue;
                string dll = Path.Combine(dir, "flaxmcp-nav.dll");
                if (File.Exists(dll))
                {
                    try
                    {
                        using var probe = File.Open(dll, FileMode.Open, FileAccess.Read, FileShare.None);
                    }
                    catch
                    {
                        continue; // in use by a still-running older daemon
                    }
                }
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException ex) { SwallowedCatch.Record("NavDaemon.CleanStaleShadows.io", ex); }
                catch (UnauthorizedAccessException ex) { SwallowedCatch.Record("NavDaemon.CleanStaleShadows.auth", ex); }
            }
        }
        catch (DirectoryNotFoundException ex) { SwallowedCatch.Record("NavDaemon.CleanStaleShadows.notFound", ex); }
        catch (Exception ex) { SwallowedCatch.Record("NavDaemon.CleanStaleShadows", ex); }
    }
}

