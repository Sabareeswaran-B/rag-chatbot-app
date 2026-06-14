using MongoDB.Driver;
using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public class ModerationViolationRepository : IModerationViolationRepository
{
    private readonly IMongoCollection<ModerationViolation> _collection;

    public ModerationViolationRepository(IConfiguration configuration)
    {
        var connectionString = configuration["MongoDB:ConnectionString"]
            ?? throw new InvalidOperationException("MongoDB:ConnectionString not configured");
        var databaseName = configuration["MongoDB:DatabaseName"] ?? "ragchatbot";
        var client = new MongoClient(connectionString);
        _collection = client.GetDatabase(databaseName)
            .GetCollection<ModerationViolation>("moderation_violations");

        _collection.Indexes.CreateOne(new CreateIndexModel<ModerationViolation>(
            Builders<ModerationViolation>.IndexKeys.Ascending(v => v.UserId)));
        _collection.Indexes.CreateOne(new CreateIndexModel<ModerationViolation>(
            Builders<ModerationViolation>.IndexKeys.Descending(v => v.CreatedAt)));
    }

    public async Task SaveAsync(ModerationViolation violation)
        => await _collection.InsertOneAsync(violation);

    public async Task<List<ModerationViolation>> GetAllAsync()
        => await _collection.Find(FilterDefinition<ModerationViolation>.Empty)
            .SortByDescending(v => v.CreatedAt)
            .ToListAsync();
}
