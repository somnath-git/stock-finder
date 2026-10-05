using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrendsTracker.Config;

/// <summary>
/// Strongly-typed configuration loaded from appsettings.json.
/// All API keys live here; leaving them blank puts the app in "stub mode"
/// so you can see the pipeline run end-to-end before adding real keys.
/// </summary>
public sealed class AppConfig
{
    public int LookbackDays { get; set; } = 7;
    public int MaxArticlesPerFeed { get; set; } = 25;
    public int MaxThemes { get; set; } = 8;
    public int MaxCompaniesPerTheme { get; set; } = 5;

    /// <summary>Which LLM backend to use: "Ollama" (local, free) or "Gemini" (cloud).</summary>
    public string Provider { get; set; } = "Ollama";

    public GeminiConfig Gemini { get; set; } = new();
    public OllamaConfig Ollama { get; set; } = new();
    public GoogleSearchConfig GoogleSearch { get; set; } = new();
    public List<RssFeedConfig> RssFeeds { get; set; } = new();
    public RagConfig Rag { get; set; } = new();

    [JsonIgnore]
    public bool UseOllama => string.Equals(Provider, "Ollama", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasGeminiKey => !string.IsNullOrWhiteSpace(Gemini.ApiKey);

    [JsonIgnore]
    public bool HasGoogleSearch =>
        !string.IsNullOrWhiteSpace(GoogleSearch.ApiKey) &&
        !string.IsNullOrWhiteSpace(GoogleSearch.SearchEngineId);

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}");

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, options)
                  ?? throw new InvalidOperationException("Failed to parse appsettings.json");

        // Environment variables override file values (handy for keeping keys out of source control).
        var envGemini = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envGemini)) cfg.Gemini.ApiKey = envGemini;

        var envGoogle = Environment.GetEnvironmentVariable("GOOGLE_SEARCH_API_KEY");
        if (!string.IsNullOrWhiteSpace(envGoogle)) cfg.GoogleSearch.ApiKey = envGoogle;

        var envCx = Environment.GetEnvironmentVariable("GOOGLE_SEARCH_CX");
        if (!string.IsNullOrWhiteSpace(envCx)) cfg.GoogleSearch.SearchEngineId = envCx;

        return cfg;
    }
}

public sealed class GeminiConfig
{
    public string ApiKey { get; set; } = "";
    public string GenerationModel { get; set; } = "gemini-3.8-flash";
    public string EmbeddingModel { get; set; } = "text-embedding-004";

    /// <summary>
    /// Minimum spacing between any two Gemini calls, in seconds. Tune to your
    /// model's free-tier RPM: e.g. 10 RPM -> 6s, 15 RPM -> 4s. The retry logic
    /// still backs off if a 429 slips through, so you can run this fairly tight.
    /// </summary>
    public double MinRequestIntervalSeconds { get; set; } = 4;

    /// <summary>Number of automatic retries on a transient 429/503 before failing.</summary>
    public int MaxRetries { get; set; } = 4;

    /// <summary>
    /// Longest we'll ever auto-wait for a single retry, in seconds. If Google's
    /// "retry in Xs" hint exceeds this (e.g. a DAILY-quota reset of many hours),
    /// we stop retrying and fail fast instead of sleeping for ages.
    /// </summary>
    public double MaxRetryWaitSeconds { get; set; } = 60;
}

public sealed class OllamaConfig
{
    /// <summary>Base URL of the Ollama server (default local container).</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string GenerationModel { get; set; } = "qwen2.5:7b";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>Per-request timeout in seconds (local models can be slow on CPU).</summary>
    public int RequestTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Use Ollama's strict JSON mode (format=json). Reliable on capable models like
    /// qwen2.5; tiny models (llama3.2 3B) sometimes emit an empty "{}" under it.
    /// </summary>
    public bool UseJsonMode { get; set; } = true;

    /// <summary>Max output tokens per generation (Ollama num_predict).</summary>
    public int MaxOutputTokens { get; set; } = 2048;

    /// <summary>
    /// Context window (Ollama num_ctx). llama3.2 defaults to ~2048, which truncates
    /// our prompt and makes the model return "{}". 8192 fits the headlines + schema.
    /// </summary>
    public int ContextWindow { get; set; } = 8192;
}

public sealed class GoogleSearchConfig
{
    public string ApiKey { get; set; } = "";
    public string SearchEngineId { get; set; } = "";
    public List<string> Queries { get; set; } = new();
}

public sealed class RssFeedConfig
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class RagConfig
{
    public int ChunkSize { get; set; } = 1200;
    public int ChunkOverlap { get; set; } = 150;
    public int TopK { get; set; } = 6;

    /// <summary>
    /// Max chunks embedded per company. Growth guidance often sits DEEP in a deck
    /// (not the intro slides), so this must cover the whole document. A typical
    /// investor PDF is ~30-60 chunks; batch embedding makes that cheap. Set high.
    /// </summary>
    public int MaxChunksPerCompany { get; set; } = 120;
}
