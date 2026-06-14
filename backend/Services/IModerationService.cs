namespace RagChatbot.API.Services;

public class ContentModerationResult
{
    public bool IsFlagged { get; set; }
    public string? Category { get; set; }
}

public interface IModerationService
{
    /// <summary>
    /// Checks query against all moderation categories.
    /// Returns the result plus token counts consumed by the LLM-based check
    /// (built-in moderation API is free and always returns 0 tokens).
    /// </summary>
    Task<(ContentModerationResult Result, int InputTokens, int OutputTokens)> CheckAsync(string query);
}
