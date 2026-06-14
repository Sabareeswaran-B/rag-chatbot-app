using MongoDB.Driver;
using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public class UserRepository : IUserRepository
{
    private readonly IMongoCollection<User> _users;
    private readonly IMongoCollection<StoredToken> _tokens;

    public UserRepository(IConfiguration configuration)
    {
        var connectionString = configuration["MongoDB:ConnectionString"]
            ?? throw new InvalidOperationException("MongoDB:ConnectionString not configured");
        var databaseName = configuration["MongoDB:DatabaseName"] ?? "ragchatbot";
        var client = new MongoClient(connectionString);
        var db = client.GetDatabase(databaseName);
        _users = db.GetCollection<User>("users");
        _tokens = db.GetCollection<StoredToken>("stored_tokens");

        _users.Indexes.CreateOne(new CreateIndexModel<User>(
            Builders<User>.IndexKeys.Ascending(u => u.Username),
            new CreateIndexOptions { Unique = true }));
        _tokens.Indexes.CreateOne(new CreateIndexModel<StoredToken>(
            Builders<StoredToken>.IndexKeys.Ascending(t => t.TokenHash)));
        _tokens.Indexes.CreateOne(new CreateIndexModel<StoredToken>(
            Builders<StoredToken>.IndexKeys.Ascending(t => t.ExpiresAt),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));
    }

    public async Task<User?> GetByUsernameAsync(string username)
        => await _users.Find(u => u.Username == username).FirstOrDefaultAsync();

    public async Task<User?> GetByIdAsync(string id)
        => await _users.Find(u => u.Id == id).FirstOrDefaultAsync();

    public async Task<User> CreateAsync(User user)
    {
        await _users.InsertOneAsync(user);
        return user;
    }

    public async Task<long> CountAsync()
        => await _users.CountDocumentsAsync(FilterDefinition<User>.Empty);

    public async Task SaveTokenAsync(StoredToken token)
        => await _tokens.InsertOneAsync(token);

    public async Task<StoredToken?> GetTokenAsync(string tokenHash, string tokenType)
        => await _tokens.Find(t =>
            t.TokenHash == tokenHash &&
            t.TokenType == tokenType &&
            !t.IsRevoked &&
            t.ExpiresAt > DateTime.UtcNow).FirstOrDefaultAsync();

    public async Task RevokeTokenAsync(string tokenHash)
        => await _tokens.UpdateOneAsync(
            t => t.TokenHash == tokenHash,
            Builders<StoredToken>.Update.Set(t => t.IsRevoked, true));

    public async Task RevokeAllUserTokensAsync(string userId, string tokenType)
        => await _tokens.UpdateManyAsync(
            t => t.UserId == userId && t.TokenType == tokenType && !t.IsRevoked,
            Builders<StoredToken>.Update.Set(t => t.IsRevoked, true));
}
