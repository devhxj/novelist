using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Infrastructure.App;

public sealed class SqliteRagIndexService : IRagIndexService, IRagSemanticSearchService, IRagIndexRefreshNotifier
{
    private const string ChunkerVersion = "paragraph-v1";
    private const int MaxChunkChars = 1800;
    private const int EmbeddingBatchSize = 64;
    private static readonly Regex BlankLinePattern = new(@"\n\s*\n", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly IChapterContentService _chapters;
    private readonly IEmbeddingConfigurationService _embeddingConfiguration;
    private readonly IEmbeddingClient _embeddings;
    private readonly ISqliteVecTableProvisioner _vecProvisioner;
    private readonly ISqliteVecQueryProvider _vecQuery;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqliteRagIndexService(
        AppInitializationOptions? options = null,
        INovelService? novels = null,
        IChapterContentService? chapters = null,
        IEmbeddingConfigurationService? embeddingConfiguration = null,
        IEmbeddingClient? embeddings = null,
        ISqliteVecTableProvisioner? vecProvisioner = null,
        ISqliteVecQueryProvider? vecQuery = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
        _chapters = chapters ?? new FileSystemChapterContentService(_options, _novels);
        _embeddingConfiguration = embeddingConfiguration ?? new NullEmbeddingConfigurationService();
        _embeddings = embeddings ?? new StandardEmbeddingClient();
        var defaultVec = vecProvisioner as SqliteVecTableProvisioner ?? new SqliteVecTableProvisioner();
        _vecProvisioner = vecProvisioner ?? defaultVec;
        _vecQuery = vecQuery ?? (vecProvisioner as ISqliteVecQueryProvider) ?? defaultVec;
    }

    public async ValueTask<RagIndexStatePayload?> GetIndexStateAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT novel_id, provider_key, model_id, dimensions, chunker_version, status,
                       chunk_count, vector_table, last_error, updated_at
                FROM rag_index_state
                WHERE novel_id = $novel_id;
                """;
            command.Parameters.AddWithValue("$novel_id", novelId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadState(reader) : null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<RagChunkPayload>> GetIndexedChunksAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT chunk_id, novel_id, chapter_number, chunk_type, chunk_index, start_position,
                       content, content_hash, file_path, title
                FROM rag_chunks
                WHERE novel_id = $novel_id
                ORDER BY chapter_number ASC, chunk_index ASC;
                """;
            command.Parameters.AddWithValue("$novel_id", novelId);
            var chunks = new List<RagChunkPayload>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                chunks.Add(ReadChunk(reader));
            }

            return chunks;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<RagIndexStatePayload> RebuildNovelAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        await EnsureNovelExistsAsync(novelId, cancellationToken);
        var chunks = await BuildChunksAsync(novelId, cancellationToken);
        var embeddingOptions = await _embeddingConfiguration.GetActiveEmbeddingOptionsAsync(cancellationToken);

        if (embeddingOptions is null)
        {
            var state = new RagIndexStatePayload(
                novelId,
                ProviderKey: string.Empty,
                ModelId: string.Empty,
                Dimensions: 0,
                ChunkerVersion,
                Status: "missing_config",
                ChunkCount: chunks.Count,
                VectorTable: string.Empty,
                LastError: "Embedding provider is not configured.",
                UpdatedAt: DateTimeOffset.UtcNow);
            await ReplaceChunksAndStateAsync(chunks, state, cancellationToken);
            return state;
        }

        if (chunks.Count == 0)
        {
            var emptyState = new RagIndexStatePayload(
                novelId,
                embeddingOptions.ProviderKey,
                embeddingOptions.ModelId,
                embeddingOptions.Dimensions ?? 0,
                ChunkerVersion,
                Status: "ready",
                ChunkCount: 0,
                VectorTable: string.Empty,
                LastError: string.Empty,
                UpdatedAt: DateTimeOffset.UtcNow);
            await ReplaceChunksAndStateAsync(chunks, emptyState, cancellationToken);
            return emptyState;
        }

