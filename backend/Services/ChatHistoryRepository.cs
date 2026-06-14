using MongoDB.Driver;
using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public class ChatHistoryRepository : IChatHistoryRepository
{
    private readonly IMongoCollection<ChatSession> _collection;

    public ChatHistoryRepository(IConfiguration configuration)
    {
        var connectionString = configuration["MongoDB:ConnectionString"]
            ?? throw new InvalidOperationException("MongoDB:ConnectionString not configured");
        var databaseName = configuration["MongoDB:DatabaseName"] ?? "ragchatbot";
        var client = new MongoClient(connectionString);
        var db = client.GetDatabase(databaseName);
        _collection = db.GetCollection<ChatSession>("chat_sessions");

        _collection.Indexes.CreateOne(new CreateIndexModel<ChatSession>(
            Builders<ChatSession>.IndexKeys.Ascending(s => s.UserId).Descending(s => s.UpdatedAt)));
    }

    public async Task<ChatSession> CreateAsync(ChatSession session)
    {
        await _collection.InsertOneAsync(session);
        return session;
    }

    public async Task<ChatSession?> GetAsync(string sessionId, string userId)
        => await _collection.Find(s => s.Id == sessionId && s.UserId == userId).FirstOrDefaultAsync();

    public async Task<List<ChatSession>> GetRecentAsync(string userId, int limit = 30)
        => await _collection.Find(s => s.UserId == userId)
            .SortByDescending(s => s.UpdatedAt)
            .Limit(limit)
            .Project<ChatSession>(Builders<ChatSession>.Projection.Exclude(s => s.Messages))
            .ToListAsync();

    public async Task UpdateAsync(ChatSession session)
    {
        session.UpdatedAt = DateTime.UtcNow;
        await _collection.ReplaceOneAsync(s => s.Id == session.Id, session);
    }

    public async Task DeleteAsync(string sessionId, string userId)
        => await _collection.DeleteOneAsync(s => s.Id == sessionId && s.UserId == userId);

    public async Task DeleteAllAsync(string userId)
        => await _collection.DeleteManyAsync(s => s.UserId == userId);

    public async Task EnforceCapAsync(string userId, int maxCount)
    {
        var sessions = await _collection.Find(s => s.UserId == userId)
            .SortBy(s => s.UpdatedAt)
            .Project<ChatSession>(Builders<ChatSession>.Projection.Include(s => s.Id))
            .ToListAsync();

        if (sessions.Count > maxCount)
        {
            var toDelete = sessions.Take(sessions.Count - maxCount).Select(s => s.Id!).ToList();
            await _collection.DeleteManyAsync(s => toDelete.Contains(s.Id!));
        }
    }

    public async Task<List<ChatSession>> GetAllSessionsForAnalyticsAsync()
        => await _collection.Find(MongoDB.Driver.FilterDefinition<ChatSession>.Empty)
            .SortByDescending(s => s.UpdatedAt)
            .ToListAsync();
}
