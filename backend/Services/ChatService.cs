using OpenAI.Chat;
using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public class ChatService : IChatService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IMongoDbService _mongoDbService;
    private readonly IChatHistoryRepository _historyRepo;
    private readonly IUserRepository _userRepo;
    private readonly IRerankingService _rerankingService;
    private readonly IQueryRewriteService _queryRewriteService;
    private readonly IModerationService _moderationService;
    private readonly IModerationViolationRepository _violationRepo;
    private readonly IMultiQueryService _multiQueryService;
    private readonly ChatClient _chatClient;

    public ChatService(IEmbeddingService embeddingService, IMongoDbService mongoDbService,
        IChatHistoryRepository historyRepo, IUserRepository userRepo,
        IRerankingService rerankingService, IQueryRewriteService queryRewriteService,
        IModerationService moderationService, IModerationViolationRepository violationRepo,
        IMultiQueryService multiQueryService, IConfiguration configuration)
    {
        _embeddingService = embeddingService;
        _mongoDbService = mongoDbService;
        _historyRepo = historyRepo;
        _userRepo = userRepo;
        _rerankingService = rerankingService;
        _queryRewriteService = queryRewriteService;
        _moderationService = moderationService;
        _violationRepo = violationRepo;
        _multiQueryService = multiQueryService;
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

        // Moderation check — runs built-in API + LLM check in parallel
        var (moderation, modInputTokens, modOutputTokens) = await _moderationService.CheckAsync(query);
        if (moderation.IsFlagged)
        {
            var username = user?.Username ?? $"anon-{userId[..Math.Min(8, userId.Length)]}";
            await _violationRepo.SaveAsync(new ModerationViolation
            {
                UserId = userId,
                Username = username,
                IsAnonymous = user == null,
                Query = query,
                Category = moderation.Category ?? "Policy Violation"
            });
            if (user != null && modInputTokens + modOutputTokens > 0)
                await _userRepo.IncrementTokensUsedAsync(userId, modInputTokens + modOutputTokens);
            return new ChatResponse
            {
                Answer = $"Your message was flagged for {moderation.Category}. Please ensure your queries comply with our usage policy.",
                Sources = [], Success = false, Error = "CONTENT_MODERATED"
            };
        }

        // Load recent history to rewrite follow-up queries into self-contained search queries
        List<SessionMessage> recentHistory = [];
        if (sessionId != null)
        {
            var existingSession = await _historyRepo.GetAsync(sessionId, userId);
            recentHistory = existingSession?.Messages.TakeLast(6).ToList() ?? [];
        }

        var (searchQuery, rewriteInput, rewriteOutput) = await _queryRewriteService.RewriteAsync(query, recentHistory);

        // Generate query variations and embed original in parallel
        var variationsTask = _multiQueryService.GenerateVariationsAsync(searchQuery);
        var primaryEmbeddingTask = _embeddingService.GetEmbeddingAsync(searchQuery);
        await Task.WhenAll(variationsTask, primaryEmbeddingTask);

        var (variations, mqInput, mqOutput) = variationsTask.Result;
        var primaryEmbedding = primaryEmbeddingTask.Result;

        // Run vector searches for original + all variations in parallel (10 candidates each)
        var searchTasks = new List<Task<List<DocumentChunk>>>
        {
            _mongoDbService.VectorSearchAsync(primaryEmbedding, 10)
        };
        foreach (var variation in variations)
        {
            searchTasks.Add(Task.Run(async () =>
            {
                var emb = await _embeddingService.GetEmbeddingAsync(variation);
                return await _mongoDbService.VectorSearchAsync(emb, 10);
            }));
        }
        await Task.WhenAll(searchTasks);

        // Deduplicate by chunk ID, preserving order (primary results first)
        var seen = new HashSet<string>();
        var candidates = new List<DocumentChunk>();
        foreach (var chunk in searchTasks.SelectMany(t => t.Result))
        {
            if (chunk.Id != null && seen.Add(chunk.Id))
                candidates.Add(chunk);
        }

        if (candidates.Count == 0)
        {
            var noDocResponse = new ChatResponse
            {
                Answer = "I don't have any relevant documents to answer your question. Please upload some documents first.",
                Sources = [], Success = true
            };
            await PersistToSessionAsync(sessionId, userId, query, noDocResponse, rewriteInput + mqInput, rewriteOutput + mqOutput);
            if (user != null && rewriteInput + mqInput + rewriteOutput + mqOutput > 0)
                await _userRepo.IncrementTokensUsedAsync(userId, rewriteInput + mqInput + rewriteOutput + mqOutput);
            return noDocResponse;
        }

        var (relevantChunks, rerankInput, rerankOutput) = await _rerankingService.RerankAsync(searchQuery, candidates, topK: 5);

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
            - Cite sources inline in your answer using bracketed numbers: [1], [2], etc.
            - Be concise, accurate, and well-structured.
            - Format your response as valid JSON matching this exact structure:
              {
                "answer": "Your answer with inline citations like [1] and [2].",
                "reasoning": "Brief explanation of how you derived the answer from the sources",
                "sources": ["filename1.pdf", "filename2.txt"]
              }
            - The sources array must be ordered to match citation numbers: sources[0] = [1], sources[1] = [2], etc.
            - Only include a filename in sources if you cited it inline. No unused sources.
            """;

        var userMessage = $"""
            Context Documents:
            {contextBuilder}

            Question: {query}

            Respond with JSON only.
            """;

        var chatMessages = new List<ChatMessage> { new SystemChatMessage(systemPrompt) };

        foreach (var msg in recentHistory)
        {
            if (msg.Role == "user")
                chatMessages.Add(new UserChatMessage(msg.Content));
            else
                chatMessages.Add(new AssistantChatMessage(msg.Content));
        }

        chatMessages.Add(new UserChatMessage(userMessage));

        bool isNew = sessionId == null;
        var completionTask = _chatClient.CompleteChatAsync(chatMessages);
        var nameTask = isNew
            ? GenerateSessionNameAsync(query)
            : Task.FromResult(("New Chat", 0, 0));

        await Task.WhenAll(completionTask, nameTask);

        var completionResult = completionTask.Result.Value;
        var rawResponse = completionResult.Content[0].Text;
        var mainInputTokens = (int)(completionResult.Usage?.InputTokenCount ?? 0);
        var mainOutputTokens = (int)(completionResult.Usage?.OutputTokenCount ?? 0);
        var (sessionName, nameInput, nameOutput) = nameTask.Result;

        var totalInputTokens = mainInputTokens + rewriteInput + rerankInput + nameInput + modInputTokens + mqInput;
        var totalOutputTokens = mainOutputTokens + rewriteOutput + rerankOutput + nameOutput + modOutputTokens + mqOutput;

        var response = ParseResponse(rawResponse, relevantChunks);
        await PersistToSessionAsync(sessionId, userId, query, response, totalInputTokens, totalOutputTokens, isNew, sessionName);

        if (user != null && totalInputTokens + totalOutputTokens > 0)
            await _userRepo.IncrementTokensUsedAsync(userId, totalInputTokens + totalOutputTokens);

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

    private async Task<(string Name, int InputTokens, int OutputTokens)> GenerateSessionNameAsync(string query)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("Generate a short title (2-5 words) for a conversation starting with this question. Return ONLY the title, no punctuation."),
                new UserChatMessage(query.Length > 200 ? query[..200] : query)
            };
            var result = await _chatClient.CompleteChatAsync(messages);
            var name = result.Value.Content[0].Text.Trim().Trim('"').Trim('.');
            var inputTokens = (int)(result.Value.Usage?.InputTokenCount ?? 0);
            var outputTokens = (int)(result.Value.Usage?.OutputTokenCount ?? 0);
            return (name, inputTokens, outputTokens);
        }
        catch { return ("New Chat", 0, 0); }
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
