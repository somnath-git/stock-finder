using System.Text.Json;

namespace TrendsTracker.Config;

/// <summary>
/// Strongly-typed configuration loaded from appsettings.json. The LLM is a local
/// Ollama model, so no API keys are needed.
/// </summary>
public sealed class AppConfig
{
    public int LookbackDays { get; set; } = 7;
    public int MaxArticlesPerFeed { get; set; } = 25;
    public int MaxThemes { get; set; } = 8;
    public int MaxCompaniesPerTheme { get; set; } = 15;

    /// <summary>
    /// Ignore companies whose market cap (₹ crore) exceeds this — large-caps rarely
    /// double in 3-5 years. We want smaller companies with room to grow. Default 30000.
    /// </summary>
    public decimal MaxMarketCapCr { get; set; } = 30000;

    public OllamaConfig Ollama { get; set; } = new();
    public List<RssFeedConfig> RssFeeds { get; set; } = new();
    public RagConfig Rag { get; set; } = new();

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}");

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<AppConfig>(json, options)
               ?? throw new InvalidOperationException("Failed to parse appsettings.json");
    }
}

public sealed class OllamaConfig
{
    /// <summary>Base URL of the Ollama server (default local container).</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string GenerationModel { get; set; } = "qwen2.5:7b";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>Per-request timeout in seconds (local models can be slow on CPU).</summary>
    public int RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>Use Ollama's strict JSON mode (format=json). Reliable on qwen2.5.</summary>
    public bool UseJsonMode { get; set; } = true;

    /// <summary>Max output tokens per generation (Ollama num_predict).</summary>
    public int MaxOutputTokens { get; set; } = 2048;

    /// <summary>Context window (Ollama num_ctx). 8192 fits our prompts comfortably.</summary>
    public int ContextWindow { get; set; } = 8192;
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
