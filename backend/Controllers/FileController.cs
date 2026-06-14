using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RagChatbot.API.Models;
using RagChatbot.API.Services;

namespace RagChatbot.API.Controllers;

/// <summary>File management — upload documents, list indexed files, and remove them. Admin only.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "admin")]
[Produces("application/json")]
public class FileController(IFileProcessingService fileProcessingService, IEmbeddingService embeddingService, IMongoDbService mongoDbService) : ControllerBase
{

    /// <summary>
    /// Upload a document (PDF, DOCX, TXT, MD, CSV). The file is extracted, split into 1 000-character
    /// chunks with 200-character overlap, embedded via OpenAI, and stored in MongoDB. Max 50 MB.
    /// </summary>
    /// <param name="file">The document file to index.</param>
    [HttpPost("upload")]
    [RequestSizeLimit(50 * 1024 * 1024)]
    [ProducesResponseType(typeof(UploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UploadResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(UploadResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(UploadResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<UploadResponse>> Upload(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new UploadResponse { Success = false, Error = "No file provided." });

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

    /// <summary>List all documents currently indexed in the knowledge base.</summary>
    [HttpGet("list")]
    [ProducesResponseType(typeof(List<UploadedFile>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<UploadedFile>>> GetFiles()
    {
        var files = await mongoDbService.GetUploadedFilesAsync();
        return Ok(files);
    }

    /// <summary>Remove all chunks for a document from the knowledge base.</summary>
    /// <param name="fileName">The file name as returned by the list endpoint.</param>
    [HttpDelete("{fileName}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteFile(string fileName)
    {
        await mongoDbService.DeleteFileChunksAsync(Uri.UnescapeDataString(fileName));
        return NoContent();
    }
}
