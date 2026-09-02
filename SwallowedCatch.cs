using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace FlaxMcp.NavDaemon
{
    public static class SwallowedCatch
    {
        private static readonly ConcurrentDictionary<string, long> _counts =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        public static volatile Action<string, Exception, long>? Logger;

        public static void Record(Exception ex,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0)
        {
            var tag = $"{System.IO.Path.GetFileNameWithoutExtension(sourceFilePath)}.L{sourceLineNumber}";
            Record(tag, ex);
        }

        public static void Record(string siteTag, Exception ex)
        {
            if (string.IsNullOrEmpty(siteTag)) siteTag = "(unknown)";
            if (ex == null) return;
            long occurrence = _counts.AddOrUpdate(siteTag, 1, (_, count) => count + 1);
            var logger = Logger;
            if (logger == null) return;
            try { logger(siteTag, ex, occurrence); }
            catch (Exception loggerEx)
            {
                try
                {
                    string msg = $"[SwallowedCatch] Logger failed for '{siteTag}' (occurrence {occurrence}): {loggerEx.GetType().Name}: {loggerEx.Message}";
                    System.Diagnostics.Trace.WriteLine(msg);
                    System.Diagnostics.Debug.WriteLine(msg);
                }
                catch (Exception) { }
            }
        }

        public static Dictionary<string, long> GetCounters()
        {
            return new Dictionary<string, long>(_counts, StringComparer.Ordinal);
        }

        public static void ResetForTests()
        {
            _counts.Clear();
        }
    }
}
