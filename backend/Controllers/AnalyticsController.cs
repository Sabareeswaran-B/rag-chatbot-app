using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RagChatbot.API.Models;
using RagChatbot.API.Services;

namespace RagChatbot.API.Controllers;

/// <summary>Analytics — admin-only token usage and cost statistics per user and session.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "admin")]
[Produces("application/json")]
public class AnalyticsController(IChatHistoryRepository historyRepo, IUserRepository userRepo) : ControllerBase
{
    private static decimal CalculateCost(long inputTokens, long outputTokens)
        => (inputTokens * 0.00000015m) + (outputTokens * 0.00000060m);

    private static string AnonLabel(string userId)
        => $"anon-{userId[..Math.Min(8, userId.Length)]}";

    /// <summary>
    /// Returns aggregated token usage and cost for all users (including zero-usage registered users),
    /// ordered by most recent activity, plus the top 5 most expensive sessions with full message history.
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

        // All registered users (includes 0-usage)
        foreach (var user in allUsers)
        {
            var sessions = sessionsByUser.GetValueOrDefault(user.Id!, []);
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
                LastActivity = sessions.Any() ? sessions.Max(s => s.UpdatedAt) : null
            });
        }

        // Anonymous users — any userId not in the registered users set
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
                LastActivity = sessions.Max(s => s.UpdatedAt)
            });
        }

        // Sort by most recent activity, users with activity first
        userStats = userStats
            .OrderByDescending(u => u.LastActivity ?? DateTime.MinValue)
            .ToList();

        var userDict = allUsers
            .Where(u => u.Id != null)
            .ToDictionary(u => u.Id!, u => u.Username);

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
                    Role = m.Role,
                    Content = m.Content,
                    Sources = m.Sources,
                    Timestamp = m.Timestamp
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
            TopSessions = topSessions
        });
    }
}
