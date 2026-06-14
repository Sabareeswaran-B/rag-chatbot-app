using MongoDB.Driver;
using MongoDB.Bson;
using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public class MongoDbService : IMongoDbService
{
    private readonly IMongoCollection<DocumentChunk> _collection;
    private readonly IMongoCollection<FileMetadata> _fileMetadata;
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
        await _collection.DeleteManyAsync(Builders<DocumentChunk>.Filter.Eq(x => x.FileName, fileName));

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
        return await _collection.Aggregate<DocumentChunk>(pipeline).ToListAsync();
    }

    private async Task<List<DocumentChunk>> CosineSimFallbackAsync(float[] queryEmbedding, int limit)
    {
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
        // Upsert by FileName so re-uploading a file name replaces its metadata
        await _fileMetadata.ReplaceOneAsync(
            Builders<FileMetadata>.Filter.Eq(x => x.FileName, metadata.FileName),
            metadata,
            new ReplaceOptions { IsUpsert = true });
    }

    public async Task DeleteFileMetadataAsync(string fileName)
        => await _fileMetadata.DeleteOneAsync(Builders<FileMetadata>.Filter.Eq(x => x.FileName, fileName));
}
