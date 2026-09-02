using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace FlaxMcp.NavDaemon;

internal static partial class Program
{
    private const string ReceiptFileName = "receipts.fts5.db";

    private static JObject DispatchReceiptAtomic(string atomic, JObject args)
    {
        try
        {
            return atomic switch
            {
                "receipt/search" => ReceiptSearch(args),
                "receipt/recent" => ReceiptRecent(args),
                "receipt/by_id" => ReceiptById(args),
                _ => Err("unknown_atomic", atomic),
            };
        }
        catch (Exception ex)
        {
            return Err("receipt_atomic_threw", ex.Message);
        }
    }

    private static JObject ReceiptSearch(JObject args)
    {
        string? query = (string?)args["query"];
        if (string.IsNullOrWhiteSpace(query))
            return Err("invalid_arguments", "'query' required");

        int limit = Math.Clamp((int?)args["limit"] ?? 25, 1, 200);
        string? outcome = (string?)args["outcome"];
        string? tool = (string?)args["tool"];
        string? policy = (string?)args["policy"];

        var dbPath = ResolveReceiptDbPath();
        if (dbPath == null)
            return Err("fts5_unavailable", "No receipt FTS5 database found. The kernel must be running with FTS5 enabled.");

        try
        {
            using var conn = new SqliteConnection("Data Source=" + dbPath);
            conn.Open();

            var sql = new System.Text.StringBuilder();
            sql.Append("SELECT id, tool, effective_tool, intent, error, session_id, utc, outcome, policy, rank AS score FROM receipts_fts WHERE receipts_fts MATCH @q");
            if (!string.IsNullOrEmpty(outcome))
            {
                sql.Append(" AND outcome = @outcome");
            }
            if (!string.IsNullOrEmpty(tool))
            {
                sql.Append(" AND tool = @tool");
            }
            if (!string.IsNullOrEmpty(policy))
            {
                sql.Append(" AND policy = @policy");
            }
            sql.Append(" ORDER BY rank LIMIT @limit");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql.ToString();
            cmd.Parameters.AddWithValue("@q", query);
            cmd.Parameters.AddWithValue("@limit", limit);
            if (!string.IsNullOrEmpty(outcome))
                cmd.Parameters.AddWithValue("@outcome", outcome.ToLowerInvariant());
            if (!string.IsNullOrEmpty(tool))
                cmd.Parameters.AddWithValue("@tool", tool);
            if (!string.IsNullOrEmpty(policy))
                cmd.Parameters.AddWithValue("@policy", policy.ToLowerInvariant());

            var hits = new JArray();
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var h = new JObject
                {
                    ["id"] = rdr.IsDBNull(0) ? null : rdr.GetString(0),
                    ["tool"] = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                    ["effectiveTool"] = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    ["intent"] = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    ["error"] = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    ["sessionId"] = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    ["utc"] = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                    ["outcome"] = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                    ["policy"] = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                    ["score"] = rdr.IsDBNull(9) ? 0.0 : rdr.GetDouble(9),
                };
                hits.Add(h);
            }

            return new JObject
            {
                ["ok"] = true,
                ["query"] = query,
                ["count"] = hits.Count,
                ["results"] = hits,
            };
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            return Err("fts5_query_error", $"FTS5 query syntax error: {ex.Message}. Check the MATCH expression.");
        }
    }

    private static JObject ReceiptRecent(JObject args)
    {
        int limit = Math.Clamp((int?)args["limit"] ?? 50, 1, 500);
        string? outcome = (string?)args["outcome"];

        var dbPath = ResolveReceiptDbPath();
        if (dbPath == null)
            return Err("fts5_unavailable", "No receipt FTS5 database found.");

        try
        {
            using var conn = new SqliteConnection("Data Source=" + dbPath);
            conn.Open();

            var sql = "SELECT id, tool, effective_tool, intent, error, session_id, utc, outcome, policy FROM receipts_fts";
            if (!string.IsNullOrEmpty(outcome))
            {
                sql += " WHERE outcome = @outcome";
            }
            sql += " ORDER BY utc DESC LIMIT @limit";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@limit", limit);
            if (!string.IsNullOrEmpty(outcome))
                cmd.Parameters.AddWithValue("@outcome", outcome.ToLowerInvariant());

            var hits = new JArray();
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var h = new JObject
                {
                    ["id"] = rdr.IsDBNull(0) ? null : rdr.GetString(0),
                    ["tool"] = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                    ["effectiveTool"] = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    ["intent"] = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    ["error"] = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    ["sessionId"] = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    ["utc"] = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                    ["outcome"] = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                    ["policy"] = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                };
                hits.Add(h);
            }

            return new JObject
            {
                ["ok"] = true,
                ["count"] = hits.Count,
                ["results"] = hits,
            };
        }
        catch (Exception ex)
        {
            return Err("receipt_recent_failed", ex.Message);
        }
    }

    private static JObject ReceiptById(JObject args)
    {
        string? id = (string?)args["id"];
        if (string.IsNullOrWhiteSpace(id))
            return Err("invalid_arguments", "'id' required");

        var dbPath = ResolveReceiptDbPath();
        if (dbPath == null)
            return Err("fts5_unavailable", "No receipt FTS5 database found.");

        try
        {
            using var conn = new SqliteConnection("Data Source=" + dbPath);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, tool, effective_tool, intent, error, session_id, utc, outcome, policy FROM receipts_fts WHERE id = @id LIMIT 1";
            cmd.Parameters.AddWithValue("@id", id);

            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read())
            {
                return Err("not_found", $"Receipt '{id}' not found");
            }

            var result = new JObject
            {
                ["ok"] = true,
                ["receipt"] = new JObject
                {
                    ["id"] = rdr.IsDBNull(0) ? null : rdr.GetString(0),
                    ["tool"] = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                    ["effectiveTool"] = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    ["intent"] = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    ["error"] = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    ["sessionId"] = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    ["utc"] = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                    ["outcome"] = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                    ["policy"] = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                },
            };

            return result;
        }
        catch (Exception ex)
        {
            return Err("receipt_by_id_failed", ex.Message);
        }
    }

    private static string? _receiptDbPathCached;

    private static string? ResolveReceiptDbPath()
    {
        if (_receiptDbPathCached != null) return _receiptDbPathCached;

        var explicitPath = Environment.GetEnvironmentVariable("FLAXMCP_FTS5_PATH") ?? Environment.GetEnvironmentVariable("FLAX_MCP_FTS5_PATH");
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            { _receiptDbPathCached = explicitPath; return explicitPath; }

        var walDir = Environment.GetEnvironmentVariable("FLAXMCP_WAL_DIR") ?? Environment.GetEnvironmentVariable("FLAX_MCP_WAL_DIR");
        if (!string.IsNullOrEmpty(walDir))
        {
            var candidate = Path.Combine(walDir, ReceiptFileName);
            if (File.Exists(candidate))
                { _receiptDbPathCached = candidate; return candidate; }
        }

        var repoRoot = ResolveRepoRoot();
        var repoDb = Path.Combine(repoRoot, "test-receipts.fts5.db");
        if (File.Exists(repoDb))
            { _receiptDbPathCached = repoDb; return repoDb; }

        var projectDir = Path.Combine(repoRoot, "tools", "flaxmcp-nav");
        var projectDb = Path.Combine(projectDir, "test-receipts.fts5.db");
        if (File.Exists(projectDb))
            { _receiptDbPathCached = projectDb; return projectDb; }

        return null;
    }
}
