using MongoDB.Driver;
using MongoDB.Bson;
using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public class MongoDbService : IMongoDbService
{
    private readonly IMongoCollection<DocumentChunk> _collection;
    private readonly bool _useAtlasVectorSearch;

    public MongoDbService(IConfiguration configuration)
    {
        var connectionString = configuration["MongoDB:ConnectionString"]
            ?? throw new InvalidOperationException("MongoDB:ConnectionString not configured");
        var databaseName = configuration["MongoDB:DatabaseName"] ?? "ragchatbot";
        _useAtlasVectorSearch = bool.TryParse(configuration["MongoDB:UseAtlasVectorSearch"], out var val) && val;

        var client = new MongoClient(connectionString);
        var database = client.GetDatabase(databaseName);
        _collection = database.GetCollection<DocumentChunk>("document_chunks");

        // Create text index on FileName for filtering
        var indexKeys = Builders<DocumentChunk>.IndexKeys.Ascending(x => x.FileName);
        _collection.Indexes.CreateOne(new CreateIndexModel<DocumentChunk>(indexKeys));
    }

    public async Task SaveChunksAsync(string fileName, List<string> chunks, List<float[]> embeddings, string fileType)
    {
        // Delete existing chunks for this file
        await _collection.DeleteManyAsync(Builders<DocumentChunk>.Filter.Eq(x => x.FileName, fileName));

        var documents = chunks.Select((chunk, i) => new DocumentChunk
        {
            FileName = fileName,
            Content = chunk,
            Embedding = embeddings[i],
            ChunkIndex = i,
            UploadedAt = DateTime.UtcNow,
            FileType = fileType
        }).ToList();

        await _collection.InsertManyAsync(documents);
    }

    public async Task<List<DocumentChunk>> VectorSearchAsync(float[] queryEmbedding, int limit = 5)
    {
        if (_useAtlasVectorSearch)
            return await AtlasVectorSearchAsync(queryEmbedding, limit);
        return await CosineSimFallbackAsync(queryEmbedding, limit);
    }

    private async Task<List<DocumentChunk>> AtlasVectorSearchAsync(float[] queryEmbedding, int limit)
    {
        var pipeline = new[]
        {
            new BsonDocument("$vectorSearch", new BsonDocument
            {
                { "index", "vector_index" },
                { "path", "Embedding" },
                { "queryVector", new BsonArray(queryEmbedding.Select(f => (BsonValue)(double)f)) },
                { "numCandidates", limit * 10 },
                { "limit", limit }
            })
        };

        var results = await _collection.Aggregate<DocumentChunk>(pipeline).ToListAsync();
        return results;
    }

    private async Task<List<DocumentChunk>> CosineSimFallbackAsync(float[] queryEmbedding, int limit)
    {
        // Fetch all and compute cosine similarity in memory (suitable for small datasets)
        var all = await _collection.Find(FilterDefinition<DocumentChunk>.Empty).ToListAsync();

        return all
            .Select(doc => (doc, score: CosineSimilarity(queryEmbedding, doc.Embedding)))
            .OrderByDescending(x => x.score)
            .Take(limit)
            .Select(x => x.doc)
            .ToList();
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        float denom = (float)(Math.Sqrt(normA) * Math.Sqrt(normB));
        return denom == 0 ? 0 : dot / denom;
    }

    public async Task<List<UploadedFile>> GetUploadedFilesAsync()
    {
        var pipeline = new[]
        {
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", "$FileName" },
                { "chunkCount", new BsonDocument("$sum", 1) },
                { "uploadedAt", new BsonDocument("$first", "$UploadedAt") }
            }),
            new BsonDocument("$sort", new BsonDocument("uploadedAt", -1))
        };

        var results = await _collection.Aggregate<BsonDocument>(pipeline).ToListAsync();
        return results.Select(r => new UploadedFile
        {
            FileName = r["_id"].AsString,
            ChunkCount = r["chunkCount"].AsInt32,
            UploadedAt = r["uploadedAt"].ToUniversalTime()
        }).ToList();
    }

    public async Task DeleteFileChunksAsync(string fileName)
    {
        await _collection.DeleteManyAsync(Builders<DocumentChunk>.Filter.Eq(x => x.FileName, fileName));
    }

    public async Task<List<DocumentChunk>> GetChunksByFileAsync(string fileName)
        => await _collection
            .Find(Builders<DocumentChunk>.Filter.Eq(x => x.FileName, fileName))
            .SortBy(x => x.ChunkIndex)
            .ToListAsync();
}
