using System.IdentityModel.Tokens.Jwt;
using RagChatbot.API.Services;

namespace RagChatbot.API.Middleware;

public class BlockedUserMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IUserRepository _userRepo;

    public BlockedUserMiddleware(RequestDelegate next, IUserRepository userRepo)
    {
        _next = next;
        _userRepo = userRepo;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (userId != null)
            {
                var user = await _userRepo.GetByIdAsync(userId);
                if (user?.IsBlocked == true)
                {
                    context.Response.StatusCode = 403;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "USER_BLOCKED",
                        message = "Your account has been blocked due to policy violations. Please contact an administrator."
                    });
                    return;
                }
            }
        }
        await _next(context);
    }
}
