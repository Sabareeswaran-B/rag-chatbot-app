using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public interface IUserRepository
{
    Task<User?> GetByUsernameAsync(string username);
    Task<User?> GetByIdAsync(string id);
    Task<User> CreateAsync(User user);
    Task<long> CountAsync();
    Task SaveTokenAsync(StoredToken token);
    Task<StoredToken?> GetTokenAsync(string tokenHash, string tokenType);
    Task RevokeTokenAsync(string tokenHash);
    Task RevokeAllUserTokensAsync(string userId, string tokenType);
}
