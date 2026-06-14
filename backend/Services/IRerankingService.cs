using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public interface IRerankingService
{
    Task<List<DocumentChunk>> RerankAsync(string query, List<DocumentChunk> chunks, int topK = 5);
}
