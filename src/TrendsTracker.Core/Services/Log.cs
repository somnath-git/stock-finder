namespace TrendsTracker.Services;

/// <summary>
/// Minimal logging indirection so Core library classes don't call Console directly.
/// Console apps leave Verbose on; the API/UI can turn it off. Keeps output centralised
/// and makes it trivial to silence or redirect later.
/// </summary>
public static class Log
{
    /// <summary>When false, Info/Step messages are suppressed (Warn still shows).</summary>
    public static bool Verbose { get; set; } = true;

    /// <summary>A pipeline step / progress line (only shown when Verbose).</summary>
    public static void Step(string message)
    {
        if (Verbose) Console.WriteLine(message);
    }

    /// <summary>A warning worth seeing even in quiet mode.</summary>
    public static void Warn(string message) => Console.WriteLine(message);
}
