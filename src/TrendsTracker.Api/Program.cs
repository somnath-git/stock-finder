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

    float[] emb;
    try { emb = await llm.EmbedAsync(q); }
    catch (Exception ex) { return Results.Problem($"Embedding failed: {ex.Message}"); }

    var vec = new Vector(emb);
    var topK = k ?? 10;
    var hits = string.IsNullOrWhiteSpace(stock)
        ? await store.SearchAcrossCompaniesAsync(vec, topK)
        : await store.SearchByCompanyAsync(stock, vec, topK);

    if (hits.Count == 0)
        return Results.Ok(new { answer = "No stored transcript passages matched. Analyze the stock first.", passages = Array.Empty<object>() });

    var context = string.Join("\n\n", hits.Select((h, i) => $"[{i + 1}] ({h.Stock} {h.TranscriptDate}) {h.ChunkText}"));
    var prompt =
        $"Answer the user's question using ONLY the transcript excerpts below. " +
        $"Be direct and concise — AT MOST 5 lines. If the excerpts give a specific number or figure, state it. " +
        $"If the answer isn't in the excerpts, say so.\n\n" +
        $"QUESTION: {q}\n\nEXCERPTS:\n{context}";

    string answer;
    try { answer = (await llm.GenerateTextAsync(prompt, jsonMode: false)).Trim(); }
    catch (Exception ex) { return Results.Problem($"Answer generation failed: {ex.Message}"); }

    return Results.Ok(new
    {
        answer,
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
