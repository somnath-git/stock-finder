using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TrendsTracker.Config;
using TrendsTracker.Models;
using TrendsTracker.Rag;
using TrendsTracker.Services;

namespace TrendsTracker.Stages;

/// <summary>
/// STAGE 4 — RAG CONFIRMATION.
///
/// This is the full Retrieval-Augmented Generation loop:
///   1. RETRIEVE SOURCES : find the company's concall / investor PDFs.
///   2. INGEST           : extract text, chunk it, embed each chunk, store vectors.
///   3. RETRIEVE         : embed a growth-focused query, pull the top-K chunks.
///   4. AUGMENT + GENERATE: feed those chunks to Gemini and ask whether management
///                          itself guides for strong, long-term growth — with quotes.
///
/// The output is grounded in the company's own words, not the model's priors.
/// </summary>
public sealed class CompanyConfirmationStage
{
    private readonly AppConfig _cfg;
    private readonly ILlmClient _llm;
    private readonly DocumentFinder _finder;
    private readonly HttpFetcher _fetcher;

    // We retrieve against several focused queries (not one vague one) so each of the
    // three things the user cares about — revenue growth, margins, expansion — gets
    // its own best-matching passages pulled from the document.
    private static readonly string[] RetrievalQueries =
    {
        "revenue growth guidance outlook multi-year future revenue target doubling",
        "profit PAT net profit earnings growth doubling bottom line guidance",
        "margin guidance EBITDA operating margin profitability outlook improvement",
        "capacity expansion capex new plant greenfield brownfield order book pipeline demand"
    };

    // Chunks mentioning these terms are boosted — they're where guidance lives.
    // Includes PROFIT terms, since profit doubling counts as much as revenue doubling.
    private static readonly string[] GuidanceKeywords =
    {
        "guidance", "guide", "outlook", "expect", "target", "double", "triple", "cagr",
        "capex", "capacity", "expansion", "margin", "ebitda", "order book", "pipeline",
        "demand", "growth", "revenue", "crore", "billion",
        "profit", "pat", "net profit", "earnings", "bottom line", "profitability"
    };

    public CompanyConfirmationStage(
        AppConfig cfg, ILlmClient llm, DocumentFinder finder, HttpFetcher fetcher)
    {
        _cfg = cfg;
        _llm = llm;
        _finder = finder;
        _fetcher = fetcher;
    }

    public async Task<ConfirmationResult> ConfirmAsync(Company company, CancellationToken ct = default)
    {
        var result = new ConfirmationResult { Company = company };

        // 1. Fetch Screener page ONCE: latest concall transcripts (url+date) + market cap.
        var screener = await _finder.FetchAsync(company, maxTranscripts: 4, ct: ct);
        var transcripts = screener.Transcripts;

        // Record market cap and whether it's above the configured limit.
        result.MarketCapCr = screener.MarketCapCr;
        company.MarketCapCr = screener.MarketCapCr;
        result.AboveMarketCapLimit =
            screener.MarketCapCr is decimal mc && mc > _cfg.MaxMarketCapCr;

        if (transcripts.Count == 0)
        {
            result.DocumentsFound = false;
            result.Verdict = "No concall transcripts found on Screener.";
            return result;
        }

        // 2. Ingest -> chunk -> embed -> store. Keep each chunk's source + concall date.
        var store = new InProcVectorStore();

        var pending = new List<(string Chunk, string Source, string Date)>();
        foreach (var t in transcripts)
        {
            if (pending.Count >= _cfg.Rag.MaxChunksPerCompany) break;

            var bytes = await _fetcher.GetBytesAsync(t.Url, ct);
            if (bytes is null) continue;

            var text = PdfExtractor.ExtractText(bytes);
            if (string.IsNullOrWhiteSpace(text)) continue;

            result.DocumentSources.Add(t.Url);
            var chunks = TextChunker.Chunk(text, _cfg.Rag.ChunkSize, _cfg.Rag.ChunkOverlap);

            foreach (var chunk in chunks)
            {
                if (pending.Count >= _cfg.Rag.MaxChunksPerCompany) break;
                pending.Add((chunk, t.Url, t.Date));
            }
        }

        if (pending.Count == 0)
        {
            result.DocumentsFound = false;
            result.Verdict = "Documents found but could not be read.";
            return result;
        }

        // Embed ALL chunks in ONE batch request (fast + one throttle slot).
        try
        {
            var vectors = await _llm.EmbedBatchAsync(pending.Select(p => p.Chunk).ToList(), ct);
            for (var k = 0; k < vectors.Count && k < pending.Count; k++)
            {
                store.Add(pending[k].Chunk, vectors[k], pending[k].Source);
                result.EmbeddedChunks.Add(new EmbeddedChunk
                {
                    Text = pending[k].Chunk,
                    Embedding = vectors[k],
                    Source = pending[k].Source,
                    TranscriptDate = pending[k].Date
                });
            }
        }
        catch (Exception ex)
        {
            result.DocumentsFound = false;
            result.Verdict = $"Documents found but embedding failed: {ex.Message}";
            return result;
        }

        if (store.Count == 0)
        {
            result.DocumentsFound = false;
            result.Verdict = "Documents found but could not be embedded.";
            return result;
        }

        result.DocumentsFound = true;

        // 3. MULTI-QUERY RETRIEVAL: run each focused query (growth / margin /
        //    expansion), merge the hits, apply a keyword boost, and keep the best.
        var hits = await RetrieveRelevantAsync(store, ct);

        // 4. Augment the prompt with retrieved context and ask for a grounded verdict.
        var context = BuildContext(hits);
        var mathNote = BuildCagrMathNote(hits);
        var prompt = BuildPrompt(company, context, mathNote);
        var response = await _llm.GenerateTextAsync(prompt, ct);

        var dto = JsonHelper.Deserialize<VerdictDto>(response);
        if (dto is null)
        {
            result.Verdict = "Could not parse the confirmation verdict.";
            result.Confidence = 0;
            return result;
        }

        result.Confidence = Math.Max(0, Math.Min(100, dto.Confidence));
        result.LongTermGrowthClaimed = dto.LongTermGrowthClaimed;
        result.Verdict = dto.Verdict ?? "";
        result.GrowthSignal = dto.GrowthSignal ?? "";
        result.MarginSignal = dto.MarginSignal ?? "";
        result.ExpansionSignal = dto.ExpansionSignal ?? "";
        result.EvidenceQuotes = dto.Evidence ?? new();
        return result;
    }

