using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public interface IAuthService
{
    Task<AuthResponse> LoginAsync(LoginRequest request);
    Task<AuthResponse> RefreshAsync(string refreshToken);
    Task<AuthResponse> RememberMeLoginAsync(string token);
    Task<bool> LogoutAsync(string refreshToken);
    Task<AuthResponse> SetupAdminAsync(RegisterRequest request);
    Task<bool> HasAdminAsync();
    Task<AuthResponse> RegisterAsync(RegisterRequest request);
}
