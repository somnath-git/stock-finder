using System.Net.Http.Headers;
using TrendsTracker.Config;
using TrendsTracker.Data;
using TrendsTracker.Models;
using TrendsTracker.Services;
using TrendsTracker.Stages;

namespace TrendsTracker.ScreenApp;

/// <summary>
/// Batch growth-analysis over the constituents of Screener index/screen pages
/// (e.g. Smallcap 250, Nifty Microcap 250). Reuses the SAME growth-verification
/// engine as the single-company and theme tools — the only difference is where the
/// list of companies comes from.
///
/// Designed for a slow local LLM: it resumes from the DB (skips already-analyzed
/// stocks), isolates per-company failures, and takes a --limit so you can run it in
/// bite-sized batches.
///
/// Usage:
///   dotnet run --project src/TrendsTracker.Screen -- --limit 20
///   dotnet run --project src/TrendsTracker.Screen -- --index SMALLCA250 --limit 10
///   dotnet run --project src/TrendsTracker.Screen -- --force --limit 5
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var force = HasFlag(args, "--force");
        var limit = GetInt(args, "--limit") ?? 250;
        var indexOverride = GetValue(args, "--index");
        var maxPages = GetInt(args, "--pages") ?? 10;

        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        AppConfig cfg;
        try { cfg = AppConfig.Load(configPath); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to load config: {ex.Message}"); return 1; }

        var indexes = indexOverride is not null
            ? new List<string> { indexOverride }
            : cfg.ScreenerIndexes;

        using var http = BuildHttpClient();
        var fetcher = new HttpFetcher(http);
        ILlmClient llm = new OllamaClient(http, cfg.Ollama);
        var docFinder = new DocumentFinder(fetcher);
        var indexClient = new ScreenerIndexClient(fetcher);
        var stage = new CompanyConfirmationStage(cfg, llm, docFinder, fetcher);

        AnalysisRepository? repo = null;
        try { repo = DataFactory.CreateRepository(); await repo.EnsureCreatedAsync(); }
        catch (Exception ex) { Console.WriteLine($"  (DB unavailable: {ex.Message})"); repo = null; }

        var runner = new AnalysisRunner(cfg, stage, docFinder, repo);

        Console.WriteLine("=================================================");
        Console.WriteLine("  StockFinder — index screen batch analysis");
        Console.WriteLine("=================================================");
        Console.WriteLine($"  Indexes : {string.Join(", ", indexes)}");
        Console.WriteLine($"  LLM     : Ollama ({cfg.Ollama.GenerationModel})");
        Console.WriteLine($"  Limit   : {(limit == int.MaxValue ? "all new" : limit.ToString())}");
        Console.WriteLine($"  Cap     : ≤ ₹{cfg.MaxMarketCapCr:N0} cr");
        Console.WriteLine();

        // 1. Collect constituents across the configured indexes.
        var stocks = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var idx in indexes)
        {
            Console.WriteLine($"Fetching constituents of {idx}...");
            var members = await indexClient.GetConstituentsAsync(idx, maxPages);
            foreach (var s in members)
                if (seen.Add(s)) stocks.Add(s);
        }
        Console.WriteLine($"Total distinct candidate stocks: {stocks.Count}\n");

        // 2. Analyze each (up to --limit NEW analyses), resuming from the DB.
        var counts = new Dictionary<AnalysisRunner.Outcome, int>();
        var analyzedThisRun = 0;

        foreach (var stock in stocks)
        {
            if (analyzedThisRun >= limit)
            {
                Console.WriteLine($"\nReached --limit {limit} new analyses. Re-run to continue.");
                break;
            }

            var outcome = await runner.ProcessAsync(new Company { Name = stock, Stock = stock }, force);
            counts[outcome] = counts.GetValueOrDefault(outcome) + 1;

            // Only "real" analyses (not cached/skipped) count toward the limit.
            if (outcome is AnalysisRunner.Outcome.AnalyzedPass
                        or AnalysisRunner.Outcome.AnalyzedFail
                        or AnalysisRunner.Outcome.NoDocuments
                        or AnalysisRunner.Outcome.Error)
                analyzedThisRun++;
        }

        // 3. Summary.
        Console.WriteLine("\n================ SUMMARY ================");
        foreach (var kv in counts.OrderByDescending(k => k.Value))
            Console.WriteLine($"  {kv.Key,-16} {kv.Value}");
        Console.WriteLine("=========================================");
        return 0;
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetValue(string[] args, string key)
    {
        var i = Array.FindIndex(args, a => a.Equals(key, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int? GetInt(string[] args, string key) =>
        int.TryParse(GetValue(args, key), out var v) ? v : null;

    private static HttpClient BuildHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return http;
    }
}
