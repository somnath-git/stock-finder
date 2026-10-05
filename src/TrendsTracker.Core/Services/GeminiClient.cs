using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TrendsTracker.Config;

namespace TrendsTracker.Services;

/// <summary>
/// Minimal REST client for Google's Gemini API (free tier via AI Studio key).
///
/// Rate-limit handling:
///   - A single central throttle spaces EVERY call (generate + embed, all stages)
///     by at least <see cref="GeminiConfig.MinRequestIntervalSeconds"/>.
///   - Transient 429/503 responses are retried with backoff, honoring Google's
///     own "retryDelay" hint (capped by MaxRetryWaitSeconds). If still failing,
///     a normal HttpRequestException is thrown.
/// </summary>
public sealed class GeminiClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly GeminiConfig _cfg;
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    // Central throttle shared by every request this client makes.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    public GeminiClient(HttpClient http, GeminiConfig cfg)
    {
        _http = http;
        _cfg = cfg;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_cfg.ApiKey);

    /// <summary>Sends a prompt and returns the model's text output.</summary>
    public async Task<string> GenerateTextAsync(string prompt, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/{_cfg.GenerationModel}:generateContent?key={_cfg.ApiKey}";

        var payload = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            },
            generationConfig = new
            {
                temperature = 0.3,
                maxOutputTokens = 4096
            }
        };

        var body = await SendWithPolicyAsync(url, payload, "generateContent", ct);

        using var doc = JsonDocument.Parse(body);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("candidates", out var candidates))
        {
            foreach (var cand in candidates.EnumerateArray())
            {
                if (cand.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts))
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var text))
                            sb.Append(text.GetString());
                    }
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>Embeds a single piece of text into a vector.</summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var model = _cfg.EmbeddingModel;
        var url = $"{BaseUrl}/{model}:embedContent?key={_cfg.ApiKey}";

        var payload = new
        {
            model = $"models/{model}",
            content = new { parts = new[] { new { text } } }
        };

        var body = await SendWithPolicyAsync(url, payload, "embedContent", ct);

        using var doc = JsonDocument.Parse(body);
        var values = doc.RootElement
            .GetProperty("embedding")
            .GetProperty("values");

        var vector = new float[values.GetArrayLength()];
        var i = 0;
        foreach (var v in values.EnumerateArray())
            vector[i++] = v.GetSingle();
        return vector;
    }

    /// <summary>
    /// Embeds MANY texts in a single request via batchEmbedContents. This is the
    /// key to keeping runtime low: one network round-trip (and one throttle slot)
    /// instead of one per chunk. Returns vectors in the same order as the inputs.
    /// </summary>
    public async Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new List<float[]>(texts.Count);
        if (texts.Count == 0) return result;

        var model = _cfg.EmbeddingModel;
        var url = $"{BaseUrl}/{model}:batchEmbedContents?key={_cfg.ApiKey}";

        var payload = new
        {
            requests = texts.Select(t => new
            {
                model = $"models/{model}",
                content = new { parts = new[] { new { text = t } } }
            }).ToArray()
        };

        var body = await SendWithPolicyAsync(url, payload, "batchEmbedContents", ct);

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("embeddings", out var embeddings))
        {
            foreach (var emb in embeddings.EnumerateArray())
            {
                var values = emb.GetProperty("values");
                var vector = new float[values.GetArrayLength()];
                var i = 0;
                foreach (var v in values.EnumerateArray())
                    vector[i++] = v.GetSingle();
                result.Add(vector);
            }
        }
        return result;
    }

    /// <summary>
    /// Core request path: throttle -> POST -> on transient error retry (honoring
    /// retryDelay) -> on exhausted quota optionally prompt the user. Returns the
    /// successful response body or throws.
    /// </summary>
    private async Task<string> SendWithPolicyAsync(
        string url, object payload, string op, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var cap = TimeSpan.FromSeconds(Math.Max(1, _cfg.MaxRetryWaitSeconds));

        for (var attempt = 1; attempt <= Math.Max(1, _cfg.MaxRetries); attempt++)
        {
            await ThrottleAsync(ct);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
                return body;

            var status = resp.StatusCode;
            var transient = status is System.Net.HttpStatusCode.ServiceUnavailable
                                   or System.Net.HttpStatusCode.TooManyRequests;

            if (!transient)
                throw new HttpRequestException($"Gemini {op} failed ({(int)status}): {Trim(body)}");

            // Prefer the server's suggested wait; else exponential backoff.
            var wait = ParseRetryDelay(body) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));

            // A wait longer than the cap means a DAILY quota reset (hours away).
            // Don't sleep on that — fail fast with a clear message.
            if (wait > cap)
                throw new HttpRequestException(
                    $"Gemini {op} hit a quota/rate limit; server asked to wait {wait.TotalSeconds:F0}s " +
                    $"(> {cap.TotalSeconds:F0}s cap). Likely the daily free-tier limit — try later or use Ollama.");

            if (attempt < _cfg.MaxRetries)
            {
                Console.WriteLine($"    [{op}] {(int)status} — waiting {wait.TotalSeconds:F0}s before retry {attempt + 1}/{_cfg.MaxRetries}...");
                await Task.Delay(wait, ct);
            }
        }

        throw new HttpRequestException($"Gemini {op} failed after {_cfg.MaxRetries} retries (rate limited).");
    }

    /// <summary>Blocks until the configured minimum interval has elapsed since the last call.</summary>
    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _nextAllowed)
            {
                var wait = _nextAllowed - now;
                await Task.Delay(wait, ct);
            }
            var interval = TimeSpan.FromSeconds(Math.Max(0, _cfg.MinRequestIntervalSeconds));
            _nextAllowed = DateTimeOffset.UtcNow + interval;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Extracts Google's suggested retry delay from a 429 body, e.g.
    /// "Please retry in 39.658348828s" or a RetryInfo "retryDelay": "40s".
    /// </summary>
    private static TimeSpan? ParseRetryDelay(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        // Structured RetryInfo: "retryDelay": "39s"
        var m = Regex.Match(body, "\"retryDelay\"\\s*:\\s*\"(?<sec>[0-9.]+)s\"");
        if (!m.Success)
            // Free-text hint: "retry in 39.65s"
            m = Regex.Match(body, "retry in (?<sec>[0-9.]+)s");

        if (m.Success && double.TryParse(m.Groups["sec"].Value,
                System.Globalization.CultureInfo.InvariantCulture, out var sec))
        {
            // Add a 1s cushion so we don't retry a hair too early.
            return TimeSpan.FromSeconds(sec + 1);
        }
        return null;
    }

    private static string Trim(string s) => s.Length > 500 ? s[..500] : s;
}
