using System.Net.Http.Headers;
using Pgvector;
using TrendsTracker.Config;
using TrendsTracker.Data;
using TrendsTracker.Services;

namespace TrendsTracker.SearchApp;

/// <summary>
/// Ad-hoc semantic search over stored concall transcripts (pgvector). Ask ANY
/// question — not just the fixed growth check — across one company or the whole
/// universe, using embeddings already stored during analysis. No re-downloading.
///
/// Usage:
///   # Everything AIMTRON said about growth, grouped by concall date:
///   dotnet run --project src/TrendsTracker.Search -- --stock AIMTRON growth outlook guidance
///
///   # Which companies discuss a theme (across all stored transcripts):
///   dotnet run --project src/TrendsTracker.Search -- china plus one import substitution
///
///   # Add an LLM narrative over the retrieved passages:
///   dotnet run --project src/TrendsTracker.Search -- --stock AIMTRON --summarize margins
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var stock = GetValue(args, "--stock");
        var summarize = HasFlag(args, "--summarize");
        var topK = GetInt(args, "--k") ?? 12;

        // Everything that isn't a flag/flag-value is the query text.
        var query = string.Join(' ', PositionalArgs(args));
        if (string.IsNullOrWhiteSpace(query))
        {
            Console.Error.WriteLine("Usage: TrendsTracker.Search [--stock SYMBOL] [--summarize] [--k N] <query text>");
            Console.Error.WriteLine("  e.g. --stock AIMTRON growth guidance");
            Console.Error.WriteLine("       china plus one import substitution");
            return 1;
        }

        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        AppConfig cfg;
        try { cfg = AppConfig.Load(configPath); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to load config: {ex.Message}"); return 1; }

        using var http = BuildHttpClient();
        var llm = new OllamaClient(http, cfg.Ollama);
        TranscriptStore store;
        try { store = DataFactory.CreateTranscriptStore(); }
        catch (Exception ex) { Console.Error.WriteLine($"DB unavailable: {ex.Message}"); return 1; }

        Console.WriteLine($"Query   : \"{query}\"");
        Console.WriteLine(stock is null ? "Scope   : all companies" : $"Scope   : {stock}");
        Console.WriteLine();

        // Embed the query with the SAME model used to embed the transcripts.
        float[] qEmb;
        try { qEmb = await llm.EmbedAsync(query); }
        catch (Exception ex) { Console.Error.WriteLine($"Embedding failed: {ex.Message}"); return 1; }
        var qVec = new Vector(qEmb);

        var results = stock is null
            ? await store.SearchAcrossCompaniesAsync(qVec, topK)
            : await store.SearchByCompanyAsync(stock, qVec, topK);

        if (results.Count == 0)
        {
            Console.WriteLine("No matching passages. (Has this stock been analyzed yet, to store its transcripts?)");
            return 0;
        }

        if (stock is null) PrintCrossCompany(results);
        else PrintByCompany(stock, results);

        if (summarize)
        {
            Console.WriteLine("\n--- Summary ---");
            var context = string.Join("\n\n", results.Select((r, i) => $"[{i + 1}] ({r.Stock} {r.TranscriptDate}) {r.ChunkText}"));
            var prompt =
                $"Based ONLY on these transcript excerpts, summarise what management says about \"{query}\". " +
                $"Be concise and cite the quarter in brackets.\n\n{context}";
            try { Console.WriteLine((await llm.GenerateTextAsync(prompt)).Trim()); }
            catch (Exception ex) { Console.WriteLine($"(summary failed: {ex.Message})"); }
        }

        return 0;
    }

    // One company: group passages by concall date (newest-ish first as returned).
    private static void PrintByCompany(string stock, List<ChunkSearchResult> results)
    {
        foreach (var group in results.GroupBy(r => r.TranscriptDate))
        {
            Console.WriteLine($"=== {stock}  [{(string.IsNullOrWhiteSpace(group.Key) ? "undated" : group.Key)}] ===");
            foreach (var r in group)
                Console.WriteLine($"  • ({r.Similarity:0.00}) {Trim(r.ChunkText)}");
            Console.WriteLine();
        }
    }

    // Cross-company: group by stock so you see which companies talk about the topic.
    private static void PrintCrossCompany(List<ChunkSearchResult> results)
    {
        foreach (var group in results.GroupBy(r => r.Stock).OrderByDescending(g => g.Max(x => x.Similarity)))
        {
            Console.WriteLine($"=== {group.Key} ===");
            foreach (var r in group)
                Console.WriteLine($"  • ({r.Similarity:0.00}) [{r.TranscriptDate}] {Trim(r.ChunkText)}");
            Console.WriteLine();
        }
    }

    private static string Trim(string s)
    {
        s = s.Replace("\n", " ").Replace("\r", " ").Trim();
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    private static bool HasFlag(string[] a, string f) => a.Any(x => x.Equals(f, StringComparison.OrdinalIgnoreCase));
    private static string? GetValue(string[] a, string k)
    {
        var i = Array.FindIndex(a, x => x.Equals(k, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
    private static int? GetInt(string[] a, string k) => int.TryParse(GetValue(a, k), out var v) ? v : null;

    // Positional = args that aren't a flag and aren't the value consumed by --stock/--k.
    private static IEnumerable<string> PositionalArgs(string[] a)
    {
        var consumed = new HashSet<int>();
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i].Equals("--stock", StringComparison.OrdinalIgnoreCase) ||
                a[i].Equals("--k", StringComparison.OrdinalIgnoreCase))
            { consumed.Add(i); consumed.Add(i + 1); }
            else if (a[i].Equals("--summarize", StringComparison.OrdinalIgnoreCase))
            { consumed.Add(i); }
        }
        for (var i = 0; i < a.Length; i++)
            if (!consumed.Contains(i) && !a[i].StartsWith("--")) yield return a[i];
    }

    private static HttpClient BuildHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return http;
    }
}
