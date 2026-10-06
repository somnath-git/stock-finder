using TrendsTracker.Config;
using TrendsTracker.Models;
using TrendsTracker.Services;
using TrendsTracker.Stages;

namespace TrendsTracker.Data;

/// <summary>
/// Shared "analyze one company and persist it" pipeline used by every entry point
/// (single-company, index-screen batch, theme batch). Centralises: DB-cache resume,
/// market-cap filter, running the RAG confirmation, mapping to an entity, and saving.
/// Keeps the console apps thin and identical in behaviour.
/// </summary>
public sealed class AnalysisRunner
{
    private readonly AppConfig _cfg;
    private readonly CompanyConfirmationStage _stage;
    private readonly DocumentFinder _finder;
    private readonly AnalysisRepository? _repo;
    private readonly TranscriptStore? _chunks;

    public AnalysisRunner(AppConfig cfg, CompanyConfirmationStage stage, DocumentFinder finder,
        AnalysisRepository? repo, TranscriptStore? chunks = null)
    {
        _cfg = cfg;
        _stage = stage;
        _finder = finder;
        _repo = repo;
        _chunks = chunks;
    }

    public enum Outcome { AnalyzedPass, AnalyzedFail, Cached, SkippedLargeCap, NoDocuments, Error }

    /// <summary>
    /// Processes one company end-to-end. Returns an outcome for progress reporting.
    /// Never throws for per-company failures — logs and returns Error so batch runs continue.
    /// </summary>
    public async Task<Outcome> ProcessAsync(Company company, bool force = false, CancellationToken ct = default)
    {
        var stock = string.IsNullOrWhiteSpace(company.Stock) ? company.Name : company.Stock;

        try
        {
            // 1. Resume: skip if already analyzed (unless forced).
            if (_repo is not null && !force)
            {
                var saved = await _repo.GetByStockAsync(stock, ct);
                if (saved is not null)
                {
                    Log.Step($"  {stock}: cached ({(saved.Verdict ? "PASS" : "fail")}, conf {saved.Confidence})");
                    return Outcome.Cached;
                }
            }

            // 2. Cheap market-cap gate before the expensive RAG.
            var capCr = await _finder.GetMarketCapCrAsync(company, ct);
            company.MarketCapCr = capCr;
            if (capCr is decimal mc && mc > _cfg.MaxMarketCapCr)
            {
                Log.Step($"  {stock}: skip — market cap ₹{mc:N0}cr > ₹{_cfg.MaxMarketCapCr:N0}cr");
                return Outcome.SkippedLargeCap;
            }

            // 3. Full RAG growth confirmation.
            var result = await _stage.ConfirmAsync(company, ct);

            if (!result.DocumentsFound)
            {
                Log.Step($"  {stock}: no documents found");
                if (_repo is not null) await _repo.UpsertAsync(ToEntity(stock, company, result), ct);
                return Outcome.NoDocuments;
            }

            if (_repo is not null) await _repo.UpsertAsync(ToEntity(stock, company, result), ct);

            // Persist embedded chunks to pgvector for later ad-hoc search.
            if (_chunks is not null && result.EmbeddedChunks.Count > 0)
            {
                if (force) await _chunks.DeleteChunksAsync(stock, ct);
                if (!await _chunks.HasChunksAsync(stock, ct))
                    await _chunks.SaveChunksAsync(stock, result.EmbeddedChunks, ct);
            }

            var tag = result.LongTermGrowthClaimed ? "PASS" : "fail";
            Log.Step($"  {stock}: {tag} (conf {result.Confidence}, cap ₹{(capCr?.ToString("N0") ?? "?")}cr)");
            return result.LongTermGrowthClaimed ? Outcome.AnalyzedPass : Outcome.AnalyzedFail;
        }
        catch (Exception ex)
        {
            Log.Warn($"  {stock}: ERROR {ex.Message}");
            return Outcome.Error;
        }
    }

    private static CompanyAnalysis ToEntity(string stock, Company company, ConfirmationResult r) => new()
    {
        Stock = stock,
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

    private static string ClassifySignal(ConfirmationResult r)
    {
        var g = (r.GrowthSignal ?? "").ToLowerInvariant();
        var e = (r.ExpansionSignal ?? "").ToLowerInvariant();
        var parts = new List<string>();
        if (g.Contains("revenue") || g.Contains("cagr") || g.Contains("sales")) parts.Add("revenue");
        if (g.Contains("profit") || g.Contains("pat") || g.Contains("earnings") || g.Contains("ebitda")) parts.Add("profit");
        if (e.Contains("expansion") || e.Contains("capacity") || e.Contains("order") || e.Contains("project")) parts.Add("thematic");
        return parts.Count == 0 ? "unknown" : string.Join("+", parts);
    }
}
