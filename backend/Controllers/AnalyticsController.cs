using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RagChatbot.API.Models;
using RagChatbot.API.Services;

namespace RagChatbot.API.Controllers;

/// <summary>Analytics — admin-only token usage, cost statistics, and token management.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "admin")]
[Produces("application/json")]
public class AnalyticsController(IChatHistoryRepository historyRepo, IUserRepository userRepo, IModerationViolationRepository violationRepo) : ControllerBase
{
    private static decimal CalculateCost(long inputTokens, long outputTokens)
        => (inputTokens * 0.00000015m) + (outputTokens * 0.00000060m);

    private static string AnonLabel(string userId)
        => $"anon-{userId[..Math.Min(8, userId.Length)]}";

    /// <summary>
    /// Returns aggregated token usage and cost for all users (including zero-usage registered users),
    /// ordered by most recent activity, plus the top 5 most expensive sessions with full message history.
    /// Token limit status is included for each registered user.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(AnalyticsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AnalyticsResponse>> GetAnalytics()
    {
        var allSessions = await historyRepo.GetAllSessionsForAnalyticsAsync();
        var allUsers = await userRepo.GetAllUsersAsync();

        var sessionsByUser = allSessions
            .GroupBy(s => s.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var userStats = new List<UserUsageStats>();

        foreach (var user in allUsers)
        {
            var sessions = sessionsByUser.GetValueOrDefault(user.Id!, []);
            var isExpired = user.TokenLimit > 0 && user.TokensUsed >= user.TokenLimit;
            userStats.Add(new UserUsageStats
            {
                UserId = user.Id!,
                Username = user.Username,
                IsAdmin = user.Role == "admin",
                IsAnonymous = false,
                TotalInputTokens = sessions.Sum(s => s.TotalInputTokens),
                TotalOutputTokens = sessions.Sum(s => s.TotalOutputTokens),
                TotalCost = CalculateCost(sessions.Sum(s => s.TotalInputTokens), sessions.Sum(s => s.TotalOutputTokens)),
                SessionCount = sessions.Count,
                MessageCount = sessions.Sum(s => s.Messages.Count),
                LastActivity = sessions.Any() ? sessions.Max(s => s.UpdatedAt) : null,
                TokenLimit = user.TokenLimit,
                TokensUsed = user.TokensUsed,
                IsExpired = isExpired,
                UsagePercentage = user.TokenLimit > 0 ? Math.Min(100, (user.TokensUsed / (double)user.TokenLimit) * 100) : 0,
                IsBlocked = user.IsBlocked
            });
        }

        var registeredIds = new HashSet<string>(allUsers.Where(u => u.Id != null).Select(u => u.Id!));
        foreach (var kvp in sessionsByUser.Where(kvp => !registeredIds.Contains(kvp.Key)))
        {
            var sessions = kvp.Value;
            userStats.Add(new UserUsageStats
            {
                UserId = kvp.Key,
                Username = AnonLabel(kvp.Key),
                IsAdmin = false,
                IsAnonymous = true,
                TotalInputTokens = sessions.Sum(s => s.TotalInputTokens),
                TotalOutputTokens = sessions.Sum(s => s.TotalOutputTokens),
                TotalCost = CalculateCost(sessions.Sum(s => s.TotalInputTokens), sessions.Sum(s => s.TotalOutputTokens)),
                SessionCount = sessions.Count,
                MessageCount = sessions.Sum(s => s.Messages.Count),
                LastActivity = sessions.Max(s => s.UpdatedAt),
                TokenLimit = 0, TokensUsed = 0, IsExpired = false, UsagePercentage = 0
            });
        }

        userStats = userStats
            .OrderByDescending(u => u.LastActivity ?? DateTime.MinValue)
            .ToList();

        var allViolations = await violationRepo.GetAllAsync();
        var blockedUserIds = new HashSet<string>(allUsers.Where(u => u.IsBlocked).Select(u => u.Id!));

        var riskProfile = new RiskProfile
        {
            TotalViolations = allViolations.Count,
            UniqueOffenders = allViolations.Select(v => v.UserId).Distinct().Count(),
            BlockedUsers = blockedUserIds.Count,
            Violations = allViolations.Select(v => new ViolationRecord
            {
                Id = v.Id!,
                UserId = v.UserId,
                Username = v.Username,
                IsAnonymous = v.IsAnonymous,
                Query = v.Query,
                Category = v.Category,
                CreatedAt = v.CreatedAt,
                IsBlocked = blockedUserIds.Contains(v.UserId)
            }).ToList()
        };

        var userDict = allUsers.Where(u => u.Id != null).ToDictionary(u => u.Id!, u => u.Username);

        var topSessions = allSessions
            .OrderByDescending(s => CalculateCost(s.TotalInputTokens, s.TotalOutputTokens))
            .Take(5)
            .Select(s => new TopSessionStats
            {
                SessionId = s.Id!,
                SessionName = s.Name,
                UserId = s.UserId,
                Username = userDict.GetValueOrDefault(s.UserId, AnonLabel(s.UserId)),
                InputTokens = s.TotalInputTokens,
                OutputTokens = s.TotalOutputTokens,
                Cost = CalculateCost(s.TotalInputTokens, s.TotalOutputTokens),
                MessageCount = s.Messages.Count,
                UpdatedAt = s.UpdatedAt,
                Messages = s.Messages.Select(m => new SessionMessageDto
                {
                    Role = m.Role, Content = m.Content,
                    Sources = m.Sources, Timestamp = m.Timestamp
                }).ToList()
            }).ToList();

        var totalInput = allSessions.Sum(s => s.TotalInputTokens);
        var totalOutput = allSessions.Sum(s => s.TotalOutputTokens);

        return Ok(new AnalyticsResponse
        {
            Summary = new AnalyticsSummary
            {
                TotalInputTokens = totalInput,
                TotalOutputTokens = totalOutput,
                TotalCost = CalculateCost(totalInput, totalOutput),
                TotalUsers = userStats.Count,
                TotalSessions = allSessions.Count,
                TotalMessages = allSessions.Sum(s => s.Messages.Count)
            },
            UserStats = userStats,
            TopSessions = topSessions,
            RiskProfile = riskProfile
        });
    }

    /// <summary>Add tokens to a registered user's token limit. Amount must be positive.</summary>
    [HttpPatch("users/{userId}/tokens")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddTokens(string userId, [FromBody] AddTokensRequest request)
    {
        if (request.Amount <= 0)
            return BadRequest(new { error = "Amount must be a positive number." });

        var user = await userRepo.GetByIdAsync(userId);
        if (user == null) return NotFound(new { error = "User not found." });

        await userRepo.AddToTokenLimitAsync(userId, request.Amount);
        return Ok(new
        {
            message = $"Added {request.Amount:N0} tokens to {user.Username}.",
            newLimit = user.TokenLimit + request.Amount,
            tokensUsed = user.TokensUsed
        });
    }

    [HttpPatch("users/{userId}/block")]
    public async Task<IActionResult> SetBlocked(string userId, [FromBody] BlockUserRequest request)
    {
        var user = await userRepo.GetByIdAsync(userId);
        if (user == null) return NotFound(new { error = "User not found." });
        if (user.Role == "admin") return BadRequest(new { error = "Cannot block an admin user." });

        await userRepo.SetBlockedAsync(userId, request.Blocked);
        return Ok(new { message = request.Blocked ? $"{user.Username} has been blocked." : $"{user.Username} has been unblocked.", blocked = request.Blocked });
    }
}
