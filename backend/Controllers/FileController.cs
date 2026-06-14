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
public class FileController(
    IFileProcessingService fileProcessingService,
    IEmbeddingService embeddingService,
    IMongoDbService mongoDbService,
    ILogger<FileController> logger) : ControllerBase
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
        logger.LogInformation("Upload started: {FileName}, size={Size} bytes", file?.FileName, file?.Length);

        if (file == null || file.Length == 0)
            return BadRequest(new UploadResponse { Success = false, Error = "No file provided." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var isPdf = ext == ".pdf";
        var maxBytes = isPdf ? 25 * 1024 * 1024 : 100 * 1024 * 1024;
        if (file.Length > maxBytes)
        {
            logger.LogWarning("Upload rejected — file too large: {FileName} ({Size} bytes)", file.FileName, file.Length);
            return BadRequest(new UploadResponse
            {
                Success = false,
                Error = isPdf ? "PDF files are limited to 25 MB." : "Files are limited to 100 MB."
            });
        }

        // Compute SHA-256 content hash
        logger.LogInformation("Computing SHA-256 hash for {FileName}", file.FileName);
        string contentHash;
        using (var hashStream = file.OpenReadStream())
            contentHash = Convert.ToHexString(SHA256.HashData(hashStream)).ToLowerInvariant();
        logger.LogInformation("Hash computed: {Hash}", contentHash[..16] + "...");

        // Duplicate check — same content already indexed?
        logger.LogInformation("Checking for duplicate content hash");
        var existing = await mongoDbService.GetFileByHashAsync(contentHash);
        if (existing != null)
        {
            logger.LogInformation("Duplicate detected — content already indexed as {ExistingFile}", existing.FileName);
            return Ok(new UploadResponse
            {
                Success = true,
                IsDuplicate = true,
                FileName = file.FileName,
                ExistingFileName = existing.FileName,
                ChunksCreated = existing.ChunkCount
            });
        }

        try
        {
            logger.LogInformation("Extracting and chunking text from {FileName}", file.FileName);
            var chunks = await fileProcessingService.ExtractAndChunkAsync(file);
            logger.LogInformation("Extracted {ChunkCount} chunks from {FileName}", chunks.Count, file.FileName);

            if (chunks.Count == 0)
            {
                logger.LogWarning("No text extracted from {FileName}", file.FileName);
                return BadRequest(new UploadResponse { Success = false, Error = "Could not extract text from file." });
            }

            logger.LogInformation("Generating embeddings for {ChunkCount} chunks (single batch request)", chunks.Count);
            var embeddings = await embeddingService.GetEmbeddingsAsync(chunks);
            logger.LogInformation("Embeddings generated: {EmbeddingCount} vectors", embeddings.Count);

            var fileType = ext.TrimStart('.');
            logger.LogInformation("Saving {ChunkCount} chunks to MongoDB", chunks.Count);
            await mongoDbService.SaveChunksAsync(file.FileName, chunks, embeddings, fileType, contentHash);
            logger.LogInformation("Chunks saved successfully");

            var uploader = User.FindFirst("unique_name")?.Value
                ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? "unknown";

            logger.LogInformation("Saving file metadata for {FileName}", file.FileName);
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

            logger.LogInformation("Upload complete: {FileName}, {ChunkCount} chunks indexed", file.FileName, chunks.Count);
            return Ok(new UploadResponse
            {
                Success = true,
                FileName = file.FileName,
                ChunksCreated = chunks.Count
            });
        }
        catch (NotSupportedException ex)
        {
            logger.LogWarning("Unsupported file type {FileName}: {Message}", file.FileName, ex.Message);
            return BadRequest(new UploadResponse { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Upload failed for {FileName}: {Message}", file.FileName, ex.Message);
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
