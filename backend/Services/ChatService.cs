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
    private readonly ILogger<ChatService> _logger;

    public ChatService(IEmbeddingService embeddingService, IMongoDbService mongoDbService,
        IChatHistoryRepository historyRepo, IUserRepository userRepo,
        IRerankingService rerankingService, IQueryRewriteService queryRewriteService,
        IModerationService moderationService, IModerationViolationRepository violationRepo,
        IMultiQueryService multiQueryService, IConfiguration configuration,
        ILogger<ChatService> logger)
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
        _logger = logger;
        var apiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _chatClient = new ChatClient("gpt-4o-mini", apiKey);
    }

    public async Task<ChatResponse> GetAnswerAsync(string query, string? sessionId, string userId)
    {
        _logger.LogInformation("[Chat] Pipeline start — userId={UserId} sessionId={SessionId}", userId, sessionId ?? "new");

        // 1. Token limit check
        var user = await _userRepo.GetByIdAsync(userId);
        if (user != null && user.TokenLimit > 0 && user.TokensUsed >= user.TokenLimit)
        {
            _logger.LogWarning("[Chat] Token limit exceeded — userId={UserId} used={Used} limit={Limit}",
                userId, user.TokensUsed, user.TokenLimit);
            return new ChatResponse
            {
                Answer = "Your token limit has been exhausted. Please contact an administrator to add more tokens.",
                Sources = [], Success = false, Error = "TOKEN_LIMIT_EXCEEDED"
            };
        }

        // 2. Moderation — built-in API + LLM check in parallel
        _logger.LogInformation("[Chat] Running moderation check");
        var (moderation, modInputTokens, modOutputTokens) = await _moderationService.CheckAsync(query);
        if (moderation.IsFlagged)
        {
            _logger.LogWarning("[Chat] Query flagged — userId={UserId} category={Category}", userId, moderation.Category);
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
        _logger.LogInformation("[Chat] Moderation passed (tokens: in={In} out={Out})", modInputTokens, modOutputTokens);

        // 3. Load session history
        List<SessionMessage> recentHistory = [];
        if (sessionId != null)
        {
            var existingSession = await _historyRepo.GetAsync(sessionId, userId);
            recentHistory = existingSession?.Messages.TakeLast(6).ToList() ?? [];
            _logger.LogInformation("[Chat] Loaded {MsgCount} history messages for session {SessionId}", recentHistory.Count, sessionId);
        }

        // 4. Query rewrite
        _logger.LogInformation("[Chat] Rewriting query with history context");
        var (searchQuery, rewriteInput, rewriteOutput) = await _queryRewriteService.RewriteAsync(query, recentHistory);
        _logger.LogInformation("[Chat] Rewritten query: {SearchQuery} (tokens: in={In} out={Out})",
            searchQuery.Length > 100 ? searchQuery[..100] + "…" : searchQuery, rewriteInput, rewriteOutput);

        // 5. Generate variations + embed original in parallel
        _logger.LogInformation("[Chat] Generating query variations and primary embedding in parallel");
        var variationsTask = _multiQueryService.GenerateVariationsAsync(searchQuery);
        var primaryEmbeddingTask = _embeddingService.GetEmbeddingAsync(searchQuery);
        await Task.WhenAll(variationsTask, primaryEmbeddingTask);

        var (variations, mqInput, mqOutput) = variationsTask.Result;
        var primaryEmbedding = primaryEmbeddingTask.Result;
        _logger.LogInformation("[Chat] Got {VariationCount} query variations (tokens: in={In} out={Out})", variations.Count, mqInput, mqOutput);

        // 6. Fan-out vector searches in parallel (10 candidates each)
        // For cosine fallback: load all chunks once, then score in-memory — avoids N redundant DB round-trips.
        _logger.LogInformation("[Chat] Running {SearchCount} vector searches in parallel", variations.Count + 1);
        var allChunks = await _mongoDbService.GetAllChunksAsync();

        List<Task<List<DocumentChunk>>> searchTasks;
        if (allChunks.Count == 0)
        {
            _logger.LogWarning("[Chat] No chunks in collection — skipping vector search");
            searchTasks = [];
        }
        else
        {
            searchTasks =
            [
                Task.FromResult(_mongoDbService.VectorSearchInMemory(primaryEmbedding, allChunks, 10))
            ];
            foreach (var variation in variations)
            {
                searchTasks.Add(Task.Run(async () =>
                {
                    var emb = await _embeddingService.GetEmbeddingAsync(variation);
                    return _mongoDbService.VectorSearchInMemory(emb, allChunks, 10);
                }));
            }
            await Task.WhenAll(searchTasks);
        }

        // 7. Deduplicate by chunk ID
        var seen = new HashSet<string>();
        var candidates = new List<DocumentChunk>();
        foreach (var chunk in searchTasks.SelectMany(t => t.Result))
        {
            if (chunk.Id != null && seen.Add(chunk.Id))
                candidates.Add(chunk);
        }
        _logger.LogInformation("[Chat] Vector search complete — {CandidateCount} unique candidates after dedup", candidates.Count);

        if (candidates.Count == 0)
        {
            _logger.LogWarning("[Chat] No relevant documents found for query");
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

        // 8. Rerank
        _logger.LogInformation("[Chat] Reranking {CandidateCount} candidates to top 5", candidates.Count);
        var (relevantChunks, rerankInput, rerankOutput) = await _rerankingService.RerankAsync(searchQuery, candidates, topK: 5);
        _logger.LogInformation("[Chat] Reranking complete — {ChunkCount} chunks selected (tokens: in={In} out={Out})",
            relevantChunks.Count, rerankInput, rerankOutput);

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

        // 9. LLM completion + session name in parallel
        bool isNew = sessionId == null;
        _logger.LogInformation("[Chat] Calling LLM ({MsgCount} messages, isNewSession={IsNew})", chatMessages.Count, isNew);
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

        // 10. Accumulate tokens
        var totalInputTokens = mainInputTokens + rewriteInput + rerankInput + nameInput + modInputTokens + mqInput;
        var totalOutputTokens = mainOutputTokens + rewriteOutput + rerankOutput + nameOutput + modOutputTokens + mqOutput;
        _logger.LogInformation("[Chat] LLM complete — tokens: main=({In}/{Out}) total=({TotalIn}/{TotalOut})",
            mainInputTokens, mainOutputTokens, totalInputTokens, totalOutputTokens);

        var response = ParseResponse(rawResponse, relevantChunks);
        _logger.LogInformation("[Chat] Persisting session — isNew={IsNew} sessionName={Name}", isNew, isNew ? sessionName : "(existing)");
        await PersistToSessionAsync(sessionId, userId, query, response, totalInputTokens, totalOutputTokens, isNew, sessionName);

        if (user != null && totalInputTokens + totalOutputTokens > 0)
            await _userRepo.IncrementTokensUsedAsync(userId, totalInputTokens + totalOutputTokens);

        _logger.LogInformation("[Chat] Pipeline complete — sessionId={SessionId} sources={Sources}",
            response.SessionId, string.Join(", ", response.Sources ?? []));
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
            _logger.LogInformation("[Chat] Session name generated: \"{Name}\" (tokens: in={In} out={Out})", name, inputTokens, outputTokens);
            return (name, inputTokens, outputTokens);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Chat] Session name generation failed, using default");
            return ("New Chat", 0, 0);
        }
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Chat] Failed to parse LLM JSON response, returning raw text");
            return new ChatResponse
            {
                Answer = rawResponse,
                Sources = chunks.Select(c => c.FileName).Distinct().ToList(),
                Success = true
            };
        }
    }
}
