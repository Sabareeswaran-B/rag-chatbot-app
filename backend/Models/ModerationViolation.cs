using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace RagChatbot.API.Models;

public class ModerationViolation
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool IsAnonymous { get; set; }
    public string Query { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
