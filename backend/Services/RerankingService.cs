using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;
using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public class RerankingService : IRerankingService
{
    private readonly ChatClient _client;

    public RerankingService(IConfiguration configuration)
    {
        var apiKey = configuration["OpenAI:ApiKey"]
            ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _client = new ChatClient("gpt-4o-mini", apiKey);
    }

    public async Task<List<DocumentChunk>> RerankAsync(string query, List<DocumentChunk> chunks, int topK = 5)
    {
        if (chunks.Count <= topK)
            return chunks;

        var passages = string.Join("\n\n", chunks.Select((c, i) =>
        {
            var preview = c.Content.Length > 400 ? c.Content[..400] + "…" : c.Content;
            return $"[{i}] {preview}";
        }));

        var prompt = $$"""
            You are a relevance reranker. Given a search query and a list of text passages, rank them by relevance.

            Query: {{query}}

            Passages:
            {{passages}}

            Return ONLY a JSON object with a "ranked_indices" array of the {{topK}} most relevant passage indices, ordered from most to least relevant.
            Example: {"ranked_indices": [3, 7, 1, 15, 9]}
            """;

        try
        {
            var options = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
            };
            var result = await _client.CompleteChatAsync(
                [new UserChatMessage(prompt)],
                options);

            var json = result.Value.Content[0].Text;
            var reranked = JsonSerializer.Deserialize<RerankResult>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (reranked?.RankedIndices is { Count: > 0 })
            {
                return reranked.RankedIndices
                    .Where(i => i >= 0 && i < chunks.Count)
                    .Take(topK)
                    .Select(i => chunks[i])
                    .ToList();
            }
        }
        catch { /* fall through to default */ }

        return chunks.Take(topK).ToList();
    }

    private class RerankResult
    {
        [JsonPropertyName("ranked_indices")]
        public List<int>? RankedIndices { get; set; }
    }
}
