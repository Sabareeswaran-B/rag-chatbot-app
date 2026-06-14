using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public interface IRerankingService
{
    /// <summary>
    /// Reranks <paramref name="chunks"/> by relevance to <paramref name="query"/> and returns the top K.
    /// Also returns the input/output token counts consumed by the rerank call (0 on fallback).
    /// </summary>
    Task<(List<DocumentChunk> Chunks, int InputTokens, int OutputTokens)> RerankAsync(string query, List<DocumentChunk> chunks, int topK = 5);
}
