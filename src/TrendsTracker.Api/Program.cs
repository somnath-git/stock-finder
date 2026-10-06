using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using TrendsTracker.Config;
using TrendsTracker.Data;
using TrendsTracker.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Database ---
// Connection string from TRENDSTRACKER_DB env var (set in Docker / cloud),
// falling back to the local default for dev.
var conn = DataFactory.ResolveConnectionString(
    builder.Configuration.GetConnectionString("Default"));
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(conn, o => o.UseVector()));
builder.Services.AddScoped<AnalysisRepository>();
builder.Services.AddScoped<TranscriptStore>();

// --- LLM (for embedding search queries) ---
// Load the shared TrendsTracker config (linked as trendstracker.json) for Ollama
// settings; OLLAMA_BASEURL env var overrides the host (used in Docker).
var sharedCfgPath = Path.Combine(AppContext.BaseDirectory, "trendstracker.json");
var appCfg = File.Exists(sharedCfgPath) ? AppConfig.Load(sharedCfgPath) : new AppConfig();
builder.Services.AddSingleton(appCfg.Ollama);
// Named client with a long timeout — a local 7B model on CPU is slow to generate.
builder.Services.AddHttpClient("ollama", c =>
    c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddScoped<ILlmClient>(sp =>
    new OllamaClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("ollama"), appCfg.Ollama));

// --- CORS (so the React UI, served from another origin, can call this API) ---
const string CorsPolicy = "ui";
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors(CorsPolicy);

// Ensure the schema exists on startup (safe if already created).
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("Could not ensure database created: {Message}", ex.Message);
    }
}

// --- Endpoints (READ-ONLY: analysis is done by the local console apps) ---

app.MapGet("/", () => Results.Ok(new { service = "TrendsTracker API", status = "ok" }));

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// All analyzed companies. Filters:
//   ?verdictOnly=true    -> only those that clear the "double in 3-5y" bar
//   ?minConfidence=70    -> only confidence >= 70
app.MapGet("/api/companies", async (
    AnalysisRepository repo, bool? verdictOnly, int? minConfidence) =>
{
    var list = await repo.GetAllAsync(verdictOnly ?? false);
    if (minConfidence is int min)
        list = list.Where(c => c.Confidence >= min).ToList();
    return Results.Ok(list.Select(ToDto));
});

// A single company by stock symbol.
app.MapGet("/api/companies/{stock}", async (AnalysisRepository repo, string stock) =>
{
    var c = await repo.GetByStockAsync(stock);
    return c is null ? Results.NotFound(new { message = $"No analysis for '{stock}'." }) : Results.Ok(ToDto(c));
});

// Recent news feed.
app.MapGet("/api/news", async (AnalysisRepository repo, int? max) =>
{
    var items = await repo.GetRecentNewsAsync(max ?? 100);
    return Results.Ok(items.Select(n => new
    {
        n.Title, n.Url, n.Source, n.Summary,
        publishedAt = n.PublishedAt, fetchedAt = n.FetchedAt
    }));
});

// SEMANTIC SEARCH over stored concall transcripts (pgvector).
//   ?q=<query>          required
//   ?stock=<symbol>     optional — scope to one company (else all companies)
//   ?k=<n>              optional — number of passages (default 12)
app.MapGet("/api/search", async (
    ILlmClient llm, TranscriptStore store, string? q, string? stock, int? k) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { message = "Query 'q' is required." });

    float[] emb;
    try { emb = await llm.EmbedAsync(q); }
    catch (Exception ex) { return Results.Problem($"Embedding failed: {ex.Message}"); }

    var vec = new Vector(emb);
    var topK = k ?? 12;
    var hits = string.IsNullOrWhiteSpace(stock)
        ? await store.SearchAcrossCompaniesAsync(vec, topK)
        : await store.SearchByCompanyAsync(stock, vec, topK);

    return Results.Ok(hits.Select(h => new
    {
        h.Stock,
        transcriptDate = h.TranscriptDate,
        text = h.ChunkText,
        h.Source,
        similarity = Math.Round(h.Similarity, 3)
    }));
});

