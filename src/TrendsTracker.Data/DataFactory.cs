using Microsoft.EntityFrameworkCore;

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
        if (!string.IsNullOrWhiteSpace(explicitConn)) return explicitConn;
        var env = Environment.GetEnvironmentVariable("TRENDSTRACKER_DB");
        return !string.IsNullOrWhiteSpace(env) ? env : DefaultLocal;
    }

    public static AppDbContext CreateContext(string? connectionString = null)
    {
        var conn = ResolveConnectionString(connectionString);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(conn)
            .Options;
        return new AppDbContext(options);
    }

    public static AnalysisRepository CreateRepository(string? connectionString = null)
        => new(CreateContext(connectionString));
}
