using System.Text;
using System.Text.RegularExpressions;

namespace TrendsTracker.Rag;

/// <summary>
/// Splits long documents into overlapping chunks before embedding.
///
/// Why chunk at all? Embedding models have input limits, and retrieval works best
/// when each stored vector represents a focused passage. Overlap keeps a sentence
/// that straddles a boundary from losing its context in both chunks.
///
/// This splitter is BOUNDARY-AWARE: rather than slicing at a fixed character count
/// (which can cut a sentence — or a guidance statement — in half), it breaks the
/// text into sentences and packs whole sentences into each chunk up to the target
/// size. That keeps "...we expect revenue to double by FY28..." intact in one
/// chunk, which matters a lot for retrieving growth/margin/expansion guidance.
/// </summary>
public static class TextChunker
{
    // Split on sentence terminators followed by whitespace. Not perfect (abbrevs,
    // decimals) but far better than blind character slicing for prose + concalls.
    private static readonly Regex SentenceSplit = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);

    public static List<string> Chunk(string text, int chunkSize, int overlap)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        // Normalise whitespace so sizes are meaningful.
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (chunkSize <= 0) chunkSize = 1000;
        if (overlap < 0 || overlap >= chunkSize) overlap = chunkSize / 8;

        var sentences = SentenceSplit.Split(text)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        if (sentences.Count == 0) return chunks;

        var current = new List<string>();
        var currentLen = 0;

        foreach (var sentence in sentences)
        {
            var s = sentence.Trim();

            // A single sentence longer than the chunk size: hard-split it so it still fits.
            if (s.Length > chunkSize)
            {
                FlushCurrent(chunks, current, ref currentLen);
                foreach (var piece in HardSplit(s, chunkSize, overlap))
                    chunks.Add(piece);
                continue;
            }

            // If adding this sentence would overflow, close the current chunk first,
            // then seed the next one with an overlap tail (last few sentences) so
            // context carries over.
            if (currentLen + s.Length + 1 > chunkSize && current.Count > 0)
            {
                chunks.Add(string.Join(' ', current));
                var tail = TakeOverlapTail(current, overlap);
                current = new List<string>(tail);
                currentLen = tail.Sum(t => t.Length + 1);
            }

            current.Add(s);
            currentLen += s.Length + 1;
        }

        FlushCurrent(chunks, current, ref currentLen);
        return chunks;
    }

    private static void FlushCurrent(List<string> chunks, List<string> current, ref int currentLen)
    {
        if (current.Count > 0)
        {
            chunks.Add(string.Join(' ', current));
            current.Clear();
            currentLen = 0;
        }
    }

    /// <summary>Returns trailing sentences whose combined length is about <paramref name="overlap"/>.</summary>
    private static List<string> TakeOverlapTail(List<string> sentences, int overlap)
    {
        var tail = new List<string>();
        var len = 0;
        for (var i = sentences.Count - 1; i >= 0 && len < overlap; i--)
        {
            tail.Insert(0, sentences[i]);
            len += sentences[i].Length + 1;
        }
        return tail;
    }

    /// <summary>Fallback for a single oversized sentence: fixed-size slices with overlap.</summary>
    private static IEnumerable<string> HardSplit(string s, int chunkSize, int overlap)
    {
        var step = Math.Max(1, chunkSize - overlap);
        for (var start = 0; start < s.Length; start += step)
        {
            var len = Math.Min(chunkSize, s.Length - start);
            var piece = s.Substring(start, len).Trim();
            if (piece.Length > 0) yield return piece;
            if (start + len >= s.Length) break;
        }
    }
}
