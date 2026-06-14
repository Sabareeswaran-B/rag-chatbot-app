using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public interface IModerationViolationRepository
{
    Task SaveAsync(ModerationViolation violation);
    Task<List<ModerationViolation>> GetAllAsync();
}
