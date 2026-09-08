using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    // 章节级直接提取：worker 不再用本地窗口切候选，而是把整章文本交给模型，
    // 由模型返回逐字摘录（store 贳责摘录定位、候选落库与进度推进）。
    public async ValueTask<bool> HasChapterSourceSegmentAsync(
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        if (chapterIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chapterIndex), "Chapter index must be positive.");
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
              SELECT 1
              FROM reference_materialization_runs run
              JOIN reference_source_segments segment
                ON segment.anchor_id = run.anchor_id
               AND segment.chapter_index = $chapter_index
               AND segment.segment_type = 'chapter'
               AND segment.node_id LIKE 'split-node:%'
              WHERE run.run_id = $run_id
            );
            """;
        command.Parameters.AddWithValue("$run_id", normalizedRunId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    public async ValueTask<ReferenceChapterExtractionWorkItem?> BeginChapterExtractionAsync(
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        if (chapterIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chapterIndex), "Chapter index must be positive.");
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var snapshot = await ReadExtractionSnapshotAsync(connection, transaction, normalizedRunId, chapterIndex, cancellationToken);
        if (snapshot is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        // 沁本节点缺失说明锚点没有走材料化入队的节点补建（legacy 错点），交回旧窗口管线。
        var chapterText = await ReadChapterSegmentTextAsync(connection, transaction, snapshot.AnchorId, snapshot.SplitProfileId, chapterIndex, cancellationToken);
        if (chapterText is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (snapshot.Status != ReferenceMaterializationChapterStates.LlmQualifying)
        {
            ReferenceMaterializationChapterStateMachine.EnsureCanTransition(
                snapshot.Status,
                ReferenceMaterializationChapterStates.BuildingCandidates);
            ReferenceMaterializationChapterStateMachine.EnsureCanTransition(
                ReferenceMaterializationChapterStates.BuildingCandidates,
                ReferenceMaterializationChapterStates.LlmQualifying);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE reference_materialization_chapter_progress
                SET status = $status, current_stage = $current_stage,
                    started_at = COALESCE(started_at, $started_at)
                WHERE run_id = $run_id AND chapter_index = $chapter_index;
                """;
            update.Parameters.AddWithValue("$status", ReferenceMaterializationChapterStates.LlmQualifying);
            update.Parameters.AddWithValue("$current_stage", ReferenceMaterializationChapterStates.LlmQualifying);
            update.Parameters.AddWithValue("$started_at", FormatTimestamp(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$run_id", normalizedRunId);
            update.Parameters.AddWithValue("$chapter_index", chapterIndex);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        var model = new ReferenceMaterializationLlmSelection(snapshot.ModelProvider, snapshot.ModelId, string.Empty);
        await transaction.CommitAsync(cancellationToken);
        return new ReferenceChapterExtractionWorkItem(
            snapshot.AnchorId,
            chapterIndex,
            snapshot.ChapterTitle,
            chapterText,
            snapshot.ContentStart,
            snapshot.ContentEnd,
            model);
    }

    public async ValueTask<ReferenceChapterExtractionPersistenceResult> PersistChapterExtractionAsync(
        string runId,
        int chapterIndex,
        ReferenceChapterExtractionResult extraction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        var materials = extraction.Materials;
        var normalizedRunId = NormalizeRunId(runId);
        if (chapterIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chapterIndex), "Chapter index must be positive.");
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var snapshot = await ReadExtractionSnapshotAsync(connection, transaction, normalizedRunId, chapterIndex, cancellationToken)
            ?? throw new ArgumentException("Materialization chapter progress does not exist.", nameof(chapterIndex));
        var chapterText = await ReadChapterSegmentTextAsync(
            connection,
            transaction,
            snapshot.AnchorId,
            snapshot.SplitProfileId,
            chapterIndex,
            cancellationToken)
            ?? throw new InvalidOperationException("Chapter text segment disappeared during materialization extraction.");

        var inserted = 0;
        var accepted = 0;
        var review = 0;
        var skipped = 0;
        foreach (var material in materials)
        {
            var excerpt = material.Excerpt.Trim();
            if (excerpt.Length == 0)
            {
                skipped++;
                continue;
            }

            // 逐字校验：摘录必须能在章文本中精确找到，找不到（模型改写/幻觉）直接丢弃。
            var indexInChapter = chapterText.IndexOf(excerpt, StringComparison.Ordinal);
            if (indexInChapter < 0)
            {
                skipped++;
                continue;
            }

            var absoluteStart = snapshot.ContentStart + indexInChapter;
            var absoluteEnd = absoluteStart + excerpt.Length;
            var excerptHash = HashText(excerpt);
            var candidateId = HashText(normalizedRunId + "|" + chapterIndex + "|" + excerptHash);
            var candidateKey = $"chapter-extract:{chapterIndex}:{excerptHash}";
            var decision = material.Confidence >= 0.5
                ? ReferenceMaterializationCandidateDecisions.Accepted
                : ReferenceMaterializationCandidateDecisions.ReviewRequired;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO reference_material_candidates (
                      candidate_id, candidate_key, run_id, anchor_id, candidate_type, text_hash,
                      decision, decision_origin, quality_score, confidence, scores_json, tags_json,
                      reason_codes_json, created_at)
                    VALUES (
                      $candidate_id, $candidate_key, $run_id, $anchor_id, $candidate_type, $text_hash,
                      $decision, $decision_origin, $quality_score, $confidence, $scores_json, $tags_json,
                      $reason_codes_json, $created_at)
                    ON CONFLICT(candidate_id) DO UPDATE SET
                      decision = excluded.decision,
                      decision_origin = excluded.decision_origin,
                      quality_score = excluded.quality_score,
                      confidence = excluded.confidence,
                      scores_json = excluded.scores_json,
                      tags_json = excluded.tags_json,
                      reason_codes_json = excluded.reason_codes_json,
                      row_version = reference_material_candidates.row_version + 1;
                    """;
                insert.Parameters.AddWithValue("$candidate_id", candidateId);
                insert.Parameters.AddWithValue("$candidate_key", candidateKey);
                insert.Parameters.AddWithValue("$run_id", normalizedRunId);
                insert.Parameters.AddWithValue("$anchor_id", snapshot.AnchorId);
                insert.Parameters.AddWithValue("$candidate_type", material.MaterialType);
                insert.Parameters.AddWithValue("$text_hash", excerptHash);
                insert.Parameters.AddWithValue("$decision", decision);
                insert.Parameters.AddWithValue("$decision_origin", "chapter_extraction");
                insert.Parameters.AddWithValue("$quality_score", AverageScore(material.Scores));
                insert.Parameters.AddWithValue("$confidence", material.Confidence);
                insert.Parameters.AddWithValue("$scores_json", SerializeScores(material.Scores));
                insert.Parameters.AddWithValue("$tags_json", SerializeTags(material.Tags));
                insert.Parameters.AddWithValue("$reason_codes_json", JsonSerializer.Serialize(material.ReasonCodes));
                insert.Parameters.AddWithValue("$created_at", FormatTimestamp(DateTimeOffset.UtcNow));
                // upsert：重复提取的同一摘录会重判既有候选（复核接纳后重开的章节依赖这一点）。
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            inserted++;
            if (decision == ReferenceMaterializationCandidateDecisions.Accepted)
            {
                accepted++;
            }
            else
            {
                review++;
            }

            // 证据节点按粒度择优：摘录若与句子节点精确对齐（完全包含），只链接句子节点；
            // 否则若被段落节点完全包含，只链接段落节点；再退回所有相交节点（交集作为证据区间）。
            // 混用段落+句子会因层级重叠导致材料文本重复拼接。
            var overlappingNodes = new List<ExtractionEvidenceNode>();
            await using (var selectNodes = connection.CreateCommand())
            {
                selectNodes.Transaction = transaction;
                selectNodes.CommandText = """
                    SELECT node.node_id, node.node_type, node.char_len, node.start_offset, node.text_hash
                    FROM reference_text_nodes node
                    WHERE node.anchor_id = $anchor_id
                      AND node.node_type IN ('paragraph', 'sentence')
                      AND node.start_offset < $absolute_end
                      AND node.end_offset > $absolute_start
                    ORDER BY node.start_offset;
                    """;
                selectNodes.Parameters.AddWithValue("$anchor_id", snapshot.AnchorId);
                selectNodes.Parameters.AddWithValue("$absolute_end", absoluteEnd);
                selectNodes.Parameters.AddWithValue("$absolute_start", absoluteStart);
                await using var nodeReader = await selectNodes.ExecuteReaderAsync(cancellationToken);
                while (await nodeReader.ReadAsync(cancellationToken))
                {
                    overlappingNodes.Add(new ExtractionEvidenceNode(
                        nodeReader.GetString(0),
                        nodeReader.GetString(1),
                        nodeReader.GetInt32(2),
                        nodeReader.GetInt32(3),
                        nodeReader.GetString(4)));
                }
            }

            var containedSentences = overlappingNodes
                .Where(node => node.NodeType == "sentence" &&
                    node.StartOffset >= absoluteStart && node.StartOffset + node.CharLen <= absoluteEnd)
                .ToList();
            var containedParagraphs = overlappingNodes
                .Where(node => node.NodeType == "paragraph" &&
                    node.StartOffset >= absoluteStart && node.StartOffset + node.CharLen <= absoluteEnd)
                .ToList();
            List<ExtractionEvidenceNode> evidenceNodes;
            if (containedSentences.Count > 0)
            {
                evidenceNodes = containedSentences;
            }
            else if (containedParagraphs.Count > 0)
            {
                evidenceNodes = containedParagraphs;
            }
            else
            {
                evidenceNodes = overlappingNodes;
            }

            var ordinal = 0;
            foreach (var evidenceNode in evidenceNodes)
            {
                var evidenceStart = Math.Max(0, absoluteStart - evidenceNode.StartOffset);
                var evidenceEnd = Math.Min(evidenceNode.CharLen, absoluteEnd - evidenceNode.StartOffset);
                if (evidenceEnd <= evidenceStart)
                {
                    continue;
                }

                await using var linkNode = connection.CreateCommand();
                linkNode.Transaction = transaction;
                linkNode.CommandText = """
                    INSERT OR IGNORE INTO reference_material_candidate_nodes (
                      candidate_id, node_id, ordinal, evidence_start, evidence_end, text_hash)
                    VALUES (
                      $candidate_id, $node_id, $ordinal, $evidence_start, $evidence_end, $text_hash);
                    """;
                linkNode.Parameters.AddWithValue("$candidate_id", candidateId);
                linkNode.Parameters.AddWithValue("$node_id", evidenceNode.NodeId);
                linkNode.Parameters.AddWithValue("$ordinal", ordinal);
                linkNode.Parameters.AddWithValue("$evidence_start", evidenceStart);
                linkNode.Parameters.AddWithValue("$evidence_end", evidenceEnd);
                linkNode.Parameters.AddWithValue("$text_hash", evidenceNode.TextHash);
                await linkNode.ExecuteNonQueryAsync(cancellationToken);
                ordinal++;
            }
        }

        ReferenceMaterializationChapterStateMachine.EnsureCanTransition(
            ReferenceMaterializationChapterStates.LlmQualifying,
            ReferenceMaterializationChapterStates.Embedding);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
            UPDATE reference_materialization_chapter_progress
            SET status = $status, current_stage = $current_stage,
                candidate_count = $candidate_count, decided_count = $decided_count,
                accepted_count = $accepted_count, rejected_count = 0, review_count = $review_count,
                model_call_count = MAX(model_call_count, $model_call_count),
                row_version = row_version + 1,
                started_at = COALESCE(started_at, $started_at)
            WHERE run_id = $run_id AND chapter_index = $chapter_index;
            """;
        update.Parameters.AddWithValue("$status", ReferenceMaterializationChapterStates.Embedding);
        update.Parameters.AddWithValue("$current_stage", ReferenceMaterializationChapterStates.Embedding);
        update.Parameters.AddWithValue("$candidate_count", inserted);
        update.Parameters.AddWithValue("$decided_count", inserted);
        update.Parameters.AddWithValue("$accepted_count", accepted);
        update.Parameters.AddWithValue("$review_count", review);
        update.Parameters.AddWithValue("$model_call_count", extraction.ModelCallCount);
            update.Parameters.AddWithValue("$started_at", FormatTimestamp(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$run_id", normalizedRunId);
            update.Parameters.AddWithValue("$chapter_index", chapterIndex);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await RefreshRunCountsAsync(connection, transaction, normalizedRunId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReferenceChapterExtractionPersistenceResult(chapterIndex, inserted, accepted, review, skipped);
    }

    private async ValueTask<ExtractionSnapshot?> ReadExtractionSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run.anchor_id, run.split_profile_id, run.model_provider, run.model_id,
                   boundary.title, boundary.content_start, boundary.content_end,
                   progress.status
            FROM reference_materialization_runs run
            JOIN reference_chapter_split_boundaries boundary ON boundary.split_profile_id = run.split_profile_id
            JOIN reference_materialization_chapter_progress progress
              ON progress.run_id = run.run_id
             AND progress.chapter_index = boundary.chapter_index
            WHERE run.run_id = $run_id
              AND boundary.chapter_index = $chapter_index;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExtractionSnapshot(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetString(7));
    }

    private static async ValueTask<string?> ReadChapterSegmentTextAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        string splitProfileId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT node.text
            FROM reference_source_segments segment
            JOIN reference_text_nodes node ON node.node_id = segment.node_id
            WHERE segment.anchor_id = $anchor_id
              AND segment.chapter_index = $chapter_index
              AND segment.segment_type = 'chapter'
              AND segment.node_id = $chapter_node_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        command.Parameters.AddWithValue("$chapter_node_id", $"split-node:{splitProfileId}:c{chapterIndex}");
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private sealed record ExtractionSnapshot(
        long AnchorId,
        string SplitProfileId,
        string ModelProvider,
        string ModelId,
        string ChapterTitle,
        int ContentStart,
        int ContentEnd,
        string Status);

    private sealed record ExtractionEvidenceNode(
        string NodeId,
        string NodeType,
        int CharLen,
        int StartOffset,
        string TextHash);
}
