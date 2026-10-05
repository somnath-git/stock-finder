using Microsoft.EntityFrameworkCore;

namespace TrendsTracker.Data;

/// <summary>
/// Thin data-access layer over <see cref="AppDbContext"/>: look up a saved analysis,
/// upsert a new one, and store news items. Used by the console apps and the API.
/// </summary>
public sealed class AnalysisRepository
{
    private readonly AppDbContext _db;

    public AnalysisRepository(AppDbContext db) => _db = db;

    /// <summary>Creates the database/tables if they don't exist yet.</summary>
    public Task EnsureCreatedAsync(CancellationToken ct = default) => _db.Database.EnsureCreatedAsync(ct);

    /// <summary>Returns the saved analysis for a stock, or null if we haven't analyzed it.</summary>
    public Task<CompanyAnalysis?> GetByStockAsync(string stock, CancellationToken ct = default)
    {
        var key = stock.Trim().ToUpperInvariant();
        return _db.Companies.FirstOrDefaultAsync(c => c.Stock.ToUpper() == key, ct);
    }

    /// <summary>Inserts or updates (by Stock) an analysis result.</summary>
    public async Task UpsertAsync(CompanyAnalysis analysis, CancellationToken ct = default)
    {
        analysis.Stock = analysis.Stock.Trim().ToUpperInvariant();
        var existing = await _db.Companies.FirstOrDefaultAsync(c => c.Stock == analysis.Stock, ct);

        if (existing is null)
        {
            _db.Companies.Add(analysis);
        }
        else
        {
            existing.Name = analysis.Name;
            existing.Verdict = analysis.Verdict;
            existing.Confidence = analysis.Confidence;
            existing.SignalType = analysis.SignalType;
            existing.GrowthComment = analysis.GrowthComment;
            existing.MarginComment = analysis.MarginComment;
            existing.ExpansionComment = analysis.ExpansionComment;
            existing.EvidenceQuotes = analysis.EvidenceQuotes;
            existing.Sources = analysis.Sources;
            existing.AnalyzedAt = analysis.AnalyzedAt;
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>All saved analyses, strongest growth first.</summary>
    public Task<List<CompanyAnalysis>> GetAllAsync(bool verdictOnly = false, CancellationToken ct = default)
    {
        var q = _db.Companies.AsQueryable();
        if (verdictOnly) q = q.Where(c => c.Verdict);
        return q.OrderByDescending(c => c.Confidence).ToListAsync(ct);
    }

    /// <summary>Saves news items, skipping ones whose URL we already stored.</summary>
    public async Task SaveNewsAsync(IEnumerable<NewsItem> items, CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Url)) continue;
            var exists = await _db.NewsItems.AnyAsync(n => n.Url == item.Url, ct);
            if (!exists) _db.NewsItems.Add(item);
        }
        await _db.SaveChangesAsync(ct);
    }

    public Task<List<NewsItem>> GetRecentNewsAsync(int max = 100, CancellationToken ct = default) =>
        _db.NewsItems.OrderByDescending(n => n.FetchedAt).Take(max).ToListAsync(ct);
}
