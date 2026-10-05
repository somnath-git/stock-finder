using System.Text;
using System.Text.Json;
using TrendsTracker.Config;

namespace TrendsTracker.Services;

/// <summary>
/// LLM client backed by a local Ollama server (http://localhost:11434 by default).
/// Runs models on your own machine — no quota, no API keys, no billing. The only
/// cost is speed (local compute). Implements <see cref="ILlmClient"/>, which the
/// pipeline stages depend on.
/// </summary>
public sealed class OllamaClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly OllamaConfig _cfg;

    public OllamaClient(HttpClient http, OllamaConfig cfg)
    {
        _http = http;
        _cfg = cfg;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_cfg.BaseUrl);

    public async Task<string> GenerateTextAsync(string prompt, CancellationToken ct = default)
    {
        var url = $"{_cfg.BaseUrl.TrimEnd('/')}/api/generate";

        // JSON mode is opt-in: some small models emit an empty "{}" under it, so by
        // default we let the model answer freely and parse the JSON out loosely.
        var tokens = Math.Max(256, _cfg.MaxOutputTokens);
        var ctx = Math.Max(2048, _cfg.ContextWindow);

        object payload = _cfg.UseJsonMode
            ? new
            {
                model = _cfg.GenerationModel,
                prompt,
                stream = false,
                format = "json",
                options = new { temperature = 0.1, num_predict = tokens, num_ctx = ctx }
            }
            : new
            {
                model = _cfg.GenerationModel,
                prompt,
                stream = false,
                options = new { temperature = 0.2, num_predict = tokens, num_ctx = ctx }
            };

        var body = await PostAsync(url, payload, "generate", ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() ?? "" : "";
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var url = $"{_cfg.BaseUrl.TrimEnd('/')}/api/embeddings";
        var payload = new { model = _cfg.EmbeddingModel, prompt = text };

        var body = await PostAsync(url, payload, "embeddings", ct);
        using var doc = JsonDocument.Parse(body);

        var values = doc.RootElement.GetProperty("embedding");
        var vector = new float[values.GetArrayLength()];
        var i = 0;
        foreach (var v in values.EnumerateArray())
            vector[i++] = v.GetSingle();
        return vector;
    }

    /// <summary>
    /// Ollama's native API embeds one text per call, so we loop. There's no quota,
    /// so the cost is only wall-clock time; stages already cap chunks per company.
    /// </summary>
    public async Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new List<float[]>(texts.Count);
        foreach (var t in texts)
            result.Add(await EmbedAsync(t, ct));
        return result;
    }

    private async Task<string> PostAsync(string url, object payload, string op, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _cfg.RequestTimeoutSeconds)));

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync(url, content, cts.Token);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException(
                $"Ollama {op} timed out after {_cfg.RequestTimeoutSeconds}s. " +
                "Is the model loaded? A first call can be slow while it loads into memory.");
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException(
                $"Ollama {op} could not reach {url}. Is the container running " +
                $"(docker ps) and the port published? Original: {ex.Message}");
        }

        var respBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Ollama {op} failed ({(int)resp.StatusCode}): {Trim(respBody)}");

        resp.Dispose();
        return respBody;
    }

    private static string Trim(string s) => s.Length > 500 ? s[..500] : s;
}
