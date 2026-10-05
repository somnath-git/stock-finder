using System.Text;
using UglyToad.PdfPig;

namespace TrendsTracker.Services;

/// <summary>
/// Extracts plain text from a PDF (concall transcript / investor presentation)
/// using PdfPig. Returns empty string if the bytes aren't a readable PDF.
/// </summary>
public static class PdfExtractor
{
    public static string ExtractText(byte[] pdfBytes, int maxChars = 60000)
    {
        if (pdfBytes.Length == 0) return "";

        try
        {
            using var ms = new MemoryStream(pdfBytes);
            using var pdf = PdfDocument.Open(ms);

            var sb = new StringBuilder();
            foreach (var page in pdf.GetPages())
            {
                sb.AppendLine(page.Text);
                if (sb.Length >= maxChars) break;
            }

            var text = sb.ToString();
            return text.Length > maxChars ? text[..maxChars] : text;
        }
        catch
        {
            return "";
        }
    }
}
