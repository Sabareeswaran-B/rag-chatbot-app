using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace RagChatbot.API.Models;

public class ChatSession
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = "New Chat";
    public List<SessionMessage> Messages { get; set; } = new();
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class SessionMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public List<string> Sources { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class ChatSessionSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public int MessageCount { get; set; }
}
