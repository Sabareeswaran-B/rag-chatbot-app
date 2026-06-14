namespace RagChatbot.API.Models;

public record ChatRequest(string Query, string? AnonymousId = null);

public class ChatResponse
{
    public string Answer { get; set; } = string.Empty;
    public List<string> Sources { get; set; } = new();
    public string? Reasoning { get; set; }
    public bool Success { get; set; } = true;
    public string? Error { get; set; }
}

public class UploadResponse
{
    public bool Success { get; set; }
    public string FileName { get; set; } = string.Empty;
    public int ChunksCreated { get; set; }
    public string? Error { get; set; }
}

public class UploadedFile
{
    public string FileName { get; set; } = string.Empty;
    public int ChunkCount { get; set; }
    public DateTime UploadedAt { get; set; }
}
