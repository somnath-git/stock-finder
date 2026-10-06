using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace TrendsTracker.Data;

/// <summary>
/// Read/write transcript chunks in the pgvector-backed store.
/// Once embedded chunks are stored, any thesis can be searched across all companies
/// (or one company across all its transcripts) without re-downloading or re-embedding.
/// </summary>
public sealed class TranscriptStore
{
    private readonly AppDbContext _db;

    public TranscriptStore(AppDbContext db) => _db = db;

    /// <summary>Does this stock already have chunks stored? Used to skip re-embedding.</summary>
    public Task<bool> HasChunksAsync(string stock, CancellationToken ct = default) =>
        _db.TranscriptChunks.AnyAsync(c => c.Stock == stock.ToUpper(), ct);

    /// <summary>Delete existing chunks for a stock (used with --force to re-embed fresh).</summary>
    public async Task DeleteChunksAsync(string stock, CancellationToken ct = default)
    {
        var old = await _db.TranscriptChunks.Where(c => c.Stock == stock.ToUpper()).ToListAsync(ct);
        _db.TranscriptChunks.RemoveRange(old);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Persist embedded chunks for a stock.</summary>
    public async Task SaveChunksAsync(string stock, IEnumerable<Models.EmbeddedChunk> chunks, CancellationToken ct = default)
    {
        var upper = stock.Trim().ToUpperInvariant();
        foreach (var ch in chunks)
        {
            _db.TranscriptChunks.Add(new TranscriptChunk
            {
                Stock = upper,
                TranscriptDate = ch.TranscriptDate,
                Source = ch.Source,
                ChunkText = ch.Text,
                Embedding = new Vector(ch.Embedding)
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Semantic search WITHIN ONE COMPANY's transcripts. Returns the top-K passages
    /// most similar to the query vector, grouped by transcript date (newest first).
    /// </summary>
    public async Task<List<ChunkSearchResult>> SearchByCompanyAsync(
        string stock, Vector queryEmbedding, int k = 10, CancellationToken ct = default)
    {
        var upper = stock.Trim().ToUpperInvariant();
        return await _db.TranscriptChunks
            .Where(c => c.Stock == upper)
            .OrderBy(c => c.Embedding!.CosineDistance(queryEmbedding))
            .Take(k)
            .Select(c => new ChunkSearchResult
            {
                Stock = c.Stock,
                TranscriptDate = c.TranscriptDate,
                ChunkText = c.ChunkText,
                Source = c.Source,
                Distance = c.Embedding!.CosineDistance(queryEmbedding)
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// Semantic search ACROSS ALL COMPANIES' transcripts. Returns the top-K passages
    /// most similar to the query vector from any stock.
    /// </summary>
    public async Task<List<ChunkSearchResult>> SearchAcrossCompaniesAsync(
        Vector queryEmbedding, int k = 10, CancellationToken ct = default)
    {
        return await _db.TranscriptChunks
            .OrderBy(c => c.Embedding!.CosineDistance(queryEmbedding))
            .Take(k)
            .Select(c => new ChunkSearchResult
            {
                Stock = c.Stock,
                TranscriptDate = c.TranscriptDate,
                ChunkText = c.ChunkText,
                Source = c.Source,
                Distance = c.Embedding!.CosineDistance(queryEmbedding)
            })
            .ToListAsync(ct);
    }
}

/// <summary>A result from a pgvector similarity search over transcript chunks.</summary>
public sealed class ChunkSearchResult
{
    public string Stock { get; set; } = "";
    public string TranscriptDate { get; set; } = "";
    public string ChunkText { get; set; } = "";
    public string Source { get; set; } = "";

    /// <summary>Cosine distance (0 = identical, 2 = opposite). Lower = more similar.</summary>
    public double Distance { get; set; }

    /// <summary>Similarity as a 0-1 score (1 = identical). Easier to reason about.</summary>
    public double Similarity => 1.0 - Distance;
}
