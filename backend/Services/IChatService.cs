using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public interface IChatService
{
    Task<ChatResponse> GetAnswerAsync(string query);
}
