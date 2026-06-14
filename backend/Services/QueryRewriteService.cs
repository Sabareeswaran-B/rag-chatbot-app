using OpenAI.Chat;
using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public class QueryRewriteService : IQueryRewriteService
{
    private readonly ChatClient _client;

    public QueryRewriteService(IConfiguration configuration)
    {
        var apiKey = configuration["OpenAI:ApiKey"]
            ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _client = new ChatClient("gpt-4o-mini", apiKey);
    }

    public async Task<(string Query, int InputTokens, int OutputTokens)> RewriteAsync(string query, List<SessionMessage> history)
    {
        if (history.Count == 0)
            return (query, 0, 0);

        var historyText = string.Join("\n", history.Select(m =>
            $"{(m.Role == "user" ? "User" : "Assistant")}: {m.Content}"));

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                "You are a query rewriter for a retrieval-augmented search system. " +
                "Given a conversation history and the user's latest message, rewrite the latest message " +
                "as a complete, self-contained search query that can be understood without any prior context. " +
                "Resolve all pronouns and references (e.g. 'it', 'that', 'the second point') using the history. " +
                "If the latest message is already fully self-contained, return it exactly as-is. " +
                "Return ONLY the rewritten query — no explanation, no punctuation changes."),
            new UserChatMessage(
                $"Conversation history:\n{historyText}\n\nLatest message: {query}")
        };

        try
        {
            var result = await _client.CompleteChatAsync(messages);
            var rewritten = result.Value.Content[0].Text.Trim().Trim('"');
            var inputTokens = (int)(result.Value.Usage?.InputTokenCount ?? 0);
            var outputTokens = (int)(result.Value.Usage?.OutputTokenCount ?? 0);
            return (string.IsNullOrWhiteSpace(rewritten) ? query : rewritten, inputTokens, outputTokens);
        }
        catch
        {
            return (query, 0, 0);
        }
    }
}
