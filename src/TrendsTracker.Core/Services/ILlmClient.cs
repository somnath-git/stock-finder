namespace TrendsTracker.Services;

/// <summary>
/// Abstraction over an LLM provider so the pipeline stages don't care whether
/// they're talking to Gemini (cloud, quota-limited) or Ollama (local, unlimited).
/// Both implementations expose the same three operations.
/// </summary>
public interface ILlmClient
{
    bool IsConfigured { get; }

    Task<string> GenerateTextAsync(string prompt, CancellationToken ct = default);

    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
