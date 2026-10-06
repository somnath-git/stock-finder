using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using TrendsTracker.Data;

var builder = WebApplication.CreateBuilder(args);

// --- Database ---
// Connection string from TRENDSTRACKER_DB env var (set in Docker / cloud),
// falling back to the local default for dev.
var conn = DataFactory.ResolveConnectionString(
    builder.Configuration.GetConnectionString("Default"));
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(conn, o => o.UseVector()));
builder.Services.AddScoped<AnalysisRepository>();

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
