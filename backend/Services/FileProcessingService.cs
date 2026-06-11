using UglyToad.PdfPig;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace RagChatbot.API.Services;

public class FileProcessingService : IFileProcessingService
{
    private const int ChunkSize = 1000;
    private const int Overlap = 200;

    public async Task<List<string>> ExtractAndChunkAsync(IFormFile file)
    {
        var text = await ExtractTextAsync(file);
        return ChunkText(text);
    }

    private async Task<string> ExtractTextAsync(IFormFile file)
    {
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        using var stream = file.OpenReadStream();

        return extension switch
        {
            ".pdf" => ExtractFromPdf(stream),
            ".docx" => ExtractFromDocx(stream),
            ".txt" or ".md" or ".csv" => await ExtractFromTextAsync(stream),
            _ => throw new NotSupportedException($"File type '{extension}' is not supported. Use PDF, DOCX, TXT, MD, or CSV.")
        };
    }

    private string ExtractFromPdf(Stream stream)
    {
        using var pdf = PdfDocument.Open(stream);
        var sb = new System.Text.StringBuilder();
        foreach (var page in pdf.GetPages())
            sb.AppendLine(page.Text);
        return sb.ToString();
    }

    private string ExtractFromDocx(Stream stream)
    {
        using var doc = WordprocessingDocument.Open(stream, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) return string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (var para in body.Descendants<Paragraph>())
            sb.AppendLine(para.InnerText);
        return sb.ToString();
    }

    private async Task<string> ExtractFromTextAsync(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private List<string> ChunkText(string text)
    {
        var chunks = new List<string>();
        var cleanText = string.Join(" ", text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0));

        if (cleanText.Length <= ChunkSize)
        {
            if (cleanText.Length > 50) chunks.Add(cleanText);
            return chunks;
        }

        int start = 0;
        while (start < cleanText.Length)
        {
            int end = Math.Min(start + ChunkSize, cleanText.Length);
            // Try to break at a sentence boundary
            if (end < cleanText.Length)
            {
                int lastPeriod = cleanText.LastIndexOfAny(['.', '!', '?', '\n'], end, Math.Min(100, end - start));
                if (lastPeriod > start) end = lastPeriod + 1;
            }
            var chunk = cleanText[start..end].Trim();
            if (chunk.Length > 50) chunks.Add(chunk);
            start = end - Overlap;
            if (start >= cleanText.Length) break;
        }
        return chunks;
    }
}
