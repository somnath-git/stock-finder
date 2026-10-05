using System.Text.Json;

namespace TrendsTracker.Services;

/// <summary>
/// Helpers for coaxing clean JSON out of LLM responses, which frequently
/// wrap their output in ```json ... ``` fences or add stray prose.
/// </summary>
public static class JsonHelper
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Strips markdown code fences and trims to the first/last brace or bracket.</summary>
    public static string CleanJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var s = raw.Trim();

        // Remove ```json / ``` fences.
        if (s.StartsWith("```"))
        {
            var firstNewline = s.IndexOf('\n');
            if (firstNewline >= 0) s = s[(firstNewline + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim();
        }

        // Trim to the outermost JSON structure.
        var startArr = s.IndexOf('[');
        var startObj = s.IndexOf('{');
        int start = (startArr, startObj) switch
        {
            ( < 0, >= 0) => startObj,
            ( >= 0, < 0) => startArr,
            ( >= 0, >= 0) => Math.Min(startArr, startObj),
            _ => -1
        };
        if (start < 0) return s;

        var lastArr = s.LastIndexOf(']');
        var lastObj = s.LastIndexOf('}');
        var end = Math.Max(lastArr, lastObj);
        if (end <= start) return s;

        return s[start..(end + 1)];
    }

    public static T? Deserialize<T>(string raw)
    {
        var clean = CleanJson(raw);
        try
        {
            return JsonSerializer.Deserialize<T>(clean, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// Deserializes a list, tolerating the shapes LLMs produce:
    ///   1. a bare array:              [ {...}, {...} ]
    ///   2. an object wrapping an array: { "companies": [ {...} ] }
    ///   3. a SINGLE object (one item):  { "name": "...", "ticker": "..." }
    /// For case 2 it uses the first array-valued property; for case 3 it returns a
    /// one-element list.
    /// </summary>
    public static List<T> DeserializeList<T>(string raw)
    {
        var clean = CleanJson(raw);
        if (string.IsNullOrWhiteSpace(clean)) return new();

        try
        {
            using var doc = JsonDocument.Parse(clean);
            var root = doc.RootElement;

            JsonElement arrayElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                arrayElement = root;
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // Find the first property whose value is an array.
                arrayElement = default;
                var found = false;
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        arrayElement = prop.Value;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    // Case 3: the object IS a single item. Deserialize it as one T.
                    var single = JsonSerializer.Deserialize<T>(root.GetRawText(), Options);
                    return single is null ? new() : new List<T> { single };
                }
            }
            else
            {
                return new();
            }

            var list = JsonSerializer.Deserialize<List<T>>(arrayElement.GetRawText(), Options);
            return list ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
