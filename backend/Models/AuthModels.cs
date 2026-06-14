namespace RagChatbot.API.Models;

public record LoginRequest(string Username, string Password, bool RememberMe = false);
public record RegisterRequest(string Username, string Password);
public record RefreshTokenRequest(string RefreshToken);
public record RememberMeRequest(string Token);

public class AuthResponse
{
    public bool Success { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? RememberMeToken { get; set; }
    public string? Username { get; set; }
    public string? Role { get; set; }
    public string? Error { get; set; }
}