        try
        {
            var embeddingItems = await EmbedChunksAsync(chunks, embeddingOptions, cancellationToken);
            var dimensions = embeddingItems[0].Vector.Count;
            var vectorTable = SqliteVecTableProvisioner.BuildVectorTableName(novelId, dimensions);
            var state = new RagIndexStatePayload(
                novelId,
                embeddingOptions.ProviderKey,
                embeddingOptions.ModelId,
                dimensions,
                ChunkerVersion,
                Status: "building",
                ChunkCount: chunks.Count,
                vectorTable,
                LastError: string.Empty,
                UpdatedAt: DateTimeOffset.UtcNow);

            var rowIds = await ReplaceChunksAndStateAsync(chunks, state, cancellationToken);
            var vectors = embeddingItems
                .Select((item, index) => new SqliteVecVectorRecord(rowIds[chunks[index].ChunkId], chunks[index].ChunkId, item.Vector))
                .ToArray();
            var databasePath = await DatabasePathAsync(cancellationToken);
            var provisionRequest = new SqliteVecProvisionRequest(
                vectorTable,
                dimensions,
                SqliteVecTableProvisioner.BuildCreateTableSql(vectorTable, dimensions),
                vectors);
            await _vecProvisioner.ProvisionAsync(databasePath, provisionRequest, cancellationToken);

            var readyState = state with
            {
                Status = "ready",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await UpsertStateAsync(readyState, cancellationToken);
            return readyState;
        }
        catch (BridgeRequestException ex)
        {
            var failed = new RagIndexStatePayload(
                novelId,
                embeddingOptions.ProviderKey,
                embeddingOptions.ModelId,
                embeddingOptions.Dimensions ?? 0,
                ChunkerVersion,
                Status: "failed",
                ChunkCount: chunks.Count,
                VectorTable: string.Empty,
                LastError: ex.Message,
                UpdatedAt: DateTimeOffset.UtcNow);
            await ReplaceChunksAndStateAsync(chunks, failed, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            var failed = new RagIndexStatePayload(
                novelId,
                embeddingOptions.ProviderKey,
                embeddingOptions.ModelId,
                embeddingOptions.Dimensions ?? 0,
                ChunkerVersion,
                Status: "failed",
                ChunkCount: chunks.Count,
                VectorTable: string.Empty,
                LastError: ex.Message,
                UpdatedAt: DateTimeOffset.UtcNow);
            await ReplaceChunksAndStateAsync(chunks, failed, CancellationToken.None);
            return failed;
        }
    }

    public async ValueTask<IReadOnlyList<RagSearchHitPayload>> SearchAsync(
        long novelId,
        string query,
        int topK,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        if (topK <= 0)
        {
            return [];
        }

        var normalizedQuery = (query ?? string.Empty).Trim();
        if (normalizedQuery.Length == 0)
        {
            return [];
        }

        var state = await GetIndexStateAsync(novelId, cancellationToken);
        if (state is null ||
            state.Status != "ready" ||
            state.Dimensions <= 0 ||
            string.IsNullOrWhiteSpace(state.VectorTable))
        {
            return [];
        }

        var embeddingOptions = await _embeddingConfiguration.GetActiveEmbeddingOptionsAsync(cancellationToken);
        if (embeddingOptions is null ||
            !string.Equals(embeddingOptions.ProviderKey, state.ProviderKey, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(embeddingOptions.ModelId, state.ModelId, StringComparison.Ordinal))
        {
            return [];
        }

        var queryEmbedding = await _embeddings.EmbedAsync(
            [normalizedQuery],
            embeddingOptions with { Dimensions = state.Dimensions },
            cancellationToken);
        if (queryEmbedding.Dimensions != state.Dimensions ||
            queryEmbedding.Items.Count != 1 ||
            queryEmbedding.Items[0].Vector.Count != state.Dimensions)
        {
            return [];
        }

        var databasePath = await DatabasePathAsync(cancellationToken);
        var vectorResults = await _vecQuery.SearchAsync(
            databasePath,
            new SqliteVecSearchRequest(
                state.VectorTable,
                state.Dimensions,
                queryEmbedding.Items[0].Vector,
                Math.Min(topK, 40)),
            cancellationToken);
        if (vectorResults.Count == 0)
        {
            return [];
        }

        var chunks = await GetChunksByRowIdAsync(
            novelId,
            vectorResults.Select(item => item.RowId).ToArray(),
            cancellationToken);
        var hits = new List<RagSearchHitPayload>(vectorResults.Count);
        foreach (var result in vectorResults.OrderBy(item => item.Distance).ThenBy(item => item.RowId))
        {
            if (!chunks.TryGetValue(result.RowId, out var chunk))
            {
                continue;
            }

            hits.Add(new RagSearchHitPayload(
                chunk.ChunkId,
                chunk.NovelId,
                chunk.ChapterNumber,
                chunk.ChunkType,
                chunk.ChunkIndex,
                chunk.StartPosition,
                chunk.Content,
                chunk.FilePath,
                chunk.Title,
                result.Distance,
                Math.Clamp(1.0 - result.Distance, 0, 1)));
        }

        return hits;
    }

    public async ValueTask MarkNovelIndexStaleAsync(
        long novelId,
        string reason,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        var state = await GetIndexStateAsync(novelId, cancellationToken);
        if (state is null)
        {
            return;
        }

        await UpsertStateAsync(state with
        {
            Status = "stale",
            LastError = string.IsNullOrWhiteSpace(reason) ? "Index content changed." : reason.Trim(),
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private async ValueTask<IReadOnlyList<EmbeddingItemResult>> EmbedChunksAsync(
        IReadOnlyList<RagChunkPayload> chunks,
        EmbeddingRequestOptions options,
        CancellationToken cancellationToken)
    {
        var results = new List<EmbeddingItemResult>(chunks.Count);
        for (var offset = 0; offset < chunks.Count; offset += EmbeddingBatchSize)
        {
            var batch = chunks
                .Skip(offset)
                .Take(EmbeddingBatchSize)
                .Select(chunk => chunk.Content)
                .ToArray();
            var response = await _embeddings.EmbedAsync(batch, options, cancellationToken);
            if (response.Items.Count != batch.Length)
            {
                throw new InvalidOperationException("Embedding response count does not match the requested batch.");
            }

            if (results.Count > 0 && response.Dimensions != results[0].Vector.Count)
            {
                throw new InvalidOperationException("Embedding dimensions changed during a rebuild.");
            }

            results.AddRange(response.Items
                .OrderBy(item => item.Index)
                .Select((item, index) => item with { Index = offset + index }));
        }

        return results;
    }

    private async ValueTask<Dictionary<long, RagChunkPayload>> GetChunksByRowIdAsync(
        long novelId,
        IReadOnlyList<long> rowIds,
        CancellationToken cancellationToken)
    {
        if (rowIds.Count == 0)
        {
            return [];
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var result = new Dictionary<long, RagChunkPayload>();
            foreach (var rowId in rowIds.Distinct())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT rowid, chunk_id, novel_id, chapter_number, chunk_type, chunk_index, start_position,
                           content, content_hash, file_path, title
                    FROM rag_chunks
                    WHERE novel_id = $novel_id AND rowid = $rowid;
                    """;
                command.Parameters.AddWithValue("$novel_id", novelId);
                command.Parameters.AddWithValue("$rowid", rowId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    result[rowId] = new RagChunkPayload(
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetString(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetString(7),
                        reader.GetString(8),
                        reader.GetString(9),
                        reader.GetString(10));
                }
            }

            return result;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<IReadOnlyList<RagChunkPayload>> BuildChunksAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        var chapters = await _chapters.GetChaptersAsync(novelId, cancellationToken);
        var chunks = new List<RagChunkPayload>();
        var chunkIndex = 0;
        foreach (var chapter in chapters.OrderBy(item => item.ChapterNumber))
        {
            var content = await _chapters.GetContentAsync(novelId, chapter.FilePath, cancellationToken);
            foreach (var segment in SplitContent(content))
            {
                var hash = HashContent(segment.Text);
                var chunkId = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{novelId}:{chapter.ChapterNumber}:{chunkIndex}:{hash[..16]}");
                chunks.Add(new RagChunkPayload(
                    chunkId,
                    novelId,
                    chapter.ChapterNumber,
                    "content",
                    chunkIndex,
                    segment.StartPosition,
                    segment.Text,
                    hash,
                    chapter.FilePath,
                    chapter.Title));
                chunkIndex++;
            }
        }

        return chunks;
    }

    private static IEnumerable<TextSegment> SplitContent(string content)
    {
        var normalized = (content ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        var searchStart = 0;
        foreach (var raw in BlankLinePattern.Split(normalized))
        {
            var paragraph = raw.Trim();
            if (paragraph.Length == 0)
            {
                continue;
            }

            var start = normalized.IndexOf(paragraph, searchStart, StringComparison.Ordinal);
            if (start < 0)
            {
                start = searchStart;
            }

            foreach (var segment in SplitLongParagraph(paragraph, RuneCountBefore(normalized, start)))
            {
                yield return segment;
            }

            searchStart = Math.Min(normalized.Length, start + paragraph.Length);
        }
    }

    private static IEnumerable<TextSegment> SplitLongParagraph(string paragraph, int baseRunePosition)
    {
        var runes = paragraph.EnumerateRunes().ToArray();
        for (var index = 0; index < runes.Length; index += MaxChunkChars)
        {
            var count = Math.Min(MaxChunkChars, runes.Length - index);
            yield return new TextSegment(
                RunesToString(runes, index, count),
                baseRunePosition + index);
        }
    }

    private async ValueTask<Dictionary<string, long>> ReplaceChunksAndStateAsync(
        IReadOnlyList<RagChunkPayload> chunks,
        RagIndexStatePayload state,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM rag_chunks WHERE novel_id = $novel_id;";
                delete.Parameters.AddWithValue("$novel_id", state.NovelId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            var rowIds = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var chunk in chunks)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO rag_chunks
                      (chunk_id, novel_id, chapter_number, chunk_type, chunk_index, start_position,
                       content, content_hash, file_path, title)
                    VALUES
                      ($chunk_id, $novel_id, $chapter_number, $chunk_type, $chunk_index, $start_position,
                       $content, $content_hash, $file_path, $title)
                    RETURNING rowid;
                    """;
                AddChunkParameters(insert, chunk);
                var rowId = (long)(await insert.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException("SQLite did not return a rowid for the indexed chunk."));
                rowIds[chunk.ChunkId] = rowId;
            }

            await UpsertStateAsync(connection, transaction, state, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return rowIds;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask UpsertStateAsync(
        RagIndexStatePayload state,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await UpsertStateAsync(connection, transaction, state, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static async ValueTask UpsertStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RagIndexStatePayload state,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO rag_index_state
              (novel_id, provider_key, model_id, dimensions, chunker_version, status,
               chunk_count, vector_table, last_error, updated_at)
            VALUES
              ($novel_id, $provider_key, $model_id, $dimensions, $chunker_version, $status,
               $chunk_count, $vector_table, $last_error, $updated_at)
            ON CONFLICT(novel_id) DO UPDATE SET
              provider_key = excluded.provider_key,
              model_id = excluded.model_id,
              dimensions = excluded.dimensions,
              chunker_version = excluded.chunker_version,
              status = excluded.status,
              chunk_count = excluded.chunk_count,
              vector_table = excluded.vector_table,
              last_error = excluded.last_error,
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$novel_id", state.NovelId);
        command.Parameters.AddWithValue("$provider_key", state.ProviderKey);
        command.Parameters.AddWithValue("$model_id", state.ModelId);
        command.Parameters.AddWithValue("$dimensions", state.Dimensions);
        command.Parameters.AddWithValue("$chunker_version", state.ChunkerVersion);
        command.Parameters.AddWithValue("$status", state.Status);
        command.Parameters.AddWithValue("$chunk_count", state.ChunkCount);
        command.Parameters.AddWithValue("$vector_table", state.VectorTable);
        command.Parameters.AddWithValue("$last_error", state.LastError);
        command.Parameters.AddWithValue("$updated_at", state.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask EnsureSchemaAsync(string databasePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS rag_index_state (
              novel_id INTEGER PRIMARY KEY,
              provider_key TEXT NOT NULL,
              model_id TEXT NOT NULL,
              dimensions INTEGER NOT NULL,
              chunker_version TEXT NOT NULL,
              status TEXT NOT NULL,
              chunk_count INTEGER NOT NULL,
              vector_table TEXT NOT NULL,
              last_error TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS rag_chunks (
              chunk_id TEXT PRIMARY KEY,
              novel_id INTEGER NOT NULL,
              chapter_number INTEGER NOT NULL,
              chunk_type TEXT NOT NULL,
              chunk_index INTEGER NOT NULL,
              start_position INTEGER NOT NULL,
              content TEXT NOT NULL,
              content_hash TEXT NOT NULL,
              file_path TEXT NOT NULL,
              title TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_rag_chunks_novel
              ON rag_chunks(novel_id, chapter_number, chunk_index);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask EnsureNovelExistsAsync(long novelId, CancellationToken cancellationToken)
    {
        var novels = await _novels.GetNovelsAsync(cancellationToken);
        if (!novels.Any(novel => novel.Id == novelId))
        {
            throw new ArgumentException($"Novel '{novelId}' does not exist.", nameof(novelId));
        }
    }

    private async ValueTask<string> DatabasePathAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(
            await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken),
            "rag",
            "index.sqlite");
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void AddChunkParameters(SqliteCommand command, RagChunkPayload chunk)
    {
        command.Parameters.AddWithValue("$chunk_id", chunk.ChunkId);
        command.Parameters.AddWithValue("$novel_id", chunk.NovelId);
        command.Parameters.AddWithValue("$chapter_number", chunk.ChapterNumber);
        command.Parameters.AddWithValue("$chunk_type", chunk.ChunkType);
        command.Parameters.AddWithValue("$chunk_index", chunk.ChunkIndex);
        command.Parameters.AddWithValue("$start_position", chunk.StartPosition);
        command.Parameters.AddWithValue("$content", chunk.Content);
        command.Parameters.AddWithValue("$content_hash", chunk.ContentHash);
        command.Parameters.AddWithValue("$file_path", chunk.FilePath);
        command.Parameters.AddWithValue("$title", chunk.Title);
    }

    private static RagIndexStatePayload ReadState(SqliteDataReader reader)
    {
        return new RagIndexStatePayload(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetString(8),
            DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static RagChunkPayload ReadChunk(SqliteDataReader reader)
    {
        return new RagChunkPayload(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9));
    }

    private static string HashContent(string content)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private static int RuneCountBefore(string content, int utf16Index)
    {
        return content[..utf16Index].EnumerateRunes().Count();
    }

    private static string RunesToString(Rune[] runes, int start, int count)
    {
        var builder = new StringBuilder();
        for (var i = start; i < start + count && i < runes.Length; i++)
        {
            builder.Append(runes[i]);
        }

        return builder.ToString();
    }

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }
    }

    private sealed record TextSegment(string Text, int StartPosition);
}
