using OpenAI.Chat;
using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public class ChatService : IChatService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IMongoDbService _mongoDbService;
    private readonly IChatHistoryRepository _historyRepo;
    private readonly IUserRepository _userRepo;
    private readonly ChatClient _chatClient;

    public ChatService(IEmbeddingService embeddingService, IMongoDbService mongoDbService,
        IChatHistoryRepository historyRepo, IUserRepository userRepo, IConfiguration configuration)
    {
        _embeddingService = embeddingService;
        _mongoDbService = mongoDbService;
        _historyRepo = historyRepo;
        _userRepo = userRepo;
        var apiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _chatClient = new ChatClient("gpt-4o-mini", apiKey);
    }

    public async Task<ChatResponse> GetAnswerAsync(string query, string? sessionId, string userId)
    {
        // Check token limit for registered users
        var user = await _userRepo.GetByIdAsync(userId);
        if (user != null && user.TokenLimit > 0 && user.TokensUsed >= user.TokenLimit)
        {
            return new ChatResponse
            {
                Answer = "Your token limit has been exhausted. Please contact an administrator to add more tokens.",
                Sources = [], Success = false, Error = "TOKEN_LIMIT_EXCEEDED"
            };
        }

        var queryEmbedding = await _embeddingService.GetEmbeddingAsync(query);
        var relevantChunks = await _mongoDbService.VectorSearchAsync(queryEmbedding, 5);

        if (relevantChunks.Count == 0)
        {
            var noDocResponse = new ChatResponse
            {
                Answer = "I don't have any relevant documents to answer your question. Please upload some documents first.",
                Sources = [], Success = true
            };
            await PersistToSessionAsync(sessionId, userId, query, noDocResponse, 0, 0);
            return noDocResponse;
        }

        var contextBuilder = new System.Text.StringBuilder();
        for (int i = 0; i < relevantChunks.Count; i++)
        {
            contextBuilder.AppendLine($"[Source {i + 1}: {relevantChunks[i].FileName}]");
            contextBuilder.AppendLine(relevantChunks[i].Content);
            contextBuilder.AppendLine();
        }

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

        var chatMessages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userMessage)
        };

        bool isNew = sessionId == null;
        var completionTask = _chatClient.CompleteChatAsync(chatMessages);
        var nameTask = isNew ? GenerateSessionNameAsync(query) : Task.FromResult("New Chat");

        await Task.WhenAll(completionTask, nameTask);

        var completionResult = completionTask.Result.Value;
        var rawResponse = completionResult.Content[0].Text;
        var inputTokens = (int)(completionResult.Usage?.InputTokenCount ?? 0);
        var outputTokens = (int)(completionResult.Usage?.OutputTokenCount ?? 0);
        var sessionName = nameTask.Result;

        var response = ParseResponse(rawResponse, relevantChunks);
        await PersistToSessionAsync(sessionId, userId, query, response, inputTokens, outputTokens, isNew, sessionName);

        // Increment user token usage for registered users
        if (user != null && inputTokens + outputTokens > 0)
            await _userRepo.IncrementTokensUsedAsync(userId, inputTokens + outputTokens);

        return response;
    }

    private async Task PersistToSessionAsync(string? sessionId, string userId, string query,
        ChatResponse response, int inputTokens, int outputTokens,
        bool isNew = false, string sessionName = "New Chat")
    {
        ChatSession session;
        if (isNew)
        {
            session = new ChatSession { UserId = userId, Name = sessionName };
            await _historyRepo.CreateAsync(session);
            response.SessionId = session.Id;
            response.SessionName = sessionName;
            await _historyRepo.EnforceCapAsync(userId, 30);
        }
        else
        {
            session = await _historyRepo.GetAsync(sessionId!, userId)
                ?? new ChatSession { Id = sessionId, UserId = userId, Name = "Chat" };
            response.SessionId = session.Id;
        }

        session.TotalInputTokens += inputTokens;
        session.TotalOutputTokens += outputTokens;
        session.Messages.Add(new SessionMessage { Role = "user", Content = query });
        session.Messages.Add(new SessionMessage
        {
            Role = "assistant",
            Content = response.Answer,
            Sources = response.Sources
        });
        await _historyRepo.UpdateAsync(session);
    }

    private async Task<string> GenerateSessionNameAsync(string query)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("Generate a short title (2-5 words) for a conversation starting with this question. Return ONLY the title, no punctuation."),
                new UserChatMessage(query.Length > 200 ? query[..200] : query)
            };
            var result = await _chatClient.CompleteChatAsync(messages);
            return result.Value.Content[0].Text.Trim().Trim('"').Trim('.');
        }
        catch { return "New Chat"; }
    }

    private ChatResponse ParseResponse(string rawResponse, List<DocumentChunk> chunks)
    {
        try
        {
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
