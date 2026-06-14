using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public interface IChatHistoryRepository
{
    Task<ChatSession> CreateAsync(ChatSession session);
    Task<ChatSession?> GetAsync(string sessionId, string userId);
    Task<List<ChatSession>> GetRecentAsync(string userId, int limit = 30);
    Task UpdateAsync(ChatSession session);
    Task DeleteAsync(string sessionId, string userId);
    Task DeleteAllAsync(string userId);
    Task EnforceCapAsync(string userId, int maxCount);
}
