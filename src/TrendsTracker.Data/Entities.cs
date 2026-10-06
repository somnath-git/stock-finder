namespace TrendsTracker.Data;

/// <summary>
/// A saved growth-verification result for one stock. This is what the API serves
/// and the UI shows, and it powers the "have we already analyzed this?" check.
/// </summary>
public sealed class CompanyAnalysis
{
    public int Id { get; set; }

    /// <summary>Stock symbol / code, e.g. "HSCL". Unique lookup key.</summary>
    public string Stock { get; set; } = "";

    /// <summary>Full company name, e.g. "Himadri Speciality Chemical".</summary>
    public string Name { get; set; } = "";

    /// <summary>True if the company plausibly clears the "double in 3-5 years" bar.</summary>
    public bool Verdict { get; set; }

    /// <summary>0-100 confidence in the verdict.</summary>
    public int Confidence { get; set; }

    /// <summary>Market cap in ₹ crore (null if unknown).</summary>
    public decimal? MarketCapCr { get; set; }

    /// <summary>What kind of signal drove the verdict: revenue / profit / thematic / mixed.</summary>
    public string SignalType { get; set; } = "";

    public string GrowthComment { get; set; } = "";
    public string MarginComment { get; set; } = "";
    public string ExpansionComment { get; set; } = "";

    /// <summary>Short management quotes supporting the verdict (JSON column).</summary>
    public List<string> EvidenceQuotes { get; set; } = new();

    /// <summary>Where the info came from — PDF links and any other sources (JSON column).</summary>
    public List<string> Sources { get; set; } = new();

    public DateTimeOffset AnalyzedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One embedded chunk of a company's concall transcript, stored permanently in
/// pgvector. This turns the tool into a searchable knowledge base: once stored, you
/// can ask any question ("what did X say about margins / China+1 / debt?") across one
/// company or the whole universe, without re-downloading or re-embedding.
/// </summary>
public sealed class TranscriptChunk
{
    public int Id { get; set; }

    /// <summary>Stock symbol, e.g. "AIMTRON".</summary>
    public string Stock { get; set; } = "";

    /// <summary>The concall this chunk came from, e.g. "Aug 2026" (as shown on Screener).</summary>
    public string TranscriptDate { get; set; } = "";

    /// <summary>Source transcript PDF URL.</summary>
    public string Source { get; set; } = "";

    /// <summary>The chunk text.</summary>
    public string ChunkText { get; set; } = "";

    /// <summary>Embedding vector (nomic-embed-text = 768 dims).</summary>
    public Pgvector.Vector? Embedding { get; set; }
}

/// <summary>A news item collected from the RSS feeds (the "news feed" the UI shows).</summary>
public sealed class NewsItem
{
    public int Id { get; set; }

    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
    public string Summary { get; set; } = "";
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}
