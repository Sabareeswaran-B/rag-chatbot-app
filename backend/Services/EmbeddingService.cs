using OpenAI.Embeddings;

namespace RagChatbot.API.Services;

public class EmbeddingService : IEmbeddingService
{
    private readonly EmbeddingClient _client;
    private readonly ILogger<EmbeddingService> _logger;

    // Keep batches small to avoid large serialization overhead and API timeouts.
    // Run up to 3 batches in parallel — enough concurrency without hitting rate limits.
    private const int BatchSize = 50;
    private const int MaxConcurrency = 3;

    public EmbeddingService(IConfiguration configuration, ILogger<EmbeddingService> logger)
    {
        var apiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _client = new EmbeddingClient("text-embedding-3-small", apiKey);
        _logger = logger;
    }

    public async Task<float[]> GetEmbeddingAsync(string text)
    {
        var result = await _client.GenerateEmbeddingAsync(text);
        return result.Value.ToFloats().ToArray();
    }

    public async Task<List<float[]>> GetEmbeddingsAsync(IEnumerable<string> texts)
    {
        var textList = texts.ToList();
        if (textList.Count == 0) return [];

        // Split into batches
        var batches = new List<List<string>>();
        for (int i = 0; i < textList.Count; i += BatchSize)
            batches.Add(textList.GetRange(i, Math.Min(BatchSize, textList.Count - i)));

        _logger.LogInformation("Embedding {Total} chunks in {BatchCount} batches (batch size {BatchSize}, concurrency {MaxConcurrency})",
            textList.Count, batches.Count, BatchSize, MaxConcurrency);

        using var sem = new SemaphoreSlim(MaxConcurrency);

        // Launch all batch tasks — semaphore limits how many run at once
        var batchTasks = batches.Select(async (batch, idx) =>
        {
            await sem.WaitAsync();
            try
            {
                _logger.LogInformation("Embedding batch {Idx}/{Total} ({Count} chunks)", idx + 1, batches.Count, batch.Count);
                var result = await _client.GenerateEmbeddingsAsync(batch);
                return result.Value.Select(e => e.ToFloats().ToArray()).ToList();
            }
            finally
            {
                sem.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(batchTasks);

        _logger.LogInformation("All embedding batches complete");
        return [.. results.SelectMany(r => r)];
    }
}
