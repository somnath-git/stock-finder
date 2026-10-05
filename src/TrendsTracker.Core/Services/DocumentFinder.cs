using System.Text.RegularExpressions;
using HtmlAgilityPack;
using TrendsTracker.Models;

namespace TrendsTracker.Services;

/// <summary>
/// Finds publicly-available concall transcript / investor presentation PDFs for a
/// company — WITHOUT any paid search API (Google's Custom Search JSON API is now
/// closed to new customers and returns 403).
///
/// Strategy: Screener.in aggregates each listed company's filings. Its company page
/// (screener.in/company/&lt;SYMBOL&gt;/) has a "Documents" area linking concall
/// transcripts and investor presentations — the PDFs usually live on BSE
/// (bseindia.com) or the company's IR site. We fetch that page and extract the PDF
/// links. All public, no key required.
/// </summary>
/// <summary>What we scrape from a Screener company page in one fetch.</summary>
public sealed class ScreenerResult
{
    public List<string> PdfUrls { get; set; } = new();

    /// <summary>Market capitalisation in ₹ crore, or null if we couldn't read it.</summary>
    public decimal? MarketCapCr { get; set; }
}

public sealed class DocumentFinder
{
    private readonly HttpFetcher _fetcher;

    public DocumentFinder(HttpFetcher fetcher) => _fetcher = fetcher;

    /// <summary>No API key needed — always "configured".</summary>
    public bool IsConfigured => true;

    /// <summary>
    /// Fetches the Screener company page ONCE and pulls both the document PDF links
    /// and the market cap. Prefer this over the two single-purpose methods so we
    /// don't hit Screener twice per company.
    /// </summary>
    public async Task<ScreenerResult> FetchAsync(Company company, int maxPdfs = 3, CancellationToken ct = default)
    {
        var result = new ScreenerResult();

        foreach (var pageUrl in CandidateScreenerUrls(company))
        {
            var html = await _fetcher.GetHtmlAsync(pageUrl, ct);
            if (string.IsNullOrWhiteSpace(html)) continue;

            // Market cap: take it from the first page that has it.
            result.MarketCapCr ??= ExtractMarketCapCr(html);

            foreach (var u in ExtractDocumentPdfLinks(html))
            {
                if (!result.PdfUrls.Contains(u)) result.PdfUrls.Add(u);
                if (result.PdfUrls.Count >= maxPdfs) break;
            }

            // Stop once we have docs AND a market cap (or we've tried this page).
            if (result.PdfUrls.Count > 0) break;
        }

        if (result.PdfUrls.Count == 0)
            Log.Step($"      No concall/investor PDFs found on Screener for {company.Name}.");

        return result;
    }

    /// <summary>Back-compat: just the PDF links.</summary>
    public async Task<List<string>> FindPdfUrlsAsync(Company company, int max = 3, CancellationToken ct = default)
        => (await FetchAsync(company, max, ct)).PdfUrls;

    /// <summary>Just the market cap in ₹ crore (null if unknown).</summary>
    public async Task<decimal?> GetMarketCapCrAsync(Company company, CancellationToken ct = default)
        => (await FetchAsync(company, 1, ct)).MarketCapCr;

    /// <summary>
    /// Reads "Market Cap ₹ 3,986 Cr." from the Screener ratios list. The markup is:
    ///   &lt;span class="name"&gt; Market Cap &lt;/span&gt;
    ///   &lt;span class="nowrap value"&gt; ₹ &lt;span class="number"&gt;3,986&lt;/span&gt; Cr. &lt;/span&gt;
    /// </summary>
    private static decimal? ExtractMarketCapCr(string html)
    {
        // Find "Market Cap", then the first number span after it.
        var m = Regex.Match(html,
            @"Market\s*Cap.*?<span[^>]*class=""number""[^>]*>\s*([\d,]+(?:\.\d+)?)\s*</span>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;

        var raw = m.Groups[1].Value.Replace(",", "");
        return decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// Screener uses the NSE/BSE symbol in its URL. We try the configured ticker
    /// (both consolidated and standalone variants); if absent, derive a guess from
    /// the company name as a last resort.
    /// </summary>
    private static IEnumerable<string> CandidateScreenerUrls(Company company)
    {
        var symbols = new List<string>();
        if (!string.IsNullOrWhiteSpace(company.Stock))
        {
            var t = company.Stock
                .Replace("NSE:", "").Replace("BSE:", "")
                .Trim().ToUpperInvariant();
            // Strip Yahoo-style suffixes (.NS / .BO) that models sometimes add.
            if (t.EndsWith(".NS")) t = t[..^3];
            if (t.EndsWith(".BO")) t = t[..^3];
            t = t.Trim();
            if (t.Length > 0) symbols.Add(t);
        }

        foreach (var sym in symbols)
        {
            // consolidated first, then standalone
            yield return $"https://www.screener.in/company/{Uri.EscapeDataString(sym)}/consolidated/";
            yield return $"https://www.screener.in/company/{Uri.EscapeDataString(sym)}/";
        }
    }

    /// <summary>
    /// Pulls .pdf links from the page, preferring the concall / annual-report /
    /// investor-presentation document links Screener exposes.
    /// </summary>
    private static List<string> ExtractDocumentPdfLinks(string html)
    {
        var results = new List<string>();
        HtmlDocument doc;
        try
        {
            doc = new HtmlDocument();
            doc.LoadHtml(html);
        }
        catch
        {
            return results;
        }

        var anchors = doc.DocumentNode.SelectNodes("//a[@href]");
        if (anchors == null) return results;

        // Keywords that mark the kind of document we care about.
        string[] wanted = { "concall", "transcript", "presentation", "investor", "ppt", "annual report", "earnings" };

        foreach (var a in anchors)
        {
            var href = a.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(href)) continue;

            var text = (a.InnerText ?? "").ToLowerInvariant();
            var hrefLower = href.ToLowerInvariant();

            var looksPdf = hrefLower.Contains(".pdf") || hrefLower.Contains("bseindia.com") || hrefLower.Contains("nseindia.com");
            if (!looksPdf) continue;

            // Prefer links whose text/URL mentions a document type; otherwise still
            // keep direct .pdf links as a fallback.
            var relevant = wanted.Any(w => text.Contains(w) || hrefLower.Contains(w)) || hrefLower.Contains(".pdf");
            if (!relevant) continue;

            var abs = MakeAbsolute(href);
            if (!string.IsNullOrWhiteSpace(abs) && !results.Contains(abs))
                results.Add(abs);
        }

        // Rank: explicit concall/transcript/presentation first.
        results = results
            .OrderByDescending(u => Regex.IsMatch(u, "concall|transcript|presentation|investor", RegexOptions.IgnoreCase))
            .ToList();

        return results;
    }

    private static string MakeAbsolute(string href)
    {
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return href;
        if (href.StartsWith("//")) return "https:" + href;
        if (href.StartsWith("/")) return "https://www.screener.in" + href;
        return href;
    }
}
