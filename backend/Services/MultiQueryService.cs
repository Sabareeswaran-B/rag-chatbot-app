using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;

namespace RagChatbot.API.Services;

public class MultiQueryService : IMultiQueryService
{
    private readonly ChatClient _client;

    public MultiQueryService(IConfiguration configuration)
    {
        var apiKey = configuration["OpenAI:ApiKey"]
            ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _client = new ChatClient("gpt-4o-mini", apiKey);
    }

    public async Task<(List<string> Variations, int InputTokens, int OutputTokens)> GenerateVariationsAsync(string query)
    {
        var prompt = $$"""
            Generate 3 alternative search queries for the following question. Each variation should use different terminology, synonyms, or phrasing to maximize document retrieval coverage.

            Question: {{query}}

            Return ONLY a JSON object with a "queries" array of exactly 3 strings. Do not include the original question.
            Example: {"queries": ["variation one", "variation two", "variation three"]}
            """;

        try
        {
            var options = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
            };
            var result = await _client.CompleteChatAsync([new UserChatMessage(prompt)], options);

            var inputTokens = (int)(result.Value.Usage?.InputTokenCount ?? 0);
            var outputTokens = (int)(result.Value.Usage?.OutputTokenCount ?? 0);

            var parsed = JsonSerializer.Deserialize<MultiQueryResult>(
                result.Value.Content[0].Text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var variations = parsed?.Queries?
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Take(3)
                .ToList() ?? [];

            return (variations, inputTokens, outputTokens);
        }
        catch
        {
            return ([], 0, 0);
        }
    }

    private class MultiQueryResult
    {
        [JsonPropertyName("queries")]
        public List<string>? Queries { get; set; }
    }
}
