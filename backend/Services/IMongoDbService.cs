using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public interface IMongoDbService
{
    Task SaveChunksAsync(string fileName, List<string> chunks, List<float[]> embeddings, string fileType);
    Task<List<DocumentChunk>> VectorSearchAsync(float[] queryEmbedding, int limit = 5);
    Task<List<UploadedFile>> GetUploadedFilesAsync();
    Task DeleteFileChunksAsync(string fileName);
    Task<List<DocumentChunk>> GetChunksByFileAsync(string fileName);
}
