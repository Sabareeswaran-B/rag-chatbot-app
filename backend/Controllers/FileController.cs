using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RagChatbot.API.Models;
using RagChatbot.API.Services;

namespace RagChatbot.API.Controllers;

/// <summary>File management — upload (admin only), list/download (any authenticated user), delete (admin only).</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public class FileController(IFileProcessingService fileProcessingService, IEmbeddingService embeddingService, IMongoDbService mongoDbService) : ControllerBase
{
    /// <summary>
    /// Upload a document (PDF, DOCX, TXT, MD, CSV). Admin only.
    /// Extracts text, splits into 1 000-character chunks with 200-character overlap,
    /// embeds via OpenAI, and stores in MongoDB. Max 50 MB.
    /// </summary>
    [HttpPost("upload")]
    [Authorize(Roles = "admin")]
    [RequestSizeLimit(100 * 1024 * 1024)]
    [ProducesResponseType(typeof(UploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UploadResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(UploadResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<UploadResponse>> Upload(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new UploadResponse { Success = false, Error = "No file provided." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var isPdf = ext == ".pdf";
        var maxBytes = isPdf ? 25 * 1024 * 1024 : 100 * 1024 * 1024;
        if (file.Length > maxBytes)
            return BadRequest(new UploadResponse
            {
                Success = false,
                Error = isPdf ? "PDF files are limited to 25 MB." : "Files are limited to 100 MB."
            });

        try
        {
            var chunks = await fileProcessingService.ExtractAndChunkAsync(file);
            if (chunks.Count == 0)
                return BadRequest(new UploadResponse { Success = false, Error = "Could not extract text from file." });

            var embeddings = new List<float[]>();
            foreach (var chunk in chunks)
            {
                var embedding = await embeddingService.GetEmbeddingAsync(chunk);
                embeddings.Add(embedding);
            }

            var fileType = Path.GetExtension(file.FileName).TrimStart('.');
            await mongoDbService.SaveChunksAsync(file.FileName, chunks, embeddings, fileType);

            return Ok(new UploadResponse
            {
                Success = true,
                FileName = file.FileName,
                ChunksCreated = chunks.Count
            });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new UploadResponse { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new UploadResponse { Success = false, Error = ex.Message });
        }
    }

    /// <summary>List all documents currently indexed in the knowledge base. Any authenticated user.</summary>
    [HttpGet("list")]
    [ProducesResponseType(typeof(List<UploadedFile>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<UploadedFile>>> GetFiles()
    {
        var files = await mongoDbService.GetUploadedFilesAsync();
        return Ok(files);
    }

    /// <summary>
    /// Download the extracted text content of a document reconstructed from its indexed chunks.
    /// Any authenticated user. Returns a plain-text file.
    /// </summary>
    [HttpGet("download/{fileName}")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadFile(string fileName)
    {
        var decoded = Uri.UnescapeDataString(fileName);
        var chunks = await mongoDbService.GetChunksByFileAsync(decoded);
        if (chunks.Count == 0) return NotFound(new { error = "File not found in knowledge base." });

        var content = new StringBuilder();
        foreach (var chunk in chunks)
            content.AppendLine(chunk.Content);

        var bytes = Encoding.UTF8.GetBytes(content.ToString());
        var downloadName = Path.GetFileNameWithoutExtension(decoded) + "_extracted.txt";
        return File(bytes, "text/plain; charset=utf-8", downloadName);
    }

    /// <summary>Remove all chunks for a document from the knowledge base. Admin only.</summary>
    [HttpDelete("{fileName}")]
    [Authorize(Roles = "admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteFile(string fileName)
    {
        await mongoDbService.DeleteFileChunksAsync(Uri.UnescapeDataString(fileName));
        return NoContent();
    }
}
