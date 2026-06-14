namespace RagChatbot.API.Services;

public interface IMultiQueryService
{
    /// <summary>
    /// Generates alternative phrasings of <paramref name="query"/> to broaden retrieval coverage.
    /// Returns an empty list (not an error) when the call fails — callers fall back to single-query search.
    /// </summary>
    Task<(List<string> Variations, int InputTokens, int OutputTokens)> GenerateVariationsAsync(string query);
}
