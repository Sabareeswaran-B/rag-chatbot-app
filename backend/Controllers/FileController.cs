using Microsoft.AspNetCore.Mvc;
using RagChatbot.API.Models;
using RagChatbot.API.Services;

namespace RagChatbot.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FileController : ControllerBase
{
    private readonly IFileProcessingService _fileProcessingService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IMongoDbService _mongoDbService;

    public FileController(IFileProcessingService fileProcessingService, IEmbeddingService embeddingService, IMongoDbService mongoDbService)
    {
        _fileProcessingService = fileProcessingService;
        _embeddingService = embeddingService;
        _mongoDbService = mongoDbService;
    }

    [HttpPost("upload")]
    [RequestSizeLimit(50 * 1024 * 1024)] // 50MB limit
    public async Task<ActionResult<UploadResponse>> Upload(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new UploadResponse { Success = false, Error = "No file provided." });

        try
        {
            // Extract text and chunk
            var chunks = await _fileProcessingService.ExtractAndChunkAsync(file);
            if (chunks.Count == 0)
                return BadRequest(new UploadResponse { Success = false, Error = "Could not extract text from file." });

            // Embed all chunks (sequential to avoid rate limits)
            var embeddings = new List<float[]>();
            foreach (var chunk in chunks)
            {
                var embedding = await _embeddingService.GetEmbeddingAsync(chunk);
                embeddings.Add(embedding);
            }

            // Save to MongoDB
            var fileType = Path.GetExtension(file.FileName).TrimStart('.');
            await _mongoDbService.SaveChunksAsync(file.FileName, chunks, embeddings, fileType);

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

    [HttpGet("list")]
    public async Task<ActionResult<List<UploadedFile>>> GetFiles()
    {
        var files = await _mongoDbService.GetUploadedFilesAsync();
        return Ok(files);
    }

    [HttpDelete("{fileName}")]
    public async Task<IActionResult> DeleteFile(string fileName)
    {
        await _mongoDbService.DeleteFileChunksAsync(Uri.UnescapeDataString(fileName));
        return NoContent();
    }
}
