using System.Text;
using HtmlAgilityPack;

namespace TrendsTracker.Services;

/// <summary>
/// Fetches web pages and extracts readable article text.
/// Uses a browser-like User-Agent and fails gracefully (returns empty on error)
/// so one bad URL never crashes the pipeline.
/// </summary>
public sealed class HttpFetcher
{
    private readonly HttpClient _http;

    public HttpFetcher(HttpClient http) => _http = http;

    /// <summary>Downloads raw bytes (used for PDFs). Returns null on failure.</summary>
    public async Task<byte[]?> GetBytesAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Downloads a page's HTML. Returns empty string on failure.</summary>
    public async Task<string> GetHtmlAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return "";
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Extracts the main readable text from an article page.
    /// Strips scripts/styles/nav and concatenates paragraph text.
    /// </summary>
    public async Task<string> GetArticleTextAsync(string url, int maxChars = 4000, CancellationToken ct = default)
    {
        var html = await GetHtmlAsync(url, ct);
        if (string.IsNullOrWhiteSpace(html)) return "";

        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Remove noise.
            var noise = doc.DocumentNode.SelectNodes("//script|//style|//nav|//footer|//header|//aside|//form");
            if (noise != null)
                foreach (var n in noise)
                    n.Remove();

            var paragraphs = doc.DocumentNode.SelectNodes("//p");
            if (paragraphs == null) return "";

            var sb = new StringBuilder();
            foreach (var p in paragraphs)
            {
                var text = HtmlEntity.DeEntitize(p.InnerText)?.Trim();
                if (string.IsNullOrWhiteSpace(text) || text.Length < 40) continue;
                sb.AppendLine(text);
                if (sb.Length >= maxChars) break;
            }

            var result = sb.ToString().Trim();
            return result.Length > maxChars ? result[..maxChars] : result;
        }
        catch
        {
            return "";
        }
    }
}
