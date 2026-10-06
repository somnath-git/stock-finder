namespace TrendsTracker.Services;

/// <summary>
/// Abstraction over a local LLM provider (Ollama). Exposes generation + embeddings.
/// </summary>
public interface ILlmClient
{
    bool IsConfigured { get; }

    /// <summary>
    /// Generates text. <paramref name="jsonMode"/> overrides the configured default:
    /// true = force JSON output (for structured prompts), false = plain prose (for
    /// natural-language answers), null = use the client's configured default.
    /// </summary>
    Task<string> GenerateTextAsync(string prompt, bool? jsonMode = null, CancellationToken ct = default);

    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
