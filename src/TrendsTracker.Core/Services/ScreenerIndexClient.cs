using System.Text.RegularExpressions;

namespace TrendsTracker.Services;

/// <summary>
/// Scrapes the constituent stock symbols from a Screener.in "index" / screen page
/// (e.g. SMALLCA250 = Smallcap 250, NFMICRO250 = Nifty Microcap 250). These pages
/// list member companies as /company/&lt;SYMBOL&gt;/ links, paginated via ?page=N.
///
/// Public pages return the first page(s) without login; deeper pages may require
/// authentication, so we take whatever is publicly available and stop when a page
/// yields no new symbols.
/// </summary>
public sealed class ScreenerIndexClient
{
    private readonly HttpFetcher _fetcher;

    public ScreenerIndexClient(HttpFetcher fetcher) => _fetcher = fetcher;

    // /company/SYMBOL/ — symbols are alphanumeric but we drop purely-numeric ids
    // (those are Screener internal company ids, not NSE tickers).
    private static readonly Regex CompanyLinkRx =
        new(@"/company/([A-Za-z][A-Za-z0-9&\-]*)/", RegexOptions.Compiled);

    /// <summary>
    /// Returns the distinct constituent symbols of an index, reading up to
    /// <paramref name="maxPages"/> pages. Stops early when a page adds nothing new.
    /// </summary>
    public async Task<List<string>> GetConstituentsAsync(
        string indexCode, int maxPages = 10, CancellationToken ct = default)
    {
        var symbols = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= maxPages; page++)
        {
            var url = $"https://www.screener.in/company/{Uri.EscapeDataString(indexCode)}/?page={page}";
            var html = await _fetcher.GetHtmlAsync(url, ct);
            if (string.IsNullOrWhiteSpace(html)) break;

            var added = 0;
            foreach (Match m in CompanyLinkRx.Matches(html))
            {
                var sym = m.Groups[1].Value.Trim().ToUpperInvariant();

                // Skip the index itself and obvious non-tickers.
                if (sym.Equals(indexCode, StringComparison.OrdinalIgnoreCase)) continue;
                if (sym.Length < 2) continue;

                if (seen.Add(sym))
                {
                    symbols.Add(sym);
                    added++;
                }
            }

            Log.Step($"    {indexCode} page {page}: +{added} (total {symbols.Count})");

            // No new symbols on this page -> we've hit the public limit / end.
            if (added == 0) break;
        }

        return symbols;
    }
}
