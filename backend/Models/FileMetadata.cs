using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace RagChatbot.API.Models;

public class FileMetadata
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;   // SHA-256 hex, lowercase
    public long FileSize { get; set; }                         // bytes
    public string FileType { get; set; } = string.Empty;      // extension without dot
    public int ChunkCount { get; set; }
    public long CharacterCount { get; set; }                   // total chars across all chunks
    public string UploadedBy { get; set; } = string.Empty;    // username from JWT
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
