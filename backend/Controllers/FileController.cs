using System.Security.Cryptography;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
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
    /// Upload a document. Admin only. Computes a SHA-256 content hash — if the same content is
    /// already indexed (even under a different filename) the upload is rejected as a duplicate.
    /// Supported: PDF (≤25 MB), TXT / MD / DOCX / CSV (≤100 MB).
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

        // Compute SHA-256 content hash
        string contentHash;
        using (var hashStream = file.OpenReadStream())
            contentHash = Convert.ToHexString(SHA256.HashData(hashStream)).ToLowerInvariant();

        // Duplicate check — same content already indexed?
        var existing = await mongoDbService.GetFileByHashAsync(contentHash);
        if (existing != null)
            return Ok(new UploadResponse
            {
                Success = true,
                IsDuplicate = true,
                FileName = file.FileName,
                ExistingFileName = existing.FileName,
                ChunksCreated = existing.ChunkCount
            });

        try
        {
            var chunks = await fileProcessingService.ExtractAndChunkAsync(file);
            if (chunks.Count == 0)
                return BadRequest(new UploadResponse { Success = false, Error = "Could not extract text from file." });

            var embeddings = new List<float[]>();
            foreach (var chunk in chunks)
                embeddings.Add(await embeddingService.GetEmbeddingAsync(chunk));

            var fileType = ext.TrimStart('.');
            await mongoDbService.SaveChunksAsync(file.FileName, chunks, embeddings, fileType, contentHash);

            var uploader = User.FindFirst("unique_name")?.Value
                ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? "unknown";

            await mongoDbService.SaveFileMetadataAsync(new FileMetadata
            {
                FileName = file.FileName,
                ContentHash = contentHash,
                FileSize = file.Length,
                FileType = fileType,
                ChunkCount = chunks.Count,
                CharacterCount = chunks.Sum(c => (long)c.Length),
                UploadedBy = uploader
            });

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

    /// <summary>Download extracted text content reconstructed from indexed chunks. Any authenticated user.</summary>
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

    /// <summary>Remove all chunks and metadata for a document. Admin only.</summary>
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
