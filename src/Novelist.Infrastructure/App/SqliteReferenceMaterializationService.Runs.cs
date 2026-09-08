using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed partial class SqliteReferenceMaterializationService
{
    public async ValueTask<ReferenceMaterializationStatusPayload> EnqueueMaterializationAsync(
        EnqueueReferenceMaterializationPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateReferenceInput(input.NovelId, input.AnchorId);
        var splitProfileId = NormalizeProfileId(input.SplitProfileId);
        await EnsureConfirmedProfileMatchesCurrentSourceAsync(
            input.NovelId,
            input.AnchorId,
            splitProfileId,
            cancellationToken);
        await EnsureCandidateSourceNodesAsync(
            input.NovelId,
            input.AnchorId,
            splitProfileId,
            cancellationToken);

        var models = await _modelPreflight.VerifyAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return await _runStore.CreateAsync(
            new ReferenceMaterializationRunSeed(
                Guid.NewGuid().ToString("N"),
                input.AnchorId,
                splitProfileId,
                Guid.NewGuid().ToString("N"),
                "materialization-policy-v1",
                ReferenceCandidateWindowBuilder.Version,
                ReferenceMaterializationChatCompletionQualifier.SchemaVersion,
                models.Llm,
                models.Embedding,
                now),
            cancellationToken);
    }

    // 材料化来源登记（RegisterMaterializationSource*）按设计跳过旧导出管线的全量分段，
    // 但候选窗口构建依赖段落/句子文本节点；入队时按确认边界 + 当前源文本补建节点。
    // 以 split profile 为命名空间幂等 upsert；源变更会先走 stale 校验换新 profile。
    private async ValueTask EnsureCandidateSourceNodesAsync(
        long novelId,
        long anchorId,
        string splitProfileId,
        CancellationToken cancellationToken)
    {
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        var boundaries = new List<(int ChapterIndex, string Title, int ContentStart, int ContentEnd)>();
        await using (var connection = await OpenConnectionAsync(databasePath, cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT chapter_index, title, content_start, content_end
                FROM reference_chapter_split_boundaries
                WHERE split_profile_id = $split_profile_id
                ORDER BY chapter_index;
                """;
            command.Parameters.AddWithValue("$split_profile_id", splitProfileId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                boundaries.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
            }
        }

        if (boundaries.Count == 0)
        {
            return;
        }

        var source = await ReadCurrentSourceAsync(
            novelId,
            anchorId,
            cancellationToken,
            requireAnchorSourceHash: false);
        var normalized = source.NormalizedText;
        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        await using var nodeConnection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await nodeConnection.BeginTransactionAsync(cancellationToken);
        foreach (var (chapterIndex, title, contentStart, contentEnd) in boundaries)
        {
            if (contentStart < 0 || contentEnd > normalized.Length || contentStart >= contentEnd)
            {
                continue;
            }

            var chapterText = normalized[contentStart..contentEnd];
            var chapterNodeId = $"split-node:{splitProfileId}:c{chapterIndex}";
            var chapterSegmentId = $"split-seg:{splitProfileId}:c{chapterIndex}";
            var chapterHash = Sha256Hex(chapterText);
            await UpsertCandidateNodeRowAsync(
                nodeConnection,
                transaction,
                chapterNodeId,
                anchorId,
                chapterIndex,
                "chapter",
                contentStart,
                contentEnd,
                chapterText,
                chapterHash,
                now,
                cancellationToken);
            await UpsertCandidateSourceRowAsync(
                nodeConnection,
                transaction,
                chapterSegmentId,
                anchorId,
                chapterIndex,
                title,
                "chapter",
                chapterIndex,
                parentSegmentId: null,
                contentStart,
                contentEnd,
                chapterText,
                chapterHash,
                chapterNodeId,
                now,
                cancellationToken);
            var paragraphIndex = 0;
            foreach (var (paragraphText, paragraphStart, paragraphEnd) in SplitParagraphSpans(chapterText))
            {
                paragraphIndex++;
                var absoluteStart = contentStart + paragraphStart;
                var absoluteEnd = contentStart + paragraphEnd;
                var paragraphNodeId = $"split-node:{splitProfileId}:c{chapterIndex}:p{paragraphIndex}";
                var paragraphSegmentId = $"split-seg:{splitProfileId}:c{chapterIndex}:p{paragraphIndex}";
                var paragraphHash = Sha256Hex(paragraphText);
                await UpsertCandidateNodeRowAsync(
                    nodeConnection,
                    transaction,
                    paragraphNodeId,
                    anchorId,
                    chapterIndex,
                    "paragraph",
                    absoluteStart,
                    absoluteEnd,
                    paragraphText,
                    paragraphHash,
                    now,
                    cancellationToken);
                await UpsertCandidateSourceRowAsync(
                    nodeConnection,
                    transaction,
                    paragraphSegmentId,
                    anchorId,
                    chapterIndex,
                    title,
                    "paragraph",
                    paragraphIndex,
                    parentSegmentId: null,
                    absoluteStart,
                    absoluteEnd,
                    paragraphText,
                    paragraphHash,
                    paragraphNodeId,
                    now,
                    cancellationToken);

                var sentenceIndex = 0;
                foreach (var (sentenceText, sentenceStart, sentenceEnd) in SplitSentenceSpans(paragraphText))
                {
                    sentenceIndex++;
                    var sentenceNodeId = $"{paragraphNodeId}:s{sentenceIndex}";
                    var sentenceSegmentId = $"{paragraphSegmentId}:s{sentenceIndex}";
                    var sentenceHash = Sha256Hex(sentenceText);
                    await UpsertCandidateNodeRowAsync(
                        nodeConnection,
                        transaction,
                        sentenceNodeId,
                        anchorId,
                        chapterIndex,
                        "sentence",
                        absoluteStart + sentenceStart,
                        absoluteStart + sentenceEnd,
                        sentenceText,
                        sentenceHash,
                        now,
                        cancellationToken);
                    await UpsertCandidateSourceRowAsync(
                        nodeConnection,
                        transaction,
                        sentenceSegmentId,
                        anchorId,
                        chapterIndex,
                        title,
                        "sentence",
                        sentenceIndex,
                        paragraphSegmentId,
                        absoluteStart + sentenceStart,
                        absoluteStart + sentenceEnd,
                        sentenceText,
                        sentenceHash,
                        sentenceNodeId,
                        now,
                        cancellationToken);
                }
            }
        }

        await DeleteOrphanSplitNodesAsync(nodeConnection, transaction, anchorId, splitProfileId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // 重新分析会生成新的 split profile，旧 profile 的 split 节点/分段随即失效。
    // 最佳努力清理：仍被候选证据或分析引用的节点跳过（FK RESTRICT），清理失败不阻断入队。
    private static async ValueTask DeleteOrphanSplitNodesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        string splitProfileId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var deleteSegments = connection.CreateCommand();
            deleteSegments.Transaction = transaction;
            deleteSegments.CommandText = """
                DELETE FROM reference_source_segments
                WHERE anchor_id = $anchor_id
                  AND segment_id LIKE 'split-seg:%'
                  AND segment_id NOT LIKE $keep_pattern;
                """;
            deleteSegments.Parameters.AddWithValue("$anchor_id", anchorId);
            deleteSegments.Parameters.AddWithValue("$keep_pattern", $"split-seg:{splitProfileId}:%");
            await deleteSegments.ExecuteNonQueryAsync(cancellationToken);

            await using var deleteNodes = connection.CreateCommand();
            deleteNodes.Transaction = transaction;
            deleteNodes.CommandText = """
                DELETE FROM reference_text_nodes
                WHERE anchor_id = $anchor_id
                  AND node_id LIKE 'split-node:%'
                  AND node_id NOT LIKE $keep_pattern
                  AND node_id NOT IN (SELECT node_id FROM reference_material_candidate_nodes)
                  AND node_id NOT IN (SELECT node_id FROM reference_analysis_work_items WHERE node_id IS NOT NULL)
                  AND node_id NOT IN (SELECT node_id FROM reference_blueprint_beat_pieces);
                """;
            deleteNodes.Parameters.AddWithValue("$anchor_id", anchorId);
            deleteNodes.Parameters.AddWithValue("$keep_pattern", $"split-node:{splitProfileId}:%");
            await deleteNodes.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException)
        {
            // 孤儿清理是尽力而为；引用关系复杂时保留旧节点不影响正确性。
        }
    }

    private static IEnumerable<(string Text, int Start, int End)> SplitParagraphSpans(string text)
    {
        var searchStart = 0;
        foreach (var raw in text.Split('\n'))
        {
            var paragraph = raw.Trim();
            if (paragraph.Length == 0)
            {
                continue;
            }

            var start = text.IndexOf(paragraph, searchStart, StringComparison.Ordinal);
            if (start < 0)
            {
                start = searchStart;
            }

            yield return (paragraph, start, start + paragraph.Length);
            searchStart = Math.Min(text.Length, start + paragraph.Length);
        }
    }

    private static IEnumerable<(string Text, int Start, int End)> SplitSentenceSpans(string paragraph)
    {
        var start = 0;
        for (var index = 0; index < paragraph.Length; index++)
        {
            if (paragraph[index] is not ('。' or '！' or '？' or '!' or '?' or '…'))
            {
                continue;
            }

            var end = index + 1;
            while (start < end && char.IsWhiteSpace(paragraph[start]))
            {
                start++;
            }

            while (end > start && char.IsWhiteSpace(paragraph[end - 1]))
            {
                end--;
            }

            if (end > start)
            {
                yield return (paragraph[start..end], start, end);
            }

            start = index + 1;
        }

        while (start < paragraph.Length && char.IsWhiteSpace(paragraph[start]))
        {
            start++;
        }

        if (start < paragraph.Length)
        {
            yield return (paragraph[start..], start, paragraph.Length);
        }
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async ValueTask UpsertCandidateSourceRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string segmentId,
        long anchorId,
        int chapterIndex,
        string chapterTitle,
        string segmentType,
        int segmentIndex,
        string? parentSegmentId,
        int startOffset,
        int endOffset,
        string text,
        string textHash,
        string nodeId,
        string createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_source_segments (
              segment_id, anchor_id, chapter_index, chapter_title, segment_type,
              segment_index, parent_segment_id, start_offset, end_offset, text, text_hash, node_id)
            VALUES (
              $segment_id, $anchor_id, $chapter_index, $chapter_title, $segment_type,
              $segment_index, $parent_segment_id, $start_offset, $end_offset, $text, $text_hash, $node_id)
            ON CONFLICT(segment_id) DO UPDATE SET
              anchor_id = excluded.anchor_id,
              chapter_index = excluded.chapter_index,
              chapter_title = excluded.chapter_title,
              segment_index = excluded.segment_index,
              parent_segment_id = excluded.parent_segment_id,
              start_offset = excluded.start_offset,
              end_offset = excluded.end_offset,
              text = excluded.text,
              text_hash = excluded.text_hash,
              node_id = excluded.node_id;
            """;
        command.Parameters.AddWithValue("$segment_id", segmentId);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        command.Parameters.AddWithValue("$chapter_title", chapterTitle);
        command.Parameters.AddWithValue("$segment_type", segmentType);
        command.Parameters.AddWithValue("$segment_index", segmentIndex);
        command.Parameters.AddWithValue("$parent_segment_id", parentSegmentId ?? string.Empty);
        command.Parameters.AddWithValue("$start_offset", startOffset);
        command.Parameters.AddWithValue("$end_offset", endOffset);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$text_hash", textHash);
        command.Parameters.AddWithValue("$node_id", nodeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask UpsertCandidateNodeRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId,
        long anchorId,
        int chapterIndex,
        string nodeType,
        int startOffset,
        int endOffset,
        string text,
        string textHash,
        string createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_text_nodes (
              node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
              chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES (
              $node_id, $anchor_id, NULL, $node_type, 0, 1,
              $chapter_index, $start_offset, $end_offset, $char_len, $text_hash, $text, $created_at)
            ON CONFLICT(node_id) DO UPDATE SET
              anchor_id = excluded.anchor_id,
              node_type = excluded.node_type,
              chapter_index = excluded.chapter_index,
              start_offset = excluded.start_offset,
              end_offset = excluded.end_offset,
              char_len = excluded.char_len,
              text_hash = excluded.text_hash,
              text = excluded.text;
            """;
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$node_type", nodeType);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        command.Parameters.AddWithValue("$start_offset", startOffset);
        command.Parameters.AddWithValue("$end_offset", endOffset);
        command.Parameters.AddWithValue("$char_len", text.Length);
        command.Parameters.AddWithValue("$text_hash", textHash);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$created_at", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<ReferenceMaterializationStatusPayload?> GetMaterializationStatusAsync(
        GetReferenceMaterializationStatusPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateReferenceInput(input.NovelId, input.AnchorId);
        await EnsureAnchorAccessibleAsync(input.NovelId, input.AnchorId, cancellationToken);
        var status = string.IsNullOrWhiteSpace(input.RunId)
            ? await _runStore.GetLatestForAnchorAsync(input.AnchorId, cancellationToken)
            : await _runStore.GetAsync(input.RunId, cancellationToken);
        return status is null || status.AnchorId != input.AnchorId ? null : status;
    }

    public async ValueTask<ReferenceMaterializationStatusPayload> RetryMaterializationAsync(
        RetryReferenceMaterializationPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateReferenceInput(input.NovelId, input.AnchorId);
        var current = await GetMaterializationStatusAsync(
            new GetReferenceMaterializationStatusPayload(input.NovelId, input.AnchorId, input.RunId),
            cancellationToken)
            ?? throw new ArgumentException("Materialization run does not exist.", nameof(input));
        if (current.Status is not (ReferenceMaterializationRunStates.Failed or ReferenceMaterializationRunStates.Cancelled))
        {
            throw new InvalidOperationException("Only failed or cancelled materialization runs can be retried.");
        }

        await EnsureConfirmedProfileMatchesCurrentSourceAsync(
            input.NovelId,
            input.AnchorId,
            current.SplitProfileId,
            cancellationToken);
        var models = await _modelPreflight.VerifyAsync(cancellationToken);
        if (!SameModel(current.Llm, models.Llm) || !SameModel(current.Embedding, models.Embedding))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.RetryRequiresNewRun,
                "The configured models changed after this materialization run started. Create a new run instead of retrying this generation.");
        }

        var qualifierVersion = await _runStore.GetQualifierVersionAsync(current.RunId, cancellationToken);
        if (!string.Equals(qualifierVersion, ReferenceMaterializationChatCompletionQualifier.SchemaVersion, StringComparison.Ordinal))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.RetryRequiresNewRun,
                "The material qualification schema changed after this run started. Create a new run instead of retrying this generation.");
        }

        // 旧版入队可能没有补建章节级文本分段；重试前补齐，让失败章节也走章节级提取管线。
        await EnsureCandidateSourceNodesAsync(
            input.NovelId,
            input.AnchorId,
            current.SplitProfileId,
            cancellationToken);

        return await _runStore.RetryCurrentBatchAsync(current.RunId, cancellationToken);
    }

    public async ValueTask<PageResultPayload<ReferenceMaterializationChapterProgressPayload>> ListMaterializationChapterProgressAsync(
        ListReferenceMaterializationChapterProgressPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateReferenceInput(input.NovelId, input.AnchorId);
        await EnsureAnchorAccessibleAsync(input.NovelId, input.AnchorId, cancellationToken);
        var status = await _runStore.GetAsync(input.RunId, cancellationToken);
        if (status is null || status.AnchorId != input.AnchorId)
        {
            throw new ArgumentException("Materialization run does not exist.", nameof(input));
        }

        return await _runStore.ListChapterProgressAsync(input.RunId, input.Page, input.Size, cancellationToken);
    }

    public async ValueTask<PageResultPayload<ReferenceMaterializationCandidatePayload>> ListMaterializationCandidatesAsync(
        ListReferenceMaterializationCandidatesPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateReferenceInput(input.NovelId, input.AnchorId);
        await EnsureAnchorAccessibleAsync(input.NovelId, input.AnchorId, cancellationToken);
        var status = await _runStore.GetAsync(input.RunId, cancellationToken);
        if (status is null || status.AnchorId != input.AnchorId)
        {
            throw new ArgumentException("Materialization run does not exist.", nameof(input));
        }

        return await _runStore.ListCandidatesAsync(
            input.RunId,
            input.Decision,
            input.Page,
            input.Size,
            cancellationToken);
    }

    public async ValueTask<ReferenceMaterializationCandidateReviewResultPayload> ReviewMaterializationCandidateAsync(
        ReviewReferenceMaterializationCandidatePayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateReferenceInput(input.NovelId, input.AnchorId);
        await EnsureAnchorAccessibleAsync(input.NovelId, input.AnchorId, cancellationToken);
        var status = await _runStore.GetAsync(input.RunId, cancellationToken);
        if (status is null || status.AnchorId != input.AnchorId)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.CandidateReviewInvalid,
                "Materialization run does not exist.");
        }

        var mutation = await _runStore.ReviewCandidateAsync(
            input.RunId,
            input.CandidateId,
            input.Action,
            input.ExpectedVersion,
            input.SourceSpans,
            cancellationToken);
        var updatedStatus = await _runStore.GetAsync(input.RunId, cancellationToken)
            ?? throw new InvalidOperationException("Materialization run disappeared after candidate review.");
        return new ReferenceMaterializationCandidateReviewResultPayload(
            mutation.CandidateId,
            mutation.Decision,
            mutation.RowVersion,
            mutation.RequalificationQueued,
            updatedStatus);
    }

    public async ValueTask<PageResultPayload<ReferenceMaterializationMaterialPayload>> ListActiveMaterialsAsync(
        ListActiveReferenceMaterializationMaterialsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateReferenceInput(input.NovelId, input.AnchorId);
        await EnsureAnchorAccessibleAsync(input.NovelId, input.AnchorId, cancellationToken);
        return await _runStore.ListActiveMaterialsAsync(
            input.AnchorId,
            input.Page,
            input.Size,
            input.Query,
            cancellationToken);
    }

    public async ValueTask<IReadOnlyList<ReferenceMaterializationSemanticSearchHitPayload>> SearchActiveMaterialsAsync(
        SearchActiveReferenceMaterializationMaterialsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateReferenceInput(input.NovelId, input.AnchorId);
        await EnsureAnchorAccessibleAsync(input.NovelId, input.AnchorId, cancellationToken);
        return await _semanticSearch.SearchAsync(
            input.AnchorId,
            input.Query,
            input.MaxResults,
            cancellationToken);
    }

    private async ValueTask EnsureConfirmedProfileMatchesCurrentSourceAsync(
        long novelId,
        long anchorId,
        string splitProfileId,
        CancellationToken cancellationToken)
    {
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var profileSourceHash = await ReadConfirmedProfileSourceHashAsync(
            connection,
            novelId,
            anchorId,
            splitProfileId,
            cancellationToken);
        if (profileSourceHash is null)
        {
            throw new InvalidOperationException("Reference materialization requires a confirmed chapter split profile.");
        }

        var source = await ReadCurrentSourceAsync(
            novelId,
            anchorId,
            cancellationToken,
            requireAnchorSourceHash: false);
        if (!string.Equals(profileSourceHash, source.Hash, StringComparison.Ordinal))
        {
            await MarkProfileStaleAsync(connection, splitProfileId, cancellationToken);
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.ChapterSplitProfileStale,
                "Chapter split profile is stale because the reference source changed.");
        }
    }

    private async ValueTask EnsureAnchorAccessibleAsync(
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        if (await ReadAnchorSourceAsync(connection, novelId, anchorId, cancellationToken) is null)
        {
            throw new ArgumentException("Reference source does not exist or is not accessible.", nameof(anchorId));
        }
    }

    private static async ValueTask<string?> ReadConfirmedProfileSourceHashAsync(
        SqliteConnection connection,
        long novelId,
        long anchorId,
        string splitProfileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.source_hash
            FROM reference_chapter_split_profiles p
            JOIN reference_anchors a ON a.anchor_id = p.anchor_id
            WHERE p.split_profile_id = $split_profile_id
              AND p.anchor_id = $anchor_id
              AND p.status = $status
              AND (
                a.novel_id = $novel_id OR
                ((a.novel_id IS NULL OR a.novel_id = 0) AND a.corpus_visibility = $workspace_visibility)
              );
            """;
        command.Parameters.AddWithValue("$split_profile_id", splitProfileId);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$status", ReferenceChapterSplitProfileStates.Confirmed);
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_visibility", WorkspaceCorpusVisibility);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static bool SameModel(
        ReferenceMaterializationModelIdentityPayload left,
        ReferenceMaterializationModelIdentityPayload right) =>
        string.Equals(left.Provider, right.Provider, StringComparison.Ordinal) &&
        string.Equals(left.ModelId, right.ModelId, StringComparison.Ordinal) &&
        left.Dimensions == right.Dimensions;
}
