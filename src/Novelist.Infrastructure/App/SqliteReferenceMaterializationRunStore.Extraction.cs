using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    // 章节级直接提取：worker 不再用本地窗口切候选，而是把整章文本交给模型，
    // 由模型返回逐字摘录（store 负责摘录定位、候选落库与进度推进）。
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

        // 提取结果已持久化的章节（embedding/indexing）不能也不需要重新提取：
        // embedding→building 是非法迁移，直接抛异常会把修复路径锁死；返回 null
        // 让 worker 从嵌入阶段恢复。
        if (snapshot.Status is ReferenceMaterializationChapterStates.Embedding or
            ReferenceMaterializationChapterStates.Indexing)
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

    public async ValueTask<string?> ReadChapterStageAsync(
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
            SELECT status FROM reference_materialization_chapter_progress
            WHERE run_id = $run_id AND chapter_index = $chapter_index;
            """;
        command.Parameters.AddWithValue("$run_id", normalizedRunId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async ValueTask<int> ReadChapterAcceptedCountAsync(
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
            SELECT accepted_count FROM reference_materialization_chapter_progress
            WHERE run_id = $run_id AND chapter_index = $chapter_index;
            """;
        command.Parameters.AddWithValue("$run_id", normalizedRunId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    // 计划落库：保存趟类型分组与总趟数（extraction_round_index 归零，重试重开计划时重置）。
    public async ValueTask SaveExtractionPlanAsync(
        string runId,
        int chapterIndex,
        IReadOnlyList<ReferenceChapterExtractionRound> rounds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        var normalizedRunId = NormalizeRunId(runId);
        if (chapterIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chapterIndex), "Chapter index must be positive.");
        }

        var planJson = JsonSerializer.Serialize(rounds.Select(round => new
        {
            material_types = round.MaterialTypes,
            focus = round.Focus,
        }));
        // 写入侧 round-trip 校验：序列化结果必须能被读取校验重新接受。_extractor
        // 的实现错误（空趟/类型缺失/重复/legacy 类型）在这里 fail fast，而不是
        // 把坏计划落库、再把垃圾 pass_material_types 发给模型。
        if (!TryParseExtractionPlan(planJson, 0, []))
        {
            throw new ArgumentException(
                "Extraction plan does not partition the six chapter-extraction kinds into passes.",
                nameof(rounds));
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_materialization_chapter_progress
            SET extraction_plan_json = $plan_json,
                extraction_round_count = $round_count,
                extraction_round_index = 0,
                row_version = row_version + 1
            WHERE run_id = $run_id AND chapter_index = $chapter_index;
            """;
        command.Parameters.AddWithValue("$plan_json", planJson);
        command.Parameters.AddWithValue("$round_count", rounds.Count);
        command.Parameters.AddWithValue("$run_id", normalizedRunId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal sealed record ExtractionPlanState(
        IReadOnlyList<ReferenceChapterExtractionRound> Rounds,
        int RoundIndex);

    // 本章提取候选的判定分布，一次查询供两处使用：
    // 已判定数用于 40 条上限（pending 行是丢失的判定，算进进度会让损坏章节被上限
    // 直接跳过、永远修不好）；未判定数用于判断该不该补跑判定阶段。
    public async ValueTask<(int Decided, int Undecided)> CountChapterExtractionCandidateDecisionsAsync(
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
            SELECT COALESCE(SUM(CASE WHEN decision <> $pending THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN decision = $pending THEN 1 ELSE 0 END), 0)
            FROM reference_material_candidates
            WHERE run_id = $run_id
              AND candidate_key LIKE 'chapter-extract:' || $chapter_index || ':%';
            """;
        command.Parameters.AddWithValue("$pending", ReferenceMaterializationCandidateDecisions.Pending);
        command.Parameters.AddWithValue("$run_id", normalizedRunId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0);
        }

        return (Convert.ToInt32(reader.GetInt64(0), System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(reader.GetInt64(1), System.Globalization.CultureInfo.InvariantCulture));
    }

    // 读取计划与已完成趟次：无计划或旧格式返回 null（worker 重新规划再分趟执行）。
    // 游标只反映"哪些趟已经付过模型费"，丢失判定的候选不在此处回卷重跑——重跑会重复
    // 付费并覆盖人工复核结果，交给判定阶段补打分（见 worker 的 pending 收尾分支）。
    public async ValueTask<ExtractionPlanState?> ReadExtractionPlanAsync(
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
            SELECT extraction_plan_json, extraction_round_index
            FROM reference_materialization_chapter_progress
            WHERE run_id = $run_id AND chapter_index = $chapter_index;
            """;
        command.Parameters.AddWithValue("$run_id", normalizedRunId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            return null;
        }

        var planJson = reader.GetString(0);
        var roundIndex = reader.GetInt32(1);
        var rounds = new List<ReferenceChapterExtractionRound>();
        // 读取侧与规划侧同等校验：任何不合格（旧格式、损坏 JSON、非法/重复类型、
        // 空趟、越界 round_index）都返回 null 让 worker 重规划——持久化计划是缓存，
        // 不是契约，重规划一次的代价远小于把垃圾载荷喂给模型或炸掉整批。
        if (!TryParseExtractionPlan(planJson, roundIndex, rounds))
        {
            return null;
        }

        return new ExtractionPlanState(rounds, roundIndex);
    }

    internal static bool TryParseExtractionPlan(
        string planJson,
        int roundIndex,
        List<ReferenceChapterExtractionRound> rounds)
    {
        rounds.Clear();
        try
        {
            using var document = JsonDocument.Parse(planJson);
            var root = document.RootElement;
            // 旧格式（字符区间计划）检测到即作废：调用方按类型分组格式重新规划，
            // 已完成轮的候选凭 upsert 幂等不重复计数。
            if (root.ValueKind != JsonValueKind.Array ||
                root.GetArrayLength() is 0 or > 16 ||
                (root.GetArrayLength() > 0 && root[0].TryGetProperty("start", out _)) ||
                roundIndex < 0 || roundIndex > root.GetArrayLength())
            {
                return false;
            }

            var allowedKinds = ReferenceMaterializationCandidateTypes.ChapterExtractionKinds;
            var coveredTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("material_types", out var typesElement) ||
                    !item.TryGetProperty("focus", out var focusElement) ||
                    typesElement.ValueKind != JsonValueKind.Array ||
                    focusElement.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var types = new List<string>();
                foreach (var typeElement in typesElement.EnumerateArray())
                {
                    if (typeElement.ValueKind != JsonValueKind.String ||
                        typeElement.GetString() is not { Length: > 0 } type ||
                        !allowedKinds.Contains(type) ||
                        !coveredTypes.Add(type))
                    {
                        return false;
                    }

                    types.Add(type);
                }

                if (types.Count == 0)
                {
                    return false;
                }

                var focus = focusElement.GetString() ?? string.Empty;
                rounds.Add(new ReferenceChapterExtractionRound(types, focus.Length > 80 ? focus[..80] : focus));
            }

            // 与规划校验一致：六种类型恰好各属一趟，不重不漏。
            return coveredTypes.Count == allowedKinds.Count;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // GetProperty/索引越界等结构性缺陷：作废重规划。
            return false;
        }
    }

    // 趟完成推进：已完成趟次 +1（断点续趟的位置标记）。
    public async ValueTask AdvanceExtractionRoundAsync(
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
            UPDATE reference_materialization_chapter_progress
            SET extraction_round_index = extraction_round_index + 1,
                row_version = row_version + 1
            WHERE run_id = $run_id AND chapter_index = $chapter_index;
            """;
        command.Parameters.AddWithValue("$run_id", normalizedRunId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // 每趟持久化结果：落库摘录与被丢弃的幻觉摘录数。
    internal sealed record ExtractionRoundResult(IReadOnlyList<string> PersistedExcerpts, int SkippedCount);

    // 每趟持久化：模型每返回一批材料就立即落库（逐字校验 + 候选 + 证据链接），
    // 章节保持 llm_qualifying 不迁移；计数每趟从候选表重算并刷新 run 漏斗——长提取
    // 期间 UI 能看到候选数持续增长，续跑与判定丢失修复时也一样。已判定候选（重试重放
    // 趟）幂等覆盖，未判定行交回判定阶段，保证人工复核结果不被重放冲掉。
    public async ValueTask<ExtractionRoundResult> PersistExtractionRoundAsync(
        string runId,
        int chapterIndex,
        IReadOnlyList<ReferenceChapterExtractedMaterial> materials,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(materials);
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
            connection, transaction, snapshot.AnchorId, snapshot.SplitProfileId, chapterIndex, cancellationToken)
            ?? throw new InvalidOperationException("Chapter text segment disappeared during materialization extraction.");

        var persisted = new List<string>();
        var skipped = 0;
        foreach (var material in materials)
        {
            var excerpt = material.Excerpt.Trim();
            if (excerpt.Length == 0)
            {
                skipped++;
                continue;
            }

            var indexInChapter = chapterText.IndexOf(excerpt, StringComparison.Ordinal);
            if (indexInChapter < 0)
            {
                skipped++;
                continue;
            }

            var excerptHash = HashText(excerpt);
            var candidateId = HashText(normalizedRunId + "|" + chapterIndex + "|" + excerptHash);
            var candidateKey = $"chapter-extract:{chapterIndex}:{excerptHash}";
            var decision = material.Confidence >= 0.5
                ? ReferenceMaterializationCandidateDecisions.Accepted
                : ReferenceMaterializationCandidateDecisions.ReviewRequired;
            var isExisting = false;
            await using (var probe = connection.CreateCommand())
            {
                probe.Transaction = transaction;
                probe.CommandText = "SELECT decision FROM reference_material_candidates WHERE candidate_id = $candidate_id;";
                probe.Parameters.AddWithValue("$candidate_id", candidateId);
                var existingDecision = await probe.ExecuteScalarAsync(cancellationToken) as string;
                if (existingDecision == ReferenceMaterializationCandidateDecisions.Pending)
                {
                    // 未判定行归判定阶段所有：重放趟不得用模型的新判定覆盖它，也不得重切
                    // 人工复核调整过的证据边界（复核确认的候选正停在 pending 等打分）。
                    continue;
                }

                isExisting = existingDecision is not null;
            }

            var absoluteStart = snapshot.ContentStart + indexInChapter;
            var absoluteEnd = absoluteStart + excerpt.Length;
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
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await LinkEvidenceNodesAsync(connection, transaction, snapshot.AnchorId, candidateId, absoluteStart, absoluteEnd, cancellationToken);
            if (!isExisting)
            {
                persisted.Add(excerpt);
            }
        }

        // 每趟都从候选表重算计数（不按新增行增量累加）：续跑与判定丢失自愈时行已经
        // 存在，增量写法会让实时进度停在 0 看起来像卡住。空趟同样要推进
        // model_call_count——轮询中的 UI 靠它分辨"活着"与"挂了"，模型明确回答
        // "本趟没有值得收集的素材"同样是一次真实完成。
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE reference_materialization_chapter_progress
                SET model_call_count = model_call_count + $model_calls,
                    candidate_count = (SELECT COUNT(*) FROM reference_material_candidates
                                       WHERE run_id = $run_id
                                         AND candidate_key LIKE 'chapter-extract:' || $chapter_index || ':%'),
                    decided_count = (SELECT COUNT(*) FROM reference_material_candidates
                                     WHERE run_id = $run_id
                                       AND decision <> $pending
                                       AND candidate_key LIKE 'chapter-extract:' || $chapter_index || ':%'),
                    accepted_count = (SELECT COUNT(*) FROM reference_material_candidates
                                      WHERE run_id = $run_id
                                        AND decision = $accepted
                                        AND candidate_key LIKE 'chapter-extract:' || $chapter_index || ':%'),
                    review_count = (SELECT COUNT(*) FROM reference_material_candidates
                                    WHERE run_id = $run_id
                                      AND decision = $review
                                      AND candidate_key LIKE 'chapter-extract:' || $chapter_index || ':%'),
                    row_version = row_version + 1
                WHERE run_id = $run_id AND chapter_index = $chapter_index;
                """;
            update.Parameters.AddWithValue("$model_calls", 1);
            update.Parameters.AddWithValue("$pending", ReferenceMaterializationCandidateDecisions.Pending);
            update.Parameters.AddWithValue("$accepted", ReferenceMaterializationCandidateDecisions.Accepted);
            update.Parameters.AddWithValue("$review", ReferenceMaterializationCandidateDecisions.ReviewRequired);
            update.Parameters.AddWithValue("$run_id", normalizedRunId);
            update.Parameters.AddWithValue("$chapter_index", chapterIndex);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await RefreshRunCountsAsync(connection, transaction, normalizedRunId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ExtractionRoundResult(persisted, skipped);
    }

    // 提取完成收尾：llm_qualifying -> embedding，计数从候选表重算为终值，
    // model_call_count 用 MAX 合并趟心跳与本次调用数。
    public async ValueTask<ReferenceChapterExtractionPersistenceResult> CompleteExtractionAsync(
        string runId,
        int chapterIndex,
        int modelCallCount,
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
        await ReadExtractionSnapshotAsync(connection, transaction, normalizedRunId, chapterIndex, cancellationToken);

        ReferenceMaterializationChapterStateMachine.EnsureCanTransition(
            ReferenceMaterializationChapterStates.LlmQualifying,
            ReferenceMaterializationChapterStates.Embedding);
        await using (var counts = connection.CreateCommand())
        {
            counts.Transaction = transaction;
            counts.CommandText = """
                UPDATE reference_materialization_chapter_progress
                SET status = $status, current_stage = $current_stage,
                    candidate_count = (SELECT COUNT(*) FROM reference_material_candidates candidate
                                       WHERE candidate.run_id = $run_id
                                         AND candidate.candidate_key LIKE 'chapter-extract:' || $chapter_index || ':%'),
                    accepted_count = (SELECT COUNT(*) FROM reference_material_candidates candidate
                                      WHERE candidate.run_id = $run_id
                                        AND candidate.decision = $accepted
                                        AND candidate.candidate_key LIKE 'chapter-extract:' || $chapter_index || ':%'),
                    review_count = (SELECT COUNT(*) FROM reference_material_candidates candidate
                                    WHERE candidate.run_id = $run_id
                                      AND candidate.decision = $review
                                      AND candidate.candidate_key LIKE 'chapter-extract:' || $chapter_index || ':%'),
                    decided_count = (SELECT COUNT(*) FROM reference_material_candidates candidate
                                     WHERE candidate.run_id = $run_id
                                       AND candidate.decision <> $pending
                                       AND candidate.candidate_key LIKE 'chapter-extract:' || $chapter_index || ':%'),
                    rejected_count = 0,
                    model_call_count = MAX(model_call_count, $model_call_count),
                    row_version = row_version + 1,
                    started_at = COALESCE(started_at, $started_at)
                WHERE run_id = $run_id AND chapter_index = $chapter_index;
                """;
            counts.Parameters.AddWithValue("$status", ReferenceMaterializationChapterStates.Embedding);
            counts.Parameters.AddWithValue("$current_stage", ReferenceMaterializationChapterStates.Embedding);
            counts.Parameters.AddWithValue("$accepted", ReferenceMaterializationCandidateDecisions.Accepted);
            counts.Parameters.AddWithValue("$review", ReferenceMaterializationCandidateDecisions.ReviewRequired);
            counts.Parameters.AddWithValue("$pending", ReferenceMaterializationCandidateDecisions.Pending);
            counts.Parameters.AddWithValue("$model_call_count", modelCallCount);
            counts.Parameters.AddWithValue("$started_at", FormatTimestamp(DateTimeOffset.UtcNow));
            counts.Parameters.AddWithValue("$run_id", normalizedRunId);
            counts.Parameters.AddWithValue("$chapter_index", chapterIndex);
            await counts.ExecuteNonQueryAsync(cancellationToken);
        }

        await RefreshRunCountsAsync(connection, transaction, normalizedRunId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var accepted = await ReadChapterAcceptedCountAsync(normalizedRunId, chapterIndex, cancellationToken);
        return new ReferenceChapterExtractionPersistenceResult(chapterIndex, accepted, accepted, 0, 0);
    }

    // 兼容入口：整批材料一次落库并收尾（测试与既有调用方使用）。
    public async ValueTask<ReferenceChapterExtractionPersistenceResult> PersistChapterExtractionAsync(
        string runId,
        int chapterIndex,
        ReferenceChapterExtractionResult extraction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        var round = await PersistExtractionRoundAsync(runId, chapterIndex, extraction.Materials, cancellationToken);
        var completed = await CompleteExtractionAsync(runId, chapterIndex, extraction.ModelCallCount, cancellationToken);
        return completed with { SkippedCount = round.SkippedCount };
    }

    private async ValueTask LinkEvidenceNodesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        string candidateId,
        int absoluteStart,
        int absoluteEnd,
        CancellationToken cancellationToken)
    {
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
            selectNodes.Parameters.AddWithValue("$anchor_id", anchorId);
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

        // 证据节点按粒度择优：摘录若与句子节点精确对齐（完全包含），只链接句子节点；
        // 否则若被段落节点完全包含，只链接段落节点；再退回所有相交节点（交集作为证据区间）。
        // 混用段落+句子会因层级重叠导致材料文本重复拼接。
        var containedSentences = overlappingNodes
            .Where(node => node.NodeType == "sentence" &&
                node.StartOffset >= absoluteStart && node.StartOffset + node.CharLen <= absoluteEnd)
            .ToList();
        var containedParagraphs = overlappingNodes
            .Where(node => node.NodeType == "paragraph" &&
                node.StartOffset >= absoluteStart && node.StartOffset + node.CharLen <= absoluteEnd)
            .ToList();
        List<ExtractionEvidenceNode> evidenceNodes = containedSentences.Count > 0
            ? containedSentences
            : containedParagraphs.Count > 0 ? containedParagraphs : overlappingNodes;

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
