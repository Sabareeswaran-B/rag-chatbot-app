using UglyToad.PdfPig;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace RagChatbot.API.Services;

public class FileProcessingService : IFileProcessingService
{
    private const int ChunkSize = 1000;
    private const int Overlap = 200;
    private const int MaxChunks = 2000;

    private readonly ILogger<FileProcessingService> _logger;

    public FileProcessingService(ILogger<FileProcessingService> logger)
    {
        _logger = logger;
    }

    public async Task<List<string>> ExtractAndChunkAsync(IFormFile file)
    {
        _logger.LogInformation("Buffering {FileName} ({Size} bytes) into memory", file.FileName, file.Length);

        // Copy to MemoryStream once — avoids thread-affinity issues when passing to Task.Run,
        // and ensures the stream is at position 0 regardless of prior reads (e.g. SHA-256 hashing).
        using var ms = new MemoryStream((int)file.Length);
        await file.CopyToAsync(ms);
        ms.Position = 0;

        _logger.LogInformation("Buffer complete, starting text extraction for {FileName}", file.FileName);
        var text = await ExtractTextAsync(file.FileName, ms);

        _logger.LogInformation("Extraction complete, {CharCount} chars extracted from {FileName}. Starting chunking.", text.Length, file.FileName);
        var chunks = await Task.Run(() => ChunkText(text));

        _logger.LogInformation("Chunking complete: {ChunkCount} chunks from {FileName}", chunks.Count, file.FileName);
        return chunks;
    }

    private async Task<string> ExtractTextAsync(string fileName, MemoryStream ms)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        return extension switch
        {
            ".pdf" => await Task.Run(() =>
            {
                _logger.LogInformation("PDF extraction started for {FileName}", fileName);
                var result = ExtractFromPdf(ms);
                _logger.LogInformation("PDF extraction finished for {FileName}", fileName);
                return result;
            }),
            ".docx" => await Task.Run(() =>
            {
                _logger.LogInformation("DOCX extraction started for {FileName}", fileName);
                var result = ExtractFromDocx(ms);
                _logger.LogInformation("DOCX extraction finished for {FileName}", fileName);
                return result;
            }),
            ".txt" or ".md" or ".csv" => await ExtractFromTextAsync(ms),
            _ => throw new NotSupportedException($"File type '{extension}' is not supported. Use PDF, DOCX, TXT, MD, or CSV.")
        };
    }

    private string ExtractFromPdf(MemoryStream ms)
    {
        using var pdf = PdfDocument.Open(ms);
        var sb = new System.Text.StringBuilder();
        var pageCount = 0;
        foreach (var page in pdf.GetPages())
        {
            sb.AppendLine(page.Text);
            pageCount++;
            if (pageCount % 10 == 0)
                _logger.LogInformation("PDF: processed {PageCount} pages so far", pageCount);
        }
        _logger.LogInformation("PDF: total {PageCount} pages processed", pageCount);
        return sb.ToString();
    }

    private string ExtractFromDocx(MemoryStream ms)
    {
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) return string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (var para in body.Descendants<Paragraph>())
            sb.AppendLine(para.InnerText);
        return sb.ToString();
    }

    private async Task<string> ExtractFromTextAsync(MemoryStream ms)
    {
        using var reader = new StreamReader(ms);
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
        while (start < cleanText.Length && chunks.Count < MaxChunks)
        {
            int end = Math.Min(start + ChunkSize, cleanText.Length);
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

        if (chunks.Count == MaxChunks)
            _logger.LogWarning("Chunk cap reached ({MaxChunks}) for {FileName} — document truncated at {CharCount}/{TotalChars} chars",
                MaxChunks, "file", start, cleanText.Length);

        return chunks;
    }
}
