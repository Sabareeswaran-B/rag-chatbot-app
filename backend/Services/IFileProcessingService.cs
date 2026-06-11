namespace RagChatbot.API.Services;

public interface IFileProcessingService
{
    Task<List<string>> ExtractAndChunkAsync(IFormFile file);
}
