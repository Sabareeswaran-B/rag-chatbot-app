using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public interface IQueryRewriteService
{
    /// <summary>
    /// Rewrites <paramref name="query"/> into a self-contained search query using
    /// <paramref name="history"/>. Returns the original query unchanged when history is empty.
    /// Also returns the input/output token counts consumed by the rewrite call (0 when skipped).
    /// </summary>
    Task<(string Query, int InputTokens, int OutputTokens)> RewriteAsync(string query, List<SessionMessage> history);
}