// ASK: retrieve relevant passages AND have the LLM answer the question concisely
// (RAG's "generation" step). Returns a short answer plus the supporting passages.
app.MapGet("/api/ask", async (
    ILlmClient llm, TranscriptStore store, string? q, string? stock, int? k) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { message = "Query 'q' is required." });

    // --- Step 1: LLM query planning ---
    // Ask the LLM to (a) rewrite the question into transcript-language search
    // terms, and (b) resolve any period mentioned (e.g. "H1 FY26", "Q2 FY26")
    // to one of the quarters actually stored. Grounding on the real stored list
    // lets it map Indian-FY periods to the right concall (H1/Q2 FY26 is reported
    // in the Nov 2025 call, not the newer May 2026 one).
    var quarters = string.IsNullOrWhiteSpace(stock)
        ? new List<string>()
        : await store.GetQuartersAsync(stock);

    string searchQuery = q;
    string? targetQuarter = null;
    if (quarters.Count > 0)
    {
        var quartersList = string.Join(", ", quarters.Select(x => $"\"{x}\""));
        var planPrompt =
            "You plan a search over Indian company concall transcripts. Indian fiscal year " +
            "runs Apr-Mar: Q1=Apr-Jun, Q2=Jul-Sep (H1=Q1+Q2), Q3=Oct-Dec, Q4=Jan-Mar (H2=Q3+Q4). " +
            "Results for a period are discussed in the concall held just AFTER it ends — " +
            "so H1/Q2 FY26 (ending Sep 2025) is covered in the Oct-Nov 2025 concall, and " +
            "H2/Q4 FY26 (ending Mar 2026) is covered in the Apr-May 2026 concall.\n\n" +
            $"Stored concalls for this company: [{quartersList}].\n\n" +
            "Return a JSON object with exactly these keys:\n" +
            "  \"query\": a concise search phrase in transcript wording (strip FY/quarter jargon),\n" +
            "  \"quarter\": the ONE stored concall label from the list above that best covers the " +
            "period the question asks about, or null if the question names no period.\n\n" +
            $"QUESTION: {q}";
        try
        {
            var planJson = await llm.GenerateTextAsync(planPrompt, jsonMode: true);
            using var doc = System.Text.Json.JsonDocument.Parse(planJson);
            if (doc.RootElement.TryGetProperty("query", out var qv) && qv.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var rewritten = qv.GetString();
                if (!string.IsNullOrWhiteSpace(rewritten)) searchQuery = rewritten!;
            }
            if (doc.RootElement.TryGetProperty("quarter", out var qq) && qq.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var picked = qq.GetString();
                // Only honour a quarter the model actually invented from the real list.
                targetQuarter = quarters.FirstOrDefault(x =>
                    string.Equals(x, picked, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { /* planning is best-effort; fall back to the raw question */ }
    }

    // --- Step 2: embed the (rewritten) query and retrieve ---
    float[] emb;
    try { emb = await llm.EmbedAsync(searchQuery); }
    catch (Exception ex) { return Results.Problem($"Embedding failed: {ex.Message}"); }

    var vec = new Vector(emb);
    var topK = k ?? 10;
    var pool = string.IsNullOrWhiteSpace(stock)
        ? await store.SearchAcrossCompaniesAsync(vec, topK)
        : await store.SearchByCompanyAsync(stock, vec, topK);

    if (pool.Count == 0)
        return Results.Ok(new { answer = "No stored transcript passages matched. Analyze the stock first.", quarter = (string?)null, passages = Array.Empty<object>() });

    // --- Step 3: pick the quarter ---
    // If the planner resolved a specific quarter the question targets, use that
    // quarter's passages. Otherwise fall back to the newest quarter that has hits
    // (if the latest concall answers a period-agnostic question, don't dig older).
    List<ChunkSearchResult> hits = targetQuarter is not null
        ? pool
            .Where(h => string.Equals(h.TranscriptDate, targetQuarter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(h => h.Similarity)
            .Take(3)
            .ToList()
        : new List<ChunkSearchResult>();

    // If the targeted quarter had no passages in the pool, fall back to the
    // newest quarter that does (period-agnostic default).
    if (hits.Count == 0)
    {
        hits = pool
            .GroupBy(h => new { h.Stock, h.TranscriptDate })
            .OrderByDescending(g => ParseQuarter(g.Key.TranscriptDate))
            .First()
            .OrderByDescending(h => h.Similarity)
            .Take(3)
            .ToList();
    }

    var quarter = string.IsNullOrWhiteSpace(hits[0].TranscriptDate) ? "undated" : hits[0].TranscriptDate;

    // --- Step 4: LLM answers from the chosen quarter's passages ---
    var context = string.Join("\n\n", hits.Select((h, i) => $"[{i + 1}] ({h.Stock} {h.TranscriptDate}) {h.ChunkText}"));
    var prompt =
        $"Answer the user's question using ONLY the transcript excerpts below, " +
        $"which are all from the {quarter} concall. " +
        $"Be direct and concise — AT MOST 5 lines. If the excerpts give a specific number or figure, state it. " +
        $"If the answer isn't in the excerpts, say so.\n\n" +
        $"QUESTION: {q}\n\nEXCERPTS:\n{context}";

    string answer;
    try { answer = (await llm.GenerateTextAsync(prompt, jsonMode: false)).Trim(); }
    catch (Exception ex) { return Results.Problem($"Answer generation failed: {ex.Message}"); }

    return Results.Ok(new
    {
        answer,
        quarter,
        passages = hits.Select(h => new
        {
            h.Stock,
            transcriptDate = h.TranscriptDate,
            text = h.ChunkText,
            h.Source,
            similarity = Math.Round(h.Similarity, 3)
        })
    });
});

app.Run();

// Parse a Screener quarter label like "May 2026" / "Nov 2025" into a sortable
// date. Empty or unrecognised labels sort oldest (DateTime.MinValue) so a dated
// quarter is always preferred over an undated one.
static DateTime ParseQuarter(string? label)
{
    if (string.IsNullOrWhiteSpace(label)) return DateTime.MinValue;
    var formats = new[] { "MMM yyyy", "MMMM yyyy", "MMM yy" };
    return DateTime.TryParseExact(label.Trim(), formats,
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var dt)
        ? dt
        : DateTime.MinValue;
}

// Shape the entity into a clean JSON DTO for the UI.
static object ToDto(CompanyAnalysis c) => new
{
    c.Stock,
    c.Name,
    c.Verdict,
    c.Confidence,
    c.MarketCapCr,
    c.SignalType,
    c.GrowthComment,
    c.MarginComment,
    c.ExpansionComment,
    c.EvidenceQuotes,
    c.Sources,
    analyzedAt = c.AnalyzedAt
};
