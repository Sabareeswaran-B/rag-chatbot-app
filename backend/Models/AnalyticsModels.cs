namespace RagChatbot.API.Models;

public class AnalyticsSummary
{
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public decimal TotalCost { get; set; }
    public int TotalUsers { get; set; }
    public int TotalSessions { get; set; }
    public int TotalMessages { get; set; }
}

public class UserUsageStats
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public bool IsAnonymous { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public decimal TotalCost { get; set; }
    public int SessionCount { get; set; }
    public int MessageCount { get; set; }
    public DateTime? LastActivity { get; set; }
}

public class SessionMessageDto
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public List<string> Sources { get; set; } = new();
    public DateTime Timestamp { get; set; }
}

public class TopSessionStats
{
    public string SessionId { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal Cost { get; set; }
    public int MessageCount { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<SessionMessageDto> Messages { get; set; } = new();
}

public class AnalyticsResponse
{
    public AnalyticsSummary Summary { get; set; } = new();
    public List<UserUsageStats> UserStats { get; set; } = new();
    public List<TopSessionStats> TopSessions { get; set; } = new();
}
