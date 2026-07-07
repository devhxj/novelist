using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class SqliteReferenceStyleProfileService : IReferenceStyleProfileService
{
    private const long WorkspaceCorpusNovelId = 0;
    private const int MaxAnchorCount = 50;
    private static readonly JsonSerializerOptions JsonOptions = BridgeJson.SerializerOptions;
    private static readonly string[] DefaultAllowedLicenseStatuses = ["user_provided", "licensed", "public_domain"];
    private static readonly string[] DefaultAllowedSourceTrustLevels =
    [
        ReferenceSourceTrustLevels.UserVerified,
        ReferenceSourceTrustLevels.Imported
    ];
    private static readonly HashSet<string> AllowedLicenseStatuses = new(
        ["user_provided", "licensed", "public_domain", "unknown"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedSourceTrustLevels = new(
        ReferenceSourceTrustLevels.All,
        StringComparer.Ordinal);

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqliteReferenceStyleProfileService(
        AppInitializationOptions? options = null,
        INovelService? novels = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
    }

    public async ValueTask<ReferenceStyleProfilePayload> BuildStyleProfileAsync(
        BuildReferenceStyleProfilePayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        var title = NormalizeRequiredText(input.Title, nameof(input.Title), maxLength: 200);
        var description = NormalizeOptionalText(input.Description, nameof(input.Description), maxLength: 1000);
        var anchorIds = NormalizeAnchorIds(input.AnchorIds);
        var allowedLicenseStatuses = NormalizeAllowedList(
            input.AllowedLicenseStatuses,
            DefaultAllowedLicenseStatuses,
            AllowedLicenseStatuses,
            nameof(input.AllowedLicenseStatuses));
        var allowedSourceTrustLevels = NormalizeAllowedList(
            input.AllowedSourceTrustLevels,
            DefaultAllowedSourceTrustLevels,
            AllowedSourceTrustLevels,
            nameof(input.AllowedSourceTrustLevels));
        var now = DateTimeOffset.UtcNow;
        var databasePath = await DatabasePathAsync(cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var sources = await ReadAccessibleSourcesAsync(
                connection,
                input.NovelId,
                anchorIds,
                allowedLicenseStatuses,
                allowedSourceTrustLevels,
                cancellationToken);
            var materials = await ReadActiveMaterialsAsync(connection, anchorIds, cancellationToken);
            if (materials.Count == 0)
            {
                throw new ArgumentException("Style profile requires at least one active reference material.", nameof(input.AnchorIds));
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var profileId = await InsertProfileShellAsync(
                    connection,
                    transaction,
                    input.NovelId,
                    title,
                    description,
                    anchorIds,
                    sources.Select(source => source.SourceFileHash).ToArray(),
                    allowedLicenseStatuses,
                    allowedSourceTrustLevels,
                    now,
                    cancellationToken);

                await InsertProfileSourcesAsync(
                    connection,
                    transaction,
                    profileId,
                    sources,
                    materials,
                    cancellationToken);

                var baseline = ReferenceStyleDeterministicBaselineExtractor.Build(profileId, materials);
                await InsertEvidenceAsync(
                    connection,
                    transaction,
                    baseline.EvidenceSpans,
                    now,
                    cancellationToken);
                await InsertMaterialStyleTagsAsync(
                    connection,
                    transaction,
                    baseline.EvidenceSpans,
                    now,
                    cancellationToken);
                await UpdateProfileFeaturesAsync(
                    connection,
                    transaction,
                    profileId,
                    baseline.Features,
                    baseline.AggregateConfidence,
                    now,
                    cancellationToken);
                await InsertAnalysisRunAsync(
                    connection,
                    transaction,
                    profileId,
                    anchorIds,
                    sources.Select(source => source.SourceFileHash).ToArray(),
                    baseline.Diagnostics,
                    now,
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                return new ReferenceStyleProfilePayload(
                    profileId,
                    input.NovelId,
                    title,
                    description,
                    ReferenceStyleProfileStatuses.Active,
                    ReferenceStyleAnalyzerVersions.DeterministicV1,
                    ReferenceStyleFeatureSchemaVersions.V1,
                    ReferenceStyleAnalyzerSources.DeterministicBaseline,
                    anchorIds,
                    sources.Select(source => source.SourceFileHash).ToArray(),
                    allowedLicenseStatuses,
                    allowedSourceTrustLevels,
                    baseline.AggregateConfidence,
                    baseline.Features,
                    baseline.EvidenceSpans,
                    now,
                    now,
                    ArchivedAt: null);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ReferenceStyleProfileSummaryPayload>> GetStyleProfilesAsync(
        GetReferenceStyleProfilesPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT profile_id, novel_id, title, description, status, analyzer_version,
                       feature_schema_version, analyzer_source, anchor_ids_json, source_hashes_json,
                       aggregate_confidence, created_at, updated_at, archived_at
                FROM reference_style_profiles
                WHERE novel_id = $novel_id
                  AND ($include_archived = 1 OR archived_at IS NULL)
                ORDER BY updated_at DESC, profile_id DESC;
                """;
            command.Parameters.AddWithValue("$novel_id", input.NovelId);
            command.Parameters.AddWithValue("$include_archived", input.IncludeArchived ? 1 : 0);
            var profiles = new List<ReferenceStyleProfileSummaryPayload>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                profiles.Add(ReadStyleProfileSummary(reader));
            }

            return profiles;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceStyleProfilePayload?> GetStyleProfileAsync(
        long novelId,
        long profileId,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        ValidateProfileId(profileId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var profile = await ReadStyleProfileAsync(connection, novelId, profileId, cancellationToken);
            return profile;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask EnsureSchemaAsync(string databasePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS reference_anchors (
              anchor_id INTEGER PRIMARY KEY,
              novel_id INTEGER,
              title TEXT NOT NULL,
              author TEXT NOT NULL,
              source_path TEXT NOT NULL,
              source_kind TEXT NOT NULL,
              license_status TEXT NOT NULL,
              source_file_hash TEXT NOT NULL,
              build_version TEXT NOT NULL,
              status TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              corpus_visibility TEXT NOT NULL DEFAULT 'private',
              source_trust TEXT NOT NULL DEFAULT 'user_verified',
              user_tags_json TEXT NOT NULL DEFAULT '[]'
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
              archived_at TEXT,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE,
              FOREIGN KEY(source_segment_id) REFERENCES reference_source_segments(segment_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_style_profiles (
              profile_id INTEGER PRIMARY KEY,
              novel_id INTEGER NOT NULL,
              title TEXT NOT NULL,
              description TEXT NOT NULL,
              status TEXT NOT NULL,
              analyzer_version TEXT NOT NULL,
              feature_schema_version TEXT NOT NULL,
              analyzer_source TEXT NOT NULL,
              anchor_ids_json TEXT NOT NULL,
              source_hashes_json TEXT NOT NULL,
              allowed_license_statuses_json TEXT NOT NULL,
              allowed_source_trust_levels_json TEXT NOT NULL,
              feature_vector_json TEXT NOT NULL,
              aggregate_confidence REAL NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              archived_at TEXT
            );

            CREATE TABLE IF NOT EXISTS reference_style_profile_sources (
              profile_id INTEGER NOT NULL,
              anchor_id INTEGER NOT NULL,
              source_file_hash TEXT NOT NULL,
              license_status TEXT NOT NULL,
              source_trust TEXT NOT NULL,
              corpus_visibility TEXT NOT NULL,
              material_count INTEGER NOT NULL,
              segment_count INTEGER NOT NULL,
              PRIMARY KEY(profile_id, anchor_id),
              FOREIGN KEY(profile_id) REFERENCES reference_style_profiles(profile_id) ON DELETE CASCADE,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE RESTRICT
            );

            CREATE TABLE IF NOT EXISTS reference_style_profile_evidence (
              evidence_id TEXT PRIMARY KEY,
              profile_id INTEGER NOT NULL,
              anchor_id INTEGER NOT NULL,
              source_segment_id TEXT NOT NULL,
              material_id TEXT,
              feature_key TEXT NOT NULL,
              label TEXT NOT NULL,
              start_offset INTEGER NOT NULL,
              end_offset INTEGER NOT NULL,
              text_hash TEXT NOT NULL,
              confidence REAL NOT NULL,
              analyzer_source TEXT NOT NULL,
              created_at TEXT NOT NULL,
              FOREIGN KEY(profile_id) REFERENCES reference_style_profiles(profile_id) ON DELETE CASCADE,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE RESTRICT,
              FOREIGN KEY(source_segment_id) REFERENCES reference_source_segments(segment_id) ON DELETE RESTRICT,
              FOREIGN KEY(material_id) REFERENCES reference_materials(material_id) ON DELETE RESTRICT
            );

            CREATE TABLE IF NOT EXISTS reference_style_analysis_runs (
              run_id TEXT PRIMARY KEY,
              profile_id INTEGER NOT NULL,
              analyzer_version TEXT NOT NULL,
              feature_schema_version TEXT NOT NULL,
              analyzer_source TEXT NOT NULL,
              input_anchor_ids_json TEXT NOT NULL,
              input_source_hashes_json TEXT NOT NULL,
              status TEXT NOT NULL,
              diagnostics_json TEXT NOT NULL,
              created_at TEXT NOT NULL,
              FOREIGN KEY(profile_id) REFERENCES reference_style_profiles(profile_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_material_style_tags (
              profile_id INTEGER NOT NULL,
              material_id TEXT NOT NULL,
              tag_key TEXT NOT NULL,
              tag_value TEXT NOT NULL,
              confidence REAL NOT NULL,
              evidence_id TEXT NOT NULL,
              analyzer_source TEXT NOT NULL,
              analyzer_version TEXT NOT NULL,
              created_at TEXT NOT NULL,
              PRIMARY KEY(profile_id, material_id, tag_key, tag_value, analyzer_version),
              FOREIGN KEY(profile_id) REFERENCES reference_style_profiles(profile_id) ON DELETE CASCADE,
              FOREIGN KEY(material_id) REFERENCES reference_materials(material_id) ON DELETE RESTRICT,
              FOREIGN KEY(evidence_id) REFERENCES reference_style_profile_evidence(evidence_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_reference_style_profiles_novel
              ON reference_style_profiles(novel_id, status, updated_at);

            CREATE INDEX IF NOT EXISTS idx_reference_style_profile_sources_anchor
              ON reference_style_profile_sources(anchor_id);

            CREATE INDEX IF NOT EXISTS idx_reference_style_evidence_profile_feature
              ON reference_style_profile_evidence(profile_id, feature_key);

            CREATE INDEX IF NOT EXISTS idx_reference_material_style_tags_material
              ON reference_material_style_tags(material_id, tag_key);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        var addedCorpusVisibility = await EnsureColumnAsync(
            connection,
            "reference_anchors",
            "corpus_visibility",
            "ALTER TABLE reference_anchors ADD COLUMN corpus_visibility TEXT NOT NULL DEFAULT 'private';",
            cancellationToken);
        if (addedCorpusVisibility)
        {
            await PromoteLegacyWorkspaceCorpusVisibilityAsync(connection, cancellationToken);
        }

        await EnsureColumnAsync(
            connection,
            "reference_anchors",
            "source_trust",
            "ALTER TABLE reference_anchors ADD COLUMN source_trust TEXT NOT NULL DEFAULT 'user_verified';",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_anchors",
            "user_tags_json",
            "ALTER TABLE reference_anchors ADD COLUMN user_tags_json TEXT NOT NULL DEFAULT '[]';",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_materials",
            "archived_at",
            "ALTER TABLE reference_materials ADD COLUMN archived_at TEXT;",
            cancellationToken);
    }

    private static async ValueTask<bool> EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string alterSql,
        CancellationToken cancellationToken)
    {
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(" + tableName + ");";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = alterSql;
        await alter.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    private static async ValueTask PromoteLegacyWorkspaceCorpusVisibilityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_anchors
            SET corpus_visibility = $workspace_visibility
            WHERE novel_id = $workspace_corpus_novel_id
              AND corpus_visibility = $private_visibility;
            """;
        command.Parameters.AddWithValue("$workspace_visibility", ReferenceCorpusVisibilities.Workspace);
        command.Parameters.AddWithValue("$private_visibility", ReferenceCorpusVisibilities.Private);
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask<IReadOnlyList<ReferenceStyleAnchorSource>> ReadAccessibleSourcesAsync(
        SqliteConnection connection,
        long novelId,
        IReadOnlyList<long> anchorIds,
        IReadOnlyList<string> allowedLicenseStatuses,
        IReadOnlyList<string> allowedSourceTrustLevels,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var anchorParameters = AddLongParameters(command, "$anchor_id_", anchorIds);
        command.CommandText = $$"""
            SELECT anchor_id, COALESCE(novel_id, $workspace_corpus_novel_id), source_file_hash,
                   license_status, source_trust, corpus_visibility
            FROM reference_anchors
            WHERE anchor_id IN ({{string.Join(", ", anchorParameters)}})
              AND (novel_id = $novel_id OR ((novel_id IS NULL OR novel_id = $workspace_corpus_novel_id) AND corpus_visibility = $workspace_visibility))
            ORDER BY anchor_id ASC;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        command.Parameters.AddWithValue("$workspace_visibility", ReferenceCorpusVisibilities.Workspace);

        var sources = new List<ReferenceStyleAnchorSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sources.Add(new ReferenceStyleAnchorSource(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        if (sources.Count != anchorIds.Count)
        {
            var accessibleIds = sources.Select(source => source.AnchorId).ToHashSet();
            var missing = anchorIds.Where(anchorId => !accessibleIds.Contains(anchorId)).ToArray();
            throw new ArgumentException($"Reference style profile cannot access anchors: {string.Join(", ", missing)}.", nameof(anchorIds));
        }

        var disallowedLicense = sources.FirstOrDefault(source => !allowedLicenseStatuses.Contains(source.LicenseStatus, StringComparer.Ordinal));
        if (disallowedLicense is not null)
        {
            throw new ArgumentException($"Anchor '{disallowedLicense.AnchorId}' has disallowed license status '{disallowedLicense.LicenseStatus}'.", nameof(allowedLicenseStatuses));
        }

        var disallowedTrust = sources.FirstOrDefault(source => !allowedSourceTrustLevels.Contains(source.SourceTrust, StringComparer.Ordinal));
        if (disallowedTrust is not null)
        {
            throw new ArgumentException($"Anchor '{disallowedTrust.AnchorId}' has disallowed source trust '{disallowedTrust.SourceTrust}'.", nameof(allowedSourceTrustLevels));
        }

        return anchorIds
            .Select(anchorId => sources.Single(source => source.AnchorId == anchorId))
            .ToArray();
    }

    private static async ValueTask<IReadOnlyList<ReferenceStyleMaterialSample>> ReadActiveMaterialsAsync(
        SqliteConnection connection,
        IReadOnlyList<long> anchorIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var anchorParameters = AddLongParameters(command, "$material_anchor_id_", anchorIds);
        command.CommandText = $$"""
            SELECT m.material_id, m.anchor_id, m.source_segment_id, m.material_type,
                   m.function_tag, m.emotion_tag, m.scene_tag, m.pov_tag, m.technique_tag,
                   m.function_confidence, m.emotion_confidence, m.pov_confidence,
                   m.text, m.source_hash, s.start_offset, s.end_offset, s.text_hash
            FROM reference_materials m
            INNER JOIN reference_source_segments s ON s.segment_id = m.source_segment_id
            WHERE m.anchor_id IN ({{string.Join(", ", anchorParameters)}})
              AND m.archived_at IS NULL
            ORDER BY m.anchor_id ASC, m.material_type ASC, m.material_id ASC;
            """;

        var rows = new List<ReferenceStyleMaterialSample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ReferenceStyleMaterialSample(
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
                reader.GetInt32(14),
                reader.GetInt32(15),
                reader.GetString(16)));
        }

        return rows;
    }

    private static async ValueTask<long> InsertProfileShellAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long novelId,
        string title,
        string description,
        IReadOnlyList<long> anchorIds,
        IReadOnlyList<string> sourceHashes,
        IReadOnlyList<string> allowedLicenseStatuses,
        IReadOnlyList<string> allowedSourceTrustLevels,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_style_profiles
              (novel_id, title, description, status, analyzer_version, feature_schema_version,
               analyzer_source, anchor_ids_json, source_hashes_json, allowed_license_statuses_json,
               allowed_source_trust_levels_json, feature_vector_json, aggregate_confidence,
               created_at, updated_at, archived_at)
            VALUES
              ($novel_id, $title, $description, $status, $analyzer_version, $feature_schema_version,
               $analyzer_source, $anchor_ids_json, $source_hashes_json, $allowed_license_statuses_json,
               $allowed_source_trust_levels_json, $feature_vector_json, 0,
               $created_at, $updated_at, NULL);
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$status", ReferenceStyleProfileStatuses.Active);
        command.Parameters.AddWithValue("$analyzer_version", ReferenceStyleAnalyzerVersions.DeterministicV1);
        command.Parameters.AddWithValue("$feature_schema_version", ReferenceStyleFeatureSchemaVersions.V1);
        command.Parameters.AddWithValue("$analyzer_source", ReferenceStyleAnalyzerSources.DeterministicBaseline);
        command.Parameters.AddWithValue("$anchor_ids_json", JsonSerializer.Serialize(anchorIds, JsonOptions));
        command.Parameters.AddWithValue("$source_hashes_json", JsonSerializer.Serialize(sourceHashes, JsonOptions));
        command.Parameters.AddWithValue("$allowed_license_statuses_json", JsonSerializer.Serialize(allowedLicenseStatuses, JsonOptions));
        command.Parameters.AddWithValue("$allowed_source_trust_levels_json", JsonSerializer.Serialize(allowedSourceTrustLevels, JsonOptions));
        command.Parameters.AddWithValue("$feature_vector_json", JsonSerializer.Serialize(EmptyFeatureVector(), JsonOptions));
        command.Parameters.AddWithValue("$created_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var idCommand = connection.CreateCommand();
        idCommand.Transaction = transaction;
        idCommand.CommandText = "SELECT last_insert_rowid();";
        return (long)(await idCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static async ValueTask InsertProfileSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long profileId,
        IReadOnlyList<ReferenceStyleAnchorSource> sources,
        IReadOnlyList<ReferenceStyleMaterialSample> materials,
        CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reference_style_profile_sources
                  (profile_id, anchor_id, source_file_hash, license_status, source_trust,
                   corpus_visibility, material_count, segment_count)
                VALUES
                  ($profile_id, $anchor_id, $source_file_hash, $license_status, $source_trust,
                   $corpus_visibility, $material_count, $segment_count);
                """;
            command.Parameters.AddWithValue("$profile_id", profileId);
            command.Parameters.AddWithValue("$anchor_id", source.AnchorId);
            command.Parameters.AddWithValue("$source_file_hash", source.SourceFileHash);
            command.Parameters.AddWithValue("$license_status", source.LicenseStatus);
            command.Parameters.AddWithValue("$source_trust", source.SourceTrust);
            command.Parameters.AddWithValue("$corpus_visibility", source.CorpusVisibility);
            command.Parameters.AddWithValue("$material_count", materials.Count(material => material.AnchorId == source.AnchorId));
            command.Parameters.AddWithValue("$segment_count", materials.Where(material => material.AnchorId == source.AnchorId).Select(material => material.SourceSegmentId).Distinct(StringComparer.Ordinal).Count());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask InsertEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ReferenceStyleEvidenceSpanPayload> evidenceSpans,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var evidence in evidenceSpans)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reference_style_profile_evidence
                  (evidence_id, profile_id, anchor_id, source_segment_id, material_id,
                   feature_key, label, start_offset, end_offset, text_hash, confidence,
                   analyzer_source, created_at)
                VALUES
                  ($evidence_id, $profile_id, $anchor_id, $source_segment_id, $material_id,
                   $feature_key, $label, $start_offset, $end_offset, $text_hash, $confidence,
                   $analyzer_source, $created_at);
                """;
            command.Parameters.AddWithValue("$evidence_id", evidence.EvidenceId);
            command.Parameters.AddWithValue("$profile_id", evidence.ProfileId);
            command.Parameters.AddWithValue("$anchor_id", evidence.AnchorId);
            command.Parameters.AddWithValue("$source_segment_id", evidence.SourceSegmentId);
            command.Parameters.AddWithValue("$material_id", (object?)evidence.MaterialId ?? DBNull.Value);
            command.Parameters.AddWithValue("$feature_key", evidence.FeatureKey);
            command.Parameters.AddWithValue("$label", evidence.Label);
            command.Parameters.AddWithValue("$start_offset", evidence.StartOffset);
            command.Parameters.AddWithValue("$end_offset", evidence.EndOffset);
            command.Parameters.AddWithValue("$text_hash", evidence.TextHash);
            command.Parameters.AddWithValue("$confidence", evidence.Confidence);
            command.Parameters.AddWithValue("$analyzer_source", evidence.AnalyzerSource);
            command.Parameters.AddWithValue("$created_at", FormatTimestamp(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask InsertMaterialStyleTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ReferenceStyleEvidenceSpanPayload> evidenceSpans,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var evidence in evidenceSpans.Where(evidence => !string.IsNullOrWhiteSpace(evidence.MaterialId)))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO reference_material_style_tags
                  (profile_id, material_id, tag_key, tag_value, confidence, evidence_id,
                   analyzer_source, analyzer_version, created_at)
                VALUES
                  ($profile_id, $material_id, $tag_key, $tag_value, $confidence, $evidence_id,
                   $analyzer_source, $analyzer_version, $created_at);
                """;
            command.Parameters.AddWithValue("$profile_id", evidence.ProfileId);
            command.Parameters.AddWithValue("$material_id", evidence.MaterialId!);
            command.Parameters.AddWithValue("$tag_key", evidence.FeatureKey);
            command.Parameters.AddWithValue("$tag_value", evidence.Label);
            command.Parameters.AddWithValue("$confidence", evidence.Confidence);
            command.Parameters.AddWithValue("$evidence_id", evidence.EvidenceId);
            command.Parameters.AddWithValue("$analyzer_source", evidence.AnalyzerSource);
            command.Parameters.AddWithValue("$analyzer_version", ReferenceStyleAnalyzerVersions.DeterministicV1);
            command.Parameters.AddWithValue("$created_at", FormatTimestamp(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask UpdateProfileFeaturesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long profileId,
        ReferenceStyleFeatureVectorPayload features,
        double aggregateConfidence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_style_profiles
            SET feature_vector_json = $feature_vector_json,
                aggregate_confidence = $aggregate_confidence,
                updated_at = $updated_at
            WHERE profile_id = $profile_id;
            """;
        command.Parameters.AddWithValue("$feature_vector_json", JsonSerializer.Serialize(features, JsonOptions));
        command.Parameters.AddWithValue("$aggregate_confidence", aggregateConfidence);
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$profile_id", profileId);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException("Reference style profile feature update failed.");
        }
    }

    private static async ValueTask InsertAnalysisRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long profileId,
        IReadOnlyList<long> anchorIds,
        IReadOnlyList<string> sourceHashes,
        IReadOnlyDictionary<string, string> diagnostics,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_style_analysis_runs
              (run_id, profile_id, analyzer_version, feature_schema_version, analyzer_source,
               input_anchor_ids_json, input_source_hashes_json, status, diagnostics_json, created_at)
            VALUES
              ($run_id, $profile_id, $analyzer_version, $feature_schema_version, $analyzer_source,
               $input_anchor_ids_json, $input_source_hashes_json, $status, $diagnostics_json, $created_at);
            """;
        command.Parameters.AddWithValue("$run_id", "style-run-" + Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$analyzer_version", ReferenceStyleAnalyzerVersions.DeterministicV1);
        command.Parameters.AddWithValue("$feature_schema_version", ReferenceStyleFeatureSchemaVersions.V1);
        command.Parameters.AddWithValue("$analyzer_source", ReferenceStyleAnalyzerSources.DeterministicBaseline);
        command.Parameters.AddWithValue("$input_anchor_ids_json", JsonSerializer.Serialize(anchorIds, JsonOptions));
        command.Parameters.AddWithValue("$input_source_hashes_json", JsonSerializer.Serialize(sourceHashes, JsonOptions));
        command.Parameters.AddWithValue("$status", "completed");
        command.Parameters.AddWithValue("$diagnostics_json", JsonSerializer.Serialize(diagnostics, JsonOptions));
        command.Parameters.AddWithValue("$created_at", FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask<ReferenceStyleProfilePayload?> ReadStyleProfileAsync(
        SqliteConnection connection,
        long novelId,
        long profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, novel_id, title, description, status, analyzer_version,
                   feature_schema_version, analyzer_source, anchor_ids_json, source_hashes_json,
                   allowed_license_statuses_json, allowed_source_trust_levels_json,
                   feature_vector_json, aggregate_confidence, created_at, updated_at, archived_at
            FROM reference_style_profiles
            WHERE novel_id = $novel_id
              AND profile_id = $profile_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$profile_id", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var profile = ReadStyleProfile(reader);
        var evidence = await ReadEvidenceAsync(connection, profileId, cancellationToken);
        return profile with { EvidenceSpans = evidence };
    }

    private static async ValueTask<IReadOnlyList<ReferenceStyleEvidenceSpanPayload>> ReadEvidenceAsync(
        SqliteConnection connection,
        long profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT evidence_id, profile_id, anchor_id, source_segment_id, material_id,
                   feature_key, label, start_offset, end_offset, text_hash, confidence,
                   analyzer_source
            FROM reference_style_profile_evidence
            WHERE profile_id = $profile_id
            ORDER BY feature_key ASC, evidence_id ASC;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        var evidence = new List<ReferenceStyleEvidenceSpanPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            evidence.Add(ReadEvidence(reader));
        }

        return evidence;
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

    private static IReadOnlyList<string> AddLongParameters(
        SqliteCommand command,
        string prefix,
        IReadOnlyList<long> values)
    {
        var parameterNames = new List<string>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var parameterName = prefix + index.ToString(CultureInfo.InvariantCulture);
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, values[index]);
        }

        return parameterNames;
    }

    private static IReadOnlyList<long> NormalizeAnchorIds(IReadOnlyList<long>? anchorIds)
    {
        if (anchorIds is null || anchorIds.Count == 0)
        {
            throw new ArgumentException("At least one source anchor is required.", nameof(anchorIds));
        }

        var normalized = new List<long>(anchorIds.Count);
        var seen = new HashSet<long>();
        foreach (var anchorId in anchorIds)
        {
            if (anchorId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(anchorIds), anchorId, "Reference anchor id must be positive.");
            }

            if (seen.Add(anchorId))
            {
                normalized.Add(anchorId);
            }
        }

        if (normalized.Count > MaxAnchorCount)
        {
            throw new ArgumentException($"At most {MaxAnchorCount} source anchors can be used for one style profile.", nameof(anchorIds));
        }

        return normalized;
    }

    private static IReadOnlyList<string> NormalizeAllowedList(
        IReadOnlyList<string>? input,
        IReadOnlyList<string> defaults,
        IReadOnlySet<string> allowed,
        string name)
    {
        var raw = input is null || input.Count == 0 ? defaults : input;
        var normalized = raw
            .Select(value => NormalizeRequiredText(value, name, maxLength: 128))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var value in normalized)
        {
            if (!allowed.Contains(value))
            {
                throw new ArgumentException($"Unsupported {name}: {value}.", name);
            }
        }

        return normalized;
    }

    private static ReferenceStyleFeatureVectorPayload EmptyFeatureVector()
    {
        return new ReferenceStyleFeatureVectorPayload([], [], []);
    }

    private static ReferenceStyleProfileSummaryPayload ReadStyleProfileSummary(SqliteDataReader reader)
    {
        return new ReferenceStyleProfileSummaryPayload(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            ReadLongList(reader.GetString(8)),
            ReadStringList(reader.GetString(9)),
            reader.GetDouble(10),
            ParseTimestamp(reader.GetString(11)),
            ParseTimestamp(reader.GetString(12)),
            reader.IsDBNull(13) ? null : ParseTimestamp(reader.GetString(13)));
    }

    private static ReferenceStyleProfilePayload ReadStyleProfile(SqliteDataReader reader)
    {
        return new ReferenceStyleProfilePayload(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            ReadLongList(reader.GetString(8)),
            ReadStringList(reader.GetString(9)),
            ReadStringList(reader.GetString(10)),
            ReadStringList(reader.GetString(11)),
            reader.GetDouble(13),
            ReadFeatureVector(reader.GetString(12)),
            [],
            ParseTimestamp(reader.GetString(14)),
            ParseTimestamp(reader.GetString(15)),
            reader.IsDBNull(16) ? null : ParseTimestamp(reader.GetString(16)));
    }

    private static ReferenceStyleEvidenceSpanPayload ReadEvidence(SqliteDataReader reader)
    {
        return new ReferenceStyleEvidenceSpanPayload(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetString(9),
            reader.GetDouble(10),
            reader.GetString(11));
    }

    private static ReferenceStyleFeatureVectorPayload ReadFeatureVector(string json)
    {
        return JsonSerializer.Deserialize<ReferenceStyleFeatureVectorPayload>(json, JsonOptions) ?? EmptyFeatureVector();
    }

    private static IReadOnlyList<long> ReadLongList(string json)
    {
        return JsonSerializer.Deserialize<IReadOnlyList<long>>(json, JsonOptions) ?? [];
    }

    private static IReadOnlyList<string> ReadStringList(string json)
    {
        return JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? [];
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

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }
    }

    private static void ValidateProfileId(long profileId)
    {
        if (profileId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "Reference style profile id must be positive.");
        }
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private sealed record ReferenceStyleAnchorSource(
        long AnchorId,
        long NovelId,
        string SourceFileHash,
        string LicenseStatus,
        string SourceTrust,
        string CorpusVisibility);
}
