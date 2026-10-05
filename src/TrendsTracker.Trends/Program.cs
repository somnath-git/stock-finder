using System.Net.Http.Headers;
using TrendsTracker.Config;
using TrendsTracker.Data;
using TrendsTracker.Models;
using TrendsTracker.Reporting;
using TrendsTracker.Services;
using TrendsTracker.Stages;

namespace TrendsTracker;

/// <summary>
/// TrendsTracker — a 5-stage research assistant:
///   1. Ingest recent financial news (free RSS + Google Programmable Search)
///   2. Detect forward-looking themes (Gemini)
///   3. Map themes to listed Indian companies (Gemini)
///   4. Confirm with RAG over the companies' own concalls / investor PDFs
///   5. Report (console + Markdown), with a clear "not investment advice" note.
///
/// Runs end-to-end even without keys: without a Gemini key it enters STUB MODE,
/// doing the ingest and showing exactly where real analysis would plug in.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (args.Length > 0 && File.Exists(args[0])) configPath = args[0];

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

        PrintBanner(cfg);

        using var http = BuildHttpClient();

        // Wire services.
        var fetcher = new HttpFetcher(http);
        var rss = new RssClient(fetcher);
        ILlmClient llm = new OllamaClient(http, cfg.Ollama);
        var docFinder = new DocumentFinder(fetcher);

        var report = new TrendReport();

        // ---- STAGE 1: INGEST ----
        Console.WriteLine("[1/5] Ingesting news...");
        var ingest = new NewsIngestStage(cfg, rss);
        var articles = await ingest.RunAsync();
        report.ArticlesAnalyzed = articles.Count;

        if (articles.Count == 0)
        {
            Console.WriteLine("No articles collected (check your network / feeds). Stopping.");
            ReportWriter.WriteConsole(report);
            return 0;
        }

        // ---- STAGE 2: THEME DETECTION ----
        Console.WriteLine("\n[2/5] Detecting trending themes...");
        var themeStage = new ThemeDetectionStage(cfg, llm);
        report.Themes = await themeStage.RunAsync(articles);

        if (report.Themes.Count == 0)
        {
            Console.WriteLine("No themes detected. Stopping.");
            Finish(report);
            return 0;
        }

        // ---- STAGES 3+4: PER-THEME, END-TO-END ----
        // For each theme: map its companies, then immediately analyze + save each.
        // Results flow incrementally and a crash mid-run keeps everything already saved.
        Console.WriteLine("\n[3/5] Per-theme: map companies -> confirm growth (RAG) -> save...");

        var companyStage = new CompanyMappingStage(cfg, llm);
        var confirmStage = new CompanyConfirmationStage(cfg, llm, docFinder, fetcher);

        AnalysisRepository? repo = null;
        try { repo = DataFactory.CreateRepository(); await repo.EnsureCreatedAsync(); }
        catch (Exception ex) { Console.WriteLine($"  (DB unavailable, results won't be saved: {ex.Message})"); }
        var runner = new AnalysisRunner(cfg, confirmStage, docFinder, repo);

        var themeNum = 0;
        foreach (var theme in report.Themes)
        {
            themeNum++;
            Console.WriteLine($"\n  Theme {themeNum}/{report.Themes.Count}: {theme.Name}");

            var companies = await companyStage.MapThemeAsync(theme);
            foreach (var company in companies)
            {
                var outcome = await runner.ProcessAsync(company);
                // Keep a lightweight record for the markdown report too.
                report.Confirmations.Add(new ConfirmationResult
                {
                    Company = company,
                    LongTermGrowthClaimed = outcome == AnalysisRunner.Outcome.AnalyzedPass
                });
            }
        }

        // ---- STAGE 5: REPORT ----
        Console.WriteLine("\n[5/5] Writing report...");
        Finish(report);
        return 0;
    }

    private static void Finish(TrendReport report)
    {
        ReportWriter.WriteConsole(report);
        var file = ReportWriter.WriteMarkdown(report, OutputDir());
        Console.WriteLine($"\nMarkdown report saved to: {file}");
    }

    private static string OutputDir() => Path.Combine(AppContext.BaseDirectory, "reports");

    private static HttpClient BuildHttpClient()
    {
        // Generous overall timeout; per-call limits are enforced by each client
        // (Gemini throttle/retry, Ollama per-request CTS). Local models can be slow.
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return http;
    }

    private static void PrintBanner(AppConfig cfg)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine("  TrendsTracker — financial trend research tool");
        Console.WriteLine("=================================================");
        Console.WriteLine($"  Lookback window : {cfg.LookbackDays} days");
        Console.WriteLine($"  LLM provider    : Ollama (local) — {cfg.Ollama.GenerationModel} @ {cfg.Ollama.BaseUrl}");
        Console.WriteLine($"  PDF source      : Screener.in (free)");
        Console.WriteLine();
    }
}
