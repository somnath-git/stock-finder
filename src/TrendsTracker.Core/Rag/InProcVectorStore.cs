namespace TrendsTracker.Rag;

/// <summary>
/// A tiny in-process vector database.
///
/// This is the heart of the "vector DB" learning goal, kept deliberately
/// transparent: documents are stored as (text, embedding) pairs, and a query
/// embedding is matched against them with cosine similarity. A real system
/// (Chroma, Qdrant, pgvector) does the same thing with an ANN index for scale —
/// here we brute-force it, which is perfect for a handful of documents and
/// makes the mechanics obvious.
/// </summary>
public sealed class InProcVectorStore
{
    public sealed record Entry(string Text, float[] Embedding, string Source);

    private readonly List<Entry> _entries = new();

    public int Count => _entries.Count;

    /// <summary>All stored entries (read-only), for hybrid retrieval that scores every chunk.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    public void Add(string text, float[] embedding, string source)
        => _entries.Add(new Entry(text, embedding, source));

    public void Clear() => _entries.Clear();

    /// <summary>Returns the top-K entries most similar to the query embedding.</summary>
    public List<(Entry Entry, double Score)> Search(float[] query, int k)
    {
        return _entries
            .Select(e => (Entry: e, Score: CosineSimilarity(query, e.Embedding)))
            .OrderByDescending(x => x.Score)
            .Take(k)
            .ToList();
    }

    /// <summary>
    /// Cosine similarity = dot(a,b) / (|a| * |b|). Ranges from -1 to 1;
    /// higher means more semantically similar.
    /// </summary>
    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;

        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        if (magA == 0 || magB == 0) return 0;
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}
