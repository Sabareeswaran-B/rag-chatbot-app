using System.Security.Claims;
using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public interface IJwtService
{
    string GenerateAccessToken(User user);
    ClaimsPrincipal? ValidateToken(string token);
}