    // Regex tells for the strongest growth signals (worth far more than a keyword).
    private static readonly Regex CagrRx = new(@"\b\d{1,3}\s*[-–to]{0,3}\s*\d{0,3}\s*%?\s*(cagr|yoy|y-o-y|growth)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PercentRx = new(@"\b\d{1,3}(\.\d+)?\s*%", RegexOptions.Compiled);
    private static readonly Regex MultipleRx = new(@"\b\d(\.\d+)?\s*x\b|\bdoubl|\btripl|\bmulti[- ]?fold", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// HYBRID retrieval. Pure embedding similarity sometimes ranks noise (e.g. an
    /// executive bio) above the one slide that says "40-50% CAGR for 3-5 years".
    /// So we score EVERY chunk with:
    ///   semantic  = best cosine similarity across the focused queries, and
    ///   lexical   = a strong growth-signal score (regex for %/CAGR/x + keywords),
    /// then rank by their blend. This guarantees a chunk packed with growth numbers
    /// surfaces even if its embedding similarity is middling.
    /// </summary>
    private async Task<List<(InProcVectorStore.Entry Entry, double Score)>> RetrieveRelevantAsync(
        InProcVectorStore store, CancellationToken ct)
    {
        // 1. Best semantic similarity per chunk across all focused queries.
        var sim = new Dictionary<string, double>();
        foreach (var query in RetrievalQueries)
        {
            var qEmb = await _llm.EmbedAsync(query, ct);
            // Pull a wide net so good-but-not-top chunks remain candidates.
            foreach (var (entry, s) in store.Search(qEmb, store.Count))
            {
                if (!sim.TryGetValue(entry.Text, out var cur) || s > cur)
                    sim[entry.Text] = s;
            }
        }

        // 2. Blend semantic + lexical for every chunk, rank, take top-K.
        return store.Entries
            .Select(e =>
            {
                var semantic = sim.TryGetValue(e.Text, out var sc) ? sc : 0.0;
                var lexical = LexicalGrowthScore(e.Text);
                // Weight lexical heavily — exact growth numbers are the whole point.
                var blended = (0.5 * semantic) + lexical;
                return (Entry: e, Score: blended);
            })
            .OrderByDescending(x => x.Score)
            .Take(_cfg.Rag.TopK)
            .ToList();
    }

    // Captures growth rates like "40-50% CAGR", "89% YoY", "25% growth".
    private static readonly Regex GrowthRateRx = new(
        @"(\d{1,3})\s*(?:[-–to]+\s*(\d{1,3}))?\s*%\s*(?:cagr|yoy|y-o-y|growth|per annum|p\.a\.)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Does the arithmetic the LLM keeps getting wrong: if management guides, say,
    /// "40-50% CAGR for 3-5 years", that compounds to 2.7x-7.6x — clearly past the
    /// 2x bar. We detect the growth rates in the retrieved text, compute the
    /// compounded multiple over 3 and 5 years, and hand the LLM a hard fact so it
    /// can't miscalculate. Returns empty if no growth rate is found.
    /// </summary>
    private static string BuildCagrMathNote(List<(InProcVectorStore.Entry Entry, double Score)> hits)
    {
        double maxRate = 0;
        string? example = null;

        foreach (var (entry, _) in hits)
        {
            foreach (Match m in GrowthRateRx.Matches(entry.Text))
            {
                // Use the higher end of a range (e.g. "40-50%" -> 50).
                var lo = double.TryParse(m.Groups[1].Value, out var a) ? a : 0;
                var hi = m.Groups[2].Success && double.TryParse(m.Groups[2].Value, out var b) ? b : lo;
                var rate = Math.Max(lo, hi);
                if (rate > maxRate && rate <= 200) // ignore absurd parses
                {
                    maxRate = rate;
                    example = m.Value.Trim();
                }
            }
        }

        if (maxRate <= 0) return "";

        var r = maxRate / 100.0;
        var x3 = Math.Pow(1 + r, 3);
        var x5 = Math.Pow(1 + r, 5);
        var clears2x = x3 >= 2.0 || x5 >= 2.0;

        return
            $"COMPUTED FACT (trust this math): the excerpts mention a growth rate of about " +
            $"{maxRate:0}% (\"{example}\"). Compounded, that is ~{x3:0.0}x over 3 years and " +
            $"~{x5:0.0}x over 5 years. " +
            (clears2x
                ? "This CLEARS the 'double in 3-5 years' bar (whether the rate is for revenue OR profit), so if this guidance is credible, score HIGH."
                : "This does NOT by itself reach 2x in 3-5 years.");
    }

    /// <summary>
    /// Lexical growth-signal score (0..~1.3). Hard numbers (CAGR, %, 2x, double)
    /// score far higher than soft keywords, because they're what actually proves a
    /// doubling thesis.
    /// </summary>
    private static double LexicalGrowthScore(string text)
    {
        var lower = text.ToLowerInvariant();
        double score = 0;

        if (CagrRx.IsMatch(text)) score += 0.6;               // "40-50% CAGR" / "89% YoY"
        if (MultipleRx.IsMatch(text)) score += 0.4;           // "2x", "double", "multi-fold"
        var pct = PercentRx.Matches(text).Count;
        score += Math.Min(0.3, pct * 0.1);                    // several % figures

        var kw = GuidanceKeywords.Count(k => lower.Contains(k));
        score += Math.Min(0.3, kw * 0.03);                    // soft keyword support

        return score;
    }

    private static string BuildContext(List<(InProcVectorStore.Entry Entry, double Score)> hits)
    {
        var sb = new StringBuilder();
        var i = 1;
        foreach (var (entry, score) in hits)
        {
            sb.AppendLine($"[Passage {i} | similarity {score:F3}]");
            sb.AppendLine(entry.Text);
            sb.AppendLine();
            i++;
        }
        return sb.ToString();
    }

    private static string BuildPrompt(Company company, string context, string mathNote) => $$"""
        You are analysing excerpts from {{company.Name}}'s own concall transcripts
        and/or investor presentations (retrieved below).

        {{mathNote}}

        The investor's bar is HIGH: they want companies whose management is guiding —
        DIRECTLY or INDIRECTLY — that the business could roughly DOUBLE (2x+) within
        3 to 5 years. IMPORTANT: EITHER revenue doubling OR profit (PAT / net profit /
        EBITDA) doubling counts as a PASS — profit doubling is just as valuable as
        revenue doubling. A strong named growth driver (a big new project, segment, or
        order book) with management-stated visibility also supports a pass.

        Assess from the excerpts:
        1. GROWTH — revenue AND/OR profit growth guidance. Explicit ("we aim to double
           profit by FY28", "25%+ revenue CAGR", "PAT to grow 40%") or indirect (large
           order book, strong demand, big new project like a BESS/EV plant).
        2. MARGIN — any EBITDA/operating-margin outlook.
        3. EXPANSION — capacity additions, new plants, capex, acquisitions.

        Use ONLY these excerpts (no outside knowledge). If they only show steady
        single-digit growth or don't address multi-year scale, say so and score LOW.

        Return JSON of exactly this shape:
        {
          "longTermGrowthClaimed": true,
          "confidence": 0,
          "verdict": "1-2 sentences: can the business (revenue OR profit) plausibly 2x+ in 3-5 years, grounded in the excerpts",
          "growthSignal": "revenue AND/OR profit growth guidance found (or 'none found')",
          "marginSignal": "what the excerpts say about margins (or 'none found')",
          "expansionSignal": "capacity/capex/expansion/new project mentioned (or 'none found')",
          "evidence": ["short direct quote 1", "short direct quote 2"]
        }

        "confidence" (0-100) = how strongly the excerpts support a credible path to
        doubling revenue OR profit in 3-5 years.

        RETRIEVED EXCERPTS:
        {{context}}
        """;

    private sealed class VerdictDto
    {
        [JsonPropertyName("longTermGrowthClaimed")] public bool LongTermGrowthClaimed { get; set; }
        [JsonPropertyName("confidence")] public int Confidence { get; set; }
        [JsonPropertyName("verdict")] public string? Verdict { get; set; }
        [JsonPropertyName("growthSignal")] public string? GrowthSignal { get; set; }
        [JsonPropertyName("marginSignal")] public string? MarginSignal { get; set; }
        [JsonPropertyName("expansionSignal")] public string? ExpansionSignal { get; set; }
        [JsonPropertyName("evidence")] public List<string>? Evidence { get; set; }
    }
}
