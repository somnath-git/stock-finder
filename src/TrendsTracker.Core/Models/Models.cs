namespace TrendsTracker.Models;

/// <summary>A single news item collected in Stage 1.</summary>
public sealed class Article
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTimeOffset? Published { get; set; }
    public string Summary { get; set; } = "";

    /// <summary>Compact one-line form fed to the LLM in Stage 2.</summary>
    public string ToPromptLine()
    {
        var date = Published?.ToString("yyyy-MM-dd") ?? "n/a";
        var text = string.IsNullOrWhiteSpace(Summary) ? Title : $"{Title} — {Summary}";
        if (text.Length > 300) text = text[..300];
        return $"[{date}] ({Source}) {text}";
    }
}

/// <summary>A trending theme detected in Stage 2.</summary>
public sealed class Theme
{
    public string Name { get; set; } = "";
    public string Rationale { get; set; } = "";

    /// <summary>How often it appeared across the corpus (LLM estimate, 1-100).</summary>
    public int Frequency { get; set; }

    /// <summary>Whether mentions are increasing over the window (LLM estimate, 1-100).</summary>
    public int Momentum { get; set; }

    /// <summary>True if driven by a government policy / announcement.</summary>
    public bool GovernmentDriven { get; set; }

    /// <summary>True if the pattern already played out in the US and is likely to arrive in India.</summary>
    public bool UsToIndiaPattern { get; set; }

    public List<string> Keywords { get; set; } = new();
    public List<string> SourceUrls { get; set; } = new();

    /// <summary>Combined score used for ranking themes.</summary>
    public double Score => (Frequency * 0.5) + (Momentum * 0.5)
                           + (GovernmentDriven ? 10 : 0)
                           + (UsToIndiaPattern ? 10 : 0);
}

/// <summary>A listed company mapped to a theme in Stage 3.</summary>
public sealed class Company
{
    public string Name { get; set; } = "";       // full company name, e.g. "Himadri Speciality Chemical"
    public string Stock { get; set; } = "";       // stock symbol/code, e.g. "HSCL"
    public string Exchange { get; set; } = "";   // NSE / BSE
    public decimal? MarketCapCr { get; set; }    // market cap in ₹ crore (null if unknown)
    public string ThemeName { get; set; } = "";
    public string WhyRelevant { get; set; } = "";
}

/// <summary>Result of the Stage 4 RAG confirmation for one company.</summary>
public sealed class ConfirmationResult
{
    public Company Company { get; set; } = new();

    /// <summary>Does management guide for strong long-term growth? (LLM, 0-100).</summary>
    public int Confidence { get; set; }

    public bool LongTermGrowthClaimed { get; set; }
    public string Verdict { get; set; } = "";            // short summary
    public string GrowthSignal { get; set; } = "";
    public string MarginSignal { get; set; } = "";
    public string ExpansionSignal { get; set; } = "";
    public List<string> EvidenceQuotes { get; set; } = new();
    public List<string> DocumentSources { get; set; } = new();
    public bool DocumentsFound { get; set; }

    /// <summary>Market cap in ₹ crore (null if unknown).</summary>
    public decimal? MarketCapCr { get; set; }

    /// <summary>True if market cap is known and exceeds the configured cap.</summary>
    public bool AboveMarketCapLimit { get; set; }

    /// <summary>
    /// The embedded transcript chunks produced during analysis, so the caller can
    /// persist them to the vector DB for later ad-hoc search. Each carries its text,
    /// embedding, source URL and concall date.
    /// </summary>
    public List<EmbeddedChunk> EmbeddedChunks { get; set; } = new();
}

/// <summary>An embedded transcript chunk, ready to persist to the vector store.</summary>
public sealed class EmbeddedChunk
{
    public string Text { get; set; } = "";
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public string Source { get; set; } = "";
    public string TranscriptDate { get; set; } = "";
}

/// <summary>The final report assembled in Stage 5.</summary>
public sealed class TrendReport
{
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.Now;
    public int ArticlesAnalyzed { get; set; }
    public List<Theme> Themes { get; set; } = new();
    public List<ConfirmationResult> Confirmations { get; set; } = new();
}
