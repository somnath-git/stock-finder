using System.Xml.Linq;
using TrendsTracker.Config;
using TrendsTracker.Models;

namespace TrendsTracker.Services;

/// <summary>
/// Reads RSS/Atom feeds (the legitimate, free way to get recent headlines
/// from news sites) and returns them as Article objects within a date window.
/// </summary>
public sealed class RssClient
{
    private readonly HttpFetcher _fetcher;

    public RssClient(HttpFetcher fetcher) => _fetcher = fetcher;

    public async Task<List<Article>> ReadFeedAsync(
        RssFeedConfig feed, DateTimeOffset cutoff, int max, CancellationToken ct = default)
    {
        var articles = new List<Article>();
        var xml = await _fetcher.GetHtmlAsync(feed.Url, ct);
        if (string.IsNullOrWhiteSpace(xml)) return articles;

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return articles; }

        // RSS 2.0: //item ; Atom: //entry
        var items = doc.Descendants("item").ToList();
        var isAtom = items.Count == 0;
        if (isAtom)
        {
            XNamespace atom = "http://www.w3.org/2005/Atom";
            items = doc.Descendants(atom + "entry").ToList();
        }

        foreach (var item in items)
        {
            if (articles.Count >= max) break;

            string title, link, summary;
            DateTimeOffset? published;

            if (!isAtom)
            {
                title = (string?)item.Element("title") ?? "";
                link = (string?)item.Element("link") ?? "";
                summary = StripHtml((string?)item.Element("description") ?? "");
                published = ParseDate((string?)item.Element("pubDate"));
            }
            else
            {
                XNamespace atom = "http://www.w3.org/2005/Atom";
                title = (string?)item.Element(atom + "title") ?? "";
                link = item.Elements(atom + "link").FirstOrDefault()?.Attribute("href")?.Value ?? "";
                summary = StripHtml((string?)item.Element(atom + "summary") ?? "");
                published = ParseDate((string?)item.Element(atom + "updated")
                                      ?? (string?)item.Element(atom + "published"));
            }

            // Keep items without a date (some feeds omit it) but drop clearly-old ones.
            if (published.HasValue && published.Value < cutoff) continue;

            articles.Add(new Article
            {
                Title = title.Trim(),
                Url = link.Trim(),
                Source = feed.Name,
                Published = published,
                Summary = summary.Trim()
            });
        }

        return articles;
    }

    private static DateTimeOffset? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(raw, out var dt) ? dt : null;
    }

    private static string StripHtml(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var span = s.AsSpan();
        var sb = new System.Text.StringBuilder(s.Length);
        var inside = false;
        foreach (var c in span)
        {
            if (c == '<') inside = true;
            else if (c == '>') inside = false;
            else if (!inside) sb.Append(c);
        }
        return System.Net.WebUtility.HtmlDecode(sb.ToString());
    }
}
