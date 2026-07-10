using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
    private const int MaxStyleSampleCount = 50;
    private const int MaxBuildIdLength = 128;
    private const int StyleProfileBuildProgressTotal = 7;
    private const int MaxLlmAnalysisWindows = 64;
    private const int MaxLlmAnalysisWindowTextChars = 1200;
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
    private readonly IReferenceStyleLlmAnalyzer? _llmAnalyzer;
    private readonly IStyleSampleService _styleSamples;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeBuildCancellations = new(StringComparer.Ordinal);

    public SqliteReferenceStyleProfileService(
        AppInitializationOptions? options = null,
        INovelService? novels = null,
        IReferenceStyleLlmAnalyzer? llmAnalyzer = null,
        IStyleSampleService? styleSamples = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
        _llmAnalyzer = llmAnalyzer;
        _styleSamples = styleSamples ?? new FileSystemStyleSampleService(_options, _novels);
    }

    public async ValueTask<ReferenceStyleProfilePayload> BuildStyleProfileAsync(
        BuildReferenceStyleProfilePayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var buildId = NormalizeBuildId(input.BuildId);
        var databasePath = await DatabasePathAsync(cancellationToken);
        var titleForFailure = NormalizeBestEffortTitle(input.Title);
        var anchorIdsForFailure = NormalizeBestEffortAnchorIds(input.AnchorIds);
        var styleSampleIdsForFailure = NormalizeBestEffortStyleSampleIds(input.StyleSampleIds);

        long novelIdForFailure = Math.Max(0, input.NovelId);
        string title = titleForFailure;
        IReadOnlyList<long> anchorIds = anchorIdsForFailure;
        IReadOnlyList<long> styleSampleIds = styleSampleIdsForFailure;
        IReadOnlyList<string> sourceHashesForFailure = [];
        var mutexAcquired = false;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_activeBuildCancellations.TryAdd(buildId, linkedCancellation))
        {
            throw new InvalidOperationException("Reference style profile build is already running.");
        }

        var buildCancellationToken = linkedCancellation.Token;

        try
        {
            await EnsureSchemaAsync(databasePath, buildCancellationToken);
            await UpsertStyleBuildStatusAsync(
                databasePath,
                buildId,
                Math.Max(0, input.NovelId),
                profileId: null,
                titleForFailure,
                ReferenceStyleProfileBuildStatuses.Running,
                ReferenceStyleProfileBuildStages.Queued,
                progressCompleted: 0,
                StyleProfileBuildProgressTotal,
                anchorIdsForFailure,
                [],
                styleSampleIdsForFailure,
                [],
                errorCode: null,
                errorMessage: null,
                completedAt: null,
                cancelledAt: null,
                buildCancellationToken);
            await _mutex.WaitAsync(buildCancellationToken);
            mutexAcquired = true;
            await EnsureSchemaAsync(databasePath, buildCancellationToken);
            await UpdateStyleBuildProgressAsync(
                databasePath,
                buildId,
                novelIdForFailure,
                title,
                ReferenceStyleProfileBuildStages.Validating,
                progressCompleted: 1,
                anchorIds,
                sourceHashesForFailure,
                styleSampleIds,
                buildCancellationToken);
            ValidateNovelId(input.NovelId);
            novelIdForFailure = input.NovelId;
            await EnsureNovelExistsAsync(input.NovelId, buildCancellationToken);

            title = NormalizeRequiredText(input.Title, nameof(input.Title), maxLength: 200);
            var description = NormalizeOptionalText(input.Description, nameof(input.Description), maxLength: 1000);
            styleSampleIds = NormalizeStyleSampleIds(input.StyleSampleIds);
            anchorIds = NormalizeAnchorIds(input.AnchorIds, allowEmpty: styleSampleIds.Count > 0);
            if (anchorIds.Count == 0 && styleSampleIds.Count == 0)
            {
                throw new ArgumentException("At least one source anchor or style sample is required.", nameof(input.AnchorIds));
            }
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

            await EnsureSchemaAsync(databasePath, buildCancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, buildCancellationToken);
            await ThrowIfStyleBuildCancelledAsync(connection, transaction: null, buildId, buildCancellationToken);
            await UpdateStyleBuildProgressAsync(
                databasePath,
                buildId,
                input.NovelId,
                title,
                ReferenceStyleProfileBuildStages.ReadingSources,
                progressCompleted: 2,
                anchorIds,
                sourceHashesForFailure,
                styleSampleIds,
                buildCancellationToken);
            var sources = await ReadAccessibleSourcesAsync(
                connection,
                input.NovelId,
                anchorIds,
                allowedLicenseStatuses,
                allowedSourceTrustLevels,
                buildCancellationToken);
            var sampleSources = await ReadAccessibleStyleSamplesAsync(input.NovelId, styleSampleIds, buildCancellationToken);
            sourceHashesForFailure = sources
                .Select(source => source.SourceFileHash)
                .Concat(sampleSources.Select(source => source.SourceHash))
                .ToArray();
            await ThrowIfStyleBuildCancelledAsync(connection, transaction: null, buildId, buildCancellationToken);
            await UpdateStyleBuildProgressAsync(
                databasePath,
                buildId,
                input.NovelId,
                title,
                ReferenceStyleProfileBuildStages.ReadingMaterials,
                progressCompleted: 3,
                anchorIds,
                sourceHashesForFailure,
                styleSampleIds,
                buildCancellationToken);
            var materials = await ReadActiveMaterialsAsync(connection, anchorIds, buildCancellationToken);
            var sampleMaterials = BuildStyleSampleMaterials(sampleSources);
            var allMaterials = materials.Concat(sampleMaterials).ToArray();
            if (allMaterials.Length == 0)
            {
                throw new ArgumentException("Style profile requires at least one active reference material or style sample.", nameof(input.AnchorIds));
            }

            var sourceHashes = sourceHashesForFailure;
            await ThrowIfStyleBuildCancelledAsync(connection, transaction: null, buildId, buildCancellationToken);
            await UpdateStyleBuildProgressAsync(
                databasePath,
                buildId,
                input.NovelId,
                title,
                ReferenceStyleProfileBuildStages.PersistingProfile,
                progressCompleted: 4,
                anchorIds,
                sourceHashes,
                styleSampleIds,
                buildCancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(buildCancellationToken);
            try
            {
                var profileId = await InsertProfileShellAsync(
                    connection,
                    transaction,
                    input.NovelId,
                    title,
                    description,
                    anchorIds,
                    sourceHashes,
                    styleSampleIds,
                    allowedLicenseStatuses,
                    allowedSourceTrustLevels,
                    now,
                    buildCancellationToken);
                await UpdateStyleBuildProfileIdAsync(
                    connection,
                    transaction,
                    buildId,
                    profileId,
                    now,
                    buildCancellationToken);

                await InsertProfileSourcesAsync(
                    connection,
                    transaction,
                    profileId,
                    sources,
                    materials,
                    buildCancellationToken);
                await InsertSampleProfileSourcesAsync(
                    connection,
                    transaction,
                    profileId,
                    sampleSources,
                    sampleMaterials,
                    buildCancellationToken);

                await ThrowIfStyleBuildCancelledAsync(connection, transaction, buildId, buildCancellationToken);
                await UpdateStyleBuildProgressAsync(
                    connection,
                    transaction,
                    buildId,
                    input.NovelId,
                    profileId,
                    title,
                    ReferenceStyleProfileBuildStatuses.Running,
                    ReferenceStyleProfileBuildStages.DeterministicBaseline,
                    progressCompleted: 5,
                    StyleProfileBuildProgressTotal,
                    anchorIds,
                    sourceHashes,
                    styleSampleIds,
                    [],
                    errorCode: null,
                    errorMessage: null,
                    completedAt: null,
                    cancelledAt: null,
                    now,
                    buildCancellationToken);
                var baseline = ReferenceStyleDeterministicBaselineExtractor.Build(profileId, allMaterials);
                var anchorEvidence = baseline.EvidenceSpans
                    .Where(evidence => !string.Equals(evidence.SourceType, ReferenceStyleProfileSourceTypes.StyleSample, StringComparison.Ordinal))
                    .ToArray();
                var sampleEvidence = baseline.EvidenceSpans
                    .Where(evidence => string.Equals(evidence.SourceType, ReferenceStyleProfileSourceTypes.StyleSample, StringComparison.Ordinal))
                    .ToArray();
                await InsertEvidenceAsync(
                    connection,
                    transaction,
                    anchorEvidence,
                    now,
                    buildCancellationToken);
                await InsertSampleEvidenceAsync(
                    connection,
                    transaction,
                    sampleEvidence,
                    now,
                    buildCancellationToken);
                await InsertMaterialStyleTagsAsync(
                    connection,
                    transaction,
                    anchorEvidence,
                    ReferenceStyleAnalyzerVersions.DeterministicV1,
                    now,
                    buildCancellationToken);
                await InsertAnalysisRunAsync(
                    connection,
                    transaction,
                    profileId,
                    ReferenceStyleAnalyzerVersions.DeterministicV1,
                    ReferenceStyleAnalyzerSources.DeterministicBaseline,
                    anchorIds,
                    sourceHashes,
                    "completed",
                    baseline.Diagnostics,
                    now,
                    buildCancellationToken);

                var analyzerVersion = ReferenceStyleAnalyzerVersions.DeterministicV1;
                var analyzerSource = ReferenceStyleAnalyzerSources.DeterministicBaseline;
                var features = MergeStyleSampleStatsFeatures(
                    baseline.Features,
                    sampleSources,
                    sampleEvidence,
                    replaceExisting: materials.Count == 0);
                var evidenceSpans = baseline.EvidenceSpans;
                if (_llmAnalyzer is not null)
                {
                    await ThrowIfStyleBuildCancelledAsync(connection, transaction, buildId, buildCancellationToken);
                    await UpdateStyleBuildProgressAsync(
                        connection,
                        transaction,
                        buildId,
                        input.NovelId,
                        profileId,
                        title,
                        ReferenceStyleProfileBuildStatuses.Running,
                        ReferenceStyleProfileBuildStages.LlmAnalysis,
                        progressCompleted: 6,
                        StyleProfileBuildProgressTotal,
                        anchorIds,
                        sourceHashes,
                        styleSampleIds,
                        [],
                        errorCode: null,
                        errorMessage: null,
                        completedAt: null,
                        cancelledAt: null,
                        now,
                        buildCancellationToken);
                    var windows = BuildLlmAnalysisWindows(allMaterials);
                    var validation = await RunLlmAnalysisAsync(profileId, windows, buildCancellationToken);
                    if (validation is not null)
                    {
                        await InsertAnalysisRunAsync(
                            connection,
                            transaction,
                            profileId,
                            ReferenceStyleAnalyzerVersions.LlmAssistedV1,
                            ReferenceStyleAnalyzerSources.LlmAssisted,
                            anchorIds,
                            sourceHashes,
                            validation.Status,
                            BuildLlmDiagnostics(validation),
                            now,
                            buildCancellationToken);

                        if (validation.EvidenceSpans.Count > 0)
                        {
                            await InsertEvidenceAsync(
                                connection,
                                transaction,
                                validation.EvidenceSpans,
                                now,
                                buildCancellationToken);
                            await InsertMaterialStyleTagsAsync(
                                connection,
                                transaction,
                                validation.EvidenceSpans,
                                ReferenceStyleAnalyzerVersions.LlmAssistedV1,
                                now,
                                buildCancellationToken);
                            analyzerVersion = ReferenceStyleAnalyzerVersions.LlmAssistedV1;
                            analyzerSource = ReferenceStyleAnalyzerSources.LlmAssisted;
                            features = MergeLlmCategoricalFeatures(baseline.Features, validation.EvidenceSpans);
                            evidenceSpans = baseline.EvidenceSpans.Concat(validation.EvidenceSpans).ToArray();
                        }
                    }
                }

                await UpdateProfileFeaturesAsync(
                    connection,
                    transaction,
                    profileId,
                    features,
                    baseline.AggregateConfidence,
                    analyzerVersion,
                    analyzerSource,
                    now,
                    buildCancellationToken);
                await UpdateStyleBuildProgressAsync(
                    connection,
                    transaction,
                    buildId,
                    input.NovelId,
                    profileId,
                    title,
                    ReferenceStyleProfileBuildStatuses.Completed,
                    ReferenceStyleProfileBuildStages.Completed,
                    StyleProfileBuildProgressTotal,
                    StyleProfileBuildProgressTotal,
                    anchorIds,
                    sourceHashes,
                    styleSampleIds,
                    [],
                    errorCode: null,
                    errorMessage: null,
                    completedAt: now,
                    cancelledAt: null,
                    now,
                    buildCancellationToken);

                await transaction.CommitAsync(buildCancellationToken);

                return new ReferenceStyleProfilePayload(
                    profileId,
                    input.NovelId,
                    title,
                    description,
                    ReferenceStyleProfileStatuses.Active,
                    analyzerVersion,
                    ReferenceStyleFeatureSchemaVersions.V1,
                    analyzerSource,
                    anchorIds,
                    sourceHashes,
                    allowedLicenseStatuses,
                    allowedSourceTrustLevels,
                    baseline.AggregateConfidence,
                    features,
                    evidenceSpans,
                    now,
                    now,
                    ArchivedAt: null,
                    SourceStyleSampleIds: styleSampleIds);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            await MarkStyleBuildCancelledAsync(databasePath, buildId, novelIdForFailure, title, anchorIds, sourceHashesForFailure, styleSampleIds, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await MarkStyleBuildFailedAsync(databasePath, buildId, novelIdForFailure, title, anchorIds, sourceHashesForFailure, styleSampleIds, exception, CancellationToken.None);
            throw;
        }
        finally
        {
            if (mutexAcquired)
            {
                _mutex.Release();
            }

            _activeBuildCancellations.TryRemove(buildId, out _);
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
                       style_sample_ids_json, aggregate_confidence, created_at, updated_at, archived_at
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

    public async ValueTask<ReferenceStyleProfileBuildStatusPayload?> GetStyleProfileBuildStatusAsync(
        GetReferenceStyleProfileBuildStatusPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        var buildId = NormalizeBuildId(input.BuildId);
        var databasePath = await DatabasePathAsync(cancellationToken);
        await EnsureSchemaAsync(databasePath, cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        return await ReadStyleBuildStatusAsync(connection, input.NovelId, buildId, cancellationToken);
    }

    public async ValueTask<ReferenceStyleProfileBuildStatusPayload> CancelStyleProfileBuildAsync(
        CancelReferenceStyleProfileBuildPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        var buildId = NormalizeBuildId(input.BuildId);
        var databasePath = await DatabasePathAsync(cancellationToken);
        await EnsureSchemaAsync(databasePath, cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var existing = await ReadStyleBuildStatusAsync(connection, input.NovelId, buildId, cancellationToken)
            ?? throw new ArgumentException($"Reference style profile build '{buildId}' is not accessible.", nameof(input.BuildId));
        if (string.Equals(existing.Status, ReferenceStyleProfileBuildStatuses.Completed, StringComparison.Ordinal) ||
            string.Equals(existing.Status, ReferenceStyleProfileBuildStatuses.Failed, StringComparison.Ordinal) ||
            string.Equals(existing.Status, ReferenceStyleProfileBuildStatuses.Cancelled, StringComparison.Ordinal))
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        if (_activeBuildCancellations.TryGetValue(buildId, out var activeCancellation))
        {
            activeCancellation.Cancel();
            return existing with
            {
                Status = ReferenceStyleProfileBuildStatuses.Cancelled,
                Stage = ReferenceStyleProfileBuildStages.Cancelled,
                Diagnostics = ["cancel_requested"],
                UpdatedAt = now,
                CancelledAt = now
            };
        }

        await UpsertStyleBuildStatusAsync(
            connection,
            transaction: null,
            existing.BuildId,
            existing.NovelId,
            existing.ProfileId,
            existing.Title,
            ReferenceStyleProfileBuildStatuses.Cancelled,
            ReferenceStyleProfileBuildStages.Cancelled,
            existing.ProgressCompleted,
            existing.ProgressTotal,
            existing.AnchorIds,
            existing.SourceHashes,
            existing.StyleSampleIds ?? [],
            ["cancel_requested"],
            errorCode: null,
            errorMessage: null,
            createdAt: existing.CreatedAt,
            updatedAt: now,
            completedAt: existing.CompletedAt,
            cancelledAt: now,
            cancellationToken);

        return await ReadStyleBuildStatusAsync(connection, input.NovelId, buildId, cancellationToken)
            ?? throw new InvalidOperationException("Reference style profile build disappeared after cancellation.");
    }

    public async ValueTask<ReferenceStyleProfilePayload> ArchiveStyleProfileAsync(
        ArchiveReferenceStyleProfilePayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        ValidateProfileId(input.ProfileId);
        return await UpdateStyleProfileArchiveStateAsync(input.NovelId, input.ProfileId, archive: true, cancellationToken);
    }

    public async ValueTask<ReferenceStyleProfilePayload> RestoreStyleProfileAsync(
        RestoreReferenceStyleProfilePayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        ValidateProfileId(input.ProfileId);
        return await UpdateStyleProfileArchiveStateAsync(input.NovelId, input.ProfileId, archive: false, cancellationToken);
    }

    public async ValueTask<ReferenceStyleProfileComparisonPayload> CompareStyleProfilesAsync(
        CompareReferenceStyleProfilesPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        ValidateProfileId(input.LeftProfileId);
        ValidateProfileId(input.RightProfileId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var left = await ReadStyleProfileAsync(connection, input.NovelId, input.LeftProfileId, cancellationToken);
            var right = await ReadStyleProfileAsync(connection, input.NovelId, input.RightProfileId, cancellationToken);
            if (left is null || right is null)
            {
                throw new ArgumentException("Reference style profiles must belong to the requested novel.", nameof(input));
            }

            return BuildStyleProfileComparison(input.NovelId, left, right, DateTimeOffset.UtcNow);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<ReferenceStyleProfilePayload> UpdateStyleProfileArchiveStateAsync(
        long novelId,
        long profileId,
        bool archive,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = archive
                ? """
                    UPDATE reference_style_profiles
                    SET status = $status,
                        archived_at = COALESCE(archived_at, $archived_at),
                        updated_at = $updated_at
                    WHERE novel_id = $novel_id
                      AND profile_id = $profile_id;
                    """
                : """
                    UPDATE reference_style_profiles
                    SET status = $status,
                        archived_at = NULL,
                        updated_at = $updated_at
                    WHERE novel_id = $novel_id
                      AND profile_id = $profile_id;
                    """;
            command.Parameters.AddWithValue("$status", archive ? ReferenceStyleProfileStatuses.Archived : ReferenceStyleProfileStatuses.Active);
            command.Parameters.AddWithValue("$archived_at", FormatTimestamp(now));
            command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
            command.Parameters.AddWithValue("$novel_id", novelId);
            command.Parameters.AddWithValue("$profile_id", profileId);
            var updated = await command.ExecuteNonQueryAsync(cancellationToken);
            if (updated != 1)
            {
                throw new ArgumentException($"Reference style profile '{profileId}' is not accessible.", nameof(profileId));
            }

            return await ReadStyleProfileAsync(connection, novelId, profileId, cancellationToken)
                ?? throw new InvalidOperationException("Reference style profile disappeared after archive state update.");
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
              style_sample_ids_json TEXT NOT NULL DEFAULT '[]',
              allowed_license_statuses_json TEXT NOT NULL,
              allowed_source_trust_levels_json TEXT NOT NULL,
              feature_vector_json TEXT NOT NULL,
              aggregate_confidence REAL NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              archived_at TEXT
            );

            CREATE TABLE IF NOT EXISTS reference_style_profile_builds (
              build_id TEXT PRIMARY KEY,
              novel_id INTEGER NOT NULL,
              profile_id INTEGER,
              title TEXT NOT NULL,
              status TEXT NOT NULL,
              stage TEXT NOT NULL,
              progress_completed INTEGER NOT NULL,
              progress_total INTEGER NOT NULL,
              anchor_ids_json TEXT NOT NULL,
              source_hashes_json TEXT NOT NULL,
              style_sample_ids_json TEXT NOT NULL DEFAULT '[]',
              diagnostics_json TEXT NOT NULL,
              error_code TEXT,
              error_message TEXT,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              completed_at TEXT,
              cancelled_at TEXT,
              FOREIGN KEY(profile_id) REFERENCES reference_style_profiles(profile_id) ON DELETE SET NULL
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

            CREATE TABLE IF NOT EXISTS reference_style_profile_sample_sources (
              profile_id INTEGER NOT NULL,
              sample_id INTEGER NOT NULL,
              novel_id INTEGER,
              is_global INTEGER NOT NULL,
              source_hash TEXT NOT NULL,
              stats_schema_version TEXT NOT NULL,
              material_count INTEGER NOT NULL,
              segment_count INTEGER NOT NULL,
              PRIMARY KEY(profile_id, sample_id),
              FOREIGN KEY(profile_id) REFERENCES reference_style_profiles(profile_id) ON DELETE CASCADE
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

            CREATE TABLE IF NOT EXISTS reference_style_profile_sample_evidence (
              evidence_id TEXT PRIMARY KEY,
              profile_id INTEGER NOT NULL,
              sample_id INTEGER NOT NULL,
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
              FOREIGN KEY(profile_id) REFERENCES reference_style_profiles(profile_id) ON DELETE CASCADE
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

            CREATE INDEX IF NOT EXISTS idx_reference_style_profile_builds_novel
              ON reference_style_profile_builds(novel_id, updated_at);

            CREATE INDEX IF NOT EXISTS idx_reference_style_profile_sources_anchor
              ON reference_style_profile_sources(anchor_id);

            CREATE INDEX IF NOT EXISTS idx_reference_style_profile_sample_sources_sample
              ON reference_style_profile_sample_sources(sample_id);

            CREATE INDEX IF NOT EXISTS idx_reference_style_evidence_profile_feature
              ON reference_style_profile_evidence(profile_id, feature_key);

            CREATE INDEX IF NOT EXISTS idx_reference_style_sample_evidence_profile_feature
              ON reference_style_profile_sample_evidence(profile_id, feature_key);

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
            "reference_style_profiles",
            "style_sample_ids_json",
            "ALTER TABLE reference_style_profiles ADD COLUMN style_sample_ids_json TEXT NOT NULL DEFAULT '[]';",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_style_profile_builds",
            "style_sample_ids_json",
            "ALTER TABLE reference_style_profile_builds ADD COLUMN style_sample_ids_json TEXT NOT NULL DEFAULT '[]';",
            cancellationToken);
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

    private static async ValueTask UpdateStyleBuildProfileIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string buildId,
        long profileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_style_profile_builds
            SET profile_id = $profile_id,
                updated_at = $updated_at
            WHERE build_id = $build_id;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$build_id", buildId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask UpdateStyleBuildProgressAsync(
        string databasePath,
        string buildId,
        long novelId,
        string title,
        string stage,
        int progressCompleted,
        IReadOnlyList<long> anchorIds,
        IReadOnlyList<string> sourceHashes,
        IReadOnlyList<long> styleSampleIds,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await UpsertStyleBuildStatusAsync(
            connection,
            transaction: null,
            buildId,
            novelId,
            profileId: null,
            title,
            ReferenceStyleProfileBuildStatuses.Running,
            stage,
            progressCompleted,
            StyleProfileBuildProgressTotal,
            anchorIds,
            sourceHashes,
            styleSampleIds,
            [],
            errorCode: null,
            errorMessage: null,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            completedAt: null,
            cancelledAt: null,
            cancellationToken);
    }

    private static async ValueTask UpdateStyleBuildProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string buildId,
        long novelId,
        long? profileId,
        string title,
        string status,
        string stage,
        int progressCompleted,
        int progressTotal,
        IReadOnlyList<long> anchorIds,
        IReadOnlyList<string> sourceHashes,
        IReadOnlyList<long> styleSampleIds,
        IReadOnlyList<string> diagnostics,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset? completedAt,
        DateTimeOffset? cancelledAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await UpsertStyleBuildStatusAsync(
            connection,
            transaction,
            buildId,
            novelId,
            profileId,
            title,
            status,
            stage,
            progressCompleted,
            progressTotal,
            anchorIds,
            sourceHashes,
            styleSampleIds,
            diagnostics,
            errorCode,
            errorMessage,
            createdAt: now,
            updatedAt: now,
            completedAt,
            cancelledAt,
            cancellationToken);
    }

    private static async ValueTask UpsertStyleBuildStatusAsync(
        string databasePath,
        string buildId,
        long novelId,
        long? profileId,
        string title,
        string status,
        string stage,
        int progressCompleted,
        int progressTotal,
        IReadOnlyList<long> anchorIds,
        IReadOnlyList<string> sourceHashes,
        IReadOnlyList<long> styleSampleIds,
        IReadOnlyList<string> diagnostics,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset? completedAt,
        DateTimeOffset? cancelledAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await UpsertStyleBuildStatusAsync(
            connection,
            transaction: null,
            buildId,
            novelId,
            profileId,
            title,
            status,
            stage,
            progressCompleted,
            progressTotal,
            anchorIds,
            sourceHashes,
            styleSampleIds,
            diagnostics,
            errorCode,
            errorMessage,
            createdAt: now,
            updatedAt: now,
            completedAt,
            cancelledAt,
            cancellationToken);
    }

    private static async ValueTask UpsertStyleBuildStatusAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string buildId,
        long novelId,
        long? profileId,
        string title,
        string status,
        string stage,
        int progressCompleted,
        int progressTotal,
        IReadOnlyList<long> anchorIds,
        IReadOnlyList<string> sourceHashes,
        IReadOnlyList<long> styleSampleIds,
        IReadOnlyList<string> diagnostics,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? completedAt,
        DateTimeOffset? cancelledAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_style_profile_builds
              (build_id, novel_id, profile_id, title, status, stage,
               progress_completed, progress_total, anchor_ids_json, source_hashes_json,
               style_sample_ids_json, diagnostics_json, error_code, error_message, created_at, updated_at,
               completed_at, cancelled_at)
            VALUES
              ($build_id, $novel_id, $profile_id, $title, $status, $stage,
               $progress_completed, $progress_total, $anchor_ids_json, $source_hashes_json,
               $style_sample_ids_json, $diagnostics_json, $error_code, $error_message, $created_at, $updated_at,
               $completed_at, $cancelled_at)
            ON CONFLICT(build_id) DO UPDATE SET
              novel_id = excluded.novel_id,
              profile_id = excluded.profile_id,
              title = excluded.title,
              status = excluded.status,
              stage = excluded.stage,
              progress_completed = excluded.progress_completed,
              progress_total = excluded.progress_total,
              anchor_ids_json = excluded.anchor_ids_json,
              source_hashes_json = excluded.source_hashes_json,
              style_sample_ids_json = excluded.style_sample_ids_json,
              diagnostics_json = excluded.diagnostics_json,
              error_code = excluded.error_code,
              error_message = excluded.error_message,
              updated_at = excluded.updated_at,
              completed_at = COALESCE(excluded.completed_at, reference_style_profile_builds.completed_at),
              cancelled_at = COALESCE(excluded.cancelled_at, reference_style_profile_builds.cancelled_at);
            """;
        command.Parameters.AddWithValue("$build_id", buildId);
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$profile_id", (object?)profileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$stage", stage);
        command.Parameters.AddWithValue("$progress_completed", Math.Max(0, progressCompleted));
        command.Parameters.AddWithValue("$progress_total", Math.Max(0, progressTotal));
        command.Parameters.AddWithValue("$anchor_ids_json", JsonSerializer.Serialize(anchorIds, JsonOptions));
        command.Parameters.AddWithValue("$source_hashes_json", JsonSerializer.Serialize(sourceHashes, JsonOptions));
        command.Parameters.AddWithValue("$style_sample_ids_json", JsonSerializer.Serialize(styleSampleIds, JsonOptions));
        command.Parameters.AddWithValue("$diagnostics_json", JsonSerializer.Serialize(SanitizeDiagnostics(diagnostics), JsonOptions));
        command.Parameters.AddWithValue("$error_code", (object?)NormalizeOptionalErrorField(errorCode, maxLength: 128) ?? DBNull.Value);
        command.Parameters.AddWithValue("$error_message", (object?)NormalizeOptionalErrorField(errorMessage, maxLength: 512) ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_at", FormatTimestamp(createdAt));
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(updatedAt));
        command.Parameters.AddWithValue("$completed_at", completedAt is null ? DBNull.Value : FormatTimestamp(completedAt.Value));
        command.Parameters.AddWithValue("$cancelled_at", cancelledAt is null ? DBNull.Value : FormatTimestamp(cancelledAt.Value));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<ReferenceStyleProfileBuildStatusPayload?> ReadStyleBuildStatusAsync(
        SqliteConnection connection,
        long novelId,
        string buildId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT build_id, novel_id, profile_id, title, status, stage,
                   progress_completed, progress_total, anchor_ids_json, source_hashes_json,
                   style_sample_ids_json, diagnostics_json, error_code, error_message, created_at, updated_at,
                   completed_at, cancelled_at
            FROM reference_style_profile_builds
            WHERE novel_id = $novel_id
              AND build_id = $build_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$build_id", buildId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadStyleBuildStatus(reader) : null;
    }

    private static async ValueTask ThrowIfStyleBuildCancelledAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string buildId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT status
            FROM reference_style_profile_builds
            WHERE build_id = $build_id;
            """;
        command.Parameters.AddWithValue("$build_id", buildId);
        var status = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.Equals(status, ReferenceStyleProfileBuildStatuses.Cancelled, StringComparison.Ordinal))
        {
            throw new OperationCanceledException("Reference style profile build was cancelled.");
        }
    }

    private static async ValueTask MarkStyleBuildFailedAsync(
        string databasePath,
        string buildId,
        long novelId,
        string title,
        IReadOnlyList<long> anchorIds,
        IReadOnlyList<string> sourceHashes,
        IReadOnlyList<long> styleSampleIds,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var existing = await ReadStyleBuildStatusByIdAsync(connection, buildId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await UpsertStyleBuildStatusAsync(
            connection,
            transaction: null,
            buildId,
            existing?.NovelId ?? novelId,
            profileId: null,
            existing?.Title ?? title,
            ReferenceStyleProfileBuildStatuses.Failed,
            ReferenceStyleProfileBuildStages.Failed,
            existing?.ProgressCompleted ?? 0,
            existing?.ProgressTotal ?? StyleProfileBuildProgressTotal,
            existing?.AnchorIds.Count > 0 ? existing.AnchorIds : anchorIds,
            existing?.SourceHashes.Count > 0 ? existing.SourceHashes : sourceHashes,
            existing?.StyleSampleIds?.Count > 0 ? existing.StyleSampleIds : styleSampleIds,
            BuildFailureDiagnostics(exception),
            exception.GetType().Name,
            "Style profile build failed before completion.",
            createdAt: existing?.CreatedAt ?? now,
            updatedAt: now,
            completedAt: null,
            cancelledAt: null,
            cancellationToken);
    }

    private static async ValueTask MarkStyleBuildCancelledAsync(
        string databasePath,
        string buildId,
        long novelId,
        string title,
        IReadOnlyList<long> anchorIds,
        IReadOnlyList<string> sourceHashes,
        IReadOnlyList<long> styleSampleIds,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var existing = await ReadStyleBuildStatusByIdAsync(connection, buildId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await UpsertStyleBuildStatusAsync(
            connection,
            transaction: null,
            buildId,
            existing?.NovelId ?? novelId,
            profileId: null,
            existing?.Title ?? title,
            ReferenceStyleProfileBuildStatuses.Cancelled,
            ReferenceStyleProfileBuildStages.Cancelled,
            existing?.ProgressCompleted ?? 0,
            existing?.ProgressTotal ?? StyleProfileBuildProgressTotal,
            existing?.AnchorIds.Count > 0 ? existing.AnchorIds : anchorIds,
            existing?.SourceHashes.Count > 0 ? existing.SourceHashes : sourceHashes,
            existing?.StyleSampleIds?.Count > 0 ? existing.StyleSampleIds : styleSampleIds,
            ["cancelled"],
            errorCode: null,
            errorMessage: null,
            createdAt: existing?.CreatedAt ?? now,
            updatedAt: now,
            completedAt: null,
            cancelledAt: now,
            cancellationToken);
    }

    private static async ValueTask<ReferenceStyleProfileBuildStatusPayload?> ReadStyleBuildStatusByIdAsync(
        SqliteConnection connection,
        string buildId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT build_id, novel_id, profile_id, title, status, stage,
                   progress_completed, progress_total, anchor_ids_json, source_hashes_json,
                   style_sample_ids_json, diagnostics_json, error_code, error_message, created_at, updated_at,
                   completed_at, cancelled_at
            FROM reference_style_profile_builds
            WHERE build_id = $build_id;
            """;
        command.Parameters.AddWithValue("$build_id", buildId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadStyleBuildStatus(reader) : null;
    }

    private async ValueTask<IReadOnlyList<ReferenceStyleAnchorSource>> ReadAccessibleSourcesAsync(
        SqliteConnection connection,
        long novelId,
        IReadOnlyList<long> anchorIds,
        IReadOnlyList<string> allowedLicenseStatuses,
        IReadOnlyList<string> allowedSourceTrustLevels,
        CancellationToken cancellationToken)
    {
        if (anchorIds.Count == 0)
        {
            return [];
        }

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
        if (anchorIds.Count == 0)
        {
            return [];
        }

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

    private async ValueTask<IReadOnlyList<ReferenceStyleSampleSource>> ReadAccessibleStyleSamplesAsync(
        long novelId,
        IReadOnlyList<long> sampleIds,
        CancellationToken cancellationToken)
    {
        if (sampleIds.Count == 0)
        {
            return [];
        }

        var sources = new List<ReferenceStyleSampleSource>(sampleIds.Count);
        foreach (var sampleId in sampleIds)
        {
            var sample = await _styleSamples.GetSampleAsync(new GetStyleSamplePayload(sampleId), cancellationToken)
                ?? throw new ArgumentException($"Reference style profile cannot access style samples: {sampleId}.", nameof(sampleIds));
            if (!sample.IsGlobal && sample.NovelId != novelId)
            {
                throw new ArgumentException($"Reference style profile cannot access style samples: {sampleId}.", nameof(sampleIds));
            }

            var sourceHash = NormalizeSampleSourceHash(sample);
            sources.Add(new ReferenceStyleSampleSource(
                sample.SampleId,
                sample.NovelId,
                sample.IsGlobal,
                sample.Name,
                sample.Content,
                sample.Stats,
                sample.StatsSchemaVersion,
                sourceHash));
        }

        return sampleIds
            .Select(sampleId => sources.Single(source => source.SampleId == sampleId))
            .ToArray();
    }

    private static IReadOnlyList<ReferenceStyleMaterialSample> BuildStyleSampleMaterials(
        IReadOnlyList<ReferenceStyleSampleSource> sources)
    {
        if (sources.Count == 0)
        {
            return [];
        }

        var materials = new List<ReferenceStyleMaterialSample>();
        foreach (var source in sources)
        {
            AddStyleSampleMaterials(materials, source, ReferenceMaterialTypes.Passage, SplitSampleParagraphs(source.Content));
            AddStyleSampleMaterials(materials, source, ReferenceMaterialTypes.Sentence, SplitSampleSentences(source.Content));
        }

        return materials;
    }

    private static void AddStyleSampleMaterials(
        List<ReferenceStyleMaterialSample> materials,
        ReferenceStyleSampleSource source,
        string materialType,
        IReadOnlyList<SampleTextSpan> spans)
    {
        for (var index = 0; index < spans.Count; index++)
        {
            var span = spans[index];
            if (string.IsNullOrWhiteSpace(span.Text))
            {
                continue;
            }

            var sourceSegmentId = $"style-sample:{source.SampleId}:{materialType}:{index + 1}";
            var materialId = $"style-sample-material:{source.SampleId}:{materialType}:{index + 1}";
            materials.Add(new ReferenceStyleMaterialSample(
                materialId,
                AnchorId: 0,
                sourceSegmentId,
                materialType,
                InferStyleSampleFunctionTag(span.Text),
                InferStyleSampleEmotionTag(span.Text),
                "sample",
                "unknown",
                InferStyleSampleTechniqueTag(span.Text),
                0.7,
                0.6,
                0.6,
                span.Text,
                source.SourceHash,
                span.StartOffset,
                span.EndOffset,
                HashText(span.Text),
                ReferenceStyleProfileSourceTypes.StyleSample,
                source.SampleId));
        }
    }

    private static IReadOnlyList<SampleTextSpan> SplitSampleParagraphs(string content)
    {
        var spans = new List<SampleTextSpan>();
        var start = 0;
        foreach (var part in content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var index = content.IndexOf(part, start, StringComparison.Ordinal);
            if (index < 0)
            {
                index = start;
            }

            var text = part.Trim();
            if (text.Length > 0)
            {
                spans.Add(new SampleTextSpan(text, index, index + part.Length));
            }

            start = Math.Min(content.Length, index + part.Length + 1);
        }

        return spans;
    }

    private static IReadOnlyList<SampleTextSpan> SplitSampleSentences(string content)
    {
        var spans = new List<SampleTextSpan>();
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (!IsSampleSentenceTerminator(content[index]))
            {
                continue;
            }

            var end = index + 1;
            while (end < content.Length && content[end] is '”' or '」' or '』' or '"' or '\'' or ')' or '）')
            {
                end++;
            }

            AddSpan(start, end);
            start = end;
            index = end - 1;
        }

        if (start < content.Length)
        {
            AddSpan(start, content.Length);
        }

        return spans;

        void AddSpan(int rawStart, int rawEnd)
        {
            var raw = content[rawStart..rawEnd];
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                return;
            }

            var leading = raw.Length - raw.TrimStart().Length;
            spans.Add(new SampleTextSpan(trimmed, rawStart + leading, rawStart + leading + trimmed.Length));
        }
    }

    private static bool IsSampleSentenceTerminator(char ch)
    {
        return ch is '。' or '！' or '？' or '!' or '?' or '；' or ';' or '.';
    }

    private static string InferStyleSampleFunctionTag(string text)
    {
        if (ContainsAny(text, ["“", "”", "「", "」", "『", "』", "\"", "说", "问", "答"]))
        {
            return "dialogue";
        }

        if (ContainsAny(text, ["想", "心", "意识到", "明白", "知道", "觉得"]))
        {
            return "interiority";
        }

        if (ContainsAny(text, ["雨", "风", "雪", "光", "声", "冷", "热", "疼", "气味"]))
        {
            return "environment";
        }

        return "sample";
    }

    private static string InferStyleSampleEmotionTag(string text)
    {
        if (ContainsAny(text, ["紧", "怕", "疼", "沉默", "压低", "暗"]))
        {
            return "pressure";
        }

        return "neutral";
    }

    private static string InferStyleSampleTechniqueTag(string text)
    {
        if (ContainsAny(text, ["“", "”", "「", "」", "『", "』", "\""]))
        {
            return "dialogue_exchange";
        }

        if (ContainsAny(text, ["雨", "风", "声", "光", "冷", "气味"]))
        {
            return "sensory_detail";
        }

        if (ContainsAny(text, ["停", "看", "拿", "推", "转", "站", "坐", "伸"]))
        {
            return "afterbeat";
        }

        return "style_sample";
    }

    private static string NormalizeSampleSourceHash(StyleSampleDetailPayload sample)
    {
        if (!string.IsNullOrWhiteSpace(sample.SourceMetadata?.SourceHash))
        {
            return sample.SourceMetadata.SourceHash;
        }

        return HashText(string.Join(
            "|",
            StyleTextStatistics.SchemaVersion,
            sample.SampleId.ToString(CultureInfo.InvariantCulture),
            sample.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            sample.Content));
    }

    private async ValueTask<ReferenceStyleLlmAnalysisValidationResultPayload?> RunLlmAnalysisAsync(
        long profileId,
        IReadOnlyList<ReferenceStyleAnalysisWindowPayload> windows,
        CancellationToken cancellationToken)
    {
        if (_llmAnalyzer is null)
        {
            throw new InvalidOperationException("Reference style LLM analyzer is not configured.");
        }

        try
        {
            var request = new ReferenceStyleLlmAnalysisRequestPayload(
                profileId,
                ReferenceStyleLlmAnalysisSchemaVersions.V1,
                ReferenceStyleLlmAnalysisValidator.SupportedFeatureKeys,
                windows);
            var outputJson = await _llmAnalyzer.AnalyzeAsync(request, cancellationToken);
            return outputJson is null
                ? null
                : ReferenceStyleLlmAnalysisValidator.Validate(profileId, outputJson, windows);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ReferenceStyleLlmAnalysisValidationResultPayload(
                ReferenceStyleLlmAnalysisValidationStatuses.Rejected,
                [],
                [],
                [$"LLM style analyzer failed before validation ({exception.GetType().Name})."]);
        }
    }

    private static IReadOnlyList<ReferenceStyleAnalysisWindowPayload> BuildLlmAnalysisWindows(
        IReadOnlyList<ReferenceStyleMaterialSample> materials)
    {
        return materials
            .Where(material => !string.IsNullOrWhiteSpace(material.MaterialId) &&
                !string.IsNullOrWhiteSpace(material.SourceSegmentId) &&
                !string.IsNullOrWhiteSpace(material.TextHash) &&
                !string.IsNullOrWhiteSpace(material.Text) &&
                material.AnchorId > 0 &&
                material.EndOffset > material.StartOffset)
            .GroupBy(material => material.MaterialId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(material => LlmMaterialTypePriority(material.MaterialType))
            .ThenBy(material => material.AnchorId)
            .ThenBy(material => material.StartOffset)
            .ThenBy(material => material.MaterialId, StringComparer.Ordinal)
            .Take(MaxLlmAnalysisWindows)
            .Select(BuildLlmAnalysisWindow)
            .Where(window => window.EndOffset > window.StartOffset)
            .ToArray();
    }

    private static ReferenceStyleAnalysisWindowPayload BuildLlmAnalysisWindow(
        ReferenceStyleMaterialSample material,
        int index)
    {
        var text = TruncateLlmWindowText(material.Text);
        var endOffset = Math.Min(material.EndOffset, material.StartOffset + text.Length);
        return new ReferenceStyleAnalysisWindowPayload(
            "style-window-" + (index + 1).ToString(CultureInfo.InvariantCulture),
            material.AnchorId,
            material.SourceSegmentId,
            material.MaterialId,
            material.StartOffset,
            endOffset,
            material.TextHash,
            text);
    }

    private static int LlmMaterialTypePriority(string materialType)
    {
        return materialType switch
        {
            ReferenceMaterialTypes.Sentence => 0,
            ReferenceMaterialTypes.Passage => 1,
            ReferenceMaterialTypes.DialogueExchange => 2,
            ReferenceMaterialTypes.Hook => 3,
            ReferenceMaterialTypes.ActionAfterbeat => 4,
            ReferenceMaterialTypes.ImageMotif => 5,
            ReferenceMaterialTypes.Transition => 6,
            ReferenceMaterialTypes.Payoff => 7,
            ReferenceMaterialTypes.Beat => 8,
            ReferenceMaterialTypes.Scene => 9,
            _ => 10
        };
    }

    private static string TruncateLlmWindowText(string text)
    {
        var normalized = text.Trim();
        return normalized.Length <= MaxLlmAnalysisWindowTextChars
            ? normalized
            : normalized[..MaxLlmAnalysisWindowTextChars];
    }

    private static ReferenceStyleFeatureVectorPayload MergeLlmCategoricalFeatures(
        ReferenceStyleFeatureVectorPayload baseline,
        IReadOnlyList<ReferenceStyleEvidenceSpanPayload> evidenceSpans)
    {
        var categories = new List<ReferenceStyleCategoricalFeaturePayload>(baseline.CategoricalFeatures);
        foreach (var featureGroup in evidenceSpans
            .GroupBy(evidence => evidence.FeatureKey, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var featureEvidenceCount = Math.Max(1, featureGroup.Count());
            foreach (var labelGroup in featureGroup
                .GroupBy(evidence => evidence.Label, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var evidenceIds = labelGroup
                    .Select(evidence => evidence.EvidenceId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                categories.Add(new ReferenceStyleCategoricalFeaturePayload(
                    featureGroup.Key,
                    labelGroup.Key,
                    Math.Round(labelGroup.Count() / (double)featureEvidenceCount, 4),
                    Math.Round(labelGroup.Average(evidence => Math.Clamp(evidence.Confidence, 0, 1)), 4),
                    evidenceIds));
            }
        }

        return baseline with { CategoricalFeatures = categories };
    }

    private static ReferenceStyleFeatureVectorPayload MergeStyleSampleStatsFeatures(
        ReferenceStyleFeatureVectorPayload baseline,
        IReadOnlyList<ReferenceStyleSampleSource> sampleSources,
        IReadOnlyList<ReferenceStyleEvidenceSpanPayload> sampleEvidenceSpans,
        bool replaceExisting)
    {
        if (sampleSources.Count == 0)
        {
            return baseline;
        }

        var stats = AggregateStyleSampleStats(sampleSources);
        var confidence = Math.Round(Math.Clamp(
            0.55 + Math.Min(0.25, sampleSources.Count / 10.0) + Math.Min(0.20, stats.SentenceCount / 100.0),
            0.55,
            1.0),
            4);
        var evidenceIds = sampleEvidenceSpans
            .GroupBy(evidence => evidence.FeatureKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(evidence => evidence.EvidenceId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var prefix = replaceExisting ? string.Empty : "style_sample_";

        var numericFeatures = new List<ReferenceStyleNumericFeaturePayload>(baseline.NumericFeatures);
        UpsertNumeric("character_count", stats.CharacterCount, "chars", confidence, []);
        UpsertNumeric("word_count", stats.WordCount, "count", confidence, []);
        UpsertNumeric("sentence_count", stats.SentenceCount, "count", confidence, []);
        UpsertNumeric("paragraph_count", stats.ParagraphCount, "count", confidence, []);
        UpsertNumeric("average_sentence_chars", stats.AverageSentenceChars, "chars", confidence, EvidenceIds("average_sentence_chars"));
        UpsertNumeric("sentence_length_std_dev", stats.SentenceLengthStdDev, "chars", confidence, EvidenceIds("average_sentence_chars"));
        UpsertNumeric("average_paragraph_chars", stats.AverageParagraphChars, "chars", confidence, EvidenceIds("average_paragraph_chars"));
        UpsertNumeric("dialogue_ratio", stats.DialogueRatio, "ratio", confidence, EvidenceIds("dialogue_ratio"));
        UpsertNumeric("interiority_ratio", stats.InteriorityRatio, "ratio", confidence, EvidenceIds("interiority_ratio"));
        UpsertNumeric("sensory_ratio", stats.SensoryRatio, "ratio", confidence, EvidenceIds("sensory_ratio"));
        UpsertNumeric("punctuation_per_100_chars", stats.PunctuationPer100Chars, "per_100_chars", confidence, []);
        UpsertNumeric("quote_density", stats.QuoteDensity, "per_100_chars", confidence, []);

        var distributionFeatures = new List<ReferenceStyleDistributionFeaturePayload>(baseline.DistributionFeatures);
        UpsertDistribution(
            "sentence_length_distribution",
            "chars",
            BuildStyleSampleLengthBuckets(stats.SentenceLengthDistribution),
            confidence,
            EvidenceIds("average_sentence_chars"));

        return baseline with
        {
            NumericFeatures = numericFeatures,
            DistributionFeatures = distributionFeatures
        };

        void UpsertNumeric(string featureKey, double value, string unit, double featureConfidence, IReadOnlyList<string> featureEvidenceIds)
        {
            var key = prefix + featureKey;
            if (replaceExisting)
            {
                numericFeatures.RemoveAll(feature => string.Equals(feature.FeatureKey, key, StringComparison.Ordinal));
            }

            numericFeatures.Add(new ReferenceStyleNumericFeaturePayload(
                key,
                Math.Round(value, 4),
                unit,
                featureConfidence,
                featureEvidenceIds));
        }

        void UpsertDistribution(
            string featureKey,
            string unit,
            IReadOnlyList<ReferenceStyleDistributionBucketPayload> buckets,
            double featureConfidence,
            IReadOnlyList<string> featureEvidenceIds)
        {
            var key = prefix + featureKey;
            if (replaceExisting)
            {
                distributionFeatures.RemoveAll(feature => string.Equals(feature.FeatureKey, key, StringComparison.Ordinal));
            }

            distributionFeatures.Add(new ReferenceStyleDistributionFeaturePayload(
                key,
                unit,
                buckets,
                featureConfidence,
                featureEvidenceIds));
        }

        IReadOnlyList<string> EvidenceIds(string featureKey)
        {
            return evidenceIds.TryGetValue(featureKey, out var ids) ? ids : [];
        }
    }

    private static StyleSampleStatsPayload AggregateStyleSampleStats(IReadOnlyList<ReferenceStyleSampleSource> sampleSources)
    {
        var stats = sampleSources.Select(source => source.Stats).ToArray();
        var characterCount = stats.Sum(item => Math.Max(0, item.CharacterCount));
        var wordCount = stats.Sum(item => Math.Max(0, item.WordCount));
        var sentenceCount = stats.Sum(item => Math.Max(0, item.SentenceCount));
        var paragraphCount = stats.Sum(item => Math.Max(0, item.ParagraphCount));
        var sentenceLengths = stats
            .SelectMany(item => item.SentenceLengthDistribution ?? [])
            .Where(length => length > 0)
            .ToArray();
        var punctuationCount = stats.Sum(item => item.PunctuationPer100Chars * Math.Max(0, item.CharacterCount) / 100.0);
        var quoteCount = stats.Sum(item => item.QuoteDensity * Math.Max(0, item.CharacterCount) / 100.0);
        var dialogueChars = stats.Sum(item => item.DialogueRatio * Math.Max(0, item.CharacterCount));
        var interiorityChars = stats.Sum(item => item.InteriorityRatio * Math.Max(0, item.CharacterCount));
        var sensoryChars = stats.Sum(item => item.SensoryRatio * Math.Max(0, item.CharacterCount));

        return new StyleSampleStatsPayload(
            StyleTextStatistics.SchemaVersion,
            characterCount,
            wordCount,
            sentenceCount,
            sentenceLengths,
            AverageFromLengths(sentenceLengths, fallback: WeightedAverage(stats, item => item.AverageSentenceChars, item => item.SentenceCount)),
            StandardDeviation(sentenceLengths, fallback: WeightedAverage(stats, item => item.SentenceLengthStdDev, item => item.SentenceCount)),
            characterCount == 0 ? 0 : Math.Round(punctuationCount * 100.0 / characterCount, 4),
            characterCount == 0 ? 0 : Math.Round(quoteCount * 100.0 / characterCount, 4),
            paragraphCount,
            WeightedAverage(stats, item => item.AverageParagraphChars, item => item.ParagraphCount),
            characterCount == 0 ? 0 : Math.Round(dialogueChars / characterCount, 4),
            characterCount == 0 ? 0 : Math.Round(interiorityChars / characterCount, 4),
            characterCount == 0 ? 0 : Math.Round(sensoryChars / characterCount, 4));
    }

    private static IReadOnlyList<ReferenceStyleDistributionBucketPayload> BuildStyleSampleLengthBuckets(IReadOnlyList<int> lengths)
    {
        if (lengths.Count == 0)
        {
            return
            [
                new ReferenceStyleDistributionBucketPayload("short", 0, 20, 0),
                new ReferenceStyleDistributionBucketPayload("medium", 21, 60, 0),
                new ReferenceStyleDistributionBucketPayload("long", 61, 120, 0),
                new ReferenceStyleDistributionBucketPayload("very_long", 121, 1000000, 0)
            ];
        }

        return
        [
            new ReferenceStyleDistributionBucketPayload("short", 0, 20, Ratio(lengths.Count(length => length <= 20), lengths.Count)),
            new ReferenceStyleDistributionBucketPayload("medium", 21, 60, Ratio(lengths.Count(length => length > 20 && length <= 60), lengths.Count)),
            new ReferenceStyleDistributionBucketPayload("long", 61, 120, Ratio(lengths.Count(length => length > 60 && length <= 120), lengths.Count)),
            new ReferenceStyleDistributionBucketPayload("very_long", 121, 1000000, Ratio(lengths.Count(length => length > 120), lengths.Count))
        ];
    }

    private static double AverageFromLengths(IReadOnlyList<int> lengths, double fallback)
    {
        return lengths.Count == 0 ? Math.Round(fallback, 4) : Math.Round(lengths.Average(), 4);
    }

    private static double StandardDeviation(IReadOnlyList<int> lengths, double fallback)
    {
        if (lengths.Count == 0)
        {
            return Math.Round(fallback, 4);
        }

        var average = lengths.Average();
        return Math.Round(Math.Sqrt(lengths.Sum(length => Math.Pow(length - average, 2)) / lengths.Count), 4);
    }

    private static double WeightedAverage(
        IReadOnlyList<StyleSampleStatsPayload> stats,
        Func<StyleSampleStatsPayload, double> value,
        Func<StyleSampleStatsPayload, int> weight)
    {
        var denominator = stats.Sum(item => Math.Max(0, weight(item)));
        if (denominator == 0)
        {
            return 0;
        }

        return Math.Round(stats.Sum(item => value(item) * Math.Max(0, weight(item))) / denominator, 4);
    }

    private static double Ratio(int count, int total)
    {
        return total <= 0 ? 0 : Math.Round(count / (double)total, 4);
    }

    private static IReadOnlyDictionary<string, object> BuildLlmDiagnostics(
        ReferenceStyleLlmAnalysisValidationResultPayload validation)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["status"] = validation.Status,
            ["diagnostics"] = validation.Diagnostics,
            ["rejected_labels"] = validation.RejectedLabels,
            ["accepted_evidence_count"] = validation.EvidenceSpans.Count
        };
    }

    private static async ValueTask<long> InsertProfileShellAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long novelId,
        string title,
        string description,
        IReadOnlyList<long> anchorIds,
        IReadOnlyList<string> sourceHashes,
        IReadOnlyList<long> styleSampleIds,
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
               analyzer_source, anchor_ids_json, source_hashes_json, style_sample_ids_json, allowed_license_statuses_json,
               allowed_source_trust_levels_json, feature_vector_json, aggregate_confidence,
               created_at, updated_at, archived_at)
            VALUES
              ($novel_id, $title, $description, $status, $analyzer_version, $feature_schema_version,
               $analyzer_source, $anchor_ids_json, $source_hashes_json, $style_sample_ids_json, $allowed_license_statuses_json,
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
        command.Parameters.AddWithValue("$style_sample_ids_json", JsonSerializer.Serialize(styleSampleIds, JsonOptions));
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

    private static async ValueTask InsertSampleProfileSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long profileId,
        IReadOnlyList<ReferenceStyleSampleSource> sources,
        IReadOnlyList<ReferenceStyleMaterialSample> materials,
        CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reference_style_profile_sample_sources
                  (profile_id, sample_id, novel_id, is_global, source_hash, stats_schema_version,
                   material_count, segment_count)
                VALUES
                  ($profile_id, $sample_id, $novel_id, $is_global, $source_hash, $stats_schema_version,
                   $material_count, $segment_count);
                """;
            command.Parameters.AddWithValue("$profile_id", profileId);
            command.Parameters.AddWithValue("$sample_id", source.SampleId);
            command.Parameters.AddWithValue("$novel_id", (object?)source.NovelId ?? DBNull.Value);
            command.Parameters.AddWithValue("$is_global", source.IsGlobal ? 1 : 0);
            command.Parameters.AddWithValue("$source_hash", source.SourceHash);
            command.Parameters.AddWithValue("$stats_schema_version", source.StatsSchemaVersion);
            command.Parameters.AddWithValue("$material_count", materials.Count(material => material.StyleSampleId == source.SampleId));
            command.Parameters.AddWithValue("$segment_count", materials.Where(material => material.StyleSampleId == source.SampleId).Select(material => material.SourceSegmentId).Distinct(StringComparer.Ordinal).Count());
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

    private static async ValueTask InsertSampleEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ReferenceStyleEvidenceSpanPayload> evidenceSpans,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var evidence in evidenceSpans)
        {
            if (evidence.StyleSampleId is null or <= 0)
            {
                throw new InvalidOperationException("Sample-backed style evidence must carry a style sample id.");
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reference_style_profile_sample_evidence
                  (evidence_id, profile_id, sample_id, source_segment_id, material_id,
                   feature_key, label, start_offset, end_offset, text_hash, confidence,
                   analyzer_source, created_at)
                VALUES
                  ($evidence_id, $profile_id, $sample_id, $source_segment_id, $material_id,
                   $feature_key, $label, $start_offset, $end_offset, $text_hash, $confidence,
                   $analyzer_source, $created_at);
                """;
            command.Parameters.AddWithValue("$evidence_id", evidence.EvidenceId);
            command.Parameters.AddWithValue("$profile_id", evidence.ProfileId);
            command.Parameters.AddWithValue("$sample_id", evidence.StyleSampleId.Value);
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
        string analyzerVersion,
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
            command.Parameters.AddWithValue("$analyzer_version", analyzerVersion);
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
        string analyzerVersion,
        string analyzerSource,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_style_profiles
            SET feature_vector_json = $feature_vector_json,
                aggregate_confidence = $aggregate_confidence,
                analyzer_version = $analyzer_version,
                analyzer_source = $analyzer_source,
                updated_at = $updated_at
            WHERE profile_id = $profile_id;
            """;
        command.Parameters.AddWithValue("$feature_vector_json", JsonSerializer.Serialize(features, JsonOptions));
        command.Parameters.AddWithValue("$aggregate_confidence", aggregateConfidence);
        command.Parameters.AddWithValue("$analyzer_version", analyzerVersion);
        command.Parameters.AddWithValue("$analyzer_source", analyzerSource);
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
        string analyzerVersion,
        string analyzerSource,
        IReadOnlyList<long> anchorIds,
        IReadOnlyList<string> sourceHashes,
        string status,
        object diagnostics,
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
        command.Parameters.AddWithValue("$analyzer_version", analyzerVersion);
        command.Parameters.AddWithValue("$feature_schema_version", ReferenceStyleFeatureSchemaVersions.V1);
        command.Parameters.AddWithValue("$analyzer_source", analyzerSource);
        command.Parameters.AddWithValue("$input_anchor_ids_json", JsonSerializer.Serialize(anchorIds, JsonOptions));
        command.Parameters.AddWithValue("$input_source_hashes_json", JsonSerializer.Serialize(sourceHashes, JsonOptions));
        command.Parameters.AddWithValue("$status", status);
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
                   style_sample_ids_json, allowed_license_statuses_json, allowed_source_trust_levels_json,
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
        var sampleEvidence = await ReadSampleEvidenceAsync(connection, profileId, cancellationToken);
        evidence = evidence
            .Concat(sampleEvidence)
            .OrderBy(item => item.FeatureKey, StringComparer.Ordinal)
            .ThenBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ToArray();
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

    private static async ValueTask<IReadOnlyList<ReferenceStyleEvidenceSpanPayload>> ReadSampleEvidenceAsync(
        SqliteConnection connection,
        long profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT evidence_id, profile_id, sample_id, source_segment_id, material_id,
                   feature_key, label, start_offset, end_offset, text_hash, confidence,
                   analyzer_source
            FROM reference_style_profile_sample_evidence
            WHERE profile_id = $profile_id
            ORDER BY feature_key ASC, evidence_id ASC;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        var evidence = new List<ReferenceStyleEvidenceSpanPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            evidence.Add(ReadSampleEvidence(reader));
        }

        return evidence;
    }

    private static ReferenceStyleProfileComparisonPayload BuildStyleProfileComparison(
        long novelId,
        ReferenceStyleProfilePayload left,
        ReferenceStyleProfilePayload right,
        DateTimeOffset comparedAt)
    {
        return new ReferenceStyleProfileComparisonPayload(
            novelId,
            ToSummary(left),
            ToSummary(right),
            CompareNumericFeatures(left.Features, right.Features),
            CompareDistributionFeatures(left.Features, right.Features),
            CompareCategoricalFeatures(left.Features, right.Features),
            comparedAt);
    }

    private static ReferenceStyleProfileSummaryPayload ToSummary(ReferenceStyleProfilePayload profile)
    {
        return new ReferenceStyleProfileSummaryPayload(
            profile.ProfileId,
            profile.NovelId,
            profile.Title,
            profile.Description,
            profile.Status,
            profile.AnalyzerVersion,
            profile.FeatureSchemaVersion,
            profile.AnalyzerSource,
            profile.SourceAnchorIds,
            profile.SourceHashes,
            profile.AggregateConfidence,
            profile.CreatedAt,
            profile.UpdatedAt,
            profile.ArchivedAt);
    }

    private static IReadOnlyList<ReferenceStyleNumericFeatureDifferencePayload> CompareNumericFeatures(
        ReferenceStyleFeatureVectorPayload left,
        ReferenceStyleFeatureVectorPayload right)
    {
        var leftByKey = left.NumericFeatures
            .GroupBy(feature => feature.FeatureKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var rightByKey = right.NumericFeatures
            .GroupBy(feature => feature.FeatureKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return leftByKey.Keys
            .Concat(rightByKey.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(featureKey =>
            {
                leftByKey.TryGetValue(featureKey, out var leftFeature);
                rightByKey.TryGetValue(featureKey, out var rightFeature);
                var leftValue = leftFeature?.Value;
                var rightValue = rightFeature?.Value;
                return new ReferenceStyleNumericFeatureDifferencePayload(
                    featureKey,
                    leftFeature?.Unit ?? rightFeature?.Unit ?? string.Empty,
                    leftValue,
                    rightValue,
                    BothPresentAbsoluteDelta(leftValue, rightValue),
                    RelativeDelta(leftValue, rightValue),
                    leftFeature?.Confidence,
                    rightFeature?.Confidence);
            })
            .ToArray();
    }

    private static IReadOnlyList<ReferenceStyleDistributionFeatureDifferencePayload> CompareDistributionFeatures(
        ReferenceStyleFeatureVectorPayload left,
        ReferenceStyleFeatureVectorPayload right)
    {
        var leftByKey = left.DistributionFeatures
            .GroupBy(feature => feature.FeatureKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var rightByKey = right.DistributionFeatures
            .GroupBy(feature => feature.FeatureKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return leftByKey.Keys
            .Concat(rightByKey.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(featureKey =>
            {
                leftByKey.TryGetValue(featureKey, out var leftFeature);
                rightByKey.TryGetValue(featureKey, out var rightFeature);
                return new ReferenceStyleDistributionFeatureDifferencePayload(
                    featureKey,
                    leftFeature?.Unit ?? rightFeature?.Unit ?? string.Empty,
                    CompareDistributionBuckets(leftFeature, rightFeature),
                    leftFeature?.Confidence,
                    rightFeature?.Confidence);
            })
            .ToArray();
    }

    private static IReadOnlyList<ReferenceStyleDistributionBucketDifferencePayload> CompareDistributionBuckets(
        ReferenceStyleDistributionFeaturePayload? left,
        ReferenceStyleDistributionFeaturePayload? right)
    {
        var leftByLabel = (left?.Buckets ?? [])
            .GroupBy(bucket => bucket.Label, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var rightByLabel = (right?.Buckets ?? [])
            .GroupBy(bucket => bucket.Label, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return leftByLabel.Keys
            .Concat(rightByLabel.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(label =>
            {
                leftByLabel.TryGetValue(label, out var leftBucket);
                rightByLabel.TryGetValue(label, out var rightBucket);
                var leftWeight = leftBucket?.Weight;
                var rightWeight = rightBucket?.Weight;
                return new ReferenceStyleDistributionBucketDifferencePayload(
                    label,
                    leftBucket?.Min,
                    leftBucket?.Max,
                    leftWeight,
                    rightBucket?.Min,
                    rightBucket?.Max,
                    rightWeight,
                    MissingAsZeroAbsoluteDelta(leftWeight, rightWeight));
            })
            .OrderBy(bucket => bucket.LeftMin ?? bucket.RightMin ?? 0)
            .ThenBy(bucket => bucket.Label, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ReferenceStyleCategoricalFeatureDifferencePayload> CompareCategoricalFeatures(
        ReferenceStyleFeatureVectorPayload left,
        ReferenceStyleFeatureVectorPayload right)
    {
        var leftByKey = left.CategoricalFeatures
            .GroupBy(feature => (feature.FeatureKey, feature.Label))
            .ToDictionary(group => group.Key, group => group.First());
        var rightByKey = right.CategoricalFeatures
            .GroupBy(feature => (feature.FeatureKey, feature.Label))
            .ToDictionary(group => group.Key, group => group.First());

        return leftByKey.Keys
            .Concat(rightByKey.Keys)
            .Distinct()
            .OrderBy(key => key.FeatureKey, StringComparer.Ordinal)
            .ThenBy(key => key.Label, StringComparer.Ordinal)
            .Select(key =>
            {
                leftByKey.TryGetValue(key, out var leftFeature);
                rightByKey.TryGetValue(key, out var rightFeature);
                var leftWeight = leftFeature?.Weight;
                var rightWeight = rightFeature?.Weight;
                return new ReferenceStyleCategoricalFeatureDifferencePayload(
                    key.FeatureKey,
                    key.Label,
                    leftWeight,
                    rightWeight,
                    MissingAsZeroAbsoluteDelta(leftWeight, rightWeight),
                    leftFeature?.Confidence,
                    rightFeature?.Confidence);
            })
            .ToArray();
    }

    private static double? BothPresentAbsoluteDelta(double? left, double? right)
    {
        return left is null || right is null
            ? null
            : Math.Abs(left.Value - right.Value);
    }

    private static double? MissingAsZeroAbsoluteDelta(double? left, double? right)
    {
        return left is null && right is null
            ? null
            : Math.Abs((left ?? 0) - (right ?? 0));
    }

    private static double? RelativeDelta(double? left, double? right)
    {
        if (left is null || right is null || Math.Abs(left.Value) < double.Epsilon)
        {
            return null;
        }

        return Math.Abs(right.Value - left.Value) / Math.Abs(left.Value);
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
        return NormalizeAnchorIds(anchorIds, allowEmpty: false);
    }

    private static IReadOnlyList<long> NormalizeAnchorIds(IReadOnlyList<long>? anchorIds, bool allowEmpty)
    {
        if (anchorIds is null || anchorIds.Count == 0)
        {
            if (allowEmpty)
            {
                return [];
            }

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

    private static IReadOnlyList<long> NormalizeStyleSampleIds(IReadOnlyList<long>? sampleIds)
    {
        if (sampleIds is null || sampleIds.Count == 0)
        {
            return [];
        }

        var normalized = new List<long>(sampleIds.Count);
        var seen = new HashSet<long>();
        foreach (var sampleId in sampleIds)
        {
            if (sampleId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleIds), sampleId, "Style sample id must be positive.");
            }

            if (seen.Add(sampleId))
            {
                normalized.Add(sampleId);
            }
        }

        if (normalized.Count > MaxStyleSampleCount)
        {
            throw new ArgumentException($"At most {MaxStyleSampleCount} style samples can be used for one style profile.", nameof(sampleIds));
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
            reader.GetDouble(11),
            ParseTimestamp(reader.GetString(12)),
            ParseTimestamp(reader.GetString(13)),
            reader.IsDBNull(14) ? null : ParseTimestamp(reader.GetString(14)),
            ReadLongList(reader.GetString(10)));
    }

    private static ReferenceStyleProfileBuildStatusPayload ReadStyleBuildStatus(SqliteDataReader reader)
    {
        return new ReferenceStyleProfileBuildStatusPayload(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            ReadLongList(reader.GetString(8)),
            ReadStringList(reader.GetString(9)),
            ReadStringList(reader.GetString(11)),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            ParseTimestamp(reader.GetString(14)),
            ParseTimestamp(reader.GetString(15)),
            reader.IsDBNull(16) ? null : ParseTimestamp(reader.GetString(16)),
            reader.IsDBNull(17) ? null : ParseTimestamp(reader.GetString(17)),
            ReadLongList(reader.GetString(10)));
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
            ReadStringList(reader.GetString(11)),
            ReadStringList(reader.GetString(12)),
            reader.GetDouble(14),
            ReadFeatureVector(reader.GetString(13)),
            [],
            ParseTimestamp(reader.GetString(15)),
            ParseTimestamp(reader.GetString(16)),
            reader.IsDBNull(17) ? null : ParseTimestamp(reader.GetString(17)),
            ReadLongList(reader.GetString(10)));
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
            reader.GetString(11),
            ReferenceStyleProfileSourceTypes.ReferenceAnchor,
            StyleSampleId: null);
    }

    private static ReferenceStyleEvidenceSpanPayload ReadSampleEvidence(SqliteDataReader reader)
    {
        return new ReferenceStyleEvidenceSpanPayload(
            reader.GetString(0),
            reader.GetInt64(1),
            AnchorId: 0,
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetString(9),
            reader.GetDouble(10),
            reader.GetString(11),
            ReferenceStyleProfileSourceTypes.StyleSample,
            reader.GetInt64(2));
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

    private static string NormalizeBuildId(string? buildId)
    {
        var normalized = NormalizeOptionalText(buildId, nameof(buildId), MaxBuildIdLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "style-build-" + Guid.NewGuid().ToString("N");
        }

        if (normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("Style profile build id may contain only ASCII letters, digits, '.', '_' and '-'.", nameof(buildId));
        }

        return normalized;
    }

    private static string NormalizeBestEffortTitle(string? value)
    {
        try
        {
            var normalized = NormalizeOptionalText(value, nameof(value), maxLength: 200);
            return normalized.Length == 0 ? "Untitled style profile" : normalized;
        }
        catch
        {
            return "Untitled style profile";
        }
    }

    private static IReadOnlyList<long> NormalizeBestEffortAnchorIds(IReadOnlyList<long>? anchorIds)
    {
        if (anchorIds is null || anchorIds.Count == 0)
        {
            return [];
        }

        return anchorIds
            .Where(anchorId => anchorId > 0)
            .Distinct()
            .Take(MaxAnchorCount)
            .ToArray();
    }

    private static IReadOnlyList<long> NormalizeBestEffortStyleSampleIds(IReadOnlyList<long>? sampleIds)
    {
        if (sampleIds is null || sampleIds.Count == 0)
        {
            return [];
        }

        return sampleIds
            .Where(sampleId => sampleId > 0)
            .Distinct()
            .Take(MaxStyleSampleCount)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildFailureDiagnostics(Exception exception)
    {
        return [$"failed:{NormalizeOptionalErrorField(exception.GetType().Name, maxLength: 128) ?? "Exception"}"];
    }

    private static IReadOnlyList<string> SanitizeDiagnostics(IReadOnlyList<string>? diagnostics)
    {
        if (diagnostics is null || diagnostics.Count == 0)
        {
            return [];
        }

        return diagnostics
            .Select(diagnostic => NormalizeOptionalErrorField(diagnostic, maxLength: 256))
            .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic))
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray()!;
    }

    private static string? NormalizeOptionalErrorField(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = new string(value
            .Trim()
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        normalized = normalized.Length <= maxLength ? normalized : normalized[..maxLength];
        normalized = normalized
            .Replace("\\", "/", StringComparison.Ordinal)
            .Replace("..", ".", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
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

    private static bool ContainsAny(string text, IReadOnlyList<string> markers)
    {
        return markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));
    }

    private static string HashText(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private sealed record ReferenceStyleAnchorSource(
        long AnchorId,
        long NovelId,
        string SourceFileHash,
        string LicenseStatus,
        string SourceTrust,
        string CorpusVisibility);

    private sealed record ReferenceStyleSampleSource(
        long SampleId,
        long? NovelId,
        bool IsGlobal,
        string Name,
        string Content,
        StyleSampleStatsPayload Stats,
        string StatsSchemaVersion,
        string SourceHash);

    private sealed record SampleTextSpan(
        string Text,
        int StartOffset,
        int EndOffset);
}
