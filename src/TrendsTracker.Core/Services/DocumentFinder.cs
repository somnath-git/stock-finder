using HtmlAgilityPack;
using System.Text.RegularExpressions;
using TrendsTracker.Models;

namespace TrendsTracker.Services;

/// <summary>What we scrape from a Screener company page in one fetch.</summary>
public sealed class ScreenerResult
{
    /// <summary>Concall transcript PDF URLs, newest first.</summary>
    public List<string> TranscriptUrls { get; set; } = new();

    /// <summary>Market capitalisation in ₹ crore, or null if we couldn't read it.</summary>
    public decimal? MarketCapCr { get; set; }
}

/// <summary>
/// Scrapes a company's recent concall TRANSCRIPTS and market cap from its Screener.in
/// page — no paid search API needed. The company page has a "Concalls" section where
/// each row (newest first) exposes a "Transcript" link (title="Raw Transcript"),
/// alongside PPT / AI Summary / REC buttons. We take only the transcript PDFs — the
/// richest source of management growth guidance — and only the latest few.
/// </summary>
public sealed class DocumentFinder
{
    private readonly HttpFetcher _fetcher;

    public DocumentFinder(HttpFetcher fetcher) => _fetcher = fetcher;

    /// <summary>No API key needed — always "configured".</summary>
    public bool IsConfigured => true;

    /// <summary>
    /// Fetches the Screener company page ONCE and pulls both the latest concall
    /// transcript URLs and the market cap. Prefer this over the single-purpose
    /// methods so we don't hit Screener twice per company.
    /// </summary>
    public async Task<ScreenerResult> FetchAsync(Company company, int maxTranscripts = 4, CancellationToken ct = default)
    {
        var result = new ScreenerResult();

        foreach (var pageUrl in CandidateScreenerUrls(company))
        {
            var html = await _fetcher.GetHtmlAsync(pageUrl, ct);
            if (string.IsNullOrWhiteSpace(html)) continue;

            // Market cap: take it from the first page that has it.
            result.MarketCapCr ??= ExtractMarketCapCr(html);

            var transcripts = ExtractTranscriptLinks(html, maxTranscripts);
            if (transcripts.Count > 0)
            {
                result.TranscriptUrls = transcripts;
                break; // got transcripts; no need to try the standalone page
            }
        }

        if (result.TranscriptUrls.Count == 0)
            Log.Step($"      No concall transcripts found on Screener for {company.Name}.");

        return result;
    }

    /// <summary>Just the latest concall transcript URLs (newest first).</summary>
    public async Task<List<string>> FindTranscriptUrlsAsync(Company company, int max = 4, CancellationToken ct = default)
        => (await FetchAsync(company, max, ct)).TranscriptUrls;

    /// <summary>Just the market cap in ₹ crore (null if unknown).</summary>
    public async Task<decimal?> GetMarketCapCrAsync(Company company, CancellationToken ct = default)
        => (await FetchAsync(company, 1, ct)).MarketCapCr;

    /// <summary>
    /// Extracts the concall TRANSCRIPT PDF links from the Screener "Concalls" section.
    /// Each concall row has an anchor like:
    ///   &lt;a class="button-chip" href="...pdf" title="Raw Transcript"&gt;Transcript&lt;/a&gt;
    /// We match on that marker (NOT PPT / AI Summary / REC / annual reports) and
    /// return them in page order (newest first), capped at <paramref name="max"/>.
    /// </summary>
    private static List<string> ExtractTranscriptLinks(string html, int max)
    {
        var results = new List<string>();
        HtmlDocument doc;
        try { doc = new HtmlDocument(); doc.LoadHtml(html); }
        catch { return results; }

        // Transcript buttons: anchors whose title is "Raw Transcript" (fallback: the
        // link text is exactly "Transcript").
        var anchors = doc.DocumentNode.SelectNodes(
            "//a[@href and (translate(@title,'RAWTNSCIP','rawtnscip')='raw transcript' or normalize-space(.)='Transcript')]");
        if (anchors == null) return results;

        foreach (var a in anchors)
        {
            var href = a.GetAttributeValue("href", "").Trim();
            if (string.IsNullOrWhiteSpace(href)) continue;

            var abs = MakeAbsolute(href);
            if (!string.IsNullOrWhiteSpace(abs) && !results.Contains(abs))
                results.Add(abs);

            if (results.Count >= max) break; // page order = newest first
        }

        return results;
    }

    /// <summary>
    /// Reads "Market Cap ₹ 3,986 Cr." from the Screener ratios list. The markup is:
    ///   &lt;span class="name"&gt; Market Cap &lt;/span&gt;
    ///   &lt;span class="nowrap value"&gt; ₹ &lt;span class="number"&gt;3,986&lt;/span&gt; Cr. &lt;/span&gt;
    /// </summary>
    private static decimal? ExtractMarketCapCr(string html)
    {
        var m = Regex.Match(html,
            @"Market\s*Cap.*?<span[^>]*class=""number""[^>]*>\s*([\d,]+(?:\.\d+)?)\s*</span>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;

        var raw = m.Groups[1].Value.Replace(",", "");
        return decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// Screener uses the NSE/BSE symbol in its URL. We try consolidated then standalone.
    /// </summary>
    private static IEnumerable<string> CandidateScreenerUrls(Company company)
    {
        if (string.IsNullOrWhiteSpace(company.Stock)) yield break;

        var t = company.Stock.Replace("NSE:", "").Replace("BSE:", "").Trim().ToUpperInvariant();
        if (t.EndsWith(".NS")) t = t[..^3];
        if (t.EndsWith(".BO")) t = t[..^3];
        t = t.Trim();
        if (t.Length == 0) yield break;

        yield return $"https://www.screener.in/company/{Uri.EscapeDataString(t)}/consolidated/";
        yield return $"https://www.screener.in/company/{Uri.EscapeDataString(t)}/";
    }

    private static string MakeAbsolute(string href)
    {
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return href;
        if (href.StartsWith("//")) return "https:" + href;
        if (href.StartsWith("/")) return "https://www.screener.in" + href;
        return href;
    }
}
