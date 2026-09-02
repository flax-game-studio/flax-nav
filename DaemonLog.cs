// DaemonLog — minimal file logger for the flaxmcp-nav daemon.
//
// Why this exists (Tier 1 hardening, 2026-05-29 research wave):
//   When the daemon is launched detached via `cmd /c start ""` (the
//   auto-start plugin's path), stdout goes nowhere. If the daemon
//   hangs or crashes, the operator has zero visibility.
//
//   Serilog would be heavier (~6 MB deps + 3 NuGet refs); for this
//   single-binary single-consumer daemon, a 30-line file logger with
//   the same interface is enough.
//
// Output: %TEMP%/flaxmcp-nav/daemon.log (rotated when >5 MB).
//
// Thread safety: per-call lock. The daemon's request handler is
// inherently serial (named pipe with maxNumberOfServerInstances=1)
// so contention is near zero.

using System;
using System.IO;
using System.Threading;

namespace FlaxMcp.NavDaemon;

internal static class DaemonLog
{
    private static readonly object _lock = new();
    private static readonly string _logDir;
    private static readonly string _logPath;
    private const long RotateAtBytes = 5 * 1024 * 1024;
    private const int KeepRotations = 3;

    static DaemonLog()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "flaxmcp-nav");
        _logPath = Path.Combine(_logDir, "daemon.log");
        try { Directory.CreateDirectory(_logDir); } catch (Exception dirEx) { System.Console.Error.WriteLine($"DaemonLog create dir: {dirEx.Message}"); }
    }

    public static string LogPath => _logPath;

    public static void Info(string message) => Write("INFO ", message, Console.Out);
    public static void Warn(string message) => Write("WARN ", message, Console.Error);
    public static void Error(string message) => Write("ERROR", message, Console.Error);

    private static long _loggedBytes;
    private static void Write(string level, string message, TextWriter console)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {level} {message}";
        try { console.WriteLine(line); } catch (Exception consoleEx) { System.Console.Error.WriteLine($"DaemonLog console: {consoleEx.Message}"); }

        lock (_lock)
        {
            try
            {
                if (Interlocked.Read(ref _loggedBytes) > RotateAtBytes)
                {
                    Rotate();
                    Interlocked.Exchange(ref _loggedBytes, 0);
                }
                File.AppendAllText(_logPath, line + Environment.NewLine);
                Interlocked.Add(ref _loggedBytes, line.Length + Environment.NewLine.Length);
            }
            catch (Exception fileEx) { System.Console.Error.WriteLine($"DaemonLog file: {fileEx.Message}"); }
        }
    }

    private static void Rotate()
    {
        // daemon.log -> daemon.log.1 -> daemon.log.2 -> daemon.log.3 (drop)
        try
        {
            for (int i = KeepRotations; i >= 1; i--)
            {
                var older = $"{_logPath}.{i}";
                if (File.Exists(older))
                {
                    if (i == KeepRotations) File.Delete(older);
                    else File.Move(older, $"{_logPath}.{i + 1}", overwrite: true);
                }
            }
            File.Move(_logPath, $"{_logPath}.1", overwrite: true);
        }
        catch (Exception rotEx) { System.Console.Error.WriteLine($"DaemonLog rotate: {rotEx.Message}"); }
    }
}
