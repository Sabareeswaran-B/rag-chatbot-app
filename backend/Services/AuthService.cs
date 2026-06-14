using System.Security.Cryptography;
using System.Text;
using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public class AuthService : IAuthService
{
    private readonly IUserRepository _userRepo;
    private readonly IJwtService _jwtService;
    private readonly IConfiguration _config;

    public AuthService(IUserRepository userRepo, IJwtService jwtService, IConfiguration config)
    {
        _userRepo = userRepo;
        _jwtService = jwtService;
        _config = config;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request)
    {
        var user = await _userRepo.GetByUsernameAsync(request.Username);
        if (user == null || !user.IsActive || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            return new AuthResponse { Success = false, Error = "Invalid username or password." };

        return await BuildAuthResponseAsync(user, request.RememberMe);
    }

    public async Task<AuthResponse> RefreshAsync(string refreshToken)
    {
        var hash = HashToken(refreshToken);
        var stored = await _userRepo.GetTokenAsync(hash, "refresh");
        if (stored == null)
            return new AuthResponse { Success = false, Error = "Invalid or expired refresh token." };

        var user = await _userRepo.GetByIdAsync(stored.UserId);
        if (user == null || !user.IsActive)
            return new AuthResponse { Success = false, Error = "User not found." };

        await _userRepo.RevokeTokenAsync(hash); // Rotate
        return await BuildAuthResponseAsync(user, false);
    }

    public async Task<AuthResponse> RememberMeLoginAsync(string token)
    {
        var hash = HashToken(token);
        var stored = await _userRepo.GetTokenAsync(hash, "remember");
        if (stored == null)
            return new AuthResponse { Success = false, Error = "Invalid or expired remember-me token." };

        var user = await _userRepo.GetByIdAsync(stored.UserId);
        if (user == null || !user.IsActive)
            return new AuthResponse { Success = false, Error = "User not found." };

        return await BuildAuthResponseAsync(user, false);
    }

    public async Task<bool> LogoutAsync(string refreshToken)
    {
        await _userRepo.RevokeTokenAsync(HashToken(refreshToken));
        return true;
    }

    public async Task<AuthResponse> SetupAdminAsync(RegisterRequest request)
    {
        if (await _userRepo.CountAsync() > 0)
            return new AuthResponse { Success = false, Error = "Admin already exists. Use login instead." };

        var user = new User
        {
            Username = request.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Role = "admin"
        };
        await _userRepo.CreateAsync(user);
        return await BuildAuthResponseAsync(user, false);
    }

    public async Task<bool> HasAdminAsync() => await _userRepo.CountAsync() > 0;

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request)
    {
        var existing = await _userRepo.GetByUsernameAsync(request.Username);
        if (existing != null)
            return new AuthResponse { Success = false, Error = "Username already taken." };

        var user = new User
        {
            Username = request.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Role = "user",
            TokenLimit = 500_000
        };
        await _userRepo.CreateAsync(user);
        return await BuildAuthResponseAsync(user, false);
    }

    private async Task<AuthResponse> BuildAuthResponseAsync(User user, bool rememberMe)
    {
        var accessToken = _jwtService.GenerateAccessToken(user);

        var refreshRaw = GenerateRawToken();
        var refreshExpiry = rememberMe
            ? DateTime.UtcNow.AddDays(int.Parse(_config["Jwt:RememberMeExpiryDays"] ?? "30"))
            : DateTime.UtcNow.AddHours(int.Parse(_config["Jwt:RefreshTokenExpiryHours"] ?? "1"));

        await _userRepo.SaveTokenAsync(new StoredToken
        {
            TokenHash = HashToken(refreshRaw),
            UserId = user.Id!,
            TokenType = "refresh",
            ExpiresAt = refreshExpiry
        });

        string? rememberMeToken = null;
        if (rememberMe)
        {
            rememberMeToken = GenerateRawToken();
            await _userRepo.SaveTokenAsync(new StoredToken
            {
                TokenHash = HashToken(rememberMeToken),
                UserId = user.Id!,
                TokenType = "remember",
                ExpiresAt = DateTime.UtcNow.AddDays(int.Parse(_config["Jwt:RememberMeExpiryDays"] ?? "30"))
            });
        }

        return new AuthResponse
        {
            Success = true,
            AccessToken = accessToken,
            RefreshToken = refreshRaw,
            RememberMeToken = rememberMeToken,
            Username = user.Username,
            Role = user.Role
        };
    }

    private static string GenerateRawToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
