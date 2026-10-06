using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace TrendsTracker.Data;

/// <summary>
/// Helper for console apps (no DI container) to build a DbContext + repository
/// from a connection string. The API uses standard DI instead.
///
/// Connection string resolution order:
///   1. explicit argument
///   2. TRENDSTRACKER_DB environment variable
///   3. a local default (localhost) for running outside Docker
/// </summary>
public static class DataFactory
{
    public const string DefaultLocal =
        "Host=localhost;Port=5432;Database=trendstracker;Username=postgres;Password=postgres";

    public static string ResolveConnectionString(string? explicitConn = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitConn)) return Normalize(explicitConn);
        var env = Environment.GetEnvironmentVariable("TRENDSTRACKER_DB");
        return !string.IsNullOrWhiteSpace(env) ? Normalize(env) : DefaultLocal;
    }

    /// <summary>
    /// Npgsql needs a key-value connection string (Host=...;Port=...;...), but
    /// managed providers (Neon, Supabase, Railway) hand out a postgres:// URI.
    /// Convert a URI to the key-value form so either can be pasted into
    /// TRENDSTRACKER_DB; a key-value string is returned unchanged.
    /// </summary>
    public static string Normalize(string conn)
    {
        conn = conn.Trim();
        if (!conn.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !conn.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return conn;

        var uri = new Uri(conn);
        var userInfo = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var database = uri.AbsolutePath.Trim('/');
        var port = uri.Port > 0 ? uri.Port : 5432;

        var sb = new System.Text.StringBuilder();
        sb.Append($"Host={uri.Host};Port={port};Database={database};Username={user};Password={password};");

        // Carry over the sslmode option; default to requiring SSL for managed
        // hosts (Neon, Supabase, etc. all need it).
        var sslmode = ParseQueryValue(uri.Query, "sslmode");
        sb.Append(sslmode switch
        {
            "disable" => "SSL Mode=Disable;",
            "require" => "SSL Mode=Require;Trust Server Certificate=true;",
            "verify-ca" or "verify-full" => "SSL Mode=VerifyFull;",
            _ => "SSL Mode=Require;Trust Server Certificate=true;",
        });
        return sb.ToString();
    }

    private static string? ParseQueryValue(string query, string key)
    {
        // query looks like "?sslmode=require&channel_binding=require"
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && string.Equals(kv[0], key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    public static AppDbContext CreateContext(string? connectionString = null)
    {
        var conn = ResolveConnectionString(connectionString);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(conn, o => o.UseVector())   // enable pgvector in Npgsql
            .Options;
        return new AppDbContext(options);
    }

    public static AnalysisRepository CreateRepository(string? connectionString = null)
        => new(CreateContext(connectionString));

    public static TranscriptStore CreateTranscriptStore(string? connectionString = null)
        => new(CreateContext(connectionString));
}
