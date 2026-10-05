using System.Text.Json.Serialization;
using TrendsTracker.Config;
using TrendsTracker.Models;
using TrendsTracker.Services;

namespace TrendsTracker.Stages;

/// <summary>
/// STAGE 3 — THEME -> COMPANIES.
/// For each detected theme, asks Gemini for listed Indian companies (NSE/BSE)
/// that are genuine pure-play or major beneficiaries of the theme.
/// </summary>
public sealed class CompanyMappingStage
{
    private readonly AppConfig _cfg;
    private readonly ILlmClient _llm;

    public CompanyMappingStage(AppConfig cfg, ILlmClient llm)
    {
        _cfg = cfg;
        _llm = llm;
    }

    public async Task<List<Company>> RunAsync(List<Theme> themes, CancellationToken ct = default)
    {
        var companies = new List<Company>();

        foreach (var theme in themes)
        {
            // Pacing is handled centrally by the LLM client's throttle (Gemini only).
            var prompt = BuildPrompt(theme);
            string response;
            try
            {
                response = await _llm.GenerateTextAsync(prompt, ct);
            }
            catch (Exception ex)
            {
                Log.Warn($"    Theme '{theme.Name}': company lookup failed ({ShortError(ex.Message)})");
                continue;
            }

            var dtos = JsonHelper.DeserializeList<CompanyDto>(response);
            var mapped = dtos
                .Where(d => !string.IsNullOrWhiteSpace(d.Name))
                .Take(_cfg.MaxCompaniesPerTheme)
                .Select(d => new Company
                {
                    Name = d.Name!.Trim(),
                    Stock = (d.Ticker ?? "").Trim(),
                    Exchange = (d.Exchange ?? "").Trim(),
                    WhyRelevant = (d.WhyRelevant ?? "").Trim(),
                    ThemeName = theme.Name
                })
                .ToList();

            Log.Step($"    Theme '{theme.Name}': {mapped.Count} companies");
            companies.AddRange(mapped);
        }

        return companies;
    }

    private string BuildPrompt(Theme theme) => $$"""
        Theme: "{{theme.Name}}"
        Context: {{theme.Rationale}}
        Keywords: {{string.Join(", ", theme.Keywords)}}

        List {{_cfg.MaxCompaniesPerTheme}} companies LISTED in India (NSE or BSE) that
        are genuine pure-plays on this theme.

        what are the direct beneficiaries of this theme, and which companies are likely to benefit from it in the next 3-5 years? 
        Also, what are the proxy companies that are not pure-plays but are likely to benefit from this theme, but the revenue should be significant part of their business?
        look for companies that are micro, small-cap or mid-cap, and have a strong growth potential.

        Return a JSON object with a "companies" array, exactly like this example:
        {"companies":[{"name":"KPI Green Energy","ticker":"KPIGREEN","exchange":"NSE","whyRelevant":"Small-cap pure-play solar developer with fast-growing order book"}]}
        """;

    private static string ShortError(string msg)
    {
        var nl = msg.IndexOf('\n');
        var s = nl > 0 ? msg[..nl] : msg;
        return s.Length > 120 ? s[..120] + "..." : s;
    }

    private sealed class CompanyDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("ticker")] public string? Ticker { get; set; }
        [JsonPropertyName("exchange")] public string? Exchange { get; set; }
        [JsonPropertyName("whyRelevant")] public string? WhyRelevant { get; set; }
    }
}
