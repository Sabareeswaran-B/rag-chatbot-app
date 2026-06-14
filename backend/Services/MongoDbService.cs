using MongoDB.Driver;
using MongoDB.Bson;
using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public class MongoDbService : IMongoDbService
{
    private readonly IMongoCollection<DocumentChunk> _collection;
    private readonly IMongoCollection<FileMetadata> _fileMetadata;
    private readonly bool _useAtlasVectorSearch;
    private readonly ILogger<MongoDbService> _logger;

    public MongoDbService(IConfiguration configuration, ILogger<MongoDbService> logger)
    {
        _logger = logger;
        var connectionString = configuration["MongoDB:ConnectionString"]
            ?? throw new InvalidOperationException("MongoDB:ConnectionString not configured");
        var databaseName = configuration["MongoDB:DatabaseName"] ?? "ragchatbot";
        _useAtlasVectorSearch = bool.TryParse(configuration["MongoDB:UseAtlasVectorSearch"], out var val) && val;

        _logger.LogInformation("MongoDbService init — db={Db} atlasVectorSearch={Atlas}", databaseName, _useAtlasVectorSearch);

        var client = new MongoClient(connectionString);
        var database = client.GetDatabase(databaseName);
        _collection = database.GetCollection<DocumentChunk>("document_chunks");
        _fileMetadata = database.GetCollection<FileMetadata>("file_metadata");

        _collection.Indexes.CreateOne(new CreateIndexModel<DocumentChunk>(
            Builders<DocumentChunk>.IndexKeys.Ascending(x => x.FileName)));
        _fileMetadata.Indexes.CreateOne(new CreateIndexModel<FileMetadata>(
            Builders<FileMetadata>.IndexKeys.Ascending(x => x.ContentHash),
            new CreateIndexOptions { Unique = true }));
        _fileMetadata.Indexes.CreateOne(new CreateIndexModel<FileMetadata>(
            Builders<FileMetadata>.IndexKeys.Ascending(x => x.FileName),
            new CreateIndexOptions { Unique = true }));
    }

    public async Task SaveChunksAsync(string fileName, List<string> chunks, List<float[]> embeddings, string fileType, string contentHash)
    {
        var deleted = await _collection.DeleteManyAsync(Builders<DocumentChunk>.Filter.Eq(x => x.FileName, fileName));
        _logger.LogInformation("SaveChunks: deleted {Deleted} existing chunks for {FileName}", deleted.DeletedCount, fileName);

        var now = DateTime.UtcNow;
        var documents = chunks.Select((chunk, i) => new DocumentChunk
        {
            FileName = fileName,
            Content = chunk,
            Embedding = embeddings[i],
            ChunkIndex = i,
            ContentHash = contentHash,
            UploadedAt = now,
            FileType = fileType
        }).ToList();

        // Validate embeddings before insert
        var emptyEmbeddings = documents.Count(d => d.Embedding == null || d.Embedding.Length == 0);
        if (emptyEmbeddings > 0)
            _logger.LogWarning("SaveChunks: {Empty}/{Total} chunks have empty embeddings for {FileName}", emptyEmbeddings, documents.Count, fileName);

        await _collection.InsertManyAsync(documents);
        _logger.LogInformation("SaveChunks: inserted {Count} chunks for {FileName} (embedding dim={Dim})",
            documents.Count, fileName, documents.FirstOrDefault()?.Embedding?.Length ?? 0);
    }

    public async Task<List<DocumentChunk>> VectorSearchAsync(float[] queryEmbedding, int limit = 5)
    {
        if (_useAtlasVectorSearch)
            return await AtlasVectorSearchAsync(queryEmbedding, limit);
        return await CosineSimFallbackAsync(queryEmbedding, limit);
    }

    // Overload that accepts a pre-loaded chunk list — avoids redundant MongoDB round-trips
    // when called multiple times in the same multi-query fan-out.
    public List<DocumentChunk> VectorSearchInMemory(float[] queryEmbedding, List<DocumentChunk> allChunks, int limit = 5)
    {
        return allChunks
            .Select(doc => (doc, score: CosineSimilarity(queryEmbedding, doc.Embedding)))
            .OrderByDescending(x => x.score)
            .Take(limit)
            .Select(x => x.doc)
            .ToList();
    }

    public async Task<List<DocumentChunk>> GetAllChunksAsync()
    {
        var all = await _collection.Find(FilterDefinition<DocumentChunk>.Empty).ToListAsync();
        _logger.LogInformation("GetAllChunks: {Count} total chunks in collection", all.Count);

        if (all.Count > 0)
        {
            var withEmbedding = all.Count(d => d.Embedding != null && d.Embedding.Length > 0);
            _logger.LogInformation("GetAllChunks: {WithEmbedding}/{Total} chunks have embeddings (dim={Dim})",
                withEmbedding, all.Count, all.FirstOrDefault(d => d.Embedding?.Length > 0)?.Embedding?.Length ?? 0);
        }

        return all;
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
        _logger.LogInformation("AtlasVectorSearch: returned {Count} results", results.Count);
        return results;
    }

    private async Task<List<DocumentChunk>> CosineSimFallbackAsync(float[] queryEmbedding, int limit)
    {
        var all = await GetAllChunksAsync();
        if (all.Count == 0) return [];

        var top = all
            .Select(doc => (doc, score: CosineSimilarity(queryEmbedding, doc.Embedding)))
            .OrderByDescending(x => x.score)
            .Take(limit)
            .ToList();

        _logger.LogInformation("CosineSimFallback: top score={TopScore:F4}, bottom score={BottomScore:F4}",
            top.FirstOrDefault().score, top.LastOrDefault().score);

        return top.Select(x => x.doc).ToList();
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return 0f;
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
        var metas = await _fileMetadata.Find(FilterDefinition<FileMetadata>.Empty)
            .SortByDescending(m => m.UploadedAt)
            .ToListAsync();

        return metas.Select(m => new UploadedFile
        {
            FileName = m.FileName,
            ChunkCount = m.ChunkCount,
            UploadedAt = m.UploadedAt,
            ContentHash = m.ContentHash,
            FileSize = m.FileSize,
            FileType = m.FileType,
            CharacterCount = m.CharacterCount,
            UploadedBy = m.UploadedBy
        }).ToList();
    }

    public async Task DeleteFileChunksAsync(string fileName)
    {
        await _collection.DeleteManyAsync(Builders<DocumentChunk>.Filter.Eq(x => x.FileName, fileName));
        await _fileMetadata.DeleteOneAsync(Builders<FileMetadata>.Filter.Eq(x => x.FileName, fileName));
    }

    public async Task<List<DocumentChunk>> GetChunksByFileAsync(string fileName)
        => await _collection
            .Find(Builders<DocumentChunk>.Filter.Eq(x => x.FileName, fileName))
            .SortBy(x => x.ChunkIndex)
            .ToListAsync();

    public async Task<FileMetadata?> GetFileByHashAsync(string contentHash)
        => await _fileMetadata.Find(Builders<FileMetadata>.Filter.Eq(x => x.ContentHash, contentHash))
            .FirstOrDefaultAsync();

    public async Task SaveFileMetadataAsync(FileMetadata metadata)
    {
        await _fileMetadata.ReplaceOneAsync(
            Builders<FileMetadata>.Filter.Eq(x => x.FileName, metadata.FileName),
            metadata,
            new ReplaceOptions { IsUpsert = true });
    }

    public async Task DeleteFileMetadataAsync(string fileName)
        => await _fileMetadata.DeleteOneAsync(Builders<FileMetadata>.Filter.Eq(x => x.FileName, fileName));
}
