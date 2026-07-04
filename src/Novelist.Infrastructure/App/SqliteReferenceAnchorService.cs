using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class SqliteReferenceAnchorService : IReferenceAnchorService
{
    private const string BuildVersion = "reference-anchor-v1";
    private const long MaxSourceBytes = 20L * 1024L * 1024L;
    private static readonly Regex MarkdownHeadingPattern = new(@"^\s{0,3}#{1,6}\s+(.+?)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BlankLinePattern = new(@"\n\s*\n", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RiskTokenPattern = new(@"[A-Za-z][A-Za-z0-9_]{1,}|\d+(?:\.\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = BridgeJson.SerializerOptions;
    private static readonly string[] DialogueMarkers = ["“", "”", "「", "」", "『", "』", "\"", "说：", "道：", "问：", "答："];
    private static readonly string[] SensoryMarkers = ["雨", "风", "雪", "光", "声", "呼吸", "气味", "冷", "热", "疼", "黑", "亮"];
    private static readonly string[] InteriorityMarkers = ["心", "想", "觉得", "明白", "知道", "意识到", "记得", "忘了"];
    private static readonly string[] ActionMarkers = ["走", "停", "看", "拿", "推", "转", "站", "坐", "伸", "退", "进", "出"];
    private static readonly string[] TransitionMarkers = ["后来", "然后", "这时", "与此同时", "片刻", "很快", "直到"];
    private static readonly string[] AiRiskPhrases = ["无法言喻", "复杂的情绪", "某种意义上", "仿佛有什么", "命运的齿轮", "心中涌起"];

    private static readonly HashSet<string> AllowedSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".md"
    };

    private static readonly HashSet<string> AllowedSourceKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "text",
        "markdown"
    };

    private static readonly HashSet<string> AllowedLicenseStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "user_provided",
        "licensed",
        "public_domain",
        "unknown"
    };

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqliteReferenceAnchorService(
        AppInitializationOptions? options = null,
        INovelService? novels = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
    }

    public async ValueTask<ReferenceAnchorPayload> CreateAnchorAsync(
        CreateReferenceAnchorPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        var title = NormalizeRequiredText(input.Title, nameof(input.Title), maxLength: 200);
        var author = NormalizeOptionalText(input.Author, nameof(input.Author), maxLength: 200);
        var sourcePath = ValidateSourcePath(input.SourcePath);
        var sourceKind = ValidateAllowedText(input.SourceKind, nameof(input.SourceKind), AllowedSourceKinds);
        var licenseStatus = ValidateAllowedText(input.LicenseStatus, nameof(input.LicenseStatus), AllowedLicenseStatuses);
        var source = await ReadSourceFileAsync(sourcePath, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var anchor = await InsertAnchorAsync(
                connection,
                transaction,
                input.NovelId,
                title,
                author,
                sourcePath,
                sourceKind,
                licenseStatus,
                source.Hash,
                now,
                cancellationToken);

            var segments = BuildSegments(anchor.AnchorId, source.Text);
            var materials = BuildMaterials(anchor.AnchorId, segments, now);
            await ReplaceSegmentsAsync(connection, transaction, anchor.AnchorId, segments, cancellationToken);
            var slotCount = CountMaterialSlots(materials);
            await ReplaceMaterialsAsync(connection, transaction, anchor.AnchorId, materials, cancellationToken);
            var readyAnchor = anchor with
            {
                Status = ReferenceAnchorBuildStates.Ready,
                UpdatedAt = now
            };
            await UpdateAnchorBuildResultAsync(
                connection,
                transaction,
                readyAnchor,
                ReferenceAnchorBuildStates.Ready,
                "ready",
                segments.Count,
                materials.Count,
                slotCount,
                lastError: string.Empty,
                now,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return readyAnchor;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ReferenceAnchorPayload>> GetAnchorsAsync(
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
                SELECT anchor_id, novel_id, title, author, source_path, source_kind, license_status,
                       source_file_hash, build_version, status, created_at, updated_at
                FROM reference_anchors
                WHERE novel_id = $novel_id
                ORDER BY created_at ASC, anchor_id ASC;
                """;
            command.Parameters.AddWithValue("$novel_id", novelId);
            var anchors = new List<ReferenceAnchorPayload>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                anchors.Add(ReadAnchor(reader));
            }

            return anchors;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceAnchorBuildStatusPayload> RebuildAnchorAsync(
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        ValidateAnchorId(anchorId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var anchor = await ReadAnchorAsync(connection, novelId, anchorId, cancellationToken);
            var now = DateTimeOffset.UtcNow;

            try
            {
                var source = await ReadSourceFileAsync(anchor.SourcePath, cancellationToken);
                var segments = BuildSegments(anchor.AnchorId, source.Text);
                var materials = BuildMaterials(anchor.AnchorId, segments, now);
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                var readyAnchor = anchor with
                {
                    SourceFileHash = source.Hash,
                    Status = ReferenceAnchorBuildStates.Ready,
                    UpdatedAt = now
                };
                await ReplaceSegmentsAsync(connection, transaction, anchor.AnchorId, segments, cancellationToken);
                var slotCount = CountMaterialSlots(materials);
                await ReplaceMaterialsAsync(connection, transaction, anchor.AnchorId, materials, cancellationToken);
                await UpdateAnchorBuildResultAsync(
                    connection,
                    transaction,
                    readyAnchor,
                    ReferenceAnchorBuildStates.Ready,
                    "ready",
                    segments.Count,
                    materials.Count,
                    slotCount,
                    lastError: string.Empty,
                    now,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return BuildStatus(readyAnchor, ReferenceAnchorBuildStates.Ready, "ready", segments.Count, materials.Count, slotCount, string.Empty, now);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                var failedAnchor = anchor with
                {
                    Status = ReferenceAnchorBuildStates.FailedImport,
                    UpdatedAt = now
                };
                var lastError = RedactError(exception.Message);
                await UpdateAnchorBuildResultAsync(
                    connection,
                    transaction,
                    failedAnchor,
                    ReferenceAnchorBuildStates.FailedImport,
                    ReferenceAnchorBuildStates.FailedImport,
                    sourceSegmentCount: 0,
                    materialCount: 0,
                    slotCount: 0,
                    lastError,
                    now,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return BuildStatus(failedAnchor, ReferenceAnchorBuildStates.FailedImport, ReferenceAnchorBuildStates.FailedImport, 0, 0, 0, lastError, now);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceAnchorBuildStatusPayload?> GetBuildStatusAsync(
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        ValidateAnchorId(anchorId);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT a.novel_id, s.anchor_id, s.status, s.stage, s.source_segment_count,
                       s.material_count, s.slot_count, s.vector_count, s.last_error, s.updated_at
                FROM reference_anchor_build_state s
                INNER JOIN reference_anchors a ON a.anchor_id = s.anchor_id
                WHERE a.novel_id = $novel_id AND s.anchor_id = $anchor_id;
                """;
            command.Parameters.AddWithValue("$novel_id", novelId);
            command.Parameters.AddWithValue("$anchor_id", anchorId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadBuildStatus(reader) : null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<PageResultPayload<ReferenceMaterialPayload>> SearchMaterialsAsync(
        SearchReferenceMaterialsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        var page = Math.Max(1, input.Page);
        var size = Math.Clamp(input.Size, 1, 100);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var anchorIds = input.AnchorIds?.Where(id => id > 0).Distinct().ToArray() ?? [];
            if (anchorIds.Length == 0)
            {
                anchorIds = await GetAnchorIdsAsync(connection, input.NovelId, cancellationToken);
            }

            if (anchorIds.Length == 0)
            {
                return new PageResultPayload<ReferenceMaterialPayload>([], 0, page, size, 0);
            }

            var all = await ReadMaterialsAsync(connection, input.NovelId, anchorIds, cancellationToken);
            var filtered = all
                .Where(item => MatchesMaterialFilters(item, input))
                .OrderByDescending(item => ScoreMaterial(item, input.Query))
                .ThenBy(item => item.AnchorId)
                .ThenBy(item => item.MaterialId, StringComparer.Ordinal)
                .ToArray();
            var total = filtered.LongLength;
            var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)size);
            var items = filtered
                .Skip((page - 1) * size)
                .Take(size)
                .ToArray();
            return new PageResultPayload<ReferenceMaterialPayload>(items, total, page, size, totalPages);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<AdaptReferenceMaterialResultPayload> AdaptMaterialAsync(
        AdaptReferenceMaterialPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        var materialId = NormalizeRequiredText(input.MaterialId, nameof(input.MaterialId), maxLength: 256);
        ValidateRewriteLevel(input.MaxRewriteLevel);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var material = await ReadMaterialAsync(connection, input.NovelId, materialId, cancellationToken)
                ?? throw new ArgumentException("Reference material does not exist.", nameof(input));
            var declaredSlots = await ReadMaterialSlotsAsync(connection, material.MaterialId, cancellationToken);
            var adapted = ApplySlotValues(material.Text, declaredSlots, input.SlotValues);
            var rewriteLevel = ReferenceRewriteLevelClassifier.Classify(
                material.Text,
                adapted.Text,
                adapted.ChangedSlots);
            var audit = BuildReuseAudit(
                material,
                adapted.Text,
                input.MaxRewriteLevel,
                input.SceneFacts,
                rewriteLevel,
                DateTimeOffset.UtcNow);
            var candidateId = "candidate-" + Guid.NewGuid().ToString("N");
            var result = new AdaptReferenceMaterialResultPayload(
                candidateId,
                material.MaterialId,
                rewriteLevel,
                adapted.Text,
                adapted.ChangedSlots,
                NonSlotEdits: [],
                audit);
            await PersistReuseCandidateAsync(connection, result, cancellationToken);
            return result;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceReuseAuditPayload> AuditCandidateAsync(
        AuditReferenceReusePayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        var materialId = NormalizeRequiredText(input.MaterialId, nameof(input.MaterialId), maxLength: 256);
        var candidateText = NormalizeRequiredText(input.CandidateText, nameof(input.CandidateText), maxLength: 20_000);
        ValidateRewriteLevel(input.MaxRewriteLevel);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var material = await ReadMaterialAsync(connection, input.NovelId, materialId, cancellationToken)
                ?? throw new ArgumentException("Reference material does not exist.", nameof(input));
            var rewriteLevel = ReferenceRewriteLevelClassifier.Classify(material.Text, candidateText);
            var audit = BuildReuseAudit(
                material,
                candidateText,
                input.MaxRewriteLevel,
                input.SceneFacts,
                rewriteLevel,
                DateTimeOffset.UtcNow);
            await PersistReuseAuditAsync(connection, candidateId: string.Empty, material.MaterialId, audit, cancellationToken);
            return audit;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask DeleteAnchorAsync(
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        ValidateAnchorId(anchorId);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM reference_anchors
                WHERE novel_id = $novel_id AND anchor_id = $anchor_id;
                """;
            command.Parameters.AddWithValue("$novel_id", novelId);
            command.Parameters.AddWithValue("$anchor_id", anchorId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<ReferenceAnchorPayload> InsertAnchorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long novelId,
        string title,
        string author,
        string sourcePath,
        string sourceKind,
        string licenseStatus,
        string sourceFileHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_anchors
              (novel_id, title, author, source_path, source_kind, license_status,
               source_file_hash, build_version, status, created_at, updated_at)
            VALUES
              ($novel_id, $title, $author, $source_path, $source_kind, $license_status,
               $source_file_hash, $build_version, $status, $created_at, $updated_at)
            RETURNING anchor_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$author", author);
        command.Parameters.AddWithValue("$source_path", sourcePath);
        command.Parameters.AddWithValue("$source_kind", sourceKind);
        command.Parameters.AddWithValue("$license_status", licenseStatus);
        command.Parameters.AddWithValue("$source_file_hash", sourceFileHash);
        command.Parameters.AddWithValue("$build_version", BuildVersion);
        command.Parameters.AddWithValue("$status", ReferenceAnchorBuildStates.Importing);
        command.Parameters.AddWithValue("$created_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
        var anchorId = (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("SQLite did not return a reference anchor id."));
        return new ReferenceAnchorPayload(
            anchorId,
            novelId,
            title,
            author,
            sourcePath,
            sourceKind,
            licenseStatus,
            sourceFileHash,
            BuildVersion,
            ReferenceAnchorBuildStates.Importing,
            now,
            now);
    }

    private static async ValueTask ReplaceSegmentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        IReadOnlyList<ReferenceSourceSegment> segments,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM reference_source_segments WHERE anchor_id = $anchor_id;";
            delete.Parameters.AddWithValue("$anchor_id", anchorId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var segment in segments)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO reference_source_segments
                  (segment_id, anchor_id, chapter_index, chapter_title, segment_type,
                   segment_index, parent_segment_id, start_offset, end_offset, text, text_hash)
                VALUES
                  ($segment_id, $anchor_id, $chapter_index, $chapter_title, $segment_type,
                   $segment_index, $parent_segment_id, $start_offset, $end_offset, $text, $text_hash);
                """;
            insert.Parameters.AddWithValue("$segment_id", segment.SegmentId);
            insert.Parameters.AddWithValue("$anchor_id", anchorId);
            insert.Parameters.AddWithValue("$chapter_index", segment.ChapterIndex);
            insert.Parameters.AddWithValue("$chapter_title", segment.ChapterTitle);
            insert.Parameters.AddWithValue("$segment_type", segment.SegmentType);
            insert.Parameters.AddWithValue("$segment_index", segment.SegmentIndex);
            insert.Parameters.AddWithValue("$parent_segment_id", segment.ParentSegmentId);
            insert.Parameters.AddWithValue("$start_offset", segment.StartOffset);
            insert.Parameters.AddWithValue("$end_offset", segment.EndOffset);
            insert.Parameters.AddWithValue("$text", segment.Text);
            insert.Parameters.AddWithValue("$text_hash", segment.TextHash);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask UpdateAnchorBuildResultAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReferenceAnchorPayload anchor,
        string status,
        string stage,
        int sourceSegmentCount,
        int materialCount,
        int slotCount,
        string lastError,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE reference_anchors
                SET source_file_hash = $source_file_hash,
                    build_version = $build_version,
                    status = $status,
                    updated_at = $updated_at
                WHERE anchor_id = $anchor_id;
                """;
            update.Parameters.AddWithValue("$source_file_hash", anchor.SourceFileHash);
            update.Parameters.AddWithValue("$build_version", anchor.BuildVersion);
            update.Parameters.AddWithValue("$status", anchor.Status);
            update.Parameters.AddWithValue("$updated_at", FormatTimestamp(updatedAt));
            update.Parameters.AddWithValue("$anchor_id", anchor.AnchorId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO reference_anchor_build_state
              (anchor_id, status, stage, source_segment_count, material_count,
               slot_count, vector_count, last_error, updated_at)
            VALUES
              ($anchor_id, $status, $stage, $source_segment_count, $material_count, $slot_count, 0, $last_error, $updated_at)
            ON CONFLICT(anchor_id) DO UPDATE SET
              status = excluded.status,
              stage = excluded.stage,
              source_segment_count = excluded.source_segment_count,
              material_count = excluded.material_count,
              slot_count = excluded.slot_count,
              vector_count = excluded.vector_count,
              last_error = excluded.last_error,
              updated_at = excluded.updated_at;
            """;
        upsert.Parameters.AddWithValue("$anchor_id", anchor.AnchorId);
        upsert.Parameters.AddWithValue("$status", status);
        upsert.Parameters.AddWithValue("$stage", stage);
        upsert.Parameters.AddWithValue("$source_segment_count", sourceSegmentCount);
        upsert.Parameters.AddWithValue("$material_count", materialCount);
        upsert.Parameters.AddWithValue("$slot_count", slotCount);
        upsert.Parameters.AddWithValue("$last_error", lastError);
        upsert.Parameters.AddWithValue("$updated_at", FormatTimestamp(updatedAt));
        await upsert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask ReplaceMaterialsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        IReadOnlyList<ReferenceMaterialPayload> materials,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM reference_materials WHERE anchor_id = $anchor_id;";
            delete.Parameters.AddWithValue("$anchor_id", anchorId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var material in materials)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO reference_materials
                  (material_id, anchor_id, source_segment_id, material_type, function_tag,
                   emotion_tag, scene_tag, pov_tag, technique_tag, function_confidence,
                   emotion_confidence, pov_confidence, text, source_hash, extractor_version,
                   user_verified, created_at)
                VALUES
                  ($material_id, $anchor_id, $source_segment_id, $material_type, $function_tag,
                   $emotion_tag, $scene_tag, $pov_tag, $technique_tag, $function_confidence,
                   $emotion_confidence, $pov_confidence, $text, $source_hash, $extractor_version,
                   $user_verified, $created_at);
                """;
            insert.Parameters.AddWithValue("$material_id", material.MaterialId);
            insert.Parameters.AddWithValue("$anchor_id", anchorId);
            insert.Parameters.AddWithValue("$source_segment_id", material.SourceSegmentId);
            insert.Parameters.AddWithValue("$material_type", material.MaterialType);
            insert.Parameters.AddWithValue("$function_tag", material.FunctionTag);
            insert.Parameters.AddWithValue("$emotion_tag", material.EmotionTag);
            insert.Parameters.AddWithValue("$scene_tag", material.SceneTag);
            insert.Parameters.AddWithValue("$pov_tag", material.PovTag);
            insert.Parameters.AddWithValue("$technique_tag", material.TechniqueTag);
            insert.Parameters.AddWithValue("$function_confidence", material.FunctionConfidence);
            insert.Parameters.AddWithValue("$emotion_confidence", material.EmotionConfidence);
            insert.Parameters.AddWithValue("$pov_confidence", material.PovConfidence);
            insert.Parameters.AddWithValue("$text", material.Text);
            insert.Parameters.AddWithValue("$source_hash", material.SourceHash);
            insert.Parameters.AddWithValue("$extractor_version", material.ExtractorVersion);
            insert.Parameters.AddWithValue("$user_verified", material.UserVerified ? 1 : 0);
            insert.Parameters.AddWithValue("$created_at", FormatTimestamp(material.CreatedAt));
            await insert.ExecuteNonQueryAsync(cancellationToken);

            foreach (var slot in ReferenceMaterialSlotDetector.Detect(material))
            {
                await using var slotInsert = connection.CreateCommand();
                slotInsert.Transaction = transaction;
                slotInsert.CommandText = """
                    INSERT INTO reference_material_slots
                      (slot_id, material_id, slot_name, placeholder, start_offset, end_offset, created_at)
                    VALUES
                      ($slot_id, $material_id, $slot_name, $placeholder, $start_offset, $end_offset, $created_at);
                    """;
                slotInsert.Parameters.AddWithValue("$slot_id", slot.SlotId);
                slotInsert.Parameters.AddWithValue("$material_id", slot.MaterialId);
                slotInsert.Parameters.AddWithValue("$slot_name", slot.SlotName);
                slotInsert.Parameters.AddWithValue("$placeholder", slot.Placeholder);
                slotInsert.Parameters.AddWithValue("$start_offset", slot.StartOffset);
                slotInsert.Parameters.AddWithValue("$end_offset", slot.EndOffset);
                slotInsert.Parameters.AddWithValue("$created_at", FormatTimestamp(material.CreatedAt));
                await slotInsert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async ValueTask<long[]> GetAnchorIdsAsync(
        SqliteConnection connection,
        long novelId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT anchor_id
            FROM reference_anchors
            WHERE novel_id = $novel_id
            ORDER BY anchor_id ASC;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        var anchorIds = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            anchorIds.Add(reader.GetInt64(0));
        }

        return anchorIds.ToArray();
    }

    private static async ValueTask<IReadOnlyList<ReferenceMaterialPayload>> ReadMaterialsAsync(
        SqliteConnection connection,
        long novelId,
        IReadOnlyList<long> anchorIds,
        CancellationToken cancellationToken)
    {
        if (anchorIds.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        var parameterNames = new List<string>(anchorIds.Count);
        for (var index = 0; index < anchorIds.Count; index++)
        {
            var parameterName = "$anchor_id_" + index.ToString(CultureInfo.InvariantCulture);
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, anchorIds[index]);
        }

        command.CommandText = $$"""
            SELECT m.material_id, m.anchor_id, m.source_segment_id, m.material_type,
                   m.function_tag, m.emotion_tag, m.scene_tag, m.pov_tag, m.technique_tag,
                   m.function_confidence, m.emotion_confidence, m.pov_confidence,
                   m.text, m.source_hash, m.extractor_version, m.user_verified, m.created_at
            FROM reference_materials m
            INNER JOIN reference_anchors a ON a.anchor_id = m.anchor_id
            WHERE a.novel_id = $novel_id
              AND m.anchor_id IN ({{string.Join(", ", parameterNames)}})
            ORDER BY m.anchor_id ASC, m.material_id ASC;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        var materials = new List<ReferenceMaterialPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            materials.Add(ReadMaterial(reader));
        }

        return materials;
    }

    private static async ValueTask<ReferenceMaterialPayload?> ReadMaterialAsync(
        SqliteConnection connection,
        long novelId,
        string materialId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.material_id, m.anchor_id, m.source_segment_id, m.material_type,
                   m.function_tag, m.emotion_tag, m.scene_tag, m.pov_tag, m.technique_tag,
                   m.function_confidence, m.emotion_confidence, m.pov_confidence,
                   m.text, m.source_hash, m.extractor_version, m.user_verified, m.created_at
            FROM reference_materials m
            INNER JOIN reference_anchors a ON a.anchor_id = m.anchor_id
            WHERE a.novel_id = $novel_id AND m.material_id = $material_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$material_id", materialId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMaterial(reader) : null;
    }

    private static async ValueTask<IReadOnlyList<ReferenceMaterialSlot>> ReadMaterialSlotsAsync(
        SqliteConnection connection,
        string materialId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT slot_id, material_id, slot_name, placeholder, start_offset, end_offset
            FROM reference_material_slots
            WHERE material_id = $material_id
            ORDER BY start_offset ASC, slot_name ASC;
            """;
        command.Parameters.AddWithValue("$material_id", materialId);
        var slots = new List<ReferenceMaterialSlot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            slots.Add(new ReferenceMaterialSlot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5)));
        }

        return slots;
    }

    private static async ValueTask PersistReuseCandidateAsync(
        SqliteConnection connection,
        AdaptReferenceMaterialResultPayload result,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var candidate = connection.CreateCommand())
        {
            candidate.Transaction = transaction;
            candidate.CommandText = """
                INSERT INTO reference_reuse_candidates
                  (candidate_id, material_id, rewrite_level, text, changed_slots_json,
                   non_slot_edits_json, audit_status, created_at)
                VALUES
                  ($candidate_id, $material_id, $rewrite_level, $text, $changed_slots_json,
                   $non_slot_edits_json, $audit_status, $created_at);
                """;
            candidate.Parameters.AddWithValue("$candidate_id", result.CandidateId);
            candidate.Parameters.AddWithValue("$material_id", result.MaterialId);
            candidate.Parameters.AddWithValue("$rewrite_level", result.RewriteLevel);
            candidate.Parameters.AddWithValue("$text", result.Text);
            candidate.Parameters.AddWithValue("$changed_slots_json", JsonSerializer.Serialize(result.ChangedSlots, JsonOptions));
            candidate.Parameters.AddWithValue("$non_slot_edits_json", JsonSerializer.Serialize(result.NonSlotEdits, JsonOptions));
            candidate.Parameters.AddWithValue("$audit_status", result.Audit.Status);
            candidate.Parameters.AddWithValue("$created_at", FormatTimestamp(result.Audit.AuditedAt));
            await candidate.ExecuteNonQueryAsync(cancellationToken);
        }

        await PersistReuseAuditAsync(
            connection,
            result.CandidateId,
            result.MaterialId,
            result.Audit,
            transaction,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask PersistReuseAuditAsync(
        SqliteConnection connection,
        string candidateId,
        string materialId,
        ReferenceReuseAuditPayload audit,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await PersistReuseAuditAsync(connection, candidateId, materialId, audit, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask PersistReuseAuditAsync(
        SqliteConnection connection,
        string candidateId,
        string materialId,
        ReferenceReuseAuditPayload audit,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_reuse_audits
              (audit_id, candidate_id, material_id, status, rewrite_level, provenance_errors_json,
               unsupported_fact_errors_json, ai_prose_risks_json, required_fixes_json, audited_at)
            VALUES
              ($audit_id, $candidate_id, $material_id, $status, $rewrite_level, $provenance_errors_json,
               $unsupported_fact_errors_json, $ai_prose_risks_json, $required_fixes_json, $audited_at);
            """;
        command.Parameters.AddWithValue("$audit_id", audit.AuditId);
        command.Parameters.AddWithValue("$candidate_id", candidateId);
        command.Parameters.AddWithValue("$material_id", materialId);
        command.Parameters.AddWithValue("$status", audit.Status);
        command.Parameters.AddWithValue("$rewrite_level", audit.RewriteLevel);
        command.Parameters.AddWithValue("$provenance_errors_json", JsonSerializer.Serialize(audit.ProvenanceErrors, JsonOptions));
        command.Parameters.AddWithValue("$unsupported_fact_errors_json", JsonSerializer.Serialize(audit.UnsupportedFactErrors, JsonOptions));
        command.Parameters.AddWithValue("$ai_prose_risks_json", JsonSerializer.Serialize(audit.AiProseRisks, JsonOptions));
        command.Parameters.AddWithValue("$required_fixes_json", JsonSerializer.Serialize(audit.RequiredFixes, JsonOptions));
        command.Parameters.AddWithValue("$audited_at", FormatTimestamp(audit.AuditedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask<ReferenceAnchorPayload> ReadAnchorAsync(
        SqliteConnection connection,
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT anchor_id, novel_id, title, author, source_path, source_kind, license_status,
                   source_file_hash, build_version, status, created_at, updated_at
            FROM reference_anchors
            WHERE novel_id = $novel_id AND anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ArgumentException($"Reference anchor '{anchorId}' does not exist.", nameof(anchorId));
        }

        return ReadAnchor(reader);
    }

    private async ValueTask EnsureSchemaAsync(string databasePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS reference_anchors (
              anchor_id INTEGER PRIMARY KEY,
              novel_id INTEGER NOT NULL,
              title TEXT NOT NULL,
              author TEXT NOT NULL,
              source_path TEXT NOT NULL,
              source_kind TEXT NOT NULL,
              license_status TEXT NOT NULL,
              source_file_hash TEXT NOT NULL,
              build_version TEXT NOT NULL,
              status TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS reference_anchor_build_state (
              anchor_id INTEGER PRIMARY KEY,
              status TEXT NOT NULL,
              stage TEXT NOT NULL,
              source_segment_count INTEGER NOT NULL,
              material_count INTEGER NOT NULL,
              slot_count INTEGER NOT NULL,
              vector_count INTEGER NOT NULL,
              last_error TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_source_segments (
              segment_id TEXT PRIMARY KEY,
              anchor_id INTEGER NOT NULL,
              chapter_index INTEGER NOT NULL,
              chapter_title TEXT NOT NULL,
              segment_type TEXT NOT NULL,
              segment_index INTEGER NOT NULL,
              parent_segment_id TEXT NOT NULL,
              start_offset INTEGER NOT NULL,
              end_offset INTEGER NOT NULL,
              text TEXT NOT NULL,
              text_hash TEXT NOT NULL,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_materials (
              material_id TEXT PRIMARY KEY,
              anchor_id INTEGER NOT NULL,
              source_segment_id TEXT NOT NULL,
              material_type TEXT NOT NULL,
              function_tag TEXT NOT NULL,
              emotion_tag TEXT NOT NULL,
              scene_tag TEXT NOT NULL,
              pov_tag TEXT NOT NULL,
              technique_tag TEXT NOT NULL,
              function_confidence REAL NOT NULL,
              emotion_confidence REAL NOT NULL,
              pov_confidence REAL NOT NULL,
              text TEXT NOT NULL,
              source_hash TEXT NOT NULL,
              extractor_version TEXT NOT NULL,
              user_verified INTEGER NOT NULL,
              created_at TEXT NOT NULL,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE,
              FOREIGN KEY(source_segment_id) REFERENCES reference_source_segments(segment_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_material_slots (
              slot_id TEXT PRIMARY KEY,
              material_id TEXT NOT NULL,
              slot_name TEXT NOT NULL,
              placeholder TEXT NOT NULL,
              start_offset INTEGER NOT NULL,
              end_offset INTEGER NOT NULL,
              created_at TEXT NOT NULL,
              FOREIGN KEY(material_id) REFERENCES reference_materials(material_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_reuse_candidates (
              candidate_id TEXT PRIMARY KEY,
              material_id TEXT NOT NULL,
              rewrite_level TEXT NOT NULL,
              text TEXT NOT NULL,
              changed_slots_json TEXT NOT NULL,
              non_slot_edits_json TEXT NOT NULL,
              audit_status TEXT NOT NULL,
              created_at TEXT NOT NULL,
              FOREIGN KEY(material_id) REFERENCES reference_materials(material_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_reuse_audits (
              audit_id TEXT PRIMARY KEY,
              candidate_id TEXT NOT NULL,
              material_id TEXT NOT NULL,
              status TEXT NOT NULL,
              rewrite_level TEXT NOT NULL,
              provenance_errors_json TEXT NOT NULL,
              unsupported_fact_errors_json TEXT NOT NULL,
              ai_prose_risks_json TEXT NOT NULL,
              required_fixes_json TEXT NOT NULL,
              audited_at TEXT NOT NULL,
              FOREIGN KEY(material_id) REFERENCES reference_materials(material_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_reference_anchors_novel
              ON reference_anchors(novel_id);

            CREATE INDEX IF NOT EXISTS idx_reference_segments_anchor_type
              ON reference_source_segments(anchor_id, segment_type, segment_index);

            CREATE INDEX IF NOT EXISTS idx_reference_materials_anchor_type
              ON reference_materials(anchor_id, material_type);

            CREATE INDEX IF NOT EXISTS idx_reference_materials_tags
              ON reference_materials(function_tag, emotion_tag, pov_tag, technique_tag);

            CREATE INDEX IF NOT EXISTS idx_reference_material_slots_material
              ON reference_material_slots(material_id, slot_name);

            CREATE INDEX IF NOT EXISTS idx_reference_candidates_material
              ON reference_reuse_candidates(material_id, created_at);
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
            "reference-anchor",
            "index.sqlite");
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async ValueTask<SourceSnapshot> ReadSourceFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new ArgumentException("Reference source file does not exist.", nameof(path));
        }

        if (info.Length <= 0)
        {
            throw new ArgumentException("Reference source file must not be empty.", nameof(path));
        }

        if (info.Length > MaxSourceBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(path), info.Length, $"Reference source file must be at most {MaxSourceBytes} bytes.");
        }

        await using var stream = File.OpenRead(info.FullName);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();
        var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        return new SourceSnapshot(text, HashBytes(bytes));
    }

    private static IReadOnlyList<ReferenceSourceSegment> BuildSegments(long anchorId, string sourceText)
    {
        var chapters = SplitChapters(sourceText);
        var segments = new List<ReferenceSourceSegment>();
        var chapterIndex = 0;
        var paragraphIndex = 0;
        var sentenceIndex = 0;

        foreach (var chapter in chapters)
        {
            chapterIndex++;
            var chapterHash = HashText(chapter.Text);
            var chapterId = BuildSegmentId(anchorId, "chapter", chapterIndex, 0, chapterHash);
            segments.Add(new ReferenceSourceSegment(
                chapterId,
                chapterIndex,
                chapter.Title,
                "chapter",
                chapterIndex,
                string.Empty,
                chapter.StartOffset,
                chapter.EndOffset,
                chapter.Text,
                chapterHash));

            foreach (var paragraph in SplitParagraphs(chapter.Text, chapter.StartOffset))
            {
                paragraphIndex++;
                var paragraphHash = HashText(paragraph.Text);
                var paragraphId = BuildSegmentId(anchorId, "paragraph", chapterIndex, paragraphIndex, paragraphHash);
                segments.Add(new ReferenceSourceSegment(
                    paragraphId,
                    chapterIndex,
                    chapter.Title,
                    "paragraph",
                    paragraphIndex,
                    chapterId,
                    paragraph.StartOffset,
                    paragraph.EndOffset,
                    paragraph.Text,
                    paragraphHash));

                foreach (var sentence in SplitSentences(paragraph.Text, paragraph.StartOffset))
                {
                    sentenceIndex++;
                    var sentenceHash = HashText(sentence.Text);
                    segments.Add(new ReferenceSourceSegment(
                        BuildSegmentId(anchorId, "sentence", chapterIndex, sentenceIndex, sentenceHash),
                        chapterIndex,
                        chapter.Title,
                        "sentence",
                        sentenceIndex,
                        paragraphId,
                        sentence.StartOffset,
                        sentence.EndOffset,
                        sentence.Text,
                        sentenceHash));
                }
            }
        }

        return segments;
    }

    private static IReadOnlyList<ReferenceMaterialPayload> BuildMaterials(
        long anchorId,
        IReadOnlyList<ReferenceSourceSegment> segments,
        DateTimeOffset now)
    {
        var materials = new List<ReferenceMaterialPayload>();
        foreach (var segment in segments)
        {
            var materialType = segment.SegmentType switch
            {
                "sentence" => ReferenceMaterialTypes.Sentence,
                "paragraph" => ReferenceMaterialTypes.Passage,
                _ => string.Empty
            };
            if (materialType.Length == 0)
            {
                continue;
            }

            var text = segment.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var tags = ClassifyMaterial(text);
            var materialId = BuildMaterialId(anchorId, materialType, segment.SegmentIndex, segment.TextHash);
            materials.Add(new ReferenceMaterialPayload(
                materialId,
                anchorId,
                segment.SegmentId,
                materialType,
                tags.FunctionTag,
                tags.EmotionTag,
                tags.SceneTag,
                tags.PovTag,
                tags.TechniqueTag,
                tags.FunctionConfidence,
                tags.EmotionConfidence,
                tags.PovConfidence,
                text,
                segment.TextHash,
                "deterministic-v1",
                UserVerified: false,
                now));
        }

        return materials;
    }

    private static int CountMaterialSlots(IReadOnlyList<ReferenceMaterialPayload> materials)
    {
        return materials.Sum(material => ReferenceMaterialSlotDetector.Detect(material).Count);
    }

    private static MaterialTags ClassifyMaterial(string text)
    {
        var isDialogue = ContainsAny(text, DialogueMarkers);
        var hasSensory = ContainsAny(text, SensoryMarkers);
        var hasInteriority = ContainsAny(text, InteriorityMarkers);
        var hasAction = ContainsAny(text, ActionMarkers);
        var hasTransition = ContainsAny(text, TransitionMarkers);

        var functionTag = isDialogue
            ? "dialogue"
            : hasInteriority
                ? "interiority"
                : hasSensory
                    ? "environment"
                    : hasTransition
                        ? "transition"
                        : hasAction
                            ? "action"
                            : "narration";
        var techniqueTag = isDialogue
            ? "dialogue_exchange"
            : hasInteriority
                ? "interiority"
                : hasSensory
                    ? "sensory_detail"
                    : hasTransition
                        ? "transition"
                        : "plain";
        var emotionTag = hasInteriority
            ? "reflective"
            : text.Contains('？', StringComparison.Ordinal) || text.Contains('?', StringComparison.Ordinal)
                ? "uncertain"
                : text.Contains('！', StringComparison.Ordinal) || text.Contains('!', StringComparison.Ordinal)
                    ? "heightened"
                    : isDialogue
                        ? "spoken"
                        : "neutral";
        var sceneTag = hasSensory
            ? "environment"
            : isDialogue
                ? "conversation"
                : "scene";
        var functionConfidence = functionTag is "narration" ? 0.55 : 0.8;
        var emotionConfidence = emotionTag is "neutral" ? 0.5 : 0.7;
        var povConfidence = hasInteriority ? 0.7 : 0.55;

        return new MaterialTags(
            functionTag,
            emotionTag,
            sceneTag,
            hasInteriority ? "close" : "unknown",
            techniqueTag,
            functionConfidence,
            emotionConfidence,
            povConfidence);
    }

    private static IReadOnlyList<TextSpan> SplitChapters(string sourceText)
    {
        var normalized = (sourceText ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var headings = new List<Heading>();
        var offset = 0;
        foreach (var line in lines)
        {
            var match = MarkdownHeadingPattern.Match(line);
            if (match.Success)
            {
                headings.Add(new Heading(match.Groups[1].Value.Trim(), offset, offset + line.Length));
            }

            offset += line.Length + 1;
        }

        if (headings.Count == 0)
        {
            var text = normalized.Trim();
            return text.Length == 0
                ? []
                : [new TextSpan("Chapter 1", text, normalized.IndexOf(text, StringComparison.Ordinal), normalized.IndexOf(text, StringComparison.Ordinal) + text.Length)];
        }

        var chapters = new List<TextSpan>();
        for (var index = 0; index < headings.Count; index++)
        {
            var current = headings[index];
            var contentStart = Math.Min(normalized.Length, current.LineEnd + 1);
            var contentEnd = index + 1 < headings.Count ? headings[index + 1].LineStart : normalized.Length;
            var rawText = normalized[contentStart..contentEnd];
            var trimmed = rawText.Trim();
            if (trimmed.Length == 0)
            {
                trimmed = current.Title;
            }

            var start = rawText.Length == 0
                ? current.LineStart
                : contentStart + rawText.IndexOf(trimmed, StringComparison.Ordinal);
            chapters.Add(new TextSpan(current.Title, trimmed, start, start + trimmed.Length));
        }

        return chapters;
    }

    private static IEnumerable<TextSpan> SplitParagraphs(string text, int baseOffset)
    {
        var searchStart = 0;
        foreach (var raw in BlankLinePattern.Split(text))
        {
            var paragraph = raw.Trim();
            if (paragraph.Length == 0)
            {
                continue;
            }

            var startInText = text.IndexOf(paragraph, searchStart, StringComparison.Ordinal);
            if (startInText < 0)
            {
                startInText = searchStart;
            }

            yield return new TextSpan(string.Empty, paragraph, baseOffset + startInText, baseOffset + startInText + paragraph.Length);
            searchStart = Math.Min(text.Length, startInText + paragraph.Length);
        }
    }

    private static IEnumerable<TextSpan> SplitSentences(string paragraph, int baseOffset)
    {
        var start = 0;
        for (var index = 0; index < paragraph.Length; index++)
        {
            if (!IsSentenceTerminator(paragraph[index]))
            {
                continue;
            }

            var sentence = TrimSpan(paragraph, start, index + 1);
            if (sentence.Text.Length > 0)
            {
                yield return sentence with
                {
                    StartOffset = baseOffset + sentence.StartOffset,
                    EndOffset = baseOffset + sentence.EndOffset
                };
            }

            start = index + 1;
        }

        if (start < paragraph.Length)
        {
            var sentence = TrimSpan(paragraph, start, paragraph.Length);
            if (sentence.Text.Length > 0)
            {
                yield return sentence with
                {
                    StartOffset = baseOffset + sentence.StartOffset,
                    EndOffset = baseOffset + sentence.EndOffset
                };
            }
        }
    }

    private static TextSpan TrimSpan(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return new TextSpan(string.Empty, text[start..end], start, end);
    }

    private static bool MatchesMaterialFilters(
        ReferenceMaterialPayload material,
        SearchReferenceMaterialsPayload input)
    {
        if (!string.IsNullOrWhiteSpace(input.Query) &&
            !material.Text.Contains(input.Query.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return MatchesAnyFilter(material.MaterialType, input.MaterialTypes) &&
            MatchesAnyFilter(material.EmotionTag, input.EmotionTags) &&
            MatchesAnyFilter(material.FunctionTag, input.FunctionTags) &&
            MatchesAnyFilter(material.PovTag, input.PovTags) &&
            MatchesAnyFilter(material.TechniqueTag, input.TechniqueTags);
    }

    private static bool MatchesAnyFilter(string value, IReadOnlyList<string>? filters)
    {
        return filters is null ||
            filters.Count == 0 ||
            filters.Any(filter => string.Equals(value, filter, StringComparison.OrdinalIgnoreCase));
    }

    private static double ScoreMaterial(ReferenceMaterialPayload material, string? query)
    {
        var score = 0.0;
        var normalizedQuery = (query ?? string.Empty).Trim();
        if (normalizedQuery.Length > 0)
        {
            var firstIndex = material.Text.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase);
            if (firstIndex >= 0)
            {
                score += 10.0;
                score += Math.Max(0, 2.0 - firstIndex / 20.0);
            }
        }

        score += material.MaterialType == ReferenceMaterialTypes.Sentence ? 1.5 : 0.8;
        score += material.FunctionConfidence;
        score += material.EmotionConfidence * 0.2;
        score += material.PovConfidence * 0.1;
        score += Math.Max(0, 1.0 - material.Text.Length / 500.0);
        return score;
    }

    private static AdaptedMaterial ApplySlotValues(
        string sourceText,
        IReadOnlyList<ReferenceMaterialSlot> declaredSlots,
        IReadOnlyList<ReferenceSlotValuePayload>? slotValues)
    {
        var text = sourceText;
        var changed = new List<ReferenceSlotValuePayload>();
        if (slotValues is null || slotValues.Count == 0)
        {
            return new AdaptedMaterial(text, changed);
        }

        var declared = declaredSlots
            .Select(slot => slot.SlotName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var slot in slotValues)
        {
            var slotName = (slot.SlotName ?? string.Empty).Trim();
            var value = slot.Value ?? string.Empty;
            if (slotName.Length == 0)
            {
                continue;
            }

            if (!declared.Contains(slotName))
            {
                throw new ArgumentException($"Slot '{slotName}' is not declared by this reference material.", nameof(slotValues));
            }

            var before = text;
            text = text.Replace("{{" + slotName + "}}", value, StringComparison.Ordinal);
            text = text.Replace("{" + slotName + "}", value, StringComparison.Ordinal);
            if (!string.Equals(before, text, StringComparison.Ordinal))
            {
                changed.Add(new ReferenceSlotValuePayload(slotName, value));
            }
        }

        return new AdaptedMaterial(text, changed);
    }

    private static ReferenceReuseAuditPayload BuildReuseAudit(
        ReferenceMaterialPayload material,
        string candidateText,
        string maxRewriteLevel,
        IReadOnlyList<string>? sceneFacts,
        string rewriteLevel,
        DateTimeOffset now)
    {
        var provenanceErrors = new List<string>();
        var unsupportedFactErrors = new List<string>();
        var aiProseRisks = new List<string>();
        var requiredFixes = new List<string>();

        if (string.IsNullOrWhiteSpace(material.MaterialId) || string.IsNullOrWhiteSpace(material.SourceHash))
        {
            provenanceErrors.Add("Reference material provenance is missing.");
        }

        if (string.IsNullOrWhiteSpace(candidateText))
        {
            provenanceErrors.Add("Candidate text is empty.");
        }

        if (!IsRewriteLevelAllowed(rewriteLevel, maxRewriteLevel))
        {
            requiredFixes.Add($"Rewrite level {rewriteLevel} exceeds max rewrite level {maxRewriteLevel}.");
        }

        foreach (var token in FindUnsupportedRiskTokens(material.Text, candidateText, sceneFacts))
        {
            unsupportedFactErrors.Add($"Candidate introduces unsupported token: {token}");
        }

        foreach (var phrase in AiRiskPhrases.Where(phrase => candidateText.Contains(phrase, StringComparison.Ordinal)))
        {
            aiProseRisks.Add($"Candidate contains high-risk AI phrase: {phrase}");
        }

        if (string.Equals(rewriteLevel, ReferenceRewriteLevels.L4, StringComparison.Ordinal))
        {
            requiredFixes.Add("L4 rewrite cannot pass reference reuse audit.");
        }

        var status = provenanceErrors.Count == 0 &&
            unsupportedFactErrors.Count == 0 &&
            requiredFixes.Count == 0
                ? "passed"
                : "failed";

        return new ReferenceReuseAuditPayload(
            "audit-" + Guid.NewGuid().ToString("N"),
            status,
            rewriteLevel,
            provenanceErrors,
            unsupportedFactErrors,
            aiProseRisks,
            requiredFixes,
            now);
    }

    private static IReadOnlyList<string> FindUnsupportedRiskTokens(
        string sourceText,
        string candidateText,
        IReadOnlyList<string>? sceneFacts)
    {
        var allowed = new HashSet<string>(RiskTokenPattern.Matches(sourceText).Select(match => match.Value), StringComparer.OrdinalIgnoreCase);
        if (sceneFacts is not null)
        {
            foreach (var fact in sceneFacts)
            {
                foreach (Match match in RiskTokenPattern.Matches(fact ?? string.Empty))
                {
                    allowed.Add(match.Value);
                }
            }
        }

        return RiskTokenPattern.Matches(candidateText)
            .Select(match => match.Value)
            .Where(token => !allowed.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsRewriteLevelAllowed(string rewriteLevel, string maxRewriteLevel)
    {
        return RewriteLevelRank(rewriteLevel) <= RewriteLevelRank(maxRewriteLevel);
    }

    private static int RewriteLevelRank(string rewriteLevel)
    {
        return rewriteLevel switch
        {
            ReferenceRewriteLevels.L0 => 0,
            ReferenceRewriteLevels.L1 => 1,
            ReferenceRewriteLevels.L2 => 2,
            ReferenceRewriteLevels.L3 => 3,
            ReferenceRewriteLevels.L4 => 4,
            _ => 99
        };
    }

    private static void ValidateRewriteLevel(string? rewriteLevel)
    {
        if (!ReferenceRewriteLevels.All.Contains(rewriteLevel ?? string.Empty, StringComparer.Ordinal))
        {
            throw new ArgumentException("Unsupported rewrite level.", nameof(rewriteLevel));
        }
    }

    private static bool IsSentenceTerminator(char value)
    {
        return value is '。' or '！' or '？' or '!' or '?' or '.';
    }

    private static string ValidateSourcePath(string? sourcePath)
    {
        var normalized = NormalizeRequiredText(sourcePath, nameof(sourcePath), maxLength: 1024);
        var fullPath = Path.GetFullPath(normalized);
        var extension = Path.GetExtension(fullPath);
        if (!AllowedSourceExtensions.Contains(extension))
        {
            throw new ArgumentException("Reference source must be a .txt or .md file.", nameof(sourcePath));
        }

        return fullPath;
    }

    private static string ValidateAllowedText(string? value, string name, IReadOnlySet<string> allowed)
    {
        var normalized = NormalizeRequiredText(value, name, maxLength: 128);
        if (!allowed.Contains(normalized))
        {
            throw new ArgumentException($"Unsupported {name}.", name);
        }

        return normalized;
    }

    private static string NormalizeRequiredText(string? value, string name, int maxLength)
    {
        var normalized = NormalizeOptionalText(value, name, maxLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value must be a non-empty string.", name);
        }

        return normalized;
    }

    private static string NormalizeOptionalText(string? value, string name, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(name, normalized.Length, $"Value must be at most {maxLength} characters.");
        }

        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Value must not contain control characters.", name);
        }

        return normalized;
    }

    private static string BuildSegmentId(long anchorId, string type, int chapterIndex, int segmentIndex, string hash)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{anchorId}:{chapterIndex}:{type}:{segmentIndex}:{hash[..16]}");
    }

    private static string BuildMaterialId(long anchorId, string type, int segmentIndex, string hash)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{anchorId}:material:{type}:{segmentIndex}:{hash[..16]}");
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> markers)
    {
        return markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));
    }

    private static ReferenceAnchorPayload ReadAnchor(SqliteDataReader reader)
    {
        return new ReferenceAnchorPayload(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            ParseTimestamp(reader.GetString(10)),
            ParseTimestamp(reader.GetString(11)));
    }

    private static ReferenceAnchorBuildStatusPayload ReadBuildStatus(SqliteDataReader reader)
    {
        return new ReferenceAnchorBuildStatusPayload(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetString(8),
            ParseTimestamp(reader.GetString(9)));
    }

    private static ReferenceMaterialPayload ReadMaterial(SqliteDataReader reader)
    {
        return new ReferenceMaterialPayload(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetDouble(9),
            reader.GetDouble(10),
            reader.GetDouble(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetInt32(15) != 0,
            ParseTimestamp(reader.GetString(16)));
    }

    private static ReferenceAnchorBuildStatusPayload BuildStatus(
        ReferenceAnchorPayload anchor,
        string status,
        string stage,
        int sourceSegmentCount,
        int materialCount,
        int slotCount,
        string lastError,
        DateTimeOffset updatedAt)
    {
        return new ReferenceAnchorBuildStatusPayload(
            anchor.NovelId,
            anchor.AnchorId,
            status,
            stage,
            sourceSegmentCount,
            materialCount,
            slotCount,
            VectorCount: 0,
            lastError,
            updatedAt);
    }

    private static string HashText(string text)
    {
        return HashBytes(Encoding.UTF8.GetBytes(text));
    }

    private static string HashBytes(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string RedactError(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Reference anchor import failed." : value.Trim();
    }

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }
    }

    private static void ValidateAnchorId(long anchorId)
    {
        if (anchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorId), anchorId, "Reference anchor id must be positive.");
        }
    }

    private sealed record SourceSnapshot(string Text, string Hash);

    private sealed record Heading(string Title, int LineStart, int LineEnd);

    private sealed record TextSpan(string Title, string Text, int StartOffset, int EndOffset);

    private sealed record ReferenceSourceSegment(
        string SegmentId,
        int ChapterIndex,
        string ChapterTitle,
        string SegmentType,
        int SegmentIndex,
        string ParentSegmentId,
        int StartOffset,
        int EndOffset,
        string Text,
        string TextHash);

    private sealed record MaterialTags(
        string FunctionTag,
        string EmotionTag,
        string SceneTag,
        string PovTag,
        string TechniqueTag,
        double FunctionConfidence,
        double EmotionConfidence,
        double PovConfidence);

    private sealed record AdaptedMaterial(
        string Text,
        IReadOnlyList<ReferenceSlotValuePayload> ChangedSlots);
}
