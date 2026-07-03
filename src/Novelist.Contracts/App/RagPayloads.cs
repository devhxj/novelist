using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record RagIndexStatePayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("provider_key")] string ProviderKey,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("dimensions")] int Dimensions,
    [property: JsonPropertyName("chunker_version")] string ChunkerVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("chunk_count")] int ChunkCount,
    [property: JsonPropertyName("vector_table")] string VectorTable,
    [property: JsonPropertyName("last_error")] string LastError,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record RagChunkPayload(
    [property: JsonPropertyName("chunk_id")] string ChunkId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")] int ChapterNumber,
    [property: JsonPropertyName("chunk_type")] string ChunkType,
    [property: JsonPropertyName("chunk_index")] int ChunkIndex,
    [property: JsonPropertyName("start_position")] int StartPosition,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("content_hash")] string ContentHash,
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("title")] string Title);

public sealed record RagSearchHitPayload(
    [property: JsonPropertyName("chunk_id")] string ChunkId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")] int ChapterNumber,
    [property: JsonPropertyName("chunk_type")] string ChunkType,
    [property: JsonPropertyName("chunk_index")] int ChunkIndex,
    [property: JsonPropertyName("start_position")] int StartPosition,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("distance")] double Distance,
    [property: JsonPropertyName("relevance")] double Relevance);
