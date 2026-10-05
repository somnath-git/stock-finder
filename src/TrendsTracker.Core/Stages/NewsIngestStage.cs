using TrendsTracker.Config;
using TrendsTracker.Models;
using TrendsTracker.Services;

namespace TrendsTracker.Stages;

/// <summary>
/// STAGE 1 — INGEST.
/// Collects news items from the last N days using free sources:
///   - RSS feeds (CNBC, Moneycontrol, ET, Mint, Business Standard, Google News)
///   - Google Programmable Search (if a key is configured)
/// Dedupes by URL/title and returns a clean corpus for theme detection.
/// </summary>
public sealed class NewsIngestStage
{
    private readonly AppConfig _cfg;
    private readonly RssClient _rss;

    public NewsIngestStage(AppConfig cfg, RssClient rss)
    {
        _cfg = cfg;
        _rss = rss;
    }

    public async Task<List<Article>> RunAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-_cfg.LookbackDays);
        var all = new List<Article>();

        Console.WriteLine($"  Reading {_cfg.RssFeeds.Count} RSS feeds (last {_cfg.LookbackDays} days)...");
        foreach (var feed in _cfg.RssFeeds)
        {
            try
            {
                var items = await _rss.ReadFeedAsync(feed, cutoff, _cfg.MaxArticlesPerFeed, ct);
                Console.WriteLine($"    {feed.Name,-40} {items.Count,3} items");
                all.AddRange(items);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    {feed.Name,-40} FAILED ({ex.Message})");
            }
        }

        var deduped = Dedupe(all);
        Console.WriteLine($"  Collected {all.Count} items, {deduped.Count} after dedupe.");
        return deduped;
    }

    private static List<Article> Dedupe(List<Article> articles)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Article>();
        foreach (var a in articles)
        {
            var key = !string.IsNullOrWhiteSpace(a.Url)
                ? a.Url.Trim()
                : a.Title.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (seen.Add(key)) result.Add(a);
        }
        return result;
    }
}
