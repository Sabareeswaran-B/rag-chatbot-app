using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace RagChatbot.API.Models;

public class DocumentChunk
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public int ChunkIndex { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public string FileType { get; set; } = string.Empty;
}
