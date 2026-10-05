using System.Net.Http.Headers;
using TrendsTracker.Config;
using TrendsTracker.Data;
using TrendsTracker.Models;
using TrendsTracker.Services;
using TrendsTracker.Stages;

namespace TrendsTracker.CompanyApp;

/// <summary>
/// Project 2 — single-company growth check.
///
/// Give it a company (ticker preferred, e.g. "LT", or a name) and it runs the SAME
/// growth-verification used by the Trends pipeline: find the company's concall /
/// investor PDFs on Screener, chunk + embed them, retrieve the passages about
/// growth / margins / expansion, and ask the LLM whether revenue can plausibly
/// double (2x+) within 3-5 years — grounded in management's own words.
///
/// All the heavy lifting lives in TrendsTracker.Core and is shared with Project 1.
///
/// Usage:
///   dotnet run --project src/TrendsTracker.Company -- LT
///   dotnet run --project src/TrendsTracker.Company -- "Larsen &amp; Toubro"
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // --force re-analyzes even if a saved result exists.
        var force = args.Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase));
        var positional = args.Where(a => !a.StartsWith("--")).ToArray();

        // Stock comes from the command line, or we ask for it interactively
        // (e.g. when launched from Visual Studio's Run button with no arguments).
        string nameOrStock;
        if (positional.Length > 0 && !string.IsNullOrWhiteSpace(positional[0]))
        {
            nameOrStock = positional[0].Trim();
        }
        else
        {
            Console.Write("Enter stock symbol or company name (e.g. LT, TATAMOTORS, DMART): ");
            nameOrStock = (Console.ReadLine() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(nameOrStock))
            {
                Console.Error.WriteLine("No stock entered. Exiting.");
                return 1;
            }
        }

        // Optional explicit stock symbol as a second positional arg; otherwise reuse the input.
        var stock = positional.Length > 1 && !string.IsNullOrWhiteSpace(positional[1])
            ? positional[1].Trim()
            : nameOrStock;

        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        AppConfig cfg;
        try
        {
            cfg = AppConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load config: {ex.Message}");
            return 1;
        }

        using var http = BuildHttpClient();
        var fetcher = new HttpFetcher(http);
        ILlmClient llm = new OllamaClient(http, cfg.Ollama);
        var docFinder = new DocumentFinder(fetcher);
        var stage = new CompanyConfirmationStage(cfg, llm, docFinder, fetcher);

        var company = new Company
        {
            Name = nameOrStock,
            Stock = stock
        };

        Console.WriteLine("=================================================");
        Console.WriteLine("  TrendsTracker — single company growth check");
        Console.WriteLine("=================================================");
        Console.WriteLine($"  Company : {company.Name}");
        Console.WriteLine($"  Stock   : {company.Stock}");
        Console.WriteLine($"  LLM     : Ollama ({cfg.Ollama.GenerationModel})");
        Console.WriteLine($"  Docs    : Screener.in (free)");
        Console.WriteLine();

        // Connect to the database (saved results + "already analyzed?" check).
        AnalysisRepository? repo = null;
        try
        {
            repo = DataFactory.CreateRepository();
            await repo.EnsureCreatedAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (DB unavailable, results won't be saved: {ex.Message})");
            repo = null;
        }

        // Show the saved result instantly if we have one and the user didn't force a refresh.
        if (repo is not null && !force)
        {
            var saved = await repo.GetByStockAsync(stock);
            if (saved is not null)
            {
                Console.WriteLine($"Already analyzed on {saved.AnalyzedAt.LocalDateTime:yyyy-MM-dd HH:mm} (cached). " +
                                  "Use --force to re-analyze.");
                PrintSaved(saved);
                return 0;
            }
        }

        Console.WriteLine("Analysing concalls / investor presentations... (this can take a minute)");

        ConfirmationResult result;
        try
        {
            result = await stage.ConfirmAsync(company);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Analysis failed: {ex.Message}");
            return 1;
        }

        // Persist the fresh result.
        if (repo is not null)
        {
            try
            {
                await repo.UpsertAsync(ToEntity(company, result));
                Console.WriteLine("  (saved to database)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  (could not save to DB: {ex.Message})");
            }
        }

        PrintResult(result);
        return 0;
    }

    private static void PrintResult(ConfirmationResult r)
    {
        Console.WriteLine();
        Console.WriteLine("================ RESULT ================");
        Console.WriteLine($"Company                 : {r.Company.Name}");
        Console.WriteLine($"Market cap              : {(r.MarketCapCr.HasValue ? $"\u20b9{r.MarketCapCr:N0} cr" : "unknown")}" +
                          (r.AboveMarketCapLimit ? "  (ABOVE your size limit)" : ""));
        Console.WriteLine($"Documents found         : {(r.DocumentsFound ? "yes" : "no")}");

        if (!r.DocumentsFound)
        {
            Console.WriteLine($"Note                    : {r.Verdict}");
            Console.WriteLine("========================================");
            return;
        }

        Console.WriteLine($"Can revenue 2x+ in 3-5y : {(r.LongTermGrowthClaimed ? "YES" : "no")}");
        Console.WriteLine($"Confidence              : {r.Confidence}/100");
        Console.WriteLine();
        Console.WriteLine($"Verdict   : {r.Verdict}");
        if (!string.IsNullOrWhiteSpace(r.GrowthSignal))
            Console.WriteLine($"Growth    : {r.GrowthSignal}");
        if (!string.IsNullOrWhiteSpace(r.MarginSignal))
            Console.WriteLine($"Margins   : {r.MarginSignal}");
        if (!string.IsNullOrWhiteSpace(r.ExpansionSignal))
            Console.WriteLine($"Expansion : {r.ExpansionSignal}");

        if (r.EvidenceQuotes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Evidence (management's own words):");
            foreach (var q in r.EvidenceQuotes)
                Console.WriteLine($"  \u2022 {q}");
        }

        if (r.DocumentSources.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Sources:");
            foreach (var s in r.DocumentSources)
                Console.WriteLine($"  - {s}");
        }

        Console.WriteLine();
        Console.WriteLine("DISCLAIMER: research assistant output, NOT investment advice.");
        Console.WriteLine("========================================");
    }

    /// <summary>Prints a previously-saved analysis from the database.</summary>
    private static void PrintSaved(CompanyAnalysis a)
    {
        Console.WriteLine();
        Console.WriteLine("================ RESULT (cached) ================");
        Console.WriteLine($"Company                 : {a.Name}  [{a.Stock}]");
        Console.WriteLine($"Market cap              : {(a.MarketCapCr.HasValue ? $"\u20b9{a.MarketCapCr:N0} cr" : "unknown")}");
        Console.WriteLine($"Can 2x+ in 3-5y         : {(a.Verdict ? "YES" : "no")}");
        Console.WriteLine($"Confidence              : {a.Confidence}/100");
        Console.WriteLine($"Signal type             : {a.SignalType}");
        Console.WriteLine();
        if (!string.IsNullOrWhiteSpace(a.GrowthComment)) Console.WriteLine($"Growth    : {a.GrowthComment}");
        if (!string.IsNullOrWhiteSpace(a.MarginComment)) Console.WriteLine($"Margins   : {a.MarginComment}");
        if (!string.IsNullOrWhiteSpace(a.ExpansionComment)) Console.WriteLine($"Expansion : {a.ExpansionComment}");

        if (a.EvidenceQuotes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Evidence (management's own words):");
            foreach (var q in a.EvidenceQuotes) Console.WriteLine($"  \u2022 {q}");
        }
        if (a.Sources.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Sources:");
            foreach (var s in a.Sources) Console.WriteLine($"  - {s}");
        }
        Console.WriteLine();
        Console.WriteLine("DISCLAIMER: research assistant output, NOT investment advice.");
        Console.WriteLine("================================================");
    }

    /// <summary>Maps a fresh confirmation result into a persistable entity.</summary>
    private static CompanyAnalysis ToEntity(Company company, ConfirmationResult r) => new()
    {
        Stock = string.IsNullOrWhiteSpace(company.Stock) ? company.Name : company.Stock,
        Name = company.Name,
        Verdict = r.LongTermGrowthClaimed,
        Confidence = r.Confidence,
        MarketCapCr = r.MarketCapCr,
        SignalType = ClassifySignal(r),
        GrowthComment = r.GrowthSignal,
        MarginComment = r.MarginSignal,
        ExpansionComment = r.ExpansionSignal,
        EvidenceQuotes = r.EvidenceQuotes ?? new(),
        Sources = r.DocumentSources ?? new(),
        AnalyzedAt = DateTimeOffset.UtcNow
    };

    /// <summary>Crude classification of what drove the verdict, for the UI/API to show.</summary>
    private static string ClassifySignal(ConfirmationResult r)
    {
        var g = (r.GrowthSignal ?? "").ToLowerInvariant();
        var e = (r.ExpansionSignal ?? "").ToLowerInvariant();
        var hasRevenue = g.Contains("revenue") || g.Contains("cagr") || g.Contains("sales");
        var hasProfit = g.Contains("profit") || g.Contains("pat") || g.Contains("earnings") || g.Contains("ebitda");
        var hasThematic = e.Contains("expansion") || e.Contains("capacity") || e.Contains("order") || e.Contains("project");

        var parts = new List<string>();
        if (hasRevenue) parts.Add("revenue");
        if (hasProfit) parts.Add("profit");
        if (hasThematic) parts.Add("thematic");
        return parts.Count == 0 ? "unknown" : string.Join("+", parts);
    }

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
