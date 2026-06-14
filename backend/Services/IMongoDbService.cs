using RagChatbot.API.Models;

namespace RagChatbot.API.Services;

public interface IMongoDbService
{
    Task SaveChunksAsync(string fileName, List<string> chunks, List<float[]> embeddings, string fileType, string contentHash);
    Task<List<DocumentChunk>> VectorSearchAsync(float[] queryEmbedding, int limit = 5);
    Task<List<DocumentChunk>> GetAllChunksAsync();
    List<DocumentChunk> VectorSearchInMemory(float[] queryEmbedding, List<DocumentChunk> allChunks, int limit = 5);
    Task<List<UploadedFile>> GetUploadedFilesAsync();
    Task DeleteFileChunksAsync(string fileName);
    Task<List<DocumentChunk>> GetChunksByFileAsync(string fileName);
    Task<FileMetadata?> GetFileByHashAsync(string contentHash);
    Task SaveFileMetadataAsync(FileMetadata metadata);
    Task DeleteFileMetadataAsync(string fileName);
}
