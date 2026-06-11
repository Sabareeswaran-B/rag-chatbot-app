using OpenAI.Chat;
using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public class ChatService : IChatService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IMongoDbService _mongoDbService;
    private readonly ChatClient _chatClient;

    public ChatService(IEmbeddingService embeddingService, IMongoDbService mongoDbService, IConfiguration configuration)
    {
        _embeddingService = embeddingService;
        _mongoDbService = mongoDbService;
        var apiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _chatClient = new ChatClient("gpt-4o-mini", apiKey);
    }

    public async Task<ChatResponse> GetAnswerAsync(string query)
    {
        // 1. Embed the query
        var queryEmbedding = await _embeddingService.GetEmbeddingAsync(query);

        // 2. Vector search - get top 5 relevant chunks
        var relevantChunks = await _mongoDbService.VectorSearchAsync(queryEmbedding, 5);

        if (relevantChunks.Count == 0)
        {
            return new ChatResponse
            {
                Answer = "I don't have any relevant documents to answer your question. Please upload some documents first.",
                Sources = [],
                Success = true
            };
        }

        // 3. Build context from chunks
        var contextBuilder = new System.Text.StringBuilder();
        for (int i = 0; i < relevantChunks.Count; i++)
        {
            contextBuilder.AppendLine($"[Source {i + 1}: {relevantChunks[i].FileName}]");
            contextBuilder.AppendLine(relevantChunks[i].Content);
            contextBuilder.AppendLine();
        }

        // 4. Build the prompt
        var systemPrompt = """
            You are a helpful AI assistant that answers questions based ONLY on the provided context documents.

            Rules:
            - Answer based solely on the provided context. Do not use prior knowledge.
            - If the context doesn't contain enough information, say so clearly.
            - Always cite which source(s) you used in your answer.
            - Be concise, accurate, and well-structured.
            - Format your response as valid JSON matching this exact structure:
              {
                "answer": "Your detailed answer here",
                "reasoning": "Brief explanation of how you derived the answer from the sources",
                "sources": ["filename1.pdf", "filename2.txt"]
              }
            - Only include filenames in sources that you actually referenced.
            """;

        var userMessage = $"""
            Context Documents:
            {contextBuilder}

            Question: {query}

            Respond with JSON only.
            """;

        // 5. Call OpenAI
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userMessage)
        };

        var completion = await _chatClient.CompleteChatAsync(messages);
        var rawResponse = completion.Value.Content[0].Text;

        // 6. Parse and sanitize the response
        return ParseResponse(rawResponse, relevantChunks);
    }

    private ChatResponse ParseResponse(string rawResponse, List<DocumentChunk> chunks)
    {
        try
        {
            // Strip markdown code blocks if present
            var json = rawResponse.Trim();
            if (json.StartsWith("```json")) json = json[7..];
            else if (json.StartsWith("```")) json = json[3..];
            if (json.EndsWith("```")) json = json[..^3];
            json = json.Trim();

            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var sources = new List<string>();
            if (root.TryGetProperty("sources", out var sourcesEl))
                foreach (var s in sourcesEl.EnumerateArray())
                    sources.Add(s.GetString() ?? "");

            return new ChatResponse
            {
                Answer = root.TryGetProperty("answer", out var a) ? a.GetString() ?? "" : rawResponse,
                Reasoning = root.TryGetProperty("reasoning", out var r) ? r.GetString() : null,
                Sources = sources.Count > 0 ? sources : chunks.Select(c => c.FileName).Distinct().ToList(),
                Success = true
            };
        }
        catch
        {
            return new ChatResponse
            {
                Answer = rawResponse,
                Sources = chunks.Select(c => c.FileName).Distinct().ToList(),
                Success = true
            };
        }
    }
}
