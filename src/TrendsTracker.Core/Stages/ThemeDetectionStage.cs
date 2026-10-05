using System.Text;
using System.Text.Json.Serialization;
using TrendsTracker.Config;
using TrendsTracker.Models;
using TrendsTracker.Services;

namespace TrendsTracker.Stages;

/// <summary>
/// STAGE 2 — THEME DETECTION.
/// Feeds the news corpus to Gemini and asks it to surface recurring,
/// forward-looking themes. We explicitly ask the model to flag:
///   - government-driven themes (policy / budget / scheme announcements)
///   - "US-to-India" patterns (already happened in the US, likely to arrive here)
///   - how frequently and with what momentum each theme appears.
/// Themes are then ranked by a blended score.
/// </summary>
public sealed class ThemeDetectionStage
{
    private readonly AppConfig _cfg;
    private readonly ILlmClient _llm;

    // Cheap heuristics to derive theme flags from text (keeps the LLM schema tiny).
    private static readonly string[] GovWords =
        { "govt", "government", "policy", "budget", "scheme", "pli", "subsid", "regulat", "ministry", "reform", "mission" };
    private static readonly string[] UsWords =
        { "us ", "u.s", "america", "silicon valley", "nasdaq", "global trend", "western" };

    public ThemeDetectionStage(AppConfig cfg, ILlmClient llm)
    {
        _cfg = cfg;
        _llm = llm;
    }

    public async Task<List<Theme>> RunAsync(List<Article> articles, CancellationToken ct = default)
    {
        if (articles.Count == 0) return new();

        var corpus = BuildCorpus(articles);
        var prompt = BuildPrompt(corpus);

        string response;
        try
        {
            response = await _llm.GenerateTextAsync(prompt, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: theme detection call failed: {ex.Message}");
            return new();
        }

        var raw = JsonHelper.DeserializeList<ThemeDto>(response);

        if (raw.Count == 0)
        {
            Console.WriteLine("  WARNING: Could not parse themes from the model response.");
            var preview = response.Length > 1500 ? response[..1500] : response;
            Console.WriteLine("  --- RAW MODEL RESPONSE (debug) ---");
            Console.WriteLine(preview);
            Console.WriteLine("  --- END ---");
            return new();
        }

        var themes = raw.Select(d =>
        {
            var name = (d.Name ?? "").Trim();
            var keywords = d.Keywords ?? new();
            var importance = Clamp(d.Importance == 0 ? 50 : d.Importance);
            // The model only gives us name/rationale/keywords/importance reliably.
            // Derive the policy / US->India flags with a cheap keyword scan so the
            // small model doesn't have to produce nested booleans (which made it fail).
            var hay = (name + " " + d.Rationale + " " + string.Join(" ", keywords)).ToLowerInvariant();
            return new Theme
            {
                Name = name,
                Rationale = (d.Rationale ?? "").Trim(),
                Frequency = importance,
                Momentum = importance,
                GovernmentDriven = GovWords.Any(hay.Contains),
                UsToIndiaPattern = UsWords.Any(hay.Contains),
                Keywords = keywords
            };
        })
        .Where(t => !string.IsNullOrWhiteSpace(t.Name))
        .OrderByDescending(t => t.Score)
        .Take(_cfg.MaxThemes)
        .ToList();

        Console.WriteLine($"  Detected {themes.Count} themes.");
        return themes;
    }

    private string BuildCorpus(List<Article> articles)
    {
        // Headlines only, numbered, with a tight budget. Small local models choke on
        // huge contexts (they emit "{}"), so keep this compact and skimmable.
        var sb = new StringBuilder();
        var budget = 6000;
        var n = 1;
        foreach (var a in articles)
        {
            var title = a.Title.Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;
            if (title.Length > 160) title = title[..160];
            var line = $"{n}. {title}";
            if (sb.Length + line.Length > budget) break;
            sb.AppendLine(line);
            n++;
        }
        return sb.ToString();
    }

    private string BuildPrompt(string corpus) => $$"""
        You are a financial trend analyst for the Indian stock market.
        Below are recent news headlines. Find the {{_cfg.MaxThemes}} biggest
        multi-year investment THEMES (new industries, policy pushes, capex cycles).
        Ignore daily market noise.

        Return a JSON object shaped exactly like this example:
        {"themes":[{"name":"Semiconductors","rationale":"Govt subsidies drive local chip making","importance":80,"keywords":["chips","fab","PLI"]}]}

        Each theme needs: name (string), rationale (one short sentence),
        importance (number 1-100), keywords (array of short strings).

        HEADLINES:
        {{corpus}}
        """;

    private static int Clamp(int v) => Math.Max(0, Math.Min(100, v));

    private sealed class ThemeDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("rationale")] public string? Rationale { get; set; }
        [JsonPropertyName("importance")] public int Importance { get; set; }
        [JsonPropertyName("keywords")] public List<string>? Keywords { get; set; }
    }
}
