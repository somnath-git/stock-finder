using System.Net.Http.Headers;
using TrendsTracker.Config;
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
        ILlmClient llm = cfg.UseOllama
            ? new OllamaClient(http, cfg.Ollama)
            : new GeminiClient(http, cfg.Gemini);
        var docFinder = new DocumentFinder(fetcher);

        // "Stub mode" = no usable LLM. Ollama needs no key; Gemini needs one.
        var llmReady = cfg.UseOllama || cfg.HasGeminiKey;
        var report = new TrendReport { StubMode = !llmReady };

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

        // Without a usable LLM, stages 2-4 can't run. Show the ingest result and stop gracefully.
        if (!llmReady)
        {
            Console.WriteLine();
            Console.WriteLine("STUB MODE: no Gemini API key configured, so theme detection, company");
            Console.WriteLine("mapping, and RAG confirmation are skipped. Either set Provider to");
            Console.WriteLine("\"Ollama\" (local, free) in appsettings.json, or add a Gemini key.");
            Console.WriteLine();
            Console.WriteLine($"Sample of what was ingested ({Math.Min(10, articles.Count)} of {articles.Count}):");
            foreach (var a in articles.Take(10))
                Console.WriteLine($"  - {a.ToPromptLine()}");

            ReportWriter.WriteConsole(report);
            var stubFile = ReportWriter.WriteMarkdown(report, OutputDir());
            Console.WriteLine($"\nMarkdown report: {stubFile}");
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

        // ---- STAGE 3: THEME -> COMPANIES ----
        Console.WriteLine("\n[3/5] Mapping themes to listed companies...");
        var companyStage = new CompanyMappingStage(cfg, llm);
        var companies = await companyStage.RunAsync(report.Themes);
        Console.WriteLine($"  {companies.Count} candidate companies.");

        // ---- STAGE 4: RAG CONFIRMATION ----
        Console.WriteLine("\n[4/5] Confirming via concalls / investor presentations (RAG)...");
        Console.WriteLine("  PDF discovery: Screener.in (free, no API key).");

        var confirmStage = new CompanyConfirmationStage(cfg, llm, docFinder, fetcher);
        foreach (var company in companies)
        {
            Console.WriteLine($"  Analysing {company.Name}...");
            try
            {
                var result = await confirmStage.ConfirmAsync(company);
                report.Confirmations.Add(result);
                Console.WriteLine($"    docs={(result.DocumentsFound ? "yes" : "no")}, " +
                                  $"growth={(result.LongTermGrowthClaimed ? "yes" : "no")}, " +
                                  $"confidence={result.Confidence}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    failed: {ex.Message}");
                report.Confirmations.Add(new ConfirmationResult
                {
                    Company = company,
                    Verdict = $"Error: {ex.Message}"
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
        if (cfg.UseOllama)
            Console.WriteLine($"  LLM provider    : Ollama (local) — {cfg.Ollama.GenerationModel} @ {cfg.Ollama.BaseUrl}");
        else
            Console.WriteLine($"  LLM provider    : Gemini — key {(cfg.HasGeminiKey ? "configured" : "MISSING (stub mode)")}");
        Console.WriteLine($"  PDF source      : Screener.in (free)");
        Console.WriteLine();
    }
}
