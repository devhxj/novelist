using System.Collections.Concurrent;
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

public sealed class SqliteReferenceAnchorService : IReferenceAnchorService, IReferenceAnchorProcessingRecoveryService
{
    private const string BuildVersion = "reference-anchor-v1";
    private const long WorkspaceCorpusNovelId = 0;
    private const string NovelOrVisibleWorkspaceCorpusPredicate =
        "(novel_id = $novel_id OR ((novel_id IS NULL OR novel_id = $workspace_corpus_novel_id) AND corpus_visibility = $workspace_corpus_visibility))";
    private const string AnchorAliasNovelOrVisibleWorkspaceCorpusPredicate =
        "(a.novel_id = $novel_id OR ((a.novel_id IS NULL OR a.novel_id = $workspace_corpus_novel_id) AND a.corpus_visibility = $workspace_corpus_visibility))";
    private const long MaxSourceBytes = 20L * 1024L * 1024L;
    private const int EmbeddingBatchSize = 64;
    private const int UnknownLicensePreviewMaxChars = 48;
    private const int MaterialDetailTextPreviewMaxChars = 240;
    private const int MaterialDetailSegmentPreviewMaxChars = 240;
    private const int MaterialDetailMaxSegments = 3;
    private const int MaterialDetailMaxSlots = 20;
    private const int MaterialListPreviewMaxChars = 160;
    private const double MaterialReviewConfidenceThreshold = 0.75;
    private const int MaxExplicitAnchorFilterIds = 256;
    private const int AdvancedSceneMaxParagraphs = 8;
    private const int AdvancedEvidenceWindowChars = 480;
    private const int MaxStyleProfileFilters = 8;
    private const int MaxStyleDimensionFilters = 16;
    private const int SqliteConstraintErrorCode = 19;
    private static readonly Regex MarkdownHeadingPattern = new(@"^\s{0,3}#{1,6}\s+(.+?)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BlankLinePattern = new(@"\n\s*\n", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RiskTokenPattern = new(@"[A-Za-z][A-Za-z0-9_]{1,}|\d+(?:\.\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SecretPattern = new(@"\b(?:sk-[A-Za-z0-9_-]{12,}|Bearer\s+[A-Za-z0-9._~+/=-]{8,})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex FileUriPattern = new(@"\bfile://[^\s;'""]+", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex UncPathPattern = new(@"\\\\[^\\/:*?""<>|\r\n;]+\\[^\\/:*?""<>|\r\n;]+(?:\\[^\\/:*?""<>|\r\n;]+)*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WindowsPathPattern = new(@"[A-Za-z]:[\\/](?:[^\\/:*?""<>|\r\n]+[\\/])*[^\\/:*?""<>|\r\n]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnixPathPattern = new(@"(?<![\w])/(?:Users|home|var|tmp|private|Volumes|mnt|opt|etc|root)/[^\s:'""]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveFieldPattern = new(@"(?i)\b(source_text|prompt|candidate_text|api_key|authorization|password|token)\b\s*[:=]\s*[^\r\n;]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = BridgeJson.SerializerOptions;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ImportIdentityLocks = new(StringComparer.Ordinal);
    private static readonly string[] DialogueMarkers = ["“", "”", "「", "」", "『", "』", "\"", "说：", "道：", "问：", "答："];
    private static readonly string[] SensoryMarkers = ["雨", "风", "雪", "光", "声", "呼吸", "气味", "冷", "热", "疼", "黑", "亮"];
    private static readonly string[] InteriorityMarkers = ["心", "想", "觉得", "明白", "知道", "意识到", "记得", "忘了"];
    private static readonly string[] ActionMarkers = ["走", "停", "看", "拿", "推", "转", "站", "坐", "伸", "退", "进", "出"];
    private static readonly string[] TransitionMarkers = ["后来", "然后", "这时", "与此同时", "片刻", "很快", "直到"];
    private static readonly string[] HookMarkers = ["忽然", "突然", "竟", "没想到", "门外", "安静下来", "悬", "威胁", "？", "?"];
    private static readonly string[] PayoffMarkers = ["终于", "答案", "真相", "原来", "明白", "不是", "兑现", "揭开", "揭露"];
    private static readonly string[] EmotionEvidenceMarkers = ["喉咙", "指尖", "手指", "指节", "掌心", "眼神", "目光", "声音", "沉默", "停顿", "没有回答", "避开", "咽下", "发紧", "发凉", "发涩", "发颤", "颤", "扣紧", "蜷紧", "欲言又止", "杯子推远", "只把钥匙放回"];
    private static readonly string[] RestrainedEmotionMarkers = ["没有回答", "却", "避开", "咽下", "忍住", "发紧", "发凉", "发涩", "沉默", "扣紧", "蜷紧", "欲言又止", "杯子推远", "只把钥匙放回"];
    private static readonly string[] LimitedPovMarkers = ["看不见", "没看见", "不知道", "并不知道", "没有察觉", "未曾发现", "无从知道", "背对着", "背对", "没有回头", "没回头", "未回头"];
    private static readonly string[] AfterbeatMarkers = ["移开目光", "垂下眼", "停了一下", "停住", "顿了顿", "沉默了一下", "攥紧", "松开"];
    private static readonly string[] ActionAfterbeatEvidenceMarkers = [.. AfterbeatMarkers, .. EmotionEvidenceMarkers];
    private static readonly string[] AiRiskPhrases = ["无法言喻", "复杂的情绪", "某种意义上", "仿佛有什么", "命运的齿轮", "心中涌起"];
    private static readonly HashSet<string> UnknownMaterialTags = new(StringComparer.OrdinalIgnoreCase)
    {
        string.Empty,
        "unknown",
        "untagged",
        "none",
        "null",
        "undefined"
    };

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

    private static readonly HashSet<string> AllowedCorpusVisibilities = new(ReferenceCorpusVisibilities.All, StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedSourceTrustLevels = new(ReferenceSourceTrustLevels.All, StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedFeedbackDecisions = new(ReferenceFeedbackDecisions.All, StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedFeedbackTargetTypes = new(ReferenceFeedbackTargetTypes.All, StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedMaterialArchiveFilters = new(ReferenceMaterialArchiveFilters.Allowed, StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedStyleImitationIntensities = new(ReferenceStyleImitationIntensities.All, StringComparer.Ordinal);

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly IEmbeddingConfigurationService _embeddingConfiguration;
    private readonly IEmbeddingClient _embeddings;
    private readonly ISqliteVecTableProvisioner _vecProvisioner;
    private readonly ISqliteVecQueryProvider _vecQuery;
    private readonly IReferenceMaterialSlotDetector _slotDetector;
    private readonly IReferenceAnchorProcessingStageProbe _stageProbe;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqliteReferenceAnchorService(
        AppInitializationOptions? options = null,
        INovelService? novels = null,
        IEmbeddingConfigurationService? embeddingConfiguration = null,
        IEmbeddingClient? embeddings = null,
        ISqliteVecTableProvisioner? vecProvisioner = null,
        ISqliteVecQueryProvider? vecQuery = null)
        : this(
            options,
            novels,
            embeddingConfiguration,
            embeddings,
            vecProvisioner,
            vecQuery,
            DefaultReferenceMaterialSlotDetector.Instance,
            NoopReferenceAnchorProcessingStageProbe.Instance)
    {
    }

    internal SqliteReferenceAnchorService(
        AppInitializationOptions? options,
        INovelService? novels,
        IEmbeddingConfigurationService? embeddingConfiguration,
        IEmbeddingClient? embeddings,
        ISqliteVecTableProvisioner? vecProvisioner,
        ISqliteVecQueryProvider? vecQuery,
        IReferenceMaterialSlotDetector slotDetector,
        IReferenceAnchorProcessingStageProbe? stageProbe = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
        _embeddingConfiguration = embeddingConfiguration ?? new NullEmbeddingConfigurationService();
        _embeddings = embeddings ?? new HybridEmbeddingClient();
        _vecProvisioner = vecProvisioner ?? new SqliteVecTableProvisioner();
        _vecQuery = vecQuery ?? (_vecProvisioner as ISqliteVecQueryProvider) ?? new SqliteVecTableProvisioner();
        _slotDetector = slotDetector;
        _stageProbe = stageProbe ?? NoopReferenceAnchorProcessingStageProbe.Instance;
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
        var visibility = string.IsNullOrWhiteSpace(input.Visibility)
            ? ReferenceCorpusVisibilities.Private
            : ValidateAllowedText(input.Visibility, nameof(input.Visibility), AllowedCorpusVisibilities);
        var storedNovelId = visibility == ReferenceCorpusVisibilities.Workspace
            ? (long?)null
            : input.NovelId;
        var sourceTrust = string.IsNullOrWhiteSpace(input.SourceTrust)
            ? ReferenceSourceTrustLevels.UserVerified
            : ValidateAllowedText(input.SourceTrust, nameof(input.SourceTrust), AllowedSourceTrustLevels);
        var userTags = NormalizeUserTags(input.UserTags);
        var now = DateTimeOffset.UtcNow;
        var databasePath = await DatabasePathAsync(cancellationToken);
        ReferenceAnchorPayload? stagedAnchor = null;
        IReadOnlyList<ReferenceMaterialPayload> stagedMaterials = [];
        ReferenceProcessingAffectedIds stagedAffectedIds = ReferenceProcessingAffectedIds.Empty;
        var stagedSegmentCount = 0;
        var stagedMaterialCount = 0;
        var stagedSlotCount = 0;
        long? recoverExistingAnchorId = null;
        var importLockKey = BuildImportIdentityLockKey(
            databasePath,
            storedNovelId,
            visibility,
            sourcePath,
            sourceKind,
            "source-path");
        var importLock = ImportIdentityLocks.GetOrAdd(importLockKey, _ => new SemaphoreSlim(1, 1));

        await importLock.WaitAsync(cancellationToken);
        try
        {
        SourceSnapshot source;
        try
        {
            source = await ReadSourceFileAsync(sourcePath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await RecordInitialFailedImportAsync(
                databasePath,
                storedNovelId,
                title,
                author,
                sourcePath,
                sourceKind,
                licenseStatus,
                visibility,
                sourceTrust,
                userTags,
                exception,
                cancellationToken);
        }

        var embeddingOptions = await _embeddingConfiguration.GetActiveEmbeddingOptionsAsync(cancellationToken);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var existingInitialFailure = await FindExistingInitialFailedImportForSourcePathAsync(
                connection,
                transaction,
                storedNovelId,
                visibility,
                sourcePath,
                sourceKind,
                cancellationToken);
            if (existingInitialFailure is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existingInitialFailure;
            }

            var existing = await FindExistingAnchorForImportAsync(
                connection,
                transaction,
                storedNovelId,
                visibility,
                sourcePath,
                sourceKind,
                source.Hash,
                cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                if (IsRecoverableBuildStatus(existing.Status))
                {
                    recoverExistingAnchorId = existing.AnchorId;
                }
                else
                {
                    return existing;
                }
            }

            if (!recoverExistingAnchorId.HasValue)
            {
                ReferenceAnchorPayload? anchor = null;
                try
                {
                    anchor = await InsertAnchorAsync(
                        connection,
                        transaction,
                        storedNovelId,
                        title,
                        author,
                        sourcePath,
                        sourceKind,
                        licenseStatus,
                        visibility,
                        sourceTrust,
                        userTags,
                        source.Hash,
                        now,
                        cancellationToken);
                    await UpsertDefaultCorpusMembershipAsync(
                        connection,
                        transaction,
                        anchor.AnchorId,
                        storedNovelId,
                        visibility,
                        licenseStatus,
                        sourceTrust,
                        now,
                        cancellationToken);
                }
                catch (SqliteException exception) when (IsSqliteConstraintViolation(exception))
                {
                    var conflicting = await FindExistingAnchorForImportAsync(
                        connection,
                        transaction,
                        storedNovelId,
                        visibility,
                        sourcePath,
                        sourceKind,
                        source.Hash,
                        cancellationToken);
                    if (conflicting is null)
                    {
                        throw;
                    }

                    await transaction.CommitAsync(cancellationToken);
                    if (IsRecoverableBuildStatus(conflicting.Status))
                    {
                        recoverExistingAnchorId = conflicting.AnchorId;
                    }
                    else
                    {
                        return conflicting;
                    }
                }

                if (!recoverExistingAnchorId.HasValue)
                {
                    var insertedAnchor = anchor
                        ?? throw new InvalidOperationException("Reference anchor insert was not initialized.");
                    IReadOnlyList<ReferenceSourceSegment> segments;
                    ReferenceProcessingAffectedIds segmentAffectedIds;
                    try
                    {
                        _stageProbe.BeforeStage(ReferenceAnchorBuildStates.Segmenting, insertedAnchor.AnchorId, source.Hash);
                        segments = BuildSegments(insertedAnchor.AnchorId, source.Text);
                        segmentAffectedIds = BuildAffectedProcessingIds(segments);
                        await ReplaceSegmentsAsync(connection, transaction, insertedAnchor.AnchorId, segments, cancellationToken);
                        var segmentsBuiltAnchor = insertedAnchor with
                        {
                            Status = ReferenceAnchorBuildStates.SegmentsBuilt,
                            UpdatedAt = now
                        };
                        await UpdateAnchorBuildResultAsync(
                            connection,
                            transaction,
                            segmentsBuiltAnchor,
                            ReferenceAnchorBuildStates.SegmentsBuilt,
                            ReferenceAnchorBuildStates.SegmentsBuilt,
                            segments.Count,
                            0,
                            0,
                            string.Empty,
                            now,
                            cancellationToken,
                            affectedIds: segmentAffectedIds);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        var failedAt = DateTimeOffset.UtcNow;
                        var failedAnchor = insertedAnchor with
                        {
                            Status = ReferenceAnchorBuildStates.FailedSegmenting,
                            UpdatedAt = failedAt
                        };
                        await UpdateAnchorBuildResultAsync(
                            connection,
                            transaction,
                            failedAnchor,
                            ReferenceAnchorBuildStates.FailedSegmenting,
                            ReferenceAnchorBuildStates.FailedSegmenting,
                            0,
                            0,
                            0,
                            RedactError(exception.Message),
                            failedAt,
                            cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        return failedAnchor;
                    }

                    await transaction.CommitAsync(cancellationToken);

                    IReadOnlyList<ReferenceMaterialPayload> materials;
                    ReferenceProcessingAffectedIds materialAffectedIds;
                    try
                    {
                        _stageProbe.BeforeStage(ReferenceAnchorBuildStates.ExtractingMaterials, insertedAnchor.AnchorId, source.Hash);
                        materials = BuildMaterials(insertedAnchor.AnchorId, segments, now);
                        materialAffectedIds = BuildAffectedProcessingIds(materials);
                        await using var materialTransaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                        await ReplaceMaterialRowsAsync(connection, materialTransaction, insertedAnchor.AnchorId, materials, cancellationToken);
                        await UpsertStage1DeterministicObservationsAsync(
                            connection,
                            materialTransaction,
                            insertedAnchor.AnchorId,
                            source.Hash,
                            segments,
                            now,
                            cancellationToken);
                        var materialExtractedAnchor = insertedAnchor with
                        {
                            Status = ReferenceAnchorBuildStates.MaterialsExtracted,
                            UpdatedAt = now
                        };
                        await UpdateAnchorBuildResultAsync(
                            connection,
                            materialTransaction,
                            materialExtractedAnchor,
                            ReferenceAnchorBuildStates.MaterialsExtracted,
                            ReferenceAnchorBuildStates.MaterialsExtracted,
                            segments.Count,
                            materials.Count,
                            0,
                            string.Empty,
                            now,
                            cancellationToken,
                            affectedIds: materialAffectedIds);
                        await materialTransaction.CommitAsync(cancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        var failedAt = DateTimeOffset.UtcNow;
                        var failedAnchor = insertedAnchor with
                        {
                            Status = ReferenceAnchorBuildStates.FailedExtraction,
                            UpdatedAt = failedAt
                        };
                        await using var failureTransaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                        await UpdateAnchorBuildResultAsync(
                            connection,
                            failureTransaction,
                            failedAnchor,
                            ReferenceAnchorBuildStates.FailedExtraction,
                            ReferenceAnchorBuildStates.FailedExtraction,
                            segments.Count,
                            0,
                            0,
                            RedactError(exception.Message),
                            failedAt,
                            cancellationToken,
                            affectedIds: segmentAffectedIds);
                        await failureTransaction.CommitAsync(cancellationToken);
                        return failedAnchor;
                    }

                    try
                    {
                        var slotsByMaterial = DetectMaterialSlots(materials);
                        var slotCount = CountMaterialSlots(slotsByMaterial);
                        await using var slotTransaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                        await ReplaceMaterialSlotsAsync(
                            connection,
                            slotTransaction,
                            insertedAnchor.AnchorId,
                            slotsByMaterial,
                            BuildMaterialCreatedAt(materials),
                            cancellationToken);
                        var initialStatus = embeddingOptions is not null && materials.Count > 0
                            ? ReferenceAnchorBuildStates.Embedding
                            : ReferenceAnchorBuildStates.Ready;
                        var initialStage = embeddingOptions is not null && materials.Count > 0
                            ? ReferenceAnchorBuildStates.Embedding
                            : "ready";
                        var readyAnchor = insertedAnchor with
                        {
                            Status = initialStatus,
                            UpdatedAt = now
                        };
                        var affectedIds = BuildAffectedProcessingIds(materials, slotsByMaterial);
                        await UpdateAnchorBuildResultAsync(
                            connection,
                            slotTransaction,
                            readyAnchor,
                            initialStatus,
                            initialStage,
                            segments.Count,
                            materials.Count,
                            slotCount,
                            lastError: string.Empty,
                            now,
                            cancellationToken,
                            affectedIds: affectedIds);

                        await slotTransaction.CommitAsync(cancellationToken);
                        if (embeddingOptions is null || materials.Count == 0)
                        {
                            return readyAnchor;
                        }

                        stagedAnchor = readyAnchor;
                        stagedMaterials = materials;
                        stagedSegmentCount = segments.Count;
                        stagedMaterialCount = materials.Count;
                        stagedSlotCount = slotCount;
                        stagedAffectedIds = affectedIds;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        var failedAt = DateTimeOffset.UtcNow;
                        var failedAnchor = insertedAnchor with
                        {
                            Status = ReferenceAnchorBuildStates.FailedSlotting,
                            UpdatedAt = failedAt
                        };
                        await using var failureTransaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                        await UpdateAnchorBuildResultAsync(
                            connection,
                            failureTransaction,
                            failedAnchor,
                            ReferenceAnchorBuildStates.FailedSlotting,
                            ReferenceAnchorBuildStates.FailedSlotting,
                            segments.Count,
                            materials.Count,
                            0,
                            RedactError(exception.Message),
                            failedAt,
                            cancellationToken,
                            affectedIds: materialAffectedIds);
                        await failureTransaction.CommitAsync(cancellationToken);
                        return failedAnchor;
                    }
                }
            }
        }
        finally
        {
            _mutex.Release();
        }

        if (recoverExistingAnchorId.HasValue)
        {
            await RebuildAnchorAsync(input.NovelId, recoverExistingAnchorId.Value, cancellationToken);
            var recoveredAnchors = await GetAnchorsAsync(input.NovelId, cancellationToken);
            return recoveredAnchors.First(anchor => anchor.AnchorId == recoverExistingAnchorId.Value);
        }

        var completed = await CompleteEmbeddingStageAsync(
            databasePath,
            stagedAnchor ?? throw new InvalidOperationException("Reference anchor embedding stage was not initialized."),
            stagedSegmentCount,
            stagedMaterialCount,
            stagedSlotCount,
            stagedMaterials,
            embeddingOptions ?? throw new InvalidOperationException("Reference anchor embedding options were not initialized."),
            stagedAffectedIds,
            cancellationToken);
        return completed.Anchor;
        }
        finally
        {
            importLock.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ReferenceAnchorPayload>> CreateAnchorsAsync(
        CreateReferenceAnchorsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateCreateAnchorsInput(input);

        var anchors = new List<ReferenceAnchorPayload>(input.Anchors.Count);
        foreach (var anchor in input.Anchors)
        {
            anchors.Add(await CreateAnchorAsync(anchor, cancellationToken));
        }

        return anchors;
    }

    public async ValueTask<CreateReferenceAnchorsResultPayload> CreateAnchorsWithResultAsync(
        CreateReferenceAnchorsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateCreateAnchorsInput(input);

        var succeeded = new List<ReferenceAnchorPayload>(input.Anchors.Count);
        var failed = new List<CreateReferenceAnchorFailurePayload>();
        for (var index = 0; index < input.Anchors.Count; index++)
        {
            var anchor = input.Anchors[index];
            try
            {
                var created = await CreateAnchorAsync(anchor, cancellationToken);
                if (IsFailedBuildStatus(created.Status) ||
                    string.Equals(created.Status, ReferenceAnchorBuildStates.Cancelled, StringComparison.OrdinalIgnoreCase))
                {
                    var status = await GetBuildStatusAsync(anchor.NovelId, created.AnchorId, cancellationToken);
                    failed.Add(CreateFailurePayload(index, anchor, status?.LastError ?? created.Status));
                }
                else
                {
                    succeeded.Add(created);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed.Add(CreateFailurePayload(index, anchor, exception));
            }
        }

        return new CreateReferenceAnchorsResultPayload(
            succeeded,
            failed,
            input.Anchors.Count,
            succeeded.Count,
            failed.Count);
    }

    public async ValueTask ReconcileRecoverableProcessingAsync(CancellationToken cancellationToken)
    {
        var databasePath = await DatabasePathAsync(cancellationToken);
        if (!File.Exists(databasePath))
        {
            return;
        }

        IReadOnlyList<ReferenceAnchorPayload> recoverableAnchors;
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            recoverableAnchors = await ReadRecoverableAnchorsAsync(connection, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }

        foreach (var anchor in recoverableAnchors)
        {
            await RebuildAnchorCoreAsync(anchor.NovelId, anchor.AnchorId, cancellationToken);
        }
    }

    private static async ValueTask<ReferenceAnchorPayload?> FindExistingAnchorForImportAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? storedNovelId,
        string visibility,
        string sourcePath,
        string sourceKind,
        string sourceFileHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT anchor_id, novel_id, title, author, source_path, source_kind, license_status,
                   source_file_hash, build_version, status, created_at, updated_at,
                   corpus_visibility, source_trust, user_tags_json
            FROM reference_anchors
            WHERE corpus_visibility = $corpus_visibility
              AND source_kind = $source_kind
              AND (
                    ($novel_id_is_null = 1 AND (novel_id IS NULL OR novel_id = $workspace_corpus_novel_id)) OR
                    ($novel_id_is_null = 0 AND novel_id = $novel_id)
                  )
              AND source_path = $source_path
              AND source_file_hash = $source_file_hash
            ORDER BY created_at ASC,
                     anchor_id ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$corpus_visibility", visibility);
        command.Parameters.AddWithValue("$source_kind", sourceKind);
        command.Parameters.AddWithValue("$novel_id_is_null", storedNovelId.HasValue ? 0 : 1);
        command.Parameters.AddWithValue("$novel_id", storedNovelId.HasValue ? storedNovelId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        command.Parameters.AddWithValue("$source_path", sourcePath);
        command.Parameters.AddWithValue("$source_file_hash", sourceFileHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAnchor(reader) : null;
    }

    private async ValueTask<ReferenceAnchorPayload> RecordInitialFailedImportAsync(
        string databasePath,
        long? storedNovelId,
        string title,
        string author,
        string sourcePath,
        string sourceKind,
        string licenseStatus,
        string visibility,
        string sourceTrust,
        IReadOnlyList<string> userTags,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var lastError = RedactError(exception.Message);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var anchor = await FindExistingInitialFailedImportForSourcePathAsync(
                connection,
                transaction,
                storedNovelId,
                visibility,
                sourcePath,
                sourceKind,
                cancellationToken);

            if (anchor is null)
            {
                try
                {
                    anchor = await InsertAnchorAsync(
                        connection,
                        transaction,
                        storedNovelId,
                        title,
                        author,
                        sourcePath,
                        sourceKind,
                        licenseStatus,
                        visibility,
                        sourceTrust,
                        userTags,
                        BuildUnavailableSourceHash(storedNovelId, visibility, sourcePath, sourceKind),
                        now,
                        cancellationToken);
                }
                catch (SqliteException insertException) when (IsSqliteConstraintViolation(insertException))
                {
                    anchor = await FindExistingInitialFailedImportForSourcePathAsync(
                        connection,
                        transaction,
                        storedNovelId,
                        visibility,
                        sourcePath,
                        sourceKind,
                        cancellationToken);
                    if (anchor is null)
                    {
                        throw;
                    }
                }
            }

            var failedAnchor = anchor with
            {
                Status = ReferenceAnchorBuildStates.FailedImport,
                UpdatedAt = now
            };
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
                cancellationToken,
                vectorCount: 0);
            await transaction.CommitAsync(cancellationToken);
            return failedAnchor;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static async ValueTask<ReferenceAnchorPayload?> FindExistingInitialFailedImportForSourcePathAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? storedNovelId,
        string visibility,
        string sourcePath,
        string sourceKind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT a.anchor_id, a.novel_id, a.title, a.author, a.source_path, a.source_kind,
                   a.license_status, a.source_file_hash, a.build_version,
                   COALESCE(s.status, a.status) AS status,
                   a.created_at, a.updated_at, a.corpus_visibility, a.source_trust,
                   a.user_tags_json
            FROM reference_anchors a
            LEFT JOIN reference_anchor_build_state s ON s.anchor_id = a.anchor_id
            WHERE a.corpus_visibility = $corpus_visibility
              AND a.source_kind = $source_kind
              AND (
                    ($novel_id_is_null = 1 AND (a.novel_id IS NULL OR a.novel_id = $workspace_corpus_novel_id)) OR
                    ($novel_id_is_null = 0 AND a.novel_id = $novel_id)
                  )
              AND a.source_path = $source_path
              AND a.source_file_hash LIKE 'unavailable:%'
              AND COALESCE(s.status, a.status) = $status
            ORDER BY a.created_at ASC,
                     a.anchor_id ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$corpus_visibility", visibility);
        command.Parameters.AddWithValue("$source_kind", sourceKind);
        command.Parameters.AddWithValue("$novel_id_is_null", storedNovelId.HasValue ? 0 : 1);
        command.Parameters.AddWithValue("$novel_id", storedNovelId.HasValue ? storedNovelId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        command.Parameters.AddWithValue("$source_path", sourcePath);
        command.Parameters.AddWithValue("$status", ReferenceAnchorBuildStates.FailedImport);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAnchor(reader) : null;
    }

    private static async ValueTask<IReadOnlyList<ReferenceAnchorPayload>> ReadRecoverableAnchorsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.anchor_id, a.novel_id, a.title, a.author, a.source_path, a.source_kind,
                   a.license_status, a.source_file_hash, a.build_version,
                   COALESCE(s.status, a.status) AS status,
                   a.created_at, a.updated_at, a.corpus_visibility, a.source_trust,
                   a.user_tags_json
            FROM reference_anchors a
            LEFT JOIN reference_anchor_build_state s ON s.anchor_id = a.anchor_id
            ORDER BY a.updated_at ASC, a.anchor_id ASC;
            """;

        var anchors = new List<ReferenceAnchorPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var anchor = ReadAnchor(reader);
            if (IsRecoverableBuildStatus(anchor.Status))
            {
                anchors.Add(anchor);
            }
        }

        return anchors;
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
            command.CommandText = $$"""
                SELECT anchor_id, novel_id, title, author, source_path, source_kind, license_status,
                       source_file_hash, build_version, status, created_at, updated_at,
                       corpus_visibility, source_trust, user_tags_json
                FROM reference_anchors
                WHERE {{NovelOrVisibleWorkspaceCorpusPredicate}}
                ORDER BY CASE WHEN novel_id = $novel_id THEN 0 ELSE 1 END,
                         created_at ASC, anchor_id ASC;
                """;
            command.Parameters.AddWithValue("$novel_id", novelId);
            command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
            command.Parameters.AddWithValue("$workspace_corpus_visibility", ReferenceCorpusVisibilities.Workspace);
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
        return await RebuildAnchorCoreAsync(novelId, anchorId, cancellationToken);
    }

    private async ValueTask<ReferenceAnchorBuildStatusPayload> RebuildAnchorCoreAsync(
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        var databasePath = await DatabasePathAsync(cancellationToken);
        ReferenceAnchorPayload? stagedAnchor = null;
        IReadOnlyList<ReferenceMaterialPayload> stagedMaterials = [];
        EmbeddingRequestOptions? stagedEmbeddingOptions = null;
        ReferenceProcessingAffectedIds stagedAffectedIds = ReferenceProcessingAffectedIds.Empty;
        var stagedSegmentCount = 0;
        var stagedMaterialCount = 0;
        var stagedSlotCount = 0;
        ReferenceAnchorPayload? previousAnchor = null;
        ReferenceAnchorCorpusSnapshot? previousCorpus = null;
        ReferenceAnchorBuildStatusPayload? previousBuildStatus = null;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var anchor = await ReadAnchorAsync(connection, novelId, anchorId, cancellationToken);
            previousAnchor = anchor;
            var now = DateTimeOffset.UtcNow;

            try
            {
                previousBuildStatus = await ReadBuildStatusAsync(connection, novelId, anchor.AnchorId, cancellationToken);
                var previousSegments = await ReadSourceSegmentsAsync(connection, anchor.AnchorId, cancellationToken);
                var previousMaterials = await ReadMaterialsAsync(
                    connection,
                    novelId,
                    [anchor.AnchorId],
                    ReferenceMaterialArchiveFilters.All,
                    cancellationToken);
                var previousSlots = await ReadMaterialSlotsForAnchorAsync(connection, anchor.AnchorId, cancellationToken);
                var userVerifiedMaterials = await ReadUserVerifiedMaterialsAsync(connection, anchor.AnchorId, cancellationToken);
                var archivedMaterialMarkers = await ReadArchivedMaterialMarkersAsync(connection, anchor.AnchorId, cancellationToken);
                previousCorpus = new ReferenceAnchorCorpusSnapshot(
                    previousSegments,
                    previousMaterials,
                    previousSlots,
                    BuildArchivedMaterialTimestamps(previousMaterials, archivedMaterialMarkers));
                var source = await ReadSourceFileAsync(anchor.SourcePath, cancellationToken);
                IReadOnlyList<ReferenceSourceSegment> segments;
                try
                {
                    _stageProbe.BeforeStage(ReferenceAnchorBuildStates.Segmenting, anchor.AnchorId, source.Hash);
                    segments = BuildSegments(anchor.AnchorId, source.Text);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var failedAt = DateTimeOffset.UtcNow;
                    var retainedSegmentCount = previousCorpus.Segments.Count;
                    var retainedMaterialCount = previousCorpus.Materials.Count;
                    var retainedSlotCount = previousBuildStatus?.SlotCount ?? CountMaterialSlots(previousCorpus.SlotsByMaterial);
                    var retainedVectorCount = previousBuildStatus?.VectorCount ?? 0;
                    var failedAnchor = anchor with
                    {
                        SourceFileHash = previousAnchor.SourceFileHash,
                        Status = ReferenceAnchorBuildStates.FailedSegmenting,
                        UpdatedAt = failedAt
                    };
                    var lastError = RedactError(exception.Message);
                    await using var failureTransaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                    await UpdateAnchorBuildResultAsync(
                        connection,
                        failureTransaction,
                        failedAnchor,
                        ReferenceAnchorBuildStates.FailedSegmenting,
                        ReferenceAnchorBuildStates.FailedSegmenting,
                        retainedSegmentCount,
                        retainedMaterialCount,
                        retainedSlotCount,
                        lastError,
                        failedAt,
                        cancellationToken,
                        vectorCount: retainedVectorCount,
                        affectedIds: BuildAffectedProcessingIds(previousCorpus));
                    await failureTransaction.CommitAsync(cancellationToken);
                    return BuildStatus(
                        failedAnchor,
                        ReferenceAnchorBuildStates.FailedSegmenting,
                        ReferenceAnchorBuildStates.FailedSegmenting,
                        retainedSegmentCount,
                        retainedMaterialCount,
                        retainedSlotCount,
                        lastError,
                        failedAt,
                        retainedVectorCount);
                }

                var hasPreviousMaterials = previousCorpus.Materials.Count > 0;
                var segmentAffectedIds = BuildAffectedProcessingIds(segments);
                if (!hasPreviousMaterials)
                {
                    try
                    {
                        await using var segmentTransaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                        var segmentsBuiltAnchor = anchor with
                        {
                            SourceFileHash = source.Hash,
                            Status = ReferenceAnchorBuildStates.SegmentsBuilt,
                            UpdatedAt = now
                        };
                        await ReplaceSegmentsAsync(connection, segmentTransaction, anchor.AnchorId, segments, cancellationToken);
                        await UpdateAnchorBuildResultAsync(
                            connection,
                            segmentTransaction,
                            segmentsBuiltAnchor,
                            ReferenceAnchorBuildStates.SegmentsBuilt,
                            ReferenceAnchorBuildStates.SegmentsBuilt,
                            segments.Count,
                            0,
                            0,
                            string.Empty,
                            now,
                            cancellationToken,
                            affectedIds: segmentAffectedIds);
                        await segmentTransaction.CommitAsync(cancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        var failedAt = DateTimeOffset.UtcNow;
                        var failedAnchor = anchor with
                        {
                            SourceFileHash = previousAnchor.SourceFileHash,
                            Status = ReferenceAnchorBuildStates.FailedSegmenting,
                            UpdatedAt = failedAt
                        };
                        var lastError = RedactError(exception.Message);
                        await using var failureTransaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                        await UpdateAnchorBuildResultAsync(
                            connection,
                            failureTransaction,
                            failedAnchor,
                            ReferenceAnchorBuildStates.FailedSegmenting,
                            ReferenceAnchorBuildStates.FailedSegmenting,
                            previousCorpus.Segments.Count,
                            previousCorpus.Materials.Count,
                            previousBuildStatus?.SlotCount ?? CountMaterialSlots(previousCorpus.SlotsByMaterial),
                            lastError,
                            failedAt,
                            cancellationToken,
                            vectorCount: previousBuildStatus?.VectorCount ?? 0,
                            affectedIds: BuildAffectedProcessingIds(previousCorpus));
                        await failureTransaction.CommitAsync(cancellationToken);
                        return BuildStatus(
                            failedAnchor,
                            ReferenceAnchorBuildStates.FailedSegmenting,
                            ReferenceAnchorBuildStates.FailedSegmenting,
                            previousCorpus.Segments.Count,
                            previousCorpus.Materials.Count,
                            previousBuildStatus?.SlotCount ?? CountMaterialSlots(previousCorpus.SlotsByMaterial),
                            lastError,
                            failedAt,
                            previousBuildStatus?.VectorCount ?? 0);
                    }
                }

                IReadOnlyList<ReferenceMaterialPayload> materials;
                IReadOnlyList<ReferenceMaterialPayload> activeMaterials;
                ReferenceProcessingAffectedIds materialAffectedIds;
                EmbeddingRequestOptions? embeddingOptions;
                try
                {
                    _stageProbe.BeforeStage(ReferenceAnchorBuildStates.ExtractingMaterials, anchor.AnchorId, source.Hash);
                    materials = ApplyUserVerifiedTagOverrides(
                        BuildMaterials(anchor.AnchorId, segments, now),
                        userVerifiedMaterials);
                    var archivedMaterialTimestamps = BuildArchivedMaterialTimestamps(materials, archivedMaterialMarkers);
                    activeMaterials = materials
                        .Where(material => !archivedMaterialTimestamps.ContainsKey(material.MaterialId))
                        .ToArray();
                    materialAffectedIds = BuildAffectedProcessingIds(activeMaterials.Count > 0 ? activeMaterials : materials);
                    embeddingOptions = await _embeddingConfiguration.GetActiveEmbeddingOptionsAsync(cancellationToken);
                    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                    var materialExtractedAnchor = anchor with
                    {
                        SourceFileHash = source.Hash,
                        Status = ReferenceAnchorBuildStates.MaterialsExtracted,
                        UpdatedAt = now
                    };
                    await ReplaceSegmentsAsync(connection, transaction, anchor.AnchorId, segments, cancellationToken);
                    await ReplaceMaterialRowsAsync(
                        connection,
                        transaction,
                        anchor.AnchorId,
                        materials,
                        cancellationToken,
                        archivedMaterialTimestamps);
                    await UpsertStage1DeterministicObservationsAsync(
                        connection,
                        transaction,
                        anchor.AnchorId,
                        source.Hash,
                        segments,
                        now,
                        cancellationToken);
                    await UpdateAnchorBuildResultAsync(
                        connection,
                        transaction,
                        materialExtractedAnchor,
                        ReferenceAnchorBuildStates.MaterialsExtracted,
                        ReferenceAnchorBuildStates.MaterialsExtracted,
                        segments.Count,
                        materials.Count,
                        0,
                        string.Empty,
                        now,
                        cancellationToken,
                        affectedIds: materialAffectedIds);
                    await transaction.CommitAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var failedAt = DateTimeOffset.UtcNow;
                    var retainedSegmentCount = hasPreviousMaterials ? previousCorpus.Segments.Count : segments.Count;
                    var retainedMaterialCount = hasPreviousMaterials ? previousCorpus.Materials.Count : 0;
                    var retainedSlotCount = hasPreviousMaterials
                        ? previousBuildStatus?.SlotCount ?? CountMaterialSlots(previousCorpus.SlotsByMaterial)
                        : 0;
                    var retainedVectorCount = hasPreviousMaterials ? previousBuildStatus?.VectorCount ?? 0 : 0;
                    var affectedIds = hasPreviousMaterials
                        ? BuildAffectedProcessingIds(previousCorpus)
                        : segmentAffectedIds;
                    var failedAnchor = anchor with
                    {
                        SourceFileHash = hasPreviousMaterials ? previousAnchor.SourceFileHash : source.Hash,
                        Status = ReferenceAnchorBuildStates.FailedExtraction,
                        UpdatedAt = failedAt
                    };
                    var lastError = RedactError(exception.Message);
                    await using var failureTransaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                    await UpdateAnchorBuildResultAsync(
                        connection,
                        failureTransaction,
                        failedAnchor,
                        ReferenceAnchorBuildStates.FailedExtraction,
                        ReferenceAnchorBuildStates.FailedExtraction,
                        retainedSegmentCount,
                        retainedMaterialCount,
                        retainedSlotCount,
                        lastError,
                        failedAt,
                        cancellationToken,
                        vectorCount: retainedVectorCount,
                        affectedIds: affectedIds);
                    await failureTransaction.CommitAsync(cancellationToken);
                    return BuildStatus(
                        failedAnchor,
                        ReferenceAnchorBuildStates.FailedExtraction,
                        ReferenceAnchorBuildStates.FailedExtraction,
                        retainedSegmentCount,
                        retainedMaterialCount,
                        retainedSlotCount,
                        lastError,
                        failedAt,
                        retainedVectorCount);
                }

                try
                {
                    var slotsByMaterial = DetectMaterialSlots(materials);
                    var slotCount = CountMaterialSlots(slotsByMaterial);
                    await using var slotTransaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                    await ReplaceMaterialSlotsAsync(
                        connection,
                        slotTransaction,
                        anchor.AnchorId,
                        slotsByMaterial,
                        BuildMaterialCreatedAt(materials),
                        cancellationToken);
                    var initialStatus = embeddingOptions is not null && activeMaterials.Count > 0
                        ? ReferenceAnchorBuildStates.Embedding
                        : ReferenceAnchorBuildStates.Ready;
                    var initialStage = embeddingOptions is not null && activeMaterials.Count > 0
                        ? ReferenceAnchorBuildStates.Embedding
                        : "ready";
                    var readyAnchor = anchor with
                    {
                        SourceFileHash = source.Hash,
                        Status = initialStatus,
                        UpdatedAt = now
                    };
                    var affectedIds = BuildAffectedProcessingIds(activeMaterials, materials, slotsByMaterial);
                    await UpdateAnchorBuildResultAsync(
                        connection,
                        slotTransaction,
                        readyAnchor,
                        initialStatus,
                        initialStage,
                        segments.Count,
                        materials.Count,
                        slotCount,
                        lastError: string.Empty,
                        now,
                        cancellationToken,
                        affectedIds: affectedIds);
                    await slotTransaction.CommitAsync(cancellationToken);
                    if (embeddingOptions is null || activeMaterials.Count == 0)
                    {
                        return BuildStatus(readyAnchor, ReferenceAnchorBuildStates.Ready, "ready", segments.Count, materials.Count, slotCount, string.Empty, now);
                    }

                    stagedAnchor = readyAnchor;
                    stagedMaterials = activeMaterials;
                    stagedEmbeddingOptions = embeddingOptions;
                    stagedSegmentCount = segments.Count;
                    stagedMaterialCount = materials.Count;
                    stagedSlotCount = slotCount;
                    stagedAffectedIds = affectedIds;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var failedAt = DateTimeOffset.UtcNow;
                    var retainedSegmentCount = hasPreviousMaterials ? previousCorpus.Segments.Count : segments.Count;
                    var retainedMaterialCount = hasPreviousMaterials ? previousCorpus.Materials.Count : materials.Count;
                    var retainedSlotCount = hasPreviousMaterials
                        ? previousBuildStatus?.SlotCount ?? CountMaterialSlots(previousCorpus.SlotsByMaterial)
                        : 0;
                    var retainedVectorCount = hasPreviousMaterials ? previousBuildStatus?.VectorCount ?? 0 : 0;
                    var affectedIds = hasPreviousMaterials
                        ? BuildAffectedProcessingIds(previousCorpus)
                        : materialAffectedIds;
                    var failedAnchor = anchor with
                    {
                        SourceFileHash = hasPreviousMaterials ? previousAnchor.SourceFileHash : source.Hash,
                        Status = ReferenceAnchorBuildStates.FailedSlotting,
                        UpdatedAt = failedAt
                    };
                    await using var failureTransaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                    if (hasPreviousMaterials)
                    {
                        await ReplaceSegmentsAsync(
                            connection,
                            failureTransaction,
                            anchor.AnchorId,
                            previousCorpus.Segments,
                            cancellationToken);
                        await ReplaceMaterialRowsAsync(
                            connection,
                            failureTransaction,
                            anchor.AnchorId,
                            previousCorpus.Materials,
                            cancellationToken,
                            previousCorpus.ArchivedMaterialTimestamps);
                        await ReplaceMaterialSlotsAsync(
                            connection,
                            failureTransaction,
                            anchor.AnchorId,
                            previousCorpus.SlotsByMaterial,
                            BuildMaterialCreatedAt(previousCorpus.Materials),
                            cancellationToken);
                    }

                    var lastError = RedactError(exception.Message);
                    await UpdateAnchorBuildResultAsync(
                        connection,
                        failureTransaction,
                        failedAnchor,
                        ReferenceAnchorBuildStates.FailedSlotting,
                        ReferenceAnchorBuildStates.FailedSlotting,
                        retainedSegmentCount,
                        retainedMaterialCount,
                        retainedSlotCount,
                        lastError,
                        failedAt,
                        cancellationToken,
                        vectorCount: retainedVectorCount,
                        affectedIds: affectedIds);
                    await failureTransaction.CommitAsync(cancellationToken);
                    return BuildStatus(
                        failedAnchor,
                        ReferenceAnchorBuildStates.FailedSlotting,
                        ReferenceAnchorBuildStates.FailedSlotting,
                        retainedSegmentCount,
                        retainedMaterialCount,
                        retainedSlotCount,
                        lastError,
                        failedAt,
                        retainedVectorCount);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                var retainedSegmentCount = previousCorpus?.Segments.Count ?? 0;
                var retainedMaterialCount = previousCorpus?.Materials.Count ?? 0;
                var retainedSlotCount = previousBuildStatus?.SlotCount ?? 0;
                var retainedVectorCount = previousBuildStatus?.VectorCount ?? 0;
                var affectedIds = previousCorpus is null
                    ? ReferenceProcessingAffectedIds.Empty
                    : BuildAffectedProcessingIds(previousCorpus);
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
                    retainedSegmentCount,
                    retainedMaterialCount,
                    retainedSlotCount,
                    lastError,
                    now,
                    cancellationToken,
                    retainedVectorCount,
                    affectedIds);
                await transaction.CommitAsync(cancellationToken);
                return BuildStatus(
                    failedAnchor,
                    ReferenceAnchorBuildStates.FailedImport,
                    ReferenceAnchorBuildStates.FailedImport,
                    retainedSegmentCount,
                    retainedMaterialCount,
                    retainedSlotCount,
                    lastError,
                    now,
                    retainedVectorCount);
            }
        }
        finally
        {
            _mutex.Release();
        }

        var completed = await CompleteEmbeddingStageAsync(
            databasePath,
            stagedAnchor ?? throw new InvalidOperationException("Reference anchor rebuild embedding stage was not initialized."),
            stagedSegmentCount,
            stagedMaterialCount,
            stagedSlotCount,
            stagedMaterials,
            stagedEmbeddingOptions ?? throw new InvalidOperationException("Reference anchor rebuild embedding options were not initialized."),
            stagedAffectedIds,
            cancellationToken);
        if (completed.Anchor.Status == ReferenceAnchorBuildStates.FailedEmbedding &&
            previousCorpus is not null &&
            previousCorpus.Materials.Count > 0)
        {
            return await RestorePreviousCorpusAfterFailedRebuildEmbeddingAsync(
                databasePath,
                previousAnchor ?? throw new InvalidOperationException("Reference anchor rebuild previous state was not initialized."),
                previousCorpus,
                completed.LastError,
                completed.Anchor.UpdatedAt,
                CancellationToken.None);
        }

        return BuildStatus(
            completed.Anchor,
            completed.Anchor.Status,
            completed.Stage,
            stagedSegmentCount,
            stagedMaterialCount,
            stagedSlotCount,
            completed.LastError,
            completed.Anchor.UpdatedAt,
            completed.VectorCount);
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
                WHERE (a.novel_id = $novel_id OR
                       ((a.novel_id IS NULL OR a.novel_id = $workspace_corpus_novel_id) AND a.corpus_visibility = $workspace_corpus_visibility))
                  AND s.anchor_id = $anchor_id;
                """;
            command.Parameters.AddWithValue("$novel_id", novelId);
            command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
            command.Parameters.AddWithValue("$workspace_corpus_visibility", ReferenceCorpusVisibilities.Workspace);
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
        var archiveFilter = string.IsNullOrWhiteSpace(input.ArchiveFilter)
            ? ReferenceMaterialArchiveFilters.Active
            : ValidateAllowedText(input.ArchiveFilter, nameof(input.ArchiveFilter), AllowedMaterialArchiveFilters);
        var styleOptions = NormalizeStyleSearchOptions(input);
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

            var all = await ReadMaterialsAsync(connection, input.NovelId, anchorIds, archiveFilter, cancellationToken);
            var styleContext = await ReadStyleSearchContextAsync(
                connection,
                input.NovelId,
                anchorIds,
                styleOptions,
                cancellationToken);
            var unknownLicenseAnchorIds = await ReadUnknownLicenseAnchorIdsAsync(connection, input.NovelId, anchorIds, cancellationToken);
            var acceptedFeedbackMaterialIds = await ReadAcceptedFeedbackMaterialIdsAsync(
                connection,
                input.NovelId,
                cancellationToken);
            var embeddingScores = await TryBuildEmbeddingScoresAsync(
                databasePath,
                connection,
                input,
                anchorIds,
                all.Count,
                cancellationToken);
            var filtered = all
                .Where(item => MatchesMaterialFilters(item, input))
                .Select(item => new ScoredSearchMaterial(
                    item,
                    ScoreMaterialComponents(
                        item,
                        input,
                        embeddingScores.TryGetValue(item.MaterialId, out var embeddingScore) ? embeddingScore : 0,
                        acceptedFeedbackMaterialIds.Contains(item.MaterialId),
                        styleContext.FitScores.TryGetValue(item.MaterialId, out var styleFitScore) ? styleFitScore : 0,
                        styleContext.SourceAnchorIds.Contains(item.AnchorId) ? styleContext.SourceRiskPenalty : 0)))
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Material.AnchorId)
                .ThenBy(item => item.Material.MaterialId, StringComparer.Ordinal)
                .ToArray();
            var total = filtered.LongLength;
            var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)size);
            var items = filtered
                .Skip((page - 1) * size)
                .Take(size)
                .Select(item =>
                {
                    var material = item.Material with { ScoreComponents = item.ScoreComponents };
                    return unknownLicenseAnchorIds.Contains(material.AnchorId)
                        ? material with { Text = TruncateUnknownLicensePreview(material.Text) }
                        : material;
                })
                .ToArray();
            return new PageResultPayload<ReferenceMaterialPayload>(items, total, page, size, totalPages);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<PageResultPayload<ReferenceMaterialTagReviewItemPayload>> GetMaterialTagReviewQueueAsync(
        GetReferenceMaterialTagReviewQueuePayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        var page = Math.Max(1, input.Page);
        var size = Math.Clamp(input.Size, 1, 100);
        var archiveFilter = string.IsNullOrWhiteSpace(input.ArchiveFilter)
            ? ReferenceMaterialArchiveFilters.Active
            : ValidateAllowedText(input.ArchiveFilter, nameof(input.ArchiveFilter), AllowedMaterialArchiveFilters);
        var requestedAnchorIds = input.AnchorIds?.Where(id => id > 0).Distinct().ToArray() ?? [];
        if (requestedAnchorIds.Length > MaxExplicitAnchorFilterIds)
        {
            throw new ArgumentException(
                $"At most {MaxExplicitAnchorFilterIds} anchor ids can be supplied for material tag review.",
                nameof(input));
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var anchorIds = requestedAnchorIds;
            if (anchorIds.Length == 0)
            {
                anchorIds = await GetAnchorIdsAsync(connection, input.NovelId, cancellationToken);
            }

            if (anchorIds.Length == 0)
            {
                return new PageResultPayload<ReferenceMaterialTagReviewItemPayload>([], 0, page, size, 0);
            }

            return await ReadMaterialTagReviewQueueItemsAsync(
                connection,
                input.NovelId,
                anchorIds,
                archiveFilter,
                page,
                size,
                cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceMaterialDetailPayload?> GetMaterialDetailAsync(
        GetReferenceMaterialDetailPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        var materialId = NormalizeRequiredText(input.MaterialId, nameof(input.MaterialId), maxLength: 256);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var row = await ReadMaterialDetailRowAsync(connection, input.NovelId, materialId, cancellationToken);
            if (row is null)
            {
                return null;
            }

            var previewMaxChars = IsUnknownLicense(row.Anchor.LicenseStatus)
                ? UnknownLicensePreviewMaxChars
                : MaterialDetailTextPreviewMaxChars;
            var segmentPreviewMaxChars = IsUnknownLicense(row.Anchor.LicenseStatus)
                ? UnknownLicensePreviewMaxChars
                : MaterialDetailSegmentPreviewMaxChars;
            var segments = await ReadMaterialDetailSegmentsAsync(
                connection,
                row.Material.AnchorId,
                row.Material.SourceSegmentId,
                segmentPreviewMaxChars,
                cancellationToken);
            var slots = await ReadMaterialDetailSlotsAsync(connection, materialId, cancellationToken);
            var notes = await ReadMaterialDetailProcessingNotesAsync(
                connection,
                input.NovelId,
                row.Material.AnchorId,
                cancellationToken);

            return new ReferenceMaterialDetailPayload(
                ToMaterialSummary(row.Material, previewMaxChars, row.ArchivedAt),
                ToSourceSummary(row.Anchor),
                segments,
                slots,
                notes);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceSourceSegmentDetailPayload?> GetSourceSegmentDetailAsync(
        GetReferenceSourceSegmentDetailPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        ValidateAnchorId(input.AnchorId);
        var segmentId = NormalizeRequiredText(input.SegmentId, nameof(input.SegmentId), maxLength: 256);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var anchor = await TryReadAnchorAsync(connection, input.NovelId, input.AnchorId, cancellationToken);
            if (anchor is null)
            {
                return null;
            }

            var segmentPreviewMaxChars = IsUnknownLicense(anchor.LicenseStatus)
                ? UnknownLicensePreviewMaxChars
                : MaterialDetailSegmentPreviewMaxChars;
            var segment = await ReadSourceSegmentPreviewAsync(
                connection,
                anchor.AnchorId,
                segmentId,
                segmentPreviewMaxChars,
                cancellationToken);
            if (segment is null)
            {
                return null;
            }

            var notes = await ReadMaterialDetailProcessingNotesAsync(
                connection,
                input.NovelId,
                anchor.AnchorId,
                cancellationToken);

            return new ReferenceSourceSegmentDetailPayload(
                ToSourceSummary(anchor),
                segment,
                notes);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceSourceProcessingDetailPayload?> GetSourceProcessingDetailAsync(
        GetReferenceSourceProcessingDetailPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        ValidateAnchorId(input.AnchorId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var anchor = await TryReadAnchorAsync(connection, input.NovelId, input.AnchorId, cancellationToken);
            if (anchor is null)
            {
                return null;
            }

            var currentStatus = await ReadBuildStatusAsync(connection, input.NovelId, input.AnchorId, cancellationToken);
            var events = await ReadSourceProcessingEventsAsync(connection, input.AnchorId, cancellationToken);
            if (events.Count == 0 && currentStatus is not null)
            {
                events =
                [
                    BuildSourceProcessingEventFromStatus(
                        "current",
                        input.AnchorId,
                        currentStatus)
                ];
            }

            var attempts = await ReadSourceProcessingAttemptsAsync(connection, input.AnchorId, cancellationToken);
            if (attempts.Count == 0)
            {
                attempts = BuildSourceProcessingAttempts(input.AnchorId, anchor.BuildVersion, currentStatus, events);
            }

            var currentAttempt = attempts.Count == 0 ? null : attempts[^1];
            var priorAttempts = attempts.Count <= 1
                ? Array.Empty<ReferenceSourceProcessingAttemptPayload>()
                : attempts.Take(attempts.Count - 1).Reverse().ToArray();

            return new ReferenceSourceProcessingDetailPayload(
                ToSourceSummary(anchor),
                currentStatus is null ? null : ToSourceProcessingStatus(currentStatus),
                events,
                RetryAvailable: currentStatus is not null && IsFailedBuildStatus(currentStatus.Status),
                RebuildAvailable: true,
                AttemptCount: attempts.Count,
                CurrentAttempt: currentAttempt,
                PriorAttempts: priorAttempts,
                RecoveredFromAttemptId: currentAttempt?.RecoveredFromAttemptId ?? string.Empty,
                RecoveredFromBuildId: currentAttempt?.RecoveredFromBuildId ?? string.Empty,
                BlockedReason: currentAttempt?.BlockedReason ?? string.Empty);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceMaterialPayload> UpdateMaterialTagsAsync(
        UpdateReferenceMaterialTagsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        var materialId = NormalizeRequiredText(input.MaterialId, nameof(input.MaterialId), maxLength: 256);
        var functionTag = NormalizeOptionalText(input.FunctionTag, nameof(input.FunctionTag), maxLength: 128);
        var emotionTag = NormalizeOptionalText(input.EmotionTag, nameof(input.EmotionTag), maxLength: 128);
        var sceneTag = NormalizeOptionalText(input.SceneTag, nameof(input.SceneTag), maxLength: 128);
        var povTag = NormalizeOptionalText(input.PovTag, nameof(input.PovTag), maxLength: 128);
        var techniqueTag = NormalizeOptionalText(input.TechniqueTag, nameof(input.TechniqueTag), maxLength: 128);
        _ = NormalizeOptionalText(input.Origin, nameof(input.Origin), maxLength: 128);
        _ = NormalizeOptionalText(input.Note, nameof(input.Note), maxLength: 2_000);
        EnsureMaterialTagOverride(functionTag, emotionTag, sceneTag, povTag, techniqueTag, nameof(input));

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var material = await ReadMaterialAsync(connection, input.NovelId, materialId, cancellationToken)
                ?? throw new ArgumentException("Reference material does not exist.", nameof(input));

            var updated = ApplyMaterialTagOverride(
                material,
                functionTag,
                emotionTag,
                sceneTag,
                povTag,
                techniqueTag);
            await UpdateMaterialTagsAsync(connection, transaction, input.NovelId, updated, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return updated;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ReferenceMaterialPayload>> UpdateMaterialsTagsAsync(
        UpdateReferenceMaterialsTagsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);
        if (input.MaterialIds.Count == 0)
        {
            throw new ArgumentException("At least one reference material must be selected.", nameof(input));
        }

        var materialIds = input.MaterialIds
            .Select(materialId => NormalizeRequiredText(materialId, nameof(input.MaterialIds), maxLength: 256))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var functionTag = NormalizeOptionalText(input.FunctionTag, nameof(input.FunctionTag), maxLength: 128);
        var emotionTag = NormalizeOptionalText(input.EmotionTag, nameof(input.EmotionTag), maxLength: 128);
        var sceneTag = NormalizeOptionalText(input.SceneTag, nameof(input.SceneTag), maxLength: 128);
        var povTag = NormalizeOptionalText(input.PovTag, nameof(input.PovTag), maxLength: 128);
        var techniqueTag = NormalizeOptionalText(input.TechniqueTag, nameof(input.TechniqueTag), maxLength: 128);
        _ = NormalizeOptionalText(input.Origin, nameof(input.Origin), maxLength: 128);
        _ = NormalizeOptionalText(input.Note, nameof(input.Note), maxLength: 2_000);
        EnsureMaterialTagOverride(functionTag, emotionTag, sceneTag, povTag, techniqueTag, nameof(input));

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var updated = new List<ReferenceMaterialPayload>(materialIds.Length);

            foreach (var materialId in materialIds)
            {
                var material = await ReadMaterialAsync(connection, input.NovelId, materialId, cancellationToken)
                    ?? throw new ArgumentException("Reference material does not exist.", nameof(input));
                var corrected = ApplyMaterialTagOverride(
                    material,
                    functionTag,
                    emotionTag,
                    sceneTag,
                    povTag,
                    techniqueTag);
                await UpdateMaterialTagsAsync(connection, transaction, input.NovelId, corrected, cancellationToken);
                updated.Add(corrected);
            }

            await transaction.CommitAsync(cancellationToken);
            return updated;
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
            var nonSlotEdits = ReferenceNonSlotEditReporter.Report(
                material.Text,
                adapted.Text,
                adapted.ChangedSlots);
            var audit = BuildReuseAudit(
                material,
                adapted.Text,
                input.MaxRewriteLevel,
                input.SceneFacts,
                rewriteLevel,
                nonSlotEdits,
                DateTimeOffset.UtcNow);
            var candidateId = "candidate-" + Guid.NewGuid().ToString("N");
            var result = new AdaptReferenceMaterialResultPayload(
                candidateId,
                material.MaterialId,
                rewriteLevel,
                adapted.Text,
                adapted.ChangedSlots,
                nonSlotEdits,
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
            var nonSlotEdits = ReferenceNonSlotEditReporter.Report(material.Text, candidateText);
            var audit = BuildReuseAudit(
                material,
                candidateText,
                input.MaxRewriteLevel,
                input.SceneFacts,
                rewriteLevel,
                nonSlotEdits,
                DateTimeOffset.UtcNow);
            await PersistReuseAuditAsync(connection, candidateId: string.Empty, material.MaterialId, audit, cancellationToken);
            return audit;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceUserFeedbackPayload> RecordUserFeedbackAsync(
        RecordReferenceUserFeedbackPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        var targetType = ValidateAllowedText(input.TargetType, nameof(input.TargetType), AllowedFeedbackTargetTypes);
        var targetId = NormalizeRequiredText(input.TargetId, nameof(input.TargetId), maxLength: 256);
        var decision = ValidateAllowedText(input.Decision, nameof(input.Decision), AllowedFeedbackDecisions);
        var materialId = NormalizeOptionalText(input.MaterialId, nameof(input.MaterialId), maxLength: 256);
        var candidateId = NormalizeOptionalText(input.CandidateId, nameof(input.CandidateId), maxLength: 256);
        var beatId = NormalizeOptionalText(input.BeatId, nameof(input.BeatId), maxLength: 256);
        var feedbackTags = NormalizeFeedbackTags(input.FeedbackTags);
        var note = NormalizeOptionalText(input.Note, nameof(input.Note), maxLength: 2_000);
        var editedText = NormalizeFeedbackEditedText(input.EditedText, nameof(input.EditedText), maxLength: 20_000);
        var origin = NormalizeRequiredText(input.Origin, nameof(input.Origin), maxLength: 128);
        if (string.Equals(targetType, ReferenceFeedbackTargetTypes.Material, StringComparison.Ordinal) &&
            materialId.Length == 0)
        {
            materialId = targetId;
        }

        if (string.Equals(targetType, ReferenceFeedbackTargetTypes.ReuseCandidate, StringComparison.Ordinal) &&
            candidateId.Length == 0)
        {
            candidateId = targetId;
        }

        if (string.Equals(targetType, ReferenceFeedbackTargetTypes.BlueprintBeat, StringComparison.Ordinal) &&
            beatId.Length == 0)
        {
            beatId = targetId;
        }

        if (string.Equals(decision, ReferenceFeedbackDecisions.Edited, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(editedText))
        {
            throw new ArgumentException("Edited feedback requires edited text.", nameof(input));
        }

        var now = DateTimeOffset.UtcNow;
        var feedback = new ReferenceUserFeedbackPayload(
            "feedback-" + Guid.NewGuid().ToString("N"),
            input.NovelId,
            targetType,
            targetId,
            decision,
            materialId,
            candidateId,
            Math.Max(0, input.BlueprintId),
            beatId,
            feedbackTags,
            note,
            string.IsNullOrWhiteSpace(editedText) ? string.Empty : HashText(editedText),
            origin,
            now);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);

            if (feedback.MaterialId.Length > 0 &&
                await ReadMaterialAsync(connection, feedback.NovelId, feedback.MaterialId, cancellationToken) is null)
            {
                throw new ArgumentException("Feedback material does not exist for this novel.", nameof(input));
            }

            if (feedback.CandidateId.Length > 0 &&
                string.Equals(feedback.TargetType, ReferenceFeedbackTargetTypes.ReuseCandidate, StringComparison.Ordinal) &&
                !await ReuseCandidateExistsAsync(connection, feedback.NovelId, feedback.CandidateId, cancellationToken))
            {
                throw new ArgumentException("Feedback reuse candidate does not exist for this novel.", nameof(input));
            }

            await InsertUserFeedbackAsync(connection, feedback, cancellationToken);
            return feedback;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ReferenceUserFeedbackPayload>> GetUserFeedbackAsync(
        GetReferenceUserFeedbackPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        var targetType = NormalizeOptionalText(input.TargetType, nameof(input.TargetType), maxLength: 128);
        if (targetType.Length > 0 && !AllowedFeedbackTargetTypes.Contains(targetType))
        {
            throw new ArgumentException("Unsupported feedback target type.", nameof(input));
        }

        var targetId = NormalizeOptionalText(input.TargetId, nameof(input.TargetId), maxLength: 256);
        var limit = Math.Clamp(input.Limit <= 0 ? 100 : input.Limit, 1, 500);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            return await ReadUserFeedbackAsync(connection, input.NovelId, targetType, targetId, limit, cancellationToken);
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
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            await DeleteOrArchiveAnchorAsync(connection, transaction, novelId, anchorId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask DeleteAnchorsAsync(
        DeleteReferenceAnchorsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        if (input.AnchorIds.Count == 0)
        {
            throw new ArgumentException("At least one reference anchor must be selected.", nameof(input));
        }

        var anchorIds = input.AnchorIds.Distinct().ToArray();
        foreach (var anchorId in anchorIds)
        {
            ValidateAnchorId(anchorId);
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;

            foreach (var anchorId in anchorIds)
            {
                var affected = await DeleteOrArchiveAnchorAsync(
                    connection,
                    transaction,
                    input.NovelId,
                    anchorId,
                    now,
                    cancellationToken);
                if (!affected)
                {
                    throw new ArgumentException("Reference anchor does not exist for this novel or visible workspace corpus.", nameof(input));
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public ValueTask DeleteMaterialsAsync(
        DeleteReferenceMaterialsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        if (input.MaterialIds.Count == 0)
        {
            throw new ArgumentException("At least one reference material must be selected.", nameof(input));
        }

        var materialIds = input.MaterialIds
            .Select(materialId => NormalizeRequiredText(materialId, nameof(input.MaterialIds), maxLength: 256))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return DeleteMaterialsCoreAsync(input.NovelId, materialIds, cancellationToken);
    }

    public ValueTask RestoreMaterialsAsync(
        RestoreReferenceMaterialsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        if (input.MaterialIds.Count == 0)
        {
            throw new ArgumentException("At least one reference material must be selected.", nameof(input));
        }

        var materialIds = input.MaterialIds
            .Select(materialId => NormalizeRequiredText(materialId, nameof(input.MaterialIds), maxLength: 256))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return RestoreMaterialsCoreAsync(input.NovelId, materialIds, cancellationToken);
    }

    private async ValueTask DeleteMaterialsCoreAsync(
        long novelId,
        IReadOnlyList<string> materialIds,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var now = FormatTimestamp(DateTimeOffset.UtcNow);

            foreach (var materialId in materialIds)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $$"""
                    UPDATE reference_materials
                    SET archived_at = $archived_at
                    WHERE material_id = $material_id
                      AND archived_at IS NULL
                      AND anchor_id IN (
                        SELECT anchor_id
                        FROM reference_anchors
                        WHERE {{NovelOrVisibleWorkspaceCorpusPredicate}}
                      );
                    """;
                command.Parameters.AddWithValue("$archived_at", now);
                command.Parameters.AddWithValue("$material_id", materialId);
                command.Parameters.AddWithValue("$novel_id", novelId);
                command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
                command.Parameters.AddWithValue("$workspace_corpus_visibility", ReferenceCorpusVisibilities.Workspace);
                var affected = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affected == 0)
                {
                    throw new ArgumentException("Reference material does not exist for this novel or visible workspace corpus.", nameof(materialIds));
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask RestoreMaterialsCoreAsync(
        long novelId,
        IReadOnlyList<string> materialIds,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            foreach (var materialId in materialIds)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $$"""
                    UPDATE reference_materials
                    SET archived_at = NULL
                    WHERE material_id = $material_id
                      AND archived_at IS NOT NULL
                      AND anchor_id IN (
                        SELECT anchor_id
                        FROM reference_anchors
                        WHERE {{NovelOrVisibleWorkspaceCorpusPredicate}}
                      );
                    """;
                command.Parameters.AddWithValue("$material_id", materialId);
                command.Parameters.AddWithValue("$novel_id", novelId);
                command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
                command.Parameters.AddWithValue("$workspace_corpus_visibility", ReferenceCorpusVisibilities.Workspace);
                var affected = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affected == 0)
                {
                    throw new ArgumentException("Archived reference material does not exist for this novel or visible workspace corpus.", nameof(materialIds));
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static async ValueTask<bool> DeleteOrArchiveAnchorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long novelId,
        long anchorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var archive = connection.CreateCommand())
        {
            archive.Transaction = transaction;
            archive.CommandText = """
                UPDATE reference_anchors
                SET corpus_visibility = $restricted_visibility,
                    updated_at = $updated_at
                WHERE anchor_id = $anchor_id
                  AND (novel_id IS NULL OR novel_id = $workspace_corpus_novel_id)
                  AND corpus_visibility = $workspace_visibility;
                """;
            archive.Parameters.AddWithValue("$anchor_id", anchorId);
            archive.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
            archive.Parameters.AddWithValue("$workspace_visibility", ReferenceCorpusVisibilities.Workspace);
            archive.Parameters.AddWithValue("$restricted_visibility", ReferenceCorpusVisibilities.Restricted);
            archive.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
            var archived = await archive.ExecuteNonQueryAsync(cancellationToken);
            if (archived > 0)
            {
                return true;
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM reference_anchors
                WHERE novel_id = $novel_id AND anchor_id = $anchor_id;
                """;
            delete.Parameters.AddWithValue("$novel_id", novelId);
            delete.Parameters.AddWithValue("$anchor_id", anchorId);
            return await delete.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
    }

    public async ValueTask<ReferenceAnchorPayload> PromoteAnchorToWorkspaceCorpusAsync(
        PromoteReferenceAnchorToWorkspaceCorpusPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        ValidateAnchorId(input.AnchorId);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        var sourceTrust = string.IsNullOrWhiteSpace(input.SourceTrust)
            ? null
            : ValidateAllowedText(input.SourceTrust, nameof(input.SourceTrust), AllowedSourceTrustLevels);
        var userTagsJson = input.UserTags is null
            ? null
            : JsonSerializer.Serialize(NormalizeUserTags(input.UserTags), JsonOptions);
        var now = DateTimeOffset.UtcNow;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE reference_anchors
                    SET novel_id = NULL,
                        corpus_visibility = $corpus_visibility,
                        source_trust = COALESCE($source_trust, source_trust),
                        user_tags_json = COALESCE($user_tags_json, user_tags_json),
                        updated_at = $updated_at
                    WHERE anchor_id = $anchor_id
                      AND novel_id = $novel_id;
                    """;
                update.Parameters.AddWithValue("$anchor_id", input.AnchorId);
                update.Parameters.AddWithValue("$novel_id", input.NovelId);
                update.Parameters.AddWithValue("$corpus_visibility", ReferenceCorpusVisibilities.Workspace);
                update.Parameters.AddWithValue("$source_trust", sourceTrust is null ? DBNull.Value : sourceTrust);
                update.Parameters.AddWithValue("$user_tags_json", userTagsJson is null ? DBNull.Value : userTagsJson);
                update.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
                var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                if (affected == 0)
                {
                    throw new ArgumentException("Reference anchor does not exist for this novel.", nameof(input));
                }

                await UpsertDefaultCorpusMembershipFromAnchorAsync(
                    connection,
                    transaction,
                    input.AnchorId,
                    now,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            var promoted = await ReadAnchorAsync(connection, input.NovelId, input.AnchorId, cancellationToken);
            return promoted;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ReferenceAnchorPayload>> PromoteAnchorsToWorkspaceCorpusAsync(
        PromoteReferenceAnchorsToWorkspaceCorpusPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        if (input.AnchorIds.Count == 0)
        {
            throw new ArgumentException("At least one reference anchor must be selected.", nameof(input));
        }

        var anchorIds = input.AnchorIds.Distinct().ToArray();
        foreach (var anchorId in anchorIds)
        {
            ValidateAnchorId(anchorId);
        }

        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        var sourceTrust = string.IsNullOrWhiteSpace(input.SourceTrust)
            ? null
            : ValidateAllowedText(input.SourceTrust, nameof(input.SourceTrust), AllowedSourceTrustLevels);
        var userTagsJson = input.UserTags is null
            ? null
            : JsonSerializer.Serialize(NormalizeUserTags(input.UserTags), JsonOptions);
        var now = DateTimeOffset.UtcNow;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            foreach (var anchorId in anchorIds)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE reference_anchors
                    SET novel_id = NULL,
                        corpus_visibility = $corpus_visibility,
                        source_trust = COALESCE($source_trust, source_trust),
                        user_tags_json = COALESCE($user_tags_json, user_tags_json),
                        updated_at = $updated_at
                    WHERE anchor_id = $anchor_id
                      AND novel_id = $novel_id;
                    """;
                update.Parameters.AddWithValue("$anchor_id", anchorId);
                update.Parameters.AddWithValue("$novel_id", input.NovelId);
                update.Parameters.AddWithValue("$corpus_visibility", ReferenceCorpusVisibilities.Workspace);
                update.Parameters.AddWithValue("$source_trust", sourceTrust is null ? DBNull.Value : sourceTrust);
                update.Parameters.AddWithValue("$user_tags_json", userTagsJson is null ? DBNull.Value : userTagsJson);
                update.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
                var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                if (affected == 0)
                {
                    throw new ArgumentException("Reference anchor does not exist for this novel.", nameof(input));
                }

                await UpsertDefaultCorpusMembershipFromAnchorAsync(
                    connection,
                    transaction,
                    anchorId,
                    now,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            var promoted = new List<ReferenceAnchorPayload>(anchorIds.Length);
            foreach (var anchorId in anchorIds)
            {
                promoted.Add(await ReadAnchorAsync(connection, input.NovelId, anchorId, cancellationToken));
            }

            return promoted;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceAnchorPayload> UpdateAnchorMetadataAsync(
        UpdateReferenceAnchorMetadataPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        ValidateAnchorId(input.AnchorId);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        var title = NormalizeRequiredText(input.Title, nameof(input.Title), maxLength: 200);
        var author = NormalizeOptionalText(input.Author, nameof(input.Author), maxLength: 200);
        var licenseStatus = ValidateAllowedText(input.LicenseStatus, nameof(input.LicenseStatus), AllowedLicenseStatuses);
        var visibility = ValidateAllowedText(input.Visibility, nameof(input.Visibility), AllowedCorpusVisibilities);
        var sourceTrust = ValidateAllowedText(input.SourceTrust, nameof(input.SourceTrust), AllowedSourceTrustLevels);
        var userTags = NormalizeUserTags(input.UserTags);
        var now = DateTimeOffset.UtcNow;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var current = await ReadAnchorAsync(connection, input.NovelId, input.AnchorId, cancellationToken);
            if (current.OwnerScope == ReferenceAnchorOwnerScopes.WorkspaceCorpus &&
                visibility != ReferenceCorpusVisibilities.Workspace)
            {
                throw new ArgumentException(
                    "Workspace corpus anchors must stay workspace-visible until archive/delete policy is implemented.",
                    nameof(input));
            }

            var storedNovelId = visibility == ReferenceCorpusVisibilities.Workspace
                ? (long?)null
                : input.NovelId;

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE reference_anchors
                SET novel_id = $novel_id,
                    title = $title,
                    author = $author,
                    license_status = $license_status,
                    corpus_visibility = $corpus_visibility,
                    source_trust = $source_trust,
                    user_tags_json = $user_tags_json,
                    updated_at = $updated_at
                WHERE anchor_id = $anchor_id;
                """;
            command.Parameters.AddWithValue("$anchor_id", input.AnchorId);
            command.Parameters.AddWithValue("$novel_id", storedNovelId.HasValue ? storedNovelId.Value : DBNull.Value);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$author", author);
            command.Parameters.AddWithValue("$license_status", licenseStatus);
            command.Parameters.AddWithValue("$corpus_visibility", visibility);
            command.Parameters.AddWithValue("$source_trust", sourceTrust);
            command.Parameters.AddWithValue("$user_tags_json", JsonSerializer.Serialize(userTags, JsonOptions));
            command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                throw new ArgumentException("Reference anchor does not exist for this novel.", nameof(input));
            }

            await UpsertDefaultCorpusMembershipAsync(
                connection,
                transaction,
                input.AnchorId,
                storedNovelId,
                visibility,
                licenseStatus,
                sourceTrust,
                now,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return await ReadAnchorAsync(connection, input.NovelId, input.AnchorId, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<ReferenceAnchorPayload> InsertAnchorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? novelId,
        string title,
        string author,
        string sourcePath,
        string sourceKind,
        string licenseStatus,
        string visibility,
        string sourceTrust,
        IReadOnlyList<string> userTags,
        string sourceFileHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_anchors
              (novel_id, title, author, source_path, source_kind, license_status,
               source_file_hash, build_version, status, created_at, updated_at,
               corpus_visibility, source_trust, user_tags_json)
            VALUES
              ($novel_id, $title, $author, $source_path, $source_kind, $license_status,
               $source_file_hash, $build_version, $status, $created_at, $updated_at,
               $corpus_visibility, $source_trust, $user_tags_json)
            RETURNING anchor_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId.HasValue ? novelId.Value : DBNull.Value);
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
        command.Parameters.AddWithValue("$corpus_visibility", visibility);
        command.Parameters.AddWithValue("$source_trust", sourceTrust);
        command.Parameters.AddWithValue("$user_tags_json", JsonSerializer.Serialize(userTags, JsonOptions));
        var anchorId = (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("SQLite did not return a reference anchor id."));
        var payloadNovelId = novelId ?? WorkspaceCorpusNovelId;
        return new ReferenceAnchorPayload(
            anchorId,
            payloadNovelId,
            title,
            author,
            sourcePath,
            sourceKind,
            licenseStatus,
            sourceFileHash,
            BuildVersion,
            ReferenceAnchorBuildStates.Importing,
            now,
            now,
            visibility,
            sourceTrust,
            userTags);
    }

    private static async ValueTask UpsertDefaultCorpusMembershipFromAnchorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT novel_id, corpus_visibility, license_status, source_trust
            FROM reference_anchors
            WHERE anchor_id = $anchor_id;
            """;
        read.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Reference anchor was not found while updating corpus membership.");
        }

        await UpsertDefaultCorpusMembershipAsync(
            connection,
            transaction,
            anchorId,
            reader.IsDBNull(0) ? null : reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            now,
            cancellationToken);
    }

    private static async ValueTask UpsertDefaultCorpusMembershipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        long? storedNovelId,
        string visibility,
        string licenseStatus,
        string sourceTrust,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var libraryId = BuildDefaultCorpusLibraryId(storedNovelId, visibility);
        var libraryScope = string.Equals(visibility, ReferenceCorpusVisibilities.Workspace, StringComparison.Ordinal)
            ? "global"
            : "project";
        var libraryNovelId = string.Equals(libraryScope, "project", StringComparison.Ordinal)
            ? storedNovelId ?? throw new InvalidOperationException("Project corpus library requires a novel id.")
            : (long?)null;

        await using (var library = connection.CreateCommand())
        {
            library.Transaction = transaction;
            library.CommandText = """
                INSERT INTO reference_corpus_libraries
                  (library_id, scope, novel_id, name, created_at)
                VALUES
                  ($library_id, $scope, $novel_id, $name, $created_at)
                ON CONFLICT(library_id) DO UPDATE SET
                  scope = excluded.scope,
                  novel_id = excluded.novel_id,
                  name = excluded.name;
                """;
            library.Parameters.AddWithValue("$library_id", libraryId);
            library.Parameters.AddWithValue("$scope", libraryScope);
            library.Parameters.AddWithValue("$novel_id", libraryNovelId.HasValue ? libraryNovelId.Value : DBNull.Value);
            library.Parameters.AddWithValue("$name", string.Equals(libraryScope, "global", StringComparison.Ordinal)
                ? "Workspace corpus"
                : "Project corpus");
            library.Parameters.AddWithValue("$created_at", FormatTimestamp(now));
            await library.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var member = connection.CreateCommand())
        {
            member.Transaction = transaction;
            member.CommandText = """
                INSERT INTO reference_library_members
                  (library_id, anchor_id, enabled, source_quality, disabled_reason, dedup_group_id)
                VALUES
                  ($library_id, $anchor_id, 1, $source_quality, NULL, $dedup_group_id)
                ON CONFLICT(library_id, anchor_id) DO UPDATE SET
                  enabled = 1,
                  source_quality = excluded.source_quality,
                  disabled_reason = NULL,
                  dedup_group_id = excluded.dedup_group_id;
                """;
            member.Parameters.AddWithValue("$library_id", libraryId);
            member.Parameters.AddWithValue("$anchor_id", anchorId);
            member.Parameters.AddWithValue("$source_quality", MapSourceQuality(sourceTrust));
            member.Parameters.AddWithValue("$dedup_group_id", "anchor:" + anchorId.ToString(CultureInfo.InvariantCulture));
            await member.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var binding = connection.CreateCommand())
        {
            binding.Transaction = transaction;
            binding.CommandText = """
                INSERT OR IGNORE INTO reference_session_library_binding
                  (session_id, library_id)
                VALUES
                  ($session_id, $library_id);
                """;
            binding.Parameters.AddWithValue("$session_id", libraryId);
            binding.Parameters.AddWithValue("$library_id", libraryId);
            await binding.ExecuteNonQueryAsync(cancellationToken);
        }

        var license = MapLicenseStatus(licenseStatus);
        await using (var gate = connection.CreateCommand())
        {
            gate.Transaction = transaction;
            gate.CommandText = """
                INSERT INTO reference_source_license
                  (anchor_id, license_state, authorization_evidence, reuse_policy,
                   max_verbatim_ratio, cleared_for_insertion, reviewed_at)
                VALUES
                  ($anchor_id, $license_state, $authorization_evidence, $reuse_policy,
                   $max_verbatim_ratio, $cleared_for_insertion, $reviewed_at)
                ON CONFLICT(anchor_id) DO UPDATE SET
                  license_state = excluded.license_state,
                  authorization_evidence = excluded.authorization_evidence,
                  reuse_policy = excluded.reuse_policy,
                  max_verbatim_ratio = excluded.max_verbatim_ratio,
                  cleared_for_insertion = excluded.cleared_for_insertion,
                  reviewed_at = excluded.reviewed_at;
                """;
            gate.Parameters.AddWithValue("$anchor_id", anchorId);
            gate.Parameters.AddWithValue("$license_state", license.LicenseState);
            gate.Parameters.AddWithValue("$authorization_evidence", licenseStatus);
            gate.Parameters.AddWithValue("$reuse_policy", license.ReusePolicy);
            gate.Parameters.AddWithValue("$max_verbatim_ratio", license.MaxVerbatimRatio.HasValue ? license.MaxVerbatimRatio.Value : DBNull.Value);
            gate.Parameters.AddWithValue("$cleared_for_insertion", license.ClearedForInsertion ? 1 : 0);
            gate.Parameters.AddWithValue("$reviewed_at", FormatTimestamp(now));
            await gate.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask ReplaceSegmentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        IReadOnlyList<ReferenceSourceSegment> segments,
        CancellationToken cancellationToken)
    {
        await using (var createKeep = connection.CreateCommand())
        {
            createKeep.Transaction = transaction;
            createKeep.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS temp_reference_source_segment_keep (
                  segment_id TEXT PRIMARY KEY
                );
                """;
            await createKeep.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var clearKeep = connection.CreateCommand())
        {
            clearKeep.Transaction = transaction;
            clearKeep.CommandText = "DELETE FROM temp_reference_source_segment_keep;";
            await clearKeep.ExecuteNonQueryAsync(cancellationToken);
        }

        var segmentDepths = BuildSegmentDepths(segments);
        var segmentIds = segments
            .Select(segment => segment.SegmentId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            await using (var keep = connection.CreateCommand())
            {
                keep.Transaction = transaction;
                keep.CommandText = """
                    INSERT INTO temp_reference_source_segment_keep (segment_id)
                    VALUES ($segment_id)
                    ON CONFLICT(segment_id) DO NOTHING;
                    """;
                keep.Parameters.AddWithValue("$segment_id", segment.SegmentId);
                await keep.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        foreach (var segment in segments
            .OrderBy(segment => segmentDepths.TryGetValue(segment.SegmentId, out var depth) ? depth : 0)
            .ThenBy(segment => segment.StartOffset)
            .ThenBy(segment => segment.SegmentIndex)
            .ThenBy(segment => segment.SegmentId, StringComparer.Ordinal))
        {
            await UpsertTextNodeAsync(
                connection,
                transaction,
                anchorId,
                segment,
                segmentDepths.TryGetValue(segment.SegmentId, out var depth) ? depth : 0,
                segmentIds,
                cancellationToken);
        }

        foreach (var segment in segments)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO reference_source_segments
                  (segment_id, anchor_id, chapter_index, chapter_title, segment_type,
                   segment_index, parent_segment_id, start_offset, end_offset, text, text_hash, node_id)
                VALUES
                  ($segment_id, $anchor_id, $chapter_index, $chapter_title, $segment_type,
                   $segment_index, $parent_segment_id, $start_offset, $end_offset, $text, $text_hash, $node_id)
                ON CONFLICT(segment_id) DO UPDATE SET
                  anchor_id = excluded.anchor_id,
                  chapter_index = excluded.chapter_index,
                  chapter_title = excluded.chapter_title,
                  segment_type = excluded.segment_type,
                  segment_index = excluded.segment_index,
                  parent_segment_id = excluded.parent_segment_id,
                  start_offset = excluded.start_offset,
                  end_offset = excluded.end_offset,
                  text = excluded.text,
                  text_hash = excluded.text_hash,
                  node_id = excluded.node_id;
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
            insert.Parameters.AddWithValue("$node_id", BuildTextNodeId(segment.SegmentId));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteStale = connection.CreateCommand())
        {
            deleteStale.Transaction = transaction;
            deleteStale.CommandText = """
                DELETE FROM reference_source_segments
                WHERE anchor_id = $anchor_id
                  AND segment_id NOT IN (SELECT segment_id FROM temp_reference_source_segment_keep);
                """;
            deleteStale.Parameters.AddWithValue("$anchor_id", anchorId);
            await deleteStale.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var clearKeep = connection.CreateCommand())
        {
            clearKeep.Transaction = transaction;
            clearKeep.CommandText = "DELETE FROM temp_reference_source_segment_keep;";
            await clearKeep.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask UpsertTextNodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        ReferenceSourceSegment segment,
        int depth,
        IReadOnlySet<string> segmentIds,
        CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              ($node_id, $anchor_id, $parent_node_id, $node_type, $sequence_index, $depth,
               $chapter_index, $start_offset, $end_offset, $char_len, $text_hash, $text, $created_at)
            ON CONFLICT(node_id) DO UPDATE SET
              anchor_id = excluded.anchor_id,
              parent_node_id = excluded.parent_node_id,
              node_type = excluded.node_type,
              sequence_index = excluded.sequence_index,
              depth = excluded.depth,
              chapter_index = excluded.chapter_index,
              start_offset = excluded.start_offset,
              end_offset = excluded.end_offset,
              char_len = excluded.char_len,
              text_hash = excluded.text_hash,
              text = excluded.text;
            """;
        insert.Parameters.AddWithValue("$node_id", BuildTextNodeId(segment.SegmentId));
        insert.Parameters.AddWithValue("$anchor_id", anchorId);
        insert.Parameters.AddWithValue(
            "$parent_node_id",
            string.IsNullOrWhiteSpace(segment.ParentSegmentId) ||
            !segmentIds.Contains(segment.ParentSegmentId)
                ? DBNull.Value
                : BuildTextNodeId(segment.ParentSegmentId));
        insert.Parameters.AddWithValue("$node_type", MapTextNodeType(segment.SegmentType));
        insert.Parameters.AddWithValue("$sequence_index", segment.SegmentIndex);
        insert.Parameters.AddWithValue("$depth", depth);
        insert.Parameters.AddWithValue("$chapter_index", segment.ChapterIndex);
        insert.Parameters.AddWithValue("$start_offset", segment.StartOffset);
        insert.Parameters.AddWithValue("$end_offset", segment.EndOffset);
        insert.Parameters.AddWithValue("$char_len", segment.Text.Length);
        insert.Parameters.AddWithValue("$text_hash", segment.TextHash);
        insert.Parameters.AddWithValue("$text", segment.Text);
        insert.Parameters.AddWithValue("$created_at", FormatTimestamp(DateTimeOffset.UtcNow));
        await insert.ExecuteNonQueryAsync(cancellationToken);
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
        CancellationToken cancellationToken,
        int vectorCount = 0,
        ReferenceProcessingAffectedIds? affectedIds = null)
    {
        var affected = affectedIds ?? ReferenceProcessingAffectedIds.Empty;
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
              ($anchor_id, $status, $stage, $source_segment_count, $material_count, $slot_count, $vector_count, $last_error, $updated_at)
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
        upsert.Parameters.AddWithValue("$vector_count", vectorCount);
        upsert.Parameters.AddWithValue("$last_error", lastError);
        upsert.Parameters.AddWithValue("$updated_at", FormatTimestamp(updatedAt));
        await upsert.ExecuteNonQueryAsync(cancellationToken);

        var processingAttempt = await ResolveProcessingAttemptForEventAsync(
            connection,
            transaction,
            anchor,
            status,
            updatedAt,
            cancellationToken);
        await using var history = connection.CreateCommand();
        history.Transaction = transaction;
        history.CommandText = """
            INSERT INTO reference_anchor_processing_events
              (event_id, anchor_id, attempt_id, build_id, attempt_number, build_version,
               stage, status, source_segment_count, material_count, slot_count, vector_count,
               last_error, affected_source_id, affected_material_id, affected_segment_id,
               affected_slot_id, created_at)
            VALUES
              ($event_id, $anchor_id, $attempt_id, $build_id, $attempt_number, $build_version,
               $stage, $status, $source_segment_count, $material_count, $slot_count, $vector_count,
               $last_error, $affected_source_id, $affected_material_id, $affected_segment_id,
               $affected_slot_id, $created_at);
            """;
        history.Parameters.AddWithValue("$event_id", Guid.NewGuid().ToString("N"));
        history.Parameters.AddWithValue("$anchor_id", anchor.AnchorId);
        history.Parameters.AddWithValue("$attempt_id", processingAttempt.AttemptId);
        history.Parameters.AddWithValue("$build_id", processingAttempt.BuildId);
        history.Parameters.AddWithValue("$attempt_number", processingAttempt.AttemptNumber);
        history.Parameters.AddWithValue("$build_version", processingAttempt.BuildVersion);
        history.Parameters.AddWithValue("$stage", stage);
        history.Parameters.AddWithValue("$status", status);
        history.Parameters.AddWithValue("$source_segment_count", sourceSegmentCount);
        history.Parameters.AddWithValue("$material_count", materialCount);
        history.Parameters.AddWithValue("$slot_count", slotCount);
        history.Parameters.AddWithValue("$vector_count", vectorCount);
        history.Parameters.AddWithValue("$last_error", lastError);
        history.Parameters.AddWithValue("$affected_source_id", anchor.AnchorId.ToString(CultureInfo.InvariantCulture));
        history.Parameters.AddWithValue("$affected_material_id", affected.MaterialId);
        history.Parameters.AddWithValue("$affected_segment_id", affected.SegmentId);
        history.Parameters.AddWithValue("$affected_slot_id", affected.SlotId);
        history.Parameters.AddWithValue("$created_at", FormatTimestamp(updatedAt));
        await history.ExecuteNonQueryAsync(cancellationToken);

        await UpsertProcessingAttemptAsync(
            connection,
            transaction,
            processingAttempt,
            stage,
            status,
            sourceSegmentCount,
            materialCount,
            slotCount,
            vectorCount,
            lastError,
            updatedAt,
            cancellationToken);
    }

    private static async ValueTask<ReferenceProcessingAttemptWrite> ResolveProcessingAttemptForEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReferenceAnchorPayload anchor,
        string status,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var latest = await ReadLatestProcessingAttemptAsync(
            connection,
            transaction,
            anchor.AnchorId,
            cancellationToken);
        if (latest is null || IsTerminalBuildStatus(latest.Status))
        {
            var attemptNumber = (latest?.AttemptNumber ?? 0) + 1;
            var attemptId = BuildSourceProcessingAttemptId(anchor.AnchorId, attemptNumber);
            var buildId = BuildSourceProcessingBuildId(anchor.AnchorId, attemptNumber);
            var recoveredFromAttemptId = latest is not null && IsRecoveredFromPriorAttempt(latest.Status, status)
                ? latest.AttemptId
                : string.Empty;
            var recoveredFromBuildId = latest is not null && IsRecoveredFromPriorAttempt(latest.Status, status)
                ? latest.BuildId
                : string.Empty;
            return new ReferenceProcessingAttemptWrite(
                anchor.AnchorId,
                attemptId,
                buildId,
                attemptNumber,
                anchor.BuildVersion,
                updatedAt,
                recoveredFromAttemptId,
                recoveredFromBuildId);
        }

        return new ReferenceProcessingAttemptWrite(
            anchor.AnchorId,
            latest.AttemptId,
            latest.BuildId,
            latest.AttemptNumber,
            latest.BuildVersion,
            latest.StartedAt,
            latest.RecoveredFromAttemptId,
            latest.RecoveredFromBuildId);
    }

    private static async ValueTask<ReferenceProcessingAttemptRead?> ReadLatestProcessingAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT attempt_id, build_id, attempt_number, build_version, status, started_at,
                   recovered_from_attempt_id, recovered_from_build_id
            FROM reference_anchor_processing_attempts
            WHERE anchor_id = $anchor_id
            ORDER BY attempt_number DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ReferenceProcessingAttemptRead(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            ParseTimestamp(reader.GetString(5)),
            reader.GetString(6),
            reader.GetString(7));
    }

    private static async ValueTask UpsertProcessingAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReferenceProcessingAttemptWrite attempt,
        string stage,
        string status,
        int sourceSegmentCount,
        int materialCount,
        int slotCount,
        int vectorCount,
        string lastError,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_anchor_processing_attempts
              (attempt_id, anchor_id, build_id, attempt_number, build_version, stage, status,
               started_at, updated_at, completed_at, event_count, source_segment_count,
               material_count, slot_count, vector_count, recovered_from_attempt_id,
               recovered_from_build_id, blocked_reason)
            VALUES
              ($attempt_id, $anchor_id, $build_id, $attempt_number, $build_version, $stage, $status,
               $started_at, $updated_at, $completed_at, 1, $source_segment_count,
               $material_count, $slot_count, $vector_count, $recovered_from_attempt_id,
               $recovered_from_build_id, $blocked_reason)
            ON CONFLICT(attempt_id) DO UPDATE SET
              build_version = excluded.build_version,
              stage = excluded.stage,
              status = excluded.status,
              updated_at = excluded.updated_at,
              completed_at = excluded.completed_at,
              event_count = reference_anchor_processing_attempts.event_count + 1,
              source_segment_count = excluded.source_segment_count,
              material_count = excluded.material_count,
              slot_count = excluded.slot_count,
              vector_count = excluded.vector_count,
              recovered_from_attempt_id = CASE
                  WHEN excluded.recovered_from_attempt_id <> '' THEN excluded.recovered_from_attempt_id
                  ELSE reference_anchor_processing_attempts.recovered_from_attempt_id
                END,
              recovered_from_build_id = CASE
                  WHEN excluded.recovered_from_build_id <> '' THEN excluded.recovered_from_build_id
                  ELSE reference_anchor_processing_attempts.recovered_from_build_id
                END,
              blocked_reason = excluded.blocked_reason;
            """;
        command.Parameters.AddWithValue("$attempt_id", attempt.AttemptId);
        command.Parameters.AddWithValue("$anchor_id", attempt.AnchorId);
        command.Parameters.AddWithValue("$build_id", attempt.BuildId);
        command.Parameters.AddWithValue("$attempt_number", attempt.AttemptNumber);
        command.Parameters.AddWithValue("$build_version", attempt.BuildVersion);
        command.Parameters.AddWithValue("$stage", stage);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$started_at", FormatTimestamp(attempt.StartedAt));
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(updatedAt));
        command.Parameters.AddWithValue("$completed_at", IsTerminalBuildStatus(status) ? FormatTimestamp(updatedAt) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$source_segment_count", sourceSegmentCount);
        command.Parameters.AddWithValue("$material_count", materialCount);
        command.Parameters.AddWithValue("$slot_count", slotCount);
        command.Parameters.AddWithValue("$vector_count", vectorCount);
        command.Parameters.AddWithValue("$recovered_from_attempt_id", attempt.RecoveredFromAttemptId);
        command.Parameters.AddWithValue("$recovered_from_build_id", attempt.RecoveredFromBuildId);
        command.Parameters.AddWithValue("$blocked_reason", BuildAttemptBlockedReason(status, lastError));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask<ReferenceAnchorBuildCompletion> CompleteEmbeddingStageAsync(
        string databasePath,
        ReferenceAnchorPayload anchor,
        int sourceSegmentCount,
        int materialCount,
        int slotCount,
        IReadOnlyList<ReferenceMaterialPayload> materials,
        EmbeddingRequestOptions embeddingOptions,
        ReferenceProcessingAffectedIds affectedIds,
        CancellationToken cancellationToken)
    {
        try
        {
            var vectorCount = await ProvisionMaterialVectorsAsync(
                databasePath,
                anchor.AnchorId,
                materials,
                embeddingOptions,
                cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var readyAnchor = anchor with
            {
                Status = ReferenceAnchorBuildStates.Ready,
                UpdatedAt = now
            };
            await UpdateAnchorBuildResultInNewTransactionAsync(
                databasePath,
                readyAnchor,
                ReferenceAnchorBuildStates.Ready,
                "ready",
                sourceSegmentCount,
                materialCount,
                slotCount,
                vectorCount,
                string.Empty,
                now,
                cancellationToken,
                affectedIds);
            return new ReferenceAnchorBuildCompletion(readyAnchor, "ready", vectorCount, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var cancelledAnchor = anchor with
            {
                Status = ReferenceAnchorBuildStates.Cancelled,
                UpdatedAt = now
            };
            await UpdateAnchorBuildResultInNewTransactionAsync(
                databasePath,
                cancelledAnchor,
                ReferenceAnchorBuildStates.Cancelled,
                ReferenceAnchorBuildStates.Cancelled,
                sourceSegmentCount,
                materialCount,
                slotCount,
                0,
                "Operation was cancelled.",
                now,
                CancellationToken.None,
                affectedIds);
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var now = DateTimeOffset.UtcNow;
            var failedAnchor = anchor with
            {
                Status = ReferenceAnchorBuildStates.FailedEmbedding,
                UpdatedAt = now
            };
            var lastError = RedactError(exception.Message);
            await UpdateAnchorBuildResultInNewTransactionAsync(
                databasePath,
                failedAnchor,
                ReferenceAnchorBuildStates.FailedEmbedding,
                ReferenceAnchorBuildStates.FailedEmbedding,
                sourceSegmentCount,
                materialCount,
                slotCount,
                0,
                lastError,
                now,
                CancellationToken.None,
                affectedIds);
            return new ReferenceAnchorBuildCompletion(failedAnchor, ReferenceAnchorBuildStates.FailedEmbedding, 0, lastError);
        }
    }

    private static async ValueTask<ReferenceAnchorBuildStatusPayload> RestorePreviousCorpusAfterFailedRebuildEmbeddingAsync(
        string databasePath,
        ReferenceAnchorPayload previousAnchor,
        ReferenceAnchorCorpusSnapshot previousCorpus,
        string lastError,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var failedAnchor = previousAnchor with
        {
            Status = ReferenceAnchorBuildStates.FailedEmbedding,
            UpdatedAt = updatedAt
        };
        await ReplaceSegmentsAsync(
            connection,
            transaction,
            previousAnchor.AnchorId,
            previousCorpus.Segments,
            cancellationToken);
        await ReplaceMaterialRowsAsync(
            connection,
            transaction,
            previousAnchor.AnchorId,
            previousCorpus.Materials,
            cancellationToken,
            previousCorpus.ArchivedMaterialTimestamps);
        await ReplaceMaterialSlotsAsync(
            connection,
            transaction,
            previousAnchor.AnchorId,
            previousCorpus.SlotsByMaterial,
            BuildMaterialCreatedAt(previousCorpus.Materials),
            cancellationToken);
        var slotCount = CountMaterialSlots(previousCorpus.SlotsByMaterial);
        await UpdateAnchorBuildResultAsync(
            connection,
            transaction,
            failedAnchor,
            ReferenceAnchorBuildStates.FailedEmbedding,
            ReferenceAnchorBuildStates.FailedEmbedding,
            previousCorpus.Segments.Count,
            previousCorpus.Materials.Count,
            slotCount,
            lastError,
            updatedAt,
            cancellationToken,
            vectorCount: 0,
            affectedIds: BuildAffectedProcessingIds(previousCorpus));
        await transaction.CommitAsync(cancellationToken);
        return BuildStatus(
            failedAnchor,
            ReferenceAnchorBuildStates.FailedEmbedding,
            ReferenceAnchorBuildStates.FailedEmbedding,
            previousCorpus.Segments.Count,
            previousCorpus.Materials.Count,
            slotCount,
            lastError,
            updatedAt);
    }

    private async ValueTask<int> ProvisionMaterialVectorsAsync(
        string databasePath,
        long anchorId,
        IReadOnlyList<ReferenceMaterialPayload> materials,
        EmbeddingRequestOptions embeddingOptions,
        CancellationToken cancellationToken)
    {
        if (materials.Count == 0)
        {
            return 0;
        }

        var materialEmbeddings = await EmbedMaterialsAsync(materials, embeddingOptions, cancellationToken);
        if (materialEmbeddings.Count == 0)
        {
            return 0;
        }

        var dimensions = materialEmbeddings[0].Vector.Count;
        if (dimensions <= 0)
        {
            throw new InvalidOperationException("Reference material embedding dimensions must be positive.");
        }

        var rowIds = await ReadMaterialRowIdsAsync(databasePath, anchorId, cancellationToken);
        var vectors = new List<SqliteVecVectorRecord>(materialEmbeddings.Count);
        foreach (var item in materialEmbeddings)
        {
            if (item.Vector.Count != dimensions)
            {
                throw new InvalidOperationException("Reference material embedding dimensions are inconsistent.");
            }

            if (!rowIds.TryGetValue(item.Material.MaterialId, out var rowId))
            {
                throw new InvalidOperationException("Reference material row id was not found for vector provisioning.");
            }

            vectors.Add(new SqliteVecVectorRecord(rowId, item.Material.MaterialId, item.Vector));
        }

        var tableName = SqliteVecTableProvisioner.BuildReferenceAnchorVectorTableName(anchorId, dimensions);
        var provisionRequest = new SqliteVecProvisionRequest(
            tableName,
            dimensions,
            SqliteVecTableProvisioner.BuildCreateTableSql(tableName, dimensions),
            vectors);
        await _vecProvisioner.ProvisionAsync(databasePath, provisionRequest, cancellationToken);
        return vectors.Count;
    }

    private async ValueTask<IReadOnlyList<ReferenceMaterialEmbedding>> EmbedMaterialsAsync(
        IReadOnlyList<ReferenceMaterialPayload> materials,
        EmbeddingRequestOptions embeddingOptions,
        CancellationToken cancellationToken)
    {
        var results = new List<ReferenceMaterialEmbedding>(materials.Count);
        for (var offset = 0; offset < materials.Count; offset += EmbeddingBatchSize)
        {
            var batch = materials
                .Skip(offset)
                .Take(EmbeddingBatchSize)
                .ToArray();
            var response = await _embeddings.EmbedAsync(
                batch.Select(item => item.Text).ToArray(),
                embeddingOptions with { InputKind = BuiltinOnnxEmbeddingModel.DocumentInputKind },
                cancellationToken);
            if (response.Items.Count != batch.Length)
            {
                throw new InvalidOperationException("Embedding response count does not match the requested reference material batch.");
            }

            foreach (var item in response.Items.OrderBy(item => item.Index))
            {
                if (item.Index < 0 || item.Index >= batch.Length)
                {
                    throw new InvalidOperationException("Embedding response index is outside the reference material batch.");
                }

                results.Add(new ReferenceMaterialEmbedding(batch[item.Index], item.Vector));
            }
        }

        return results;
    }

    private async ValueTask UpdateAnchorBuildResultInNewTransactionAsync(
        string databasePath,
        ReferenceAnchorPayload anchor,
        string status,
        string stage,
        int sourceSegmentCount,
        int materialCount,
        int slotCount,
        int vectorCount,
        string lastError,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken,
        ReferenceProcessingAffectedIds affectedIds)
    {
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await UpdateAnchorBuildResultAsync(
            connection,
            transaction,
            anchor,
            status,
            stage,
            sourceSegmentCount,
            materialCount,
            slotCount,
            lastError,
            updatedAt,
            cancellationToken,
            vectorCount,
            affectedIds);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask ReplaceMaterialRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        IReadOnlyList<ReferenceMaterialPayload> materials,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? archivedMaterialTimestamps = null)
    {
        await using (var createKeep = connection.CreateCommand())
        {
            createKeep.Transaction = transaction;
            createKeep.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS temp_reference_material_keep (
                  material_id TEXT PRIMARY KEY
                );
                """;
            await createKeep.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var clearKeep = connection.CreateCommand())
        {
            clearKeep.Transaction = transaction;
            clearKeep.CommandText = "DELETE FROM temp_reference_material_keep;";
            await clearKeep.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteSlots = connection.CreateCommand())
        {
            deleteSlots.Transaction = transaction;
            deleteSlots.CommandText = """
                DELETE FROM reference_material_slots
                WHERE material_id IN (
                  SELECT material_id
                  FROM reference_materials
                  WHERE anchor_id = $anchor_id
                );
                """;
            deleteSlots.Parameters.AddWithValue("$anchor_id", anchorId);
            await deleteSlots.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var material in materials)
        {
            var archivedAt = archivedMaterialTimestamps is not null &&
                archivedMaterialTimestamps.TryGetValue(material.MaterialId, out var timestamp)
                ? timestamp
                : null;
            await using (var keep = connection.CreateCommand())
            {
                keep.Transaction = transaction;
                keep.CommandText = """
                    INSERT INTO temp_reference_material_keep (material_id)
                    VALUES ($material_id)
                    ON CONFLICT(material_id) DO NOTHING;
                    """;
                keep.Parameters.AddWithValue("$material_id", material.MaterialId);
                await keep.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO reference_materials
                  (material_id, anchor_id, source_segment_id, material_type, function_tag,
                   emotion_tag, scene_tag, pov_tag, technique_tag, function_confidence,
                   emotion_confidence, pov_confidence, text, source_hash, extractor_version,
                   user_verified, created_at, archived_at, node_id)
                VALUES
                  ($material_id, $anchor_id, $source_segment_id, $material_type, $function_tag,
                   $emotion_tag, $scene_tag, $pov_tag, $technique_tag, $function_confidence,
                   $emotion_confidence, $pov_confidence, $text, $source_hash, $extractor_version,
                   $user_verified, $created_at, $archived_at, $node_id)
                ON CONFLICT(material_id) DO UPDATE SET
                  anchor_id = excluded.anchor_id,
                  source_segment_id = excluded.source_segment_id,
                  material_type = excluded.material_type,
                  function_tag = excluded.function_tag,
                  emotion_tag = excluded.emotion_tag,
                  scene_tag = excluded.scene_tag,
                  pov_tag = excluded.pov_tag,
                  technique_tag = excluded.technique_tag,
                  function_confidence = excluded.function_confidence,
                  emotion_confidence = excluded.emotion_confidence,
                  pov_confidence = excluded.pov_confidence,
                  text = excluded.text,
                  source_hash = excluded.source_hash,
                  extractor_version = excluded.extractor_version,
                  user_verified = excluded.user_verified,
                  created_at = excluded.created_at,
                  archived_at = excluded.archived_at,
                  node_id = excluded.node_id;
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
            insert.Parameters.AddWithValue("$archived_at", archivedAt is null ? DBNull.Value : archivedAt);
            insert.Parameters.AddWithValue("$node_id", BuildTextNodeId(material.SourceSegmentId));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteStale = connection.CreateCommand())
        {
            deleteStale.Transaction = transaction;
            deleteStale.CommandText = """
                DELETE FROM reference_materials
                WHERE anchor_id = $anchor_id
                  AND material_id NOT IN (SELECT material_id FROM temp_reference_material_keep);
                """;
            deleteStale.Parameters.AddWithValue("$anchor_id", anchorId);
            await deleteStale.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var clearKeep = connection.CreateCommand())
        {
            clearKeep.Transaction = transaction;
            clearKeep.CommandText = "DELETE FROM temp_reference_material_keep;";
            await clearKeep.ExecuteNonQueryAsync(cancellationToken);
        }

        await PruneStaleTextNodesAsync(connection, transaction, anchorId, cancellationToken);
    }

    private static async ValueTask PruneStaleTextNodesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM reference_text_nodes
            WHERE anchor_id = $anchor_id
              AND node_id NOT IN (
                SELECT node_id
                FROM reference_source_segments
                WHERE anchor_id = $anchor_id
                  AND node_id IS NOT NULL
              )
              AND node_id NOT IN (
                SELECT node_id
                FROM reference_materials
                WHERE anchor_id = $anchor_id
                  AND node_id IS NOT NULL
              );
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<int> UpsertStage1DeterministicObservationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        string sourceHash,
        IReadOnlyList<ReferenceSourceSegment> segments,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sentenceSegments = segments
            .Where(segment => string.Equals(segment.SegmentType, "sentence", StringComparison.Ordinal))
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .ToArray();
        var runId = BuildStage1RunId(anchorId, sourceHash);
        await UpsertStage1RunAsync(
            connection,
            transaction,
            anchorId,
            runId,
            now,
            sentenceSegments.Length,
            cancellationToken);

        var count = 0;
        foreach (var segment in sentenceSegments)
        {
            var nodeId = BuildTextNodeId(segment.SegmentId);
            var rhythm = BuildDeterministicRhythm(segment.Text);
            await ReferenceCorpusObservationWriter.UpsertAsync(
                connection,
                transaction,
                new ReferenceCorpusFeatureObservation(
                    NodeId: nodeId,
                    NodeType: ReferenceCorpusNodeTypes.Sentence,
                    RunId: runId,
                    AnchorId: anchorId,
                    FeatureFamily: "rhythm",
                    FeatureKey: "length_band",
                    ValueKind: "number",
                    ValueText: rhythm.Label,
                    ValueNum: rhythm.CharCount,
                    ValueBool: null,
                    ValueJson: JsonSerializer.Serialize(
                        new
                        {
                            feature_key = "length_band",
                            label = rhythm.Label,
                            char_count = rhythm.CharCount,
                            cadence = rhythm.Cadence
                        },
                        JsonOptions),
                    Intensity: null,
                    Confidence: 0.95,
                    EvidenceStart: 0,
                    EvidenceEnd: segment.Text.Length,
                    Explanation: "Deterministic sentence length extracted during Stage 1.",
                    ReviewState: "unverified",
                    ValidityState: "active",
                    SupersededByRunId: null,
                    CreatedAt: now),
                cancellationToken);
            count++;

            var sensory = DetectSensory(segment.Text);
            if (sensory.Count > 0)
            {
                var identity = await ReferenceCorpusObservationWriter.UpsertAsync(
                    connection,
                    transaction,
                    new ReferenceCorpusFeatureObservation(
                        NodeId: nodeId,
                        NodeType: ReferenceCorpusNodeTypes.Sentence,
                        RunId: runId,
                        AnchorId: anchorId,
                        FeatureFamily: "sensory",
                        FeatureKey: "senses",
                        ValueKind: "array",
                        ValueText: string.Join(",", sensory.Select(item => item.Sense)),
                        ValueNum: null,
                        ValueBool: null,
                        ValueJson: JsonSerializer.Serialize(
                            sensory.Select(item => new { sense = item.Sense, intensity = item.Intensity }),
                            JsonOptions),
                        Intensity: sensory.Max(item => item.Intensity),
                        Confidence: 0.72,
                        EvidenceStart: 0,
                        EvidenceEnd: segment.Text.Length,
                        Explanation: "Rule-based sensory marker detection during Stage 1.",
                        ReviewState: "unverified",
                        ValidityState: "active",
                        SupersededByRunId: null,
                        CreatedAt: now),
                    cancellationToken);
                await ReplaceSensoryProjectionAsync(
                    connection,
                    transaction,
                    identity.ObservationId,
                    nodeId,
                    anchorId,
                    sensory,
                    cancellationToken);
                count++;
            }

            var emotion = DetectEmotion(segment.Text);
            if (emotion.Length > 0)
            {
                var emotionObservation = MapDeterministicEmotion(emotion);
                await ReferenceCorpusObservationWriter.UpsertAsync(
                    connection,
                    transaction,
                    new ReferenceCorpusFeatureObservation(
                        NodeId: nodeId,
                        NodeType: ReferenceCorpusNodeTypes.Sentence,
                        RunId: runId,
                        AnchorId: anchorId,
                        FeatureFamily: "emotion",
                        FeatureKey: "emotion_state",
                        ValueKind: "enum",
                        ValueText: emotionObservation.Surface,
                        ValueNum: null,
                        ValueBool: null,
                        ValueJson: JsonSerializer.Serialize(
                            new
                            {
                                feature_key = "emotion_state",
                                surface = emotionObservation.Surface,
                                subtext = emotionObservation.Subtext,
                                direction = emotionObservation.Direction,
                                mode = emotionObservation.Mode,
                                deterministic_marker = emotion
                            },
                            JsonOptions),
                        Intensity: emotionObservation.Intensity,
                        Confidence: 0.68,
                        EvidenceStart: 0,
                        EvidenceEnd: segment.Text.Length,
                        Explanation: "Rule-based emotional surface marker detection during Stage 1.",
                        ReviewState: "unverified",
                        ValidityState: "active",
                        SupersededByRunId: null,
                        CreatedAt: now),
                    cancellationToken);
                count++;
            }
        }

        await UpdateStage1ObservationCountAsync(
            connection,
            transaction,
            runId,
            count,
            cancellationToken);
        return count;
    }

    private static async ValueTask UpsertStage1RunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        string runId,
        DateTimeOffset now,
        int sentenceCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_analysis_runs
              (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
               scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
            VALUES
              ($run_id, $anchor_id, 'deterministic-stage1-v1', 'corpus-v1', 'rule', 'deterministic-stage1',
               'sentence', 'completed', NULL, 0, NULL, $started_at, $completed_at, $observation_count)
            ON CONFLICT(run_id) DO UPDATE SET
              status = 'completed',
              completed_at = excluded.completed_at,
              observation_count = excluded.observation_count;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$started_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$completed_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$observation_count", sentenceCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask UpdateStage1ObservationCountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        int observationCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_analysis_runs
            SET observation_count = $observation_count
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$observation_count", observationCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask ReplaceSensoryProjectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string observationId,
        string nodeId,
        long anchorId,
        IReadOnlyList<SensoryDetection> sensory,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM reference_obs_sensory WHERE observation_id = $observation_id;";
            delete.Parameters.AddWithValue("$observation_id", observationId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in sensory)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO reference_obs_sensory
                  (observation_id, node_id, anchor_id, sense, intensity)
                VALUES
                  ($observation_id, $node_id, $anchor_id, $sense, $intensity);
                """;
            insert.Parameters.AddWithValue("$observation_id", observationId);
            insert.Parameters.AddWithValue("$node_id", nodeId);
            insert.Parameters.AddWithValue("$anchor_id", anchorId);
            insert.Parameters.AddWithValue("$sense", item.Sense);
            insert.Parameters.AddWithValue("$intensity", item.Intensity);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask ReplaceMaterialSlotsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        IReadOnlyDictionary<string, IReadOnlyList<ReferenceMaterialSlot>> slotsByMaterial,
        IReadOnlyDictionary<string, DateTimeOffset> materialCreatedAt,
        CancellationToken cancellationToken)
    {
        await using (var deleteSlots = connection.CreateCommand())
        {
            deleteSlots.Transaction = transaction;
            deleteSlots.CommandText = """
                DELETE FROM reference_material_slots
                WHERE material_id IN (
                  SELECT material_id
                  FROM reference_materials
                  WHERE anchor_id = $anchor_id
                );
                """;
            deleteSlots.Parameters.AddWithValue("$anchor_id", anchorId);
            await deleteSlots.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var slots in slotsByMaterial.Values)
        {
            foreach (var slot in slots)
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
                slotInsert.Parameters.AddWithValue(
                    "$created_at",
                    materialCreatedAt.TryGetValue(slot.MaterialId, out var createdAt)
                        ? FormatTimestamp(createdAt)
                        : FormatTimestamp(DateTimeOffset.UtcNow));
                await slotInsert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private IReadOnlyDictionary<string, IReadOnlyList<ReferenceMaterialSlot>> DetectMaterialSlots(
        IReadOnlyList<ReferenceMaterialPayload> materials)
    {
        var slotsByMaterial = new Dictionary<string, IReadOnlyList<ReferenceMaterialSlot>>(StringComparer.Ordinal);
        foreach (var material in materials)
        {
            slotsByMaterial[material.MaterialId] = _slotDetector.Detect(material);
        }

        return slotsByMaterial;
    }

    private static int CountMaterialSlots(IReadOnlyDictionary<string, IReadOnlyList<ReferenceMaterialSlot>> slotsByMaterial)
    {
        return slotsByMaterial.Values.Sum(slots => slots.Count);
    }

    private static IReadOnlyDictionary<string, DateTimeOffset> BuildMaterialCreatedAt(
        IReadOnlyList<ReferenceMaterialPayload> materials)
    {
        return materials.ToDictionary(material => material.MaterialId, material => material.CreatedAt, StringComparer.Ordinal);
    }

    private static async ValueTask<long[]> GetAnchorIdsAsync(
        SqliteConnection connection,
        long novelId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT anchor_id
            FROM reference_anchors
            WHERE {{NovelOrVisibleWorkspaceCorpusPredicate}}
            ORDER BY CASE WHEN novel_id = $novel_id THEN 0 ELSE 1 END,
                     anchor_id ASC;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        command.Parameters.AddWithValue("$workspace_corpus_visibility", ReferenceCorpusVisibilities.Workspace);
        var anchorIds = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            anchorIds.Add(reader.GetInt64(0));
        }

        return anchorIds.ToArray();
    }

    private static async ValueTask<IReadOnlyList<ReferenceSourceSegment>> ReadSourceSegmentsAsync(
        SqliteConnection connection,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT segment_id, chapter_index, chapter_title, segment_type, segment_index,
                   parent_segment_id, start_offset, end_offset, text, text_hash
            FROM reference_source_segments
            WHERE anchor_id = $anchor_id
            ORDER BY chapter_index ASC,
                     start_offset ASC,
                     end_offset ASC,
                     segment_type ASC,
                     segment_index ASC,
                     segment_id ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var segments = new List<ReferenceSourceSegment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            segments.Add(new ReferenceSourceSegment(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.GetString(9)));
        }

        return segments;
    }

    private static async ValueTask<IReadOnlyList<ReferenceMaterialPayload>> ReadMaterialsAsync(
        SqliteConnection connection,
        long novelId,
        IReadOnlyList<long> anchorIds,
        string archiveFilter,
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
            WHERE {{AnchorAliasNovelOrVisibleWorkspaceCorpusPredicate}}
              AND (
                $archive_filter = $archive_filter_all OR
                ($archive_filter = $archive_filter_active AND m.archived_at IS NULL) OR
                ($archive_filter = $archive_filter_archived AND m.archived_at IS NOT NULL)
              )
              AND m.anchor_id IN ({{string.Join(", ", parameterNames)}})
            ORDER BY CASE WHEN a.novel_id = $novel_id THEN 0 ELSE 1 END,
                     m.anchor_id ASC, m.material_id ASC;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        command.Parameters.AddWithValue("$workspace_corpus_visibility", ReferenceCorpusVisibilities.Workspace);
        command.Parameters.AddWithValue("$archive_filter", archiveFilter);
        command.Parameters.AddWithValue("$archive_filter_active", ReferenceMaterialArchiveFilters.Active);
        command.Parameters.AddWithValue("$archive_filter_archived", ReferenceMaterialArchiveFilters.Archived);
        command.Parameters.AddWithValue("$archive_filter_all", ReferenceMaterialArchiveFilters.All);
        var materials = new List<ReferenceMaterialPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            materials.Add(ReadMaterial(reader));
        }

        return materials;
    }

    private static async ValueTask<PageResultPayload<ReferenceMaterialTagReviewItemPayload>> ReadMaterialTagReviewQueueItemsAsync(
        SqliteConnection connection,
        long novelId,
        IReadOnlyList<long> anchorIds,
        string archiveFilter,
        int page,
        int size,
        CancellationToken cancellationToken)
    {
        if (anchorIds.Count == 0)
        {
            return new PageResultPayload<ReferenceMaterialTagReviewItemPayload>([], 0, page, size, 0);
        }

        var parameterNames = new List<string>(anchorIds.Count);
        await using var countCommand = connection.CreateCommand();
        await using var pageCommand = connection.CreateCommand();
        for (var index = 0; index < anchorIds.Count; index++)
        {
            var parameterName = "$anchor_id_" + index.ToString(CultureInfo.InvariantCulture);
            parameterNames.Add(parameterName);
            countCommand.Parameters.AddWithValue(parameterName, anchorIds[index]);
            pageCommand.Parameters.AddWithValue(parameterName, anchorIds[index]);
        }

        var reviewWhere = $$"""
            FROM reference_materials m
            INNER JOIN reference_anchors a ON a.anchor_id = m.anchor_id
            WHERE {{AnchorAliasNovelOrVisibleWorkspaceCorpusPredicate}}
              AND (
                $archive_filter = $archive_filter_all OR
                ($archive_filter = $archive_filter_active AND m.archived_at IS NULL) OR
                ($archive_filter = $archive_filter_archived AND m.archived_at IS NOT NULL)
              )
              AND m.anchor_id IN ({{string.Join(", ", parameterNames)}})
              AND (
                m.user_verified = 0 OR
                m.function_confidence < $material_review_confidence_threshold OR
                m.emotion_confidence < $material_review_confidence_threshold OR
                m.pov_confidence < $material_review_confidence_threshold OR
                lower(trim(m.function_tag)) IN ('', 'unknown', 'untagged', 'none', 'null', 'undefined') OR
                lower(trim(m.emotion_tag)) IN ('', 'unknown', 'untagged', 'none', 'null', 'undefined') OR
                lower(trim(m.scene_tag)) IN ('', 'unknown', 'untagged', 'none', 'null', 'undefined') OR
                lower(trim(m.pov_tag)) IN ('', 'unknown', 'untagged', 'none', 'null', 'undefined') OR
                lower(trim(m.technique_tag)) IN ('', 'unknown', 'untagged', 'none', 'null', 'undefined')
              )
            """;

        countCommand.CommandText = $"SELECT COUNT(*) {reviewWhere};";
        AddMaterialTagReviewQueueParameters(countCommand, novelId, archiveFilter);
        var total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (total == 0)
        {
            return new PageResultPayload<ReferenceMaterialTagReviewItemPayload>([], 0, page, size, 0);
        }

        pageCommand.CommandText = $$"""
            SELECT m.material_id, m.anchor_id, m.source_segment_id, m.material_type,
                   m.function_tag, m.emotion_tag, m.scene_tag, m.pov_tag, m.technique_tag,
                   m.function_confidence, m.emotion_confidence, m.pov_confidence,
                   CASE
                     WHEN length(m.text) > CASE WHEN lower(a.license_status) = $unknown_license_status THEN $unknown_preview_max_chars ELSE $preview_max_chars END
                     THEN substr(m.text, 1, CASE WHEN lower(a.license_status) = $unknown_license_status THEN $unknown_preview_max_chars ELSE $preview_max_chars END) || '...'
                     ELSE m.text
                   END AS text_preview,
                   CASE
                     WHEN length(m.text) > CASE WHEN lower(a.license_status) = $unknown_license_status THEN $unknown_preview_max_chars ELSE $preview_max_chars END
                     THEN 1 ELSE 0
                   END AS text_truncated,
                   m.source_hash, m.extractor_version, m.user_verified, m.created_at,
                   m.archived_at,
                   a.license_status
            {{reviewWhere}}
            ORDER BY CASE WHEN a.novel_id = $novel_id THEN 0 ELSE 1 END,
                     m.anchor_id ASC, m.material_id ASC
            LIMIT $size OFFSET $offset;
            """;
        AddMaterialTagReviewQueueParameters(pageCommand, novelId, archiveFilter);
        pageCommand.Parameters.AddWithValue("$preview_max_chars", MaterialListPreviewMaxChars);
        pageCommand.Parameters.AddWithValue("$unknown_preview_max_chars", UnknownLicensePreviewMaxChars);
        pageCommand.Parameters.AddWithValue("$unknown_license_status", "unknown");
        pageCommand.Parameters.AddWithValue("$size", size);
        pageCommand.Parameters.AddWithValue("$offset", (page - 1) * size);

        var items = new List<ReferenceMaterialTagReviewItemPayload>();
        await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var archivedAt = reader.IsDBNull(18) ? (DateTimeOffset?)null : ParseTimestamp(reader.GetString(18));
            var summary = new ReferenceMaterialSummaryPayload(
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
                NormalizePreviewText(reader.GetString(12)),
                reader.GetInt64(13) != 0,
                reader.GetString(14),
                reader.GetString(15),
                reader.GetInt64(16) != 0,
                ParseTimestamp(reader.GetString(17)),
                archivedAt.HasValue ? ReferenceMaterialArchiveFilters.Archived : ReferenceMaterialArchiveFilters.Active,
                archivedAt);
            items.Add(BuildMaterialTagReviewItem(summary));
        }

        var totalPages = (int)Math.Ceiling(total / (double)size);
        return new PageResultPayload<ReferenceMaterialTagReviewItemPayload>(
            items,
            total,
            page,
            size,
            totalPages);
    }

    private static void AddMaterialTagReviewQueueParameters(
        SqliteCommand command,
        long novelId,
        string archiveFilter)
    {
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        command.Parameters.AddWithValue("$workspace_corpus_visibility", ReferenceCorpusVisibilities.Workspace);
        command.Parameters.AddWithValue("$archive_filter", archiveFilter);
        command.Parameters.AddWithValue("$archive_filter_active", ReferenceMaterialArchiveFilters.Active);
        command.Parameters.AddWithValue("$archive_filter_archived", ReferenceMaterialArchiveFilters.Archived);
        command.Parameters.AddWithValue("$archive_filter_all", ReferenceMaterialArchiveFilters.All);
        command.Parameters.AddWithValue("$material_review_confidence_threshold", MaterialReviewConfidenceThreshold);
    }

    private static async ValueTask<IReadOnlySet<long>> ReadUnknownLicenseAnchorIdsAsync(
        SqliteConnection connection,
        long novelId,
        IReadOnlyList<long> anchorIds,
        CancellationToken cancellationToken)
    {
        if (anchorIds.Count == 0)
        {
            return new HashSet<long>();
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
            SELECT anchor_id
            FROM reference_anchors
            WHERE {{NovelOrVisibleWorkspaceCorpusPredicate}}
              AND license_status = $license_status
              AND anchor_id IN ({{string.Join(", ", parameterNames)}})
            ORDER BY anchor_id ASC;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        command.Parameters.AddWithValue("$workspace_corpus_visibility", ReferenceCorpusVisibilities.Workspace);
        command.Parameters.AddWithValue("$license_status", "unknown");
        var result = new HashSet<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    private static async ValueTask<IReadOnlyDictionary<string, long>> ReadMaterialRowIdsAsync(
        string databasePath,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rowid, material_id
            FROM reference_materials
            WHERE anchor_id = $anchor_id
              AND archived_at IS NULL;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rowIds[reader.GetString(1)] = reader.GetInt64(0);
        }

        return rowIds;
    }

    private static async ValueTask<IReadOnlyList<long>> ReadReadyVectorAnchorIdsAsync(
        SqliteConnection connection,
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
            SELECT anchor_id
            FROM reference_anchor_build_state
            WHERE status = $status
              AND vector_count > 0
              AND anchor_id IN ({{string.Join(", ", parameterNames)}})
            ORDER BY anchor_id ASC;
            """;
        command.Parameters.AddWithValue("$status", ReferenceAnchorBuildStates.Ready);
        var ready = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ready.Add(reader.GetInt64(0));
        }

        return ready;
    }

    private static async ValueTask<IReadOnlyDictionary<long, IReadOnlyDictionary<long, string>>> ReadMaterialRowIdsAsync(
        SqliteConnection connection,
        IReadOnlyList<long> anchorIds,
        CancellationToken cancellationToken)
    {
        if (anchorIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyDictionary<long, string>>();
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
            SELECT anchor_id, rowid, material_id
            FROM reference_materials
            WHERE archived_at IS NULL
              AND anchor_id IN ({{string.Join(", ", parameterNames)}});
            """;
        var byAnchor = new Dictionary<long, Dictionary<long, string>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var anchorId = reader.GetInt64(0);
            if (!byAnchor.TryGetValue(anchorId, out var rows))
            {
                rows = [];
                byAnchor[anchorId] = rows;
            }

            rows[reader.GetInt64(1)] = reader.GetString(2);
        }

        return byAnchor.ToDictionary(
            item => item.Key,
            item => (IReadOnlyDictionary<long, string>)item.Value);
    }

    private static async ValueTask<ReferenceMaterialPayload?> ReadMaterialAsync(
        SqliteConnection connection,
        long novelId,
        string materialId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT m.material_id, m.anchor_id, m.source_segment_id, m.material_type,
                   m.function_tag, m.emotion_tag, m.scene_tag, m.pov_tag, m.technique_tag,
                   m.function_confidence, m.emotion_confidence, m.pov_confidence,
                   m.text, m.source_hash, m.extractor_version, m.user_verified, m.created_at
            FROM reference_materials m
            INNER JOIN reference_anchors a ON a.anchor_id = m.anchor_id
            WHERE {{AnchorAliasNovelOrVisibleWorkspaceCorpusPredicate}}
              AND m.archived_at IS NULL
              AND m.material_id = $material_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        command.Parameters.AddWithValue("$workspace_corpus_visibility", ReferenceCorpusVisibilities.Workspace);
        command.Parameters.AddWithValue("$material_id", materialId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMaterial(reader) : null;
    }

    private static async ValueTask<ReferenceMaterialDetailRow?> ReadMaterialDetailRowAsync(
        SqliteConnection connection,
        long novelId,
        string materialId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT m.material_id, m.anchor_id, m.source_segment_id, m.material_type,
                   m.function_tag, m.emotion_tag, m.scene_tag, m.pov_tag, m.technique_tag,
                   m.function_confidence, m.emotion_confidence, m.pov_confidence,
                   m.text, m.source_hash, m.extractor_version, m.user_verified, m.created_at,
                   m.archived_at,
                   a.anchor_id, a.novel_id, a.title, a.author, '' AS source_path, a.source_kind,
                   a.license_status, a.source_file_hash, a.build_version, a.status,
                   a.created_at, a.updated_at, a.corpus_visibility, a.source_trust, a.user_tags_json
            FROM reference_materials m
            INNER JOIN reference_anchors a ON a.anchor_id = m.anchor_id
            WHERE {{AnchorAliasNovelOrVisibleWorkspaceCorpusPredicate}}
              AND m.material_id = $material_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        command.Parameters.AddWithValue("$workspace_corpus_visibility", ReferenceCorpusVisibilities.Workspace);
        command.Parameters.AddWithValue("$material_id", materialId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var material = ReadMaterial(reader);
        var archivedAt = reader.IsDBNull(17) ? (DateTimeOffset?)null : ParseTimestamp(reader.GetString(17));
        var anchor = new ReferenceAnchorPayload(
            reader.GetInt64(18),
            reader.IsDBNull(19) ? WorkspaceCorpusNovelId : reader.GetInt64(19),
            reader.GetString(20),
            reader.GetString(21),
            reader.GetString(22),
            reader.GetString(23),
            reader.GetString(24),
            reader.GetString(25),
            reader.GetString(26),
            reader.GetString(27),
            ParseTimestamp(reader.GetString(28)),
            ParseTimestamp(reader.GetString(29)),
            reader.GetString(30),
            reader.GetString(31),
            ReadStringList(reader.GetString(32)));
        return new ReferenceMaterialDetailRow(material, anchor, archivedAt);
    }

    private static async ValueTask<IReadOnlyList<ReferenceMaterialSegmentPreviewPayload>> ReadMaterialDetailSegmentsAsync(
        SqliteConnection connection,
        long anchorId,
        string sourceSegmentId,
        int previewMaxChars,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT segment_id, segment_type, chapter_index, chapter_title, segment_index, text, text_hash
            FROM reference_source_segments
            WHERE anchor_id = $anchor_id
              AND (segment_id = $source_segment_id OR parent_segment_id = $source_segment_id)
            ORDER BY CASE WHEN segment_id = $source_segment_id THEN 0 ELSE 1 END,
                     segment_index ASC, segment_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$source_segment_id", sourceSegmentId);
        command.Parameters.AddWithValue("$limit", MaterialDetailMaxSegments);

        var segments = new List<ReferenceMaterialSegmentPreviewPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var preview = BuildPreview(reader.GetString(5), previewMaxChars);
            segments.Add(new ReferenceMaterialSegmentPreviewPayload(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt32(4),
                preview.Text,
                preview.Truncated,
                reader.GetString(6)));
        }

        return segments;
    }

    private static async ValueTask<ReferenceSourceSegmentPreviewPayload?> ReadSourceSegmentPreviewAsync(
        SqliteConnection connection,
        long anchorId,
        string segmentId,
        int previewMaxChars,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT segment_id, anchor_id, segment_type, chapter_index, chapter_title, segment_index,
                   parent_segment_id, start_offset, end_offset, text, text_hash
            FROM reference_source_segments
            WHERE anchor_id = $anchor_id
              AND segment_id = $segment_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$segment_id", segmentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var preview = BuildPreview(reader.GetString(9), previewMaxChars);
        return new ReferenceSourceSegmentPreviewPayload(
            reader.GetInt64(1),
            reader.GetString(0),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            preview.Text,
            preview.Truncated,
            reader.GetString(10));
    }

    private static async ValueTask<IReadOnlyList<ReferenceMaterialPayload>> ReadUserVerifiedMaterialsAsync(
        SqliteConnection connection,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT material_id, anchor_id, source_segment_id, material_type,
                   function_tag, emotion_tag, scene_tag, pov_tag, technique_tag,
                   function_confidence, emotion_confidence, pov_confidence,
                   text, source_hash, extractor_version, user_verified, created_at
            FROM reference_materials
            WHERE anchor_id = $anchor_id
              AND user_verified != 0
            ORDER BY material_id ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var materials = new List<ReferenceMaterialPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            materials.Add(ReadMaterial(reader));
        }

        return materials;
    }

    private static async ValueTask<IReadOnlyList<ArchivedReferenceMaterialMarker>> ReadArchivedMaterialMarkersAsync(
        SqliteConnection connection,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT material_id, material_type, source_hash, archived_at
            FROM reference_materials
            WHERE anchor_id = $anchor_id
              AND archived_at IS NOT NULL
            ORDER BY material_id ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var markers = new List<ArchivedReferenceMaterialMarker>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            markers.Add(new ArchivedReferenceMaterialMarker(
                reader.GetString(0),
                new ReferenceMaterialHashKey(reader.GetString(1), reader.GetString(2)),
                reader.GetString(3)));
        }

        return markers;
    }

    private static async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<ReferenceMaterialSlot>>> ReadMaterialSlotsForAnchorAsync(
        SqliteConnection connection,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sl.slot_id, sl.material_id, sl.slot_name, sl.placeholder,
                   sl.start_offset, sl.end_offset
            FROM reference_material_slots sl
            INNER JOIN reference_materials m ON m.material_id = sl.material_id
            WHERE m.anchor_id = $anchor_id
            ORDER BY sl.material_id ASC, sl.start_offset ASC, sl.slot_id ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var slotsByMaterial = new Dictionary<string, List<ReferenceMaterialSlot>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var slot = new ReferenceMaterialSlot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5));
            if (!slotsByMaterial.TryGetValue(slot.MaterialId, out var slots))
            {
                slots = [];
                slotsByMaterial[slot.MaterialId] = slots;
            }

            slots.Add(slot);
        }

        return slotsByMaterial.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<ReferenceMaterialSlot>)item.Value.ToArray(),
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> BuildArchivedMaterialTimestamps(
        IReadOnlyList<ReferenceMaterialPayload> materials,
        IReadOnlyList<ArchivedReferenceMaterialMarker> archivedMarkers)
    {
        if (materials.Count == 0 || archivedMarkers.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var byMaterialId = archivedMarkers
            .GroupBy(marker => marker.MaterialId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ArchivedAt, StringComparer.Ordinal);
        var byHash = archivedMarkers
            .GroupBy(marker => marker.HashKey)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().ArchivedAt);
        var uniqueNewHashKeys = materials
            .GroupBy(material => new ReferenceMaterialHashKey(material.MaterialType, material.SourceHash))
            .Where(group => group.Count() == 1)
            .Select(group => group.Key)
            .ToHashSet();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var material in materials)
        {
            var hashKey = new ReferenceMaterialHashKey(material.MaterialType, material.SourceHash);
            if (byMaterialId.TryGetValue(material.MaterialId, out var archivedAt) ||
                (uniqueNewHashKeys.Contains(hashKey) && byHash.TryGetValue(hashKey, out archivedAt)))
            {
                result[material.MaterialId] = archivedAt;
            }
        }

        return result;
    }

    private static IReadOnlyList<ReferenceMaterialPayload> ApplyUserVerifiedTagOverrides(
        IReadOnlyList<ReferenceMaterialPayload> materials,
        IReadOnlyList<ReferenceMaterialPayload> userVerifiedMaterials)
    {
        if (userVerifiedMaterials.Count == 0)
        {
            return materials;
        }

        var byMaterialId = userVerifiedMaterials
            .GroupBy(material => material.MaterialId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var uniqueByHash = userVerifiedMaterials
            .GroupBy(material => new ReferenceMaterialHashKey(material.MaterialType, material.SourceHash))
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var uniqueNewHashKeys = materials
            .GroupBy(material => new ReferenceMaterialHashKey(material.MaterialType, material.SourceHash))
            .Where(group => group.Count() == 1)
            .Select(group => group.Key)
            .ToHashSet();

        return materials
            .Select(material =>
            {
                var hashKey = new ReferenceMaterialHashKey(material.MaterialType, material.SourceHash);
                if (!byMaterialId.TryGetValue(material.MaterialId, out var corrected) &&
                    (!uniqueNewHashKeys.Contains(hashKey) || !uniqueByHash.TryGetValue(hashKey, out corrected)))
                {
                    return material;
                }

                return material with
                {
                    FunctionTag = corrected.FunctionTag,
                    EmotionTag = corrected.EmotionTag,
                    SceneTag = corrected.SceneTag,
                    PovTag = corrected.PovTag,
                    TechniqueTag = corrected.TechniqueTag,
                    FunctionConfidence = corrected.FunctionConfidence,
                    EmotionConfidence = corrected.EmotionConfidence,
                    PovConfidence = corrected.PovConfidence,
                    UserVerified = true
                };
            })
            .ToArray();
    }

    private static void EnsureMaterialTagOverride(
        string functionTag,
        string emotionTag,
        string sceneTag,
        string povTag,
        string techniqueTag,
        string argumentName)
    {
        var hasTagOverride = functionTag.Length > 0 ||
            emotionTag.Length > 0 ||
            sceneTag.Length > 0 ||
            povTag.Length > 0 ||
            techniqueTag.Length > 0;
        if (!hasTagOverride)
        {
            throw new ArgumentException("At least one material tag must be provided.", argumentName);
        }
    }

    private static ReferenceMaterialPayload ApplyMaterialTagOverride(
        ReferenceMaterialPayload material,
        string functionTag,
        string emotionTag,
        string sceneTag,
        string povTag,
        string techniqueTag)
    {
        return material with
        {
            FunctionTag = functionTag.Length > 0 ? functionTag : material.FunctionTag,
            EmotionTag = emotionTag.Length > 0 ? emotionTag : material.EmotionTag,
            SceneTag = sceneTag.Length > 0 ? sceneTag : material.SceneTag,
            PovTag = povTag.Length > 0 ? povTag : material.PovTag,
            TechniqueTag = techniqueTag.Length > 0 ? techniqueTag : material.TechniqueTag,
            FunctionConfidence = functionTag.Length > 0 ? 1.0 : material.FunctionConfidence,
            EmotionConfidence = emotionTag.Length > 0 ? 1.0 : material.EmotionConfidence,
            PovConfidence = povTag.Length > 0 ? 1.0 : material.PovConfidence,
            UserVerified = true
        };
    }

    private static async ValueTask UpdateMaterialTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long novelId,
        ReferenceMaterialPayload material,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $$"""
            UPDATE reference_materials
            SET function_tag = $function_tag,
                emotion_tag = $emotion_tag,
                scene_tag = $scene_tag,
                pov_tag = $pov_tag,
                technique_tag = $technique_tag,
                function_confidence = $function_confidence,
                emotion_confidence = $emotion_confidence,
                pov_confidence = $pov_confidence,
                user_verified = 1
            WHERE material_id = $material_id
              AND anchor_id IN (
                SELECT anchor_id
                FROM reference_anchors
                WHERE {{NovelOrVisibleWorkspaceCorpusPredicate}}
              );
            """;
        command.Parameters.AddWithValue("$function_tag", material.FunctionTag);
        command.Parameters.AddWithValue("$emotion_tag", material.EmotionTag);
        command.Parameters.AddWithValue("$scene_tag", material.SceneTag);
        command.Parameters.AddWithValue("$pov_tag", material.PovTag);
        command.Parameters.AddWithValue("$technique_tag", material.TechniqueTag);
        command.Parameters.AddWithValue("$function_confidence", material.FunctionConfidence);
        command.Parameters.AddWithValue("$emotion_confidence", material.EmotionConfidence);
        command.Parameters.AddWithValue("$pov_confidence", material.PovConfidence);
        command.Parameters.AddWithValue("$material_id", material.MaterialId);
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        command.Parameters.AddWithValue("$workspace_corpus_visibility", ReferenceCorpusVisibilities.Workspace);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            throw new ArgumentException("Reference material does not exist.", nameof(material));
        }
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

    private static async ValueTask<IReadOnlyList<ReferenceMaterialSlotPreviewPayload>> ReadMaterialDetailSlotsAsync(
        SqliteConnection connection,
        string materialId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT slot_name, placeholder, start_offset, end_offset
            FROM reference_material_slots
            WHERE material_id = $material_id
            ORDER BY start_offset ASC, slot_name ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$material_id", materialId);
        command.Parameters.AddWithValue("$limit", MaterialDetailMaxSlots);

        var slots = new List<ReferenceMaterialSlotPreviewPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            slots.Add(new ReferenceMaterialSlotPreviewPayload(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3)));
        }

        return slots;
    }

    private async ValueTask<IReadOnlyList<ReferenceMaterialProcessingNotePayload>> ReadMaterialDetailProcessingNotesAsync(
        SqliteConnection connection,
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        var notes = new List<ReferenceMaterialProcessingNotePayload>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT stage, status, source_segment_count, material_count, slot_count, vector_count,
                       last_error, affected_source_id, affected_material_id, affected_segment_id,
                       affected_slot_id, created_at
                FROM reference_anchor_processing_events
                WHERE anchor_id = $anchor_id
                ORDER BY created_at DESC, event_id DESC
                LIMIT 20;
                """;
            command.Parameters.AddWithValue("$anchor_id", anchorId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var lastError = reader.GetString(6);
                var updatedAt = ParseTimestamp(reader.GetString(11));
                notes.Add(new ReferenceMaterialProcessingNotePayload(
                    reader.GetString(0),
                    reader.GetString(1),
                    BuildProcessingNoteMessage(
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        lastError),
                    updatedAt,
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10)));
            }
        }

        var buildStatus = await ReadBuildStatusAsync(connection, novelId, anchorId, cancellationToken);
        if (buildStatus is not null)
        {
            var current = new ReferenceMaterialProcessingNotePayload(
                buildStatus.Stage,
                buildStatus.Status,
                BuildProcessingNoteMessage(buildStatus),
                buildStatus.UpdatedAt,
                buildStatus.SourceSegmentCount,
                buildStatus.MaterialCount,
                buildStatus.SlotCount,
                buildStatus.VectorCount,
                anchorId.ToString(CultureInfo.InvariantCulture));
            if (notes.Count == 0 ||
                !string.Equals(notes[0].Stage, current.Stage, StringComparison.Ordinal) ||
                !string.Equals(notes[0].Status, current.Status, StringComparison.Ordinal) ||
                !string.Equals(notes[0].Message, current.Message, StringComparison.Ordinal))
            {
                notes.Insert(0, current);
            }
        }

        return notes;
    }

    private static async ValueTask<IReadOnlyList<ReferenceSourceProcessingEventPayload>> ReadSourceProcessingEventsAsync(
        SqliteConnection connection,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, stage, status, source_segment_count, material_count, slot_count,
                   vector_count, last_error, affected_source_id, affected_material_id,
                   affected_segment_id, affected_slot_id, created_at
            FROM reference_anchor_processing_events
            WHERE anchor_id = $anchor_id
            ORDER BY created_at DESC, event_id DESC
            LIMIT 100;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);

        var events = new List<ReferenceSourceProcessingEventPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new ReferenceSourceProcessingEventPayload(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                BuildProcessingNoteMessage(
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetString(7)),
                ParseTimestamp(reader.GetString(12)),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11)));
        }

        return events;
    }

    private static async ValueTask<IReadOnlyList<ReferenceSourceProcessingAttemptPayload>> ReadSourceProcessingAttemptsAsync(
        SqliteConnection connection,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attempt_id, attempt_number, build_id, build_version, stage, status,
                   started_at, updated_at, completed_at, event_count, source_segment_count,
                   material_count, slot_count, vector_count, recovered_from_attempt_id,
                   recovered_from_build_id, blocked_reason
            FROM reference_anchor_processing_attempts
            WHERE anchor_id = $anchor_id
            ORDER BY attempt_number ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);

        var attempts = new List<ReferenceSourceProcessingAttemptPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            attempts.Add(new ReferenceSourceProcessingAttemptPayload(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                ParseTimestamp(reader.GetString(6)),
                ParseTimestamp(reader.GetString(7)),
                reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetString(14),
                reader.GetString(15),
                reader.GetString(16)));
        }

        return attempts;
    }

    private static ReferenceSourceProcessingEventPayload BuildSourceProcessingEventFromStatus(
        string eventId,
        long anchorId,
        ReferenceAnchorBuildStatusPayload status)
    {
        return new ReferenceSourceProcessingEventPayload(
            eventId,
            status.Stage,
            status.Status,
            BuildProcessingNoteMessage(status),
            status.UpdatedAt,
            status.SourceSegmentCount,
            status.MaterialCount,
            status.SlotCount,
            status.VectorCount,
            anchorId.ToString(CultureInfo.InvariantCulture),
            string.Empty,
            string.Empty,
            string.Empty);
    }

    private static IReadOnlyList<ReferenceSourceProcessingAttemptPayload> BuildSourceProcessingAttempts(
        long anchorId,
        string buildVersion,
        ReferenceAnchorBuildStatusPayload? currentStatus,
        IReadOnlyList<ReferenceSourceProcessingEventPayload> events)
    {
        var chronologicalEvents = (events ?? Array.Empty<ReferenceSourceProcessingEventPayload>())
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.EventId, StringComparer.Ordinal)
            .ToArray();
        if (chronologicalEvents.Length == 0 && currentStatus is not null)
        {
            chronologicalEvents =
            [
                BuildSourceProcessingEventFromStatus("current", anchorId, currentStatus)
            ];
        }

        if (chronologicalEvents.Length == 0)
        {
            return Array.Empty<ReferenceSourceProcessingAttemptPayload>();
        }

        var attempts = new List<ReferenceSourceProcessingAttemptPayload>();
        var attemptEvents = new List<ReferenceSourceProcessingEventPayload>();
        foreach (var processingEvent in chronologicalEvents)
        {
            if (attemptEvents.Count > 0 && IsTerminalBuildStatus(attemptEvents[^1].Status))
            {
                AddSourceProcessingAttempt(anchorId, buildVersion, attempts, attemptEvents);
                attemptEvents.Clear();
            }

            attemptEvents.Add(processingEvent);
        }

        if (attemptEvents.Count > 0)
        {
            AddSourceProcessingAttempt(anchorId, buildVersion, attempts, attemptEvents);
        }

        return attempts;
    }

    private static void AddSourceProcessingAttempt(
        long anchorId,
        string buildVersion,
        List<ReferenceSourceProcessingAttemptPayload> attempts,
        IReadOnlyList<ReferenceSourceProcessingEventPayload> attemptEvents)
    {
        var first = attemptEvents[0];
        var last = attemptEvents[^1];
        var attemptNumber = attempts.Count + 1;
        var attemptId = BuildSourceProcessingAttemptId(anchorId, attemptNumber);
        var buildId = BuildSourceProcessingBuildId(anchorId, attemptNumber);
        var previous = attempts.Count == 0 ? null : attempts[^1];
        var recoveredFromAttemptId = previous is not null && IsRecoveredFromPriorAttempt(previous.Status, last.Status)
            ? previous.AttemptId
            : string.Empty;
        var recoveredFromBuildId = previous is not null && IsRecoveredFromPriorAttempt(previous.Status, last.Status)
            ? previous.BuildId
            : string.Empty;

        attempts.Add(new ReferenceSourceProcessingAttemptPayload(
            attemptId,
            attemptNumber,
            buildId,
            buildVersion,
            last.Stage,
            last.Status,
            first.CreatedAt,
            last.CreatedAt,
            IsTerminalBuildStatus(last.Status) ? last.CreatedAt : null,
            attemptEvents.Count,
            last.SourceSegmentCount,
            last.MaterialCount,
            last.SlotCount,
            last.VectorCount,
            recoveredFromAttemptId,
            recoveredFromBuildId,
            BuildAttemptBlockedReason(last.Status, last.Message)));
    }

    private static string BuildSourceProcessingAttemptId(long anchorId, int attemptNumber)
    {
        return string.Create(CultureInfo.InvariantCulture, $"anchor:{anchorId}:attempt:{attemptNumber}");
    }

    private static string BuildSourceProcessingBuildId(long anchorId, int attemptNumber)
    {
        return string.Create(CultureInfo.InvariantCulture, $"anchor:{anchorId}:build:{attemptNumber}");
    }

    private static ReferenceSourceProcessingStatusPayload ToSourceProcessingStatus(
        ReferenceAnchorBuildStatusPayload status)
    {
        return new ReferenceSourceProcessingStatusPayload(
            status.Stage,
            status.Status,
            BuildProcessingNoteMessage(status),
            status.UpdatedAt,
            status.SourceSegmentCount,
            status.MaterialCount,
            status.SlotCount,
            status.VectorCount);
    }

    private static bool IsFailedBuildStatus(string status)
    {
        return status.StartsWith("failed_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalBuildStatus(string status)
    {
        return status.Equals(ReferenceAnchorBuildStates.Ready, StringComparison.OrdinalIgnoreCase) ||
            status.Equals(ReferenceAnchorBuildStates.Cancelled, StringComparison.OrdinalIgnoreCase) ||
            IsFailedBuildStatus(status);
    }

    private static bool IsBlockedBuildStatus(string status)
    {
        return status.Equals(ReferenceAnchorBuildStates.Cancelled, StringComparison.OrdinalIgnoreCase) ||
            status.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
            IsFailedBuildStatus(status);
    }

    private static bool IsRecoveredFromPriorAttempt(string previousStatus, string currentStatus)
    {
        return IsBlockedBuildStatus(previousStatus) && !IsBlockedBuildStatus(currentStatus);
    }

    private static string BuildAttemptBlockedReason(string status, string message)
    {
        return IsBlockedBuildStatus(status) ? message : string.Empty;
    }

    private static bool IsRecoverableBuildStatus(string status)
    {
        return status.Equals(ReferenceAnchorBuildStates.Created, StringComparison.OrdinalIgnoreCase) ||
            status.Equals(ReferenceAnchorBuildStates.Importing, StringComparison.OrdinalIgnoreCase) ||
            status.Equals(ReferenceAnchorBuildStates.SourceImported, StringComparison.OrdinalIgnoreCase) ||
            status.Equals(ReferenceAnchorBuildStates.Segmenting, StringComparison.OrdinalIgnoreCase) ||
            status.Equals(ReferenceAnchorBuildStates.SegmentsBuilt, StringComparison.OrdinalIgnoreCase) ||
            status.Equals(ReferenceAnchorBuildStates.ExtractingMaterials, StringComparison.OrdinalIgnoreCase) ||
            status.Equals(ReferenceAnchorBuildStates.MaterialsExtracted, StringComparison.OrdinalIgnoreCase) ||
            status.Equals(ReferenceAnchorBuildStates.DetectingSlots, StringComparison.OrdinalIgnoreCase) ||
            status.Equals(ReferenceAnchorBuildStates.SlotsDetected, StringComparison.OrdinalIgnoreCase) ||
            status.Equals(ReferenceAnchorBuildStates.Embedding, StringComparison.OrdinalIgnoreCase) ||
            status.Equals(ReferenceAnchorBuildStates.Stale, StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask<ReferenceAnchorBuildStatusPayload?> ReadBuildStatusAsync(
        SqliteConnection connection,
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.novel_id, s.anchor_id, s.status, s.stage, s.source_segment_count,
                   s.material_count, s.slot_count, s.vector_count, s.last_error, s.updated_at
            FROM reference_anchor_build_state s
            INNER JOIN reference_anchors a ON a.anchor_id = s.anchor_id
            WHERE (a.novel_id = $novel_id OR
                   ((a.novel_id IS NULL OR a.novel_id = $workspace_corpus_novel_id) AND a.corpus_visibility = $workspace_corpus_visibility))
              AND s.anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        command.Parameters.AddWithValue("$workspace_corpus_visibility", ReferenceCorpusVisibilities.Workspace);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBuildStatus(reader) : null;
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

    private static async ValueTask InsertUserFeedbackAsync(
        SqliteConnection connection,
        ReferenceUserFeedbackPayload feedback,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reference_user_feedback
              (feedback_id, novel_id, target_type, target_id, decision, material_id, candidate_id,
               blueprint_id, beat_id, feedback_tags_json, note, edited_text_hash, origin, created_at)
            VALUES
              ($feedback_id, $novel_id, $target_type, $target_id, $decision, $material_id, $candidate_id,
               $blueprint_id, $beat_id, $feedback_tags_json, $note, $edited_text_hash, $origin, $created_at);
            """;
        command.Parameters.AddWithValue("$feedback_id", feedback.FeedbackId);
        command.Parameters.AddWithValue("$novel_id", feedback.NovelId);
        command.Parameters.AddWithValue("$target_type", feedback.TargetType);
        command.Parameters.AddWithValue("$target_id", feedback.TargetId);
        command.Parameters.AddWithValue("$decision", feedback.Decision);
        command.Parameters.AddWithValue("$material_id", feedback.MaterialId);
        command.Parameters.AddWithValue("$candidate_id", feedback.CandidateId);
        command.Parameters.AddWithValue("$blueprint_id", feedback.BlueprintId);
        command.Parameters.AddWithValue("$beat_id", feedback.BeatId);
        command.Parameters.AddWithValue("$feedback_tags_json", JsonSerializer.Serialize(feedback.FeedbackTags, JsonOptions));
        command.Parameters.AddWithValue("$note", feedback.Note);
        command.Parameters.AddWithValue("$edited_text_hash", feedback.EditedTextHash);
        command.Parameters.AddWithValue("$origin", feedback.Origin);
        command.Parameters.AddWithValue("$created_at", FormatTimestamp(feedback.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<IReadOnlyList<ReferenceUserFeedbackPayload>> ReadUserFeedbackAsync(
        SqliteConnection connection,
        long novelId,
        string targetType,
        string targetId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var where = new List<string> { "novel_id = $novel_id" };
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$limit", limit);
        if (targetType.Length > 0)
        {
            where.Add("target_type = $target_type");
            command.Parameters.AddWithValue("$target_type", targetType);
        }

        if (targetId.Length > 0)
        {
            where.Add("target_id = $target_id");
            command.Parameters.AddWithValue("$target_id", targetId);
        }

        command.CommandText = $$"""
            SELECT feedback_id, novel_id, target_type, target_id, decision, material_id, candidate_id,
                   blueprint_id, beat_id, feedback_tags_json, note, edited_text_hash, origin, created_at
            FROM reference_user_feedback
            WHERE {{string.Join(" AND ", where)}}
            ORDER BY created_at ASC, feedback_id ASC
            LIMIT $limit;
            """;
        var feedback = new List<ReferenceUserFeedbackPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            feedback.Add(ReadUserFeedback(reader));
        }

        return feedback;
    }

    private static async ValueTask<IReadOnlySet<string>> ReadAcceptedFeedbackMaterialIdsAsync(
        SqliteConnection connection,
        long novelId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT material_id
            FROM reference_user_feedback
            WHERE novel_id = $novel_id
              AND decision = $decision
              AND material_id <> '';
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$decision", ReferenceFeedbackDecisions.Accepted);
        var materialIds = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            materialIds.Add(reader.GetString(0));
        }

        return materialIds;
    }

    private static async ValueTask<bool> ReuseCandidateExistsAsync(
        SqliteConnection connection,
        long novelId,
        string candidateId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT 1
            FROM reference_reuse_candidates c
            INNER JOIN reference_materials m ON m.material_id = c.material_id
            INNER JOIN reference_anchors a ON a.anchor_id = m.anchor_id
            WHERE {{AnchorAliasNovelOrVisibleWorkspaceCorpusPredicate}}
              AND c.candidate_id = $candidate_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        command.Parameters.AddWithValue("$workspace_corpus_visibility", ReferenceCorpusVisibilities.Workspace);
        command.Parameters.AddWithValue("$candidate_id", candidateId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private async ValueTask<ReferenceAnchorPayload> ReadAnchorAsync(
        SqliteConnection connection,
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT anchor_id, novel_id, title, author, source_path, source_kind, license_status,
                   source_file_hash, build_version, status, created_at, updated_at,
                   corpus_visibility, source_trust, user_tags_json
            FROM reference_anchors
            WHERE {{NovelOrVisibleWorkspaceCorpusPredicate}}
              AND anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        command.Parameters.AddWithValue("$workspace_corpus_visibility", ReferenceCorpusVisibilities.Workspace);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ArgumentException($"Reference anchor '{anchorId}' does not exist.", nameof(anchorId));
        }

        return ReadAnchor(reader);
    }

    private async ValueTask<ReferenceAnchorPayload?> TryReadAnchorAsync(
        SqliteConnection connection,
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT anchor_id, novel_id, title, author, source_path, source_kind, license_status,
                   source_file_hash, build_version, status, created_at, updated_at,
                   corpus_visibility, source_trust, user_tags_json
            FROM reference_anchors
            WHERE {{NovelOrVisibleWorkspaceCorpusPredicate}}
              AND anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        command.Parameters.AddWithValue("$workspace_corpus_visibility", ReferenceCorpusVisibilities.Workspace);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAnchor(reader) : null;
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

            CREATE TABLE IF NOT EXISTS reference_anchor_processing_events (
              event_id TEXT PRIMARY KEY,
              anchor_id INTEGER NOT NULL,
              attempt_id TEXT NOT NULL DEFAULT '',
              build_id TEXT NOT NULL DEFAULT '',
              attempt_number INTEGER NOT NULL DEFAULT 0,
              build_version TEXT NOT NULL DEFAULT '',
              stage TEXT NOT NULL,
              status TEXT NOT NULL,
              source_segment_count INTEGER NOT NULL,
              material_count INTEGER NOT NULL,
              slot_count INTEGER NOT NULL,
              vector_count INTEGER NOT NULL,
              last_error TEXT NOT NULL,
              affected_source_id TEXT NOT NULL,
              affected_material_id TEXT NOT NULL,
              affected_segment_id TEXT NOT NULL,
              affected_slot_id TEXT NOT NULL,
              created_at TEXT NOT NULL,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_anchor_processing_attempts (
              attempt_id TEXT PRIMARY KEY,
              anchor_id INTEGER NOT NULL,
              build_id TEXT NOT NULL,
              attempt_number INTEGER NOT NULL,
              build_version TEXT NOT NULL,
              stage TEXT NOT NULL,
              status TEXT NOT NULL,
              started_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              completed_at TEXT,
              event_count INTEGER NOT NULL,
              source_segment_count INTEGER NOT NULL,
              material_count INTEGER NOT NULL,
              slot_count INTEGER NOT NULL,
              vector_count INTEGER NOT NULL,
              recovered_from_attempt_id TEXT NOT NULL,
              recovered_from_build_id TEXT NOT NULL,
              blocked_reason TEXT NOT NULL,
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
              archived_at TEXT,
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

            CREATE TABLE IF NOT EXISTS reference_user_feedback (
              feedback_id TEXT PRIMARY KEY,
              novel_id INTEGER NOT NULL,
              target_type TEXT NOT NULL,
              target_id TEXT NOT NULL,
              decision TEXT NOT NULL,
              material_id TEXT NOT NULL,
              candidate_id TEXT NOT NULL,
              blueprint_id INTEGER NOT NULL,
              beat_id TEXT NOT NULL,
              feedback_tags_json TEXT NOT NULL,
              note TEXT NOT NULL,
              edited_text_hash TEXT NOT NULL,
              origin TEXT NOT NULL,
              created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS reference_obs_sensory (
              observation_id TEXT NOT NULL,
              node_id TEXT NOT NULL,
              anchor_id INTEGER NOT NULL,
              sense TEXT NOT NULL,
              intensity REAL NOT NULL,
              PRIMARY KEY(observation_id, sense),
              FOREIGN KEY(observation_id) REFERENCES reference_feature_observations(observation_id) ON DELETE CASCADE,
              FOREIGN KEY(node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_technique_specimens (
              specimen_id TEXT PRIMARY KEY,
              source_node_id TEXT NOT NULL,
              source_anchor_id INTEGER NOT NULL,
              analysis_run_id TEXT NOT NULL,
              technique_family TEXT NOT NULL,
              technique_abstract TEXT NOT NULL,
              trigger_context TEXT NOT NULL,
              transfer_template TEXT NOT NULL,
              transfer_slots_json TEXT NOT NULL,
              effect_on_reader TEXT NOT NULL,
              applicability_conditions TEXT NOT NULL,
              failure_modes TEXT NOT NULL,
              anti_patterns TEXT NOT NULL,
              world_context_dependencies TEXT,
              why_it_works_json TEXT NOT NULL,
              confidence REAL NOT NULL,
              review_state TEXT NOT NULL DEFAULT 'unverified',
              validity_state TEXT NOT NULL DEFAULT 'active',
              superseded_by_run_id TEXT,
              mastery_notes TEXT,
              created_at TEXT NOT NULL,
              FOREIGN KEY(source_node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE,
              FOREIGN KEY(source_anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE,
              FOREIGN KEY(analysis_run_id) REFERENCES reference_analysis_runs(run_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_technique_vectors (
              vector_id TEXT PRIMARY KEY,
              specimen_id TEXT NOT NULL,
              source_node_id TEXT NOT NULL,
              source_anchor_id INTEGER NOT NULL,
              provider_key TEXT NOT NULL,
              model_id TEXT NOT NULL,
              dimensions INTEGER NOT NULL,
              technique_hash TEXT NOT NULL,
              embedding_json TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              FOREIGN KEY(specimen_id) REFERENCES reference_technique_specimens(specimen_id) ON DELETE CASCADE,
              FOREIGN KEY(source_node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE,
              FOREIGN KEY(source_anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_technique_vectors_generation
              ON reference_technique_vectors(specimen_id, provider_key, model_id, dimensions);

            CREATE INDEX IF NOT EXISTS idx_reference_technique_vectors_node
              ON reference_technique_vectors(source_node_id, provider_key, model_id, dimensions);

            CREATE INDEX IF NOT EXISTS idx_reference_technique_vectors_anchor
              ON reference_technique_vectors(source_anchor_id, provider_key, model_id, dimensions);

            CREATE TABLE IF NOT EXISTS reference_technique_vector_rows (
              index_scope_key TEXT NOT NULL,
              row_id INTEGER NOT NULL,
              vector_id TEXT NOT NULL,
              specimen_id TEXT NOT NULL,
              source_node_id TEXT NOT NULL,
              source_anchor_id INTEGER NOT NULL,
              provider_key TEXT NOT NULL,
              model_id TEXT NOT NULL,
              dimensions INTEGER NOT NULL,
              technique_hash TEXT NOT NULL,
              table_name TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              PRIMARY KEY(index_scope_key, row_id),
              FOREIGN KEY(vector_id) REFERENCES reference_technique_vectors(vector_id) ON DELETE CASCADE,
              FOREIGN KEY(specimen_id) REFERENCES reference_technique_specimens(specimen_id) ON DELETE CASCADE,
              FOREIGN KEY(source_node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE,
              FOREIGN KEY(source_anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_technique_vector_rows_vector
              ON reference_technique_vector_rows(index_scope_key, vector_id);

            CREATE INDEX IF NOT EXISTS idx_reference_technique_vector_rows_scope_node
              ON reference_technique_vector_rows(index_scope_key, source_node_id);

            CREATE TABLE IF NOT EXISTS reference_technique_vector_index_state (
              index_scope_key TEXT PRIMARY KEY,
              table_name TEXT NOT NULL,
              provider_key TEXT NOT NULL,
              model_id TEXT NOT NULL,
              dimensions INTEGER NOT NULL,
              source_hash TEXT NOT NULL,
              source_count INTEGER NOT NULL,
              updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS reference_specimen_evidence (
              specimen_id TEXT NOT NULL,
              observation_id TEXT NOT NULL,
              PRIMARY KEY(specimen_id, observation_id),
              FOREIGN KEY(specimen_id) REFERENCES reference_technique_specimens(specimen_id) ON DELETE CASCADE,
              FOREIGN KEY(observation_id) REFERENCES reference_feature_observations(observation_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_template_examples (
              template_id TEXT NOT NULL,
              node_id TEXT NOT NULL,
              PRIMARY KEY(template_id, node_id),
              FOREIGN KEY(node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_aggregate_provenance (
              aggregate_id TEXT NOT NULL,
              aggregate_kind TEXT NOT NULL,
              library_id TEXT,
              anchor_id INTEGER NOT NULL,
              run_id TEXT NOT NULL,
              PRIMARY KEY(aggregate_id, anchor_id, run_id),
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE,
              FOREIGN KEY(run_id) REFERENCES reference_analysis_runs(run_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_corpus_libraries (
              library_id TEXT PRIMARY KEY,
              scope TEXT NOT NULL,
              novel_id INTEGER,
              name TEXT NOT NULL,
              created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS reference_library_members (
              library_id TEXT NOT NULL,
              anchor_id INTEGER NOT NULL,
              enabled INTEGER NOT NULL DEFAULT 1,
              source_quality TEXT,
              disabled_reason TEXT,
              dedup_group_id TEXT,
              PRIMARY KEY(library_id, anchor_id),
              FOREIGN KEY(library_id) REFERENCES reference_corpus_libraries(library_id) ON DELETE CASCADE,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_session_library_binding (
              session_id TEXT NOT NULL,
              library_id TEXT NOT NULL,
              PRIMARY KEY(session_id, library_id),
              FOREIGN KEY(library_id) REFERENCES reference_corpus_libraries(library_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_source_license (
              anchor_id INTEGER PRIMARY KEY,
              license_state TEXT NOT NULL,
              authorization_evidence TEXT,
              reuse_policy TEXT NOT NULL,
              max_verbatim_ratio REAL,
              cleared_for_insertion INTEGER NOT NULL DEFAULT 0,
              reviewed_at TEXT,
              FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE
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

            CREATE INDEX IF NOT EXISTS idx_reference_feedback_novel_target
              ON reference_user_feedback(novel_id, target_type, target_id, created_at);

            CREATE INDEX IF NOT EXISTS idx_reference_feedback_material
              ON reference_user_feedback(material_id, created_at);

            CREATE INDEX IF NOT EXISTS idx_reference_processing_events_anchor
              ON reference_anchor_processing_events(anchor_id, created_at);

            CREATE UNIQUE INDEX IF NOT EXISTS idx_reference_processing_attempts_anchor_number
              ON reference_anchor_processing_attempts(anchor_id, attempt_number);

            CREATE INDEX IF NOT EXISTS idx_reference_processing_attempts_anchor_updated
              ON reference_anchor_processing_attempts(anchor_id, updated_at);

            CREATE INDEX IF NOT EXISTS idx_reference_obs_sensory_query
              ON reference_obs_sensory(anchor_id, sense, intensity);

            CREATE INDEX IF NOT EXISTS idx_reference_technique_specimens_source
              ON reference_technique_specimens(source_anchor_id, source_node_id, validity_state);

            CREATE INDEX IF NOT EXISTS idx_reference_specimen_evidence_observation
              ON reference_specimen_evidence(observation_id, specimen_id);

            CREATE INDEX IF NOT EXISTS idx_reference_aggregate_provenance_anchor_run
              ON reference_aggregate_provenance(anchor_id, run_id, aggregate_kind);

            CREATE INDEX IF NOT EXISTS idx_reference_library_members_anchor
              ON reference_library_members(anchor_id, enabled);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
        var addedCorpusVisibilityColumn = await EnsureColumnAsync(
            connection,
            "reference_anchors",
            "corpus_visibility",
            "ALTER TABLE reference_anchors ADD COLUMN corpus_visibility TEXT NOT NULL DEFAULT 'private';",
            cancellationToken);
        if (addedCorpusVisibilityColumn)
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
        await EnsureColumnAsync(
            connection,
            "reference_source_segments",
            "node_id",
            "ALTER TABLE reference_source_segments ADD COLUMN node_id TEXT REFERENCES reference_text_nodes(node_id);",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_materials",
            "node_id",
            "ALTER TABLE reference_materials ADD COLUMN node_id TEXT REFERENCES reference_text_nodes(node_id);",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_anchor_processing_events",
            "attempt_id",
            "ALTER TABLE reference_anchor_processing_events ADD COLUMN attempt_id TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_anchor_processing_events",
            "build_id",
            "ALTER TABLE reference_anchor_processing_events ADD COLUMN build_id TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_anchor_processing_events",
            "attempt_number",
            "ALTER TABLE reference_anchor_processing_events ADD COLUMN attempt_number INTEGER NOT NULL DEFAULT 0;",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_anchor_processing_events",
            "build_version",
            "ALTER TABLE reference_anchor_processing_events ADD COLUMN build_version TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureNullableAnchorNovelIdAsync(connection, cancellationToken);
        await PromoteLegacyOwnedWorkspaceCorpusRowsAsync(connection, cancellationToken);
        await EnsureImportIdentityUniqueIndexAsync(connection, cancellationToken);
        await BackfillReferenceProcessingAttemptsAsync(connection, cancellationToken);
        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_reference_anchors_corpus_visibility
              ON reference_anchors(novel_id, corpus_visibility);

            CREATE INDEX IF NOT EXISTS idx_reference_materials_archived
              ON reference_materials(anchor_id, archived_at);
            """;
        await indexCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask BackfillReferenceProcessingAttemptsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var anchorIds = new List<long>();
        await using (var readAnchors = connection.CreateCommand())
        {
            readAnchors.CommandText = """
                SELECT DISTINCT e.anchor_id
                FROM reference_anchor_processing_events e
                WHERE e.attempt_id = ''
                   OR e.build_id = ''
                   OR e.attempt_number = 0
                   OR e.build_version = ''
                   OR NOT EXISTS (
                        SELECT 1
                        FROM reference_anchor_processing_attempts attempts
                        WHERE attempts.attempt_id = e.attempt_id
                      )
                ORDER BY e.anchor_id ASC;
                """;
            await using var reader = await readAnchors.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                anchorIds.Add(reader.GetInt64(0));
            }
        }

        foreach (var anchorId in anchorIds)
        {
            var rows = await ReadProcessingEventBackfillRowsAsync(connection, anchorId, cancellationToken);
            await BackfillReferenceProcessingAttemptsForAnchorAsync(connection, rows, cancellationToken);
        }
    }

    private static async ValueTask<IReadOnlyList<ReferenceProcessingEventBackfillRow>> ReadProcessingEventBackfillRowsAsync(
        SqliteConnection connection,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT e.event_id, e.anchor_id, a.build_version, e.stage, e.status,
                   e.source_segment_count, e.material_count, e.slot_count, e.vector_count,
                   e.last_error, e.created_at
            FROM reference_anchor_processing_events e
            INNER JOIN reference_anchors a ON a.anchor_id = e.anchor_id
            WHERE e.anchor_id = $anchor_id
            ORDER BY e.created_at ASC, e.event_id ASC;
            """;
        read.Parameters.AddWithValue("$anchor_id", anchorId);

        var rows = new List<ReferenceProcessingEventBackfillRow>();
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ReferenceProcessingEventBackfillRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetString(9),
                ParseTimestamp(reader.GetString(10))));
        }

        return rows;
    }

    private static async ValueTask BackfillReferenceProcessingAttemptsForAnchorAsync(
        SqliteConnection connection,
        IReadOnlyList<ReferenceProcessingEventBackfillRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using (var deleteAttempts = connection.CreateCommand())
        {
            deleteAttempts.CommandText = """
                DELETE FROM reference_anchor_processing_attempts
                WHERE anchor_id = $anchor_id;
                """;
            deleteAttempts.Parameters.AddWithValue("$anchor_id", rows[0].AnchorId);
            await deleteAttempts.ExecuteNonQueryAsync(cancellationToken);
        }

        var attempts = new List<IReadOnlyList<ReferenceProcessingEventBackfillRow>>();
        var attemptRows = new List<ReferenceProcessingEventBackfillRow>();
        foreach (var row in rows)
        {
            if (attemptRows.Count > 0 && IsTerminalBuildStatus(attemptRows[^1].Status))
            {
                attempts.Add(attemptRows.ToArray());
                attemptRows.Clear();
            }

            attemptRows.Add(row);
        }

        if (attemptRows.Count > 0)
        {
            attempts.Add(attemptRows.ToArray());
        }

        for (var index = 0; index < attempts.Count; index++)
        {
            var attemptNumber = index + 1;
            var attemptEvents = attempts[index];
            var first = attemptEvents[0];
            var last = attemptEvents[^1];
            var attemptId = BuildSourceProcessingAttemptId(first.AnchorId, attemptNumber);
            var buildId = BuildSourceProcessingBuildId(first.AnchorId, attemptNumber);
            var previous = index == 0 ? null : attempts[index - 1][^1];
            var recoveredFromAttemptId = previous is not null && IsRecoveredFromPriorAttempt(previous.Status, last.Status)
                ? BuildSourceProcessingAttemptId(first.AnchorId, attemptNumber - 1)
                : string.Empty;
            var recoveredFromBuildId = previous is not null && IsRecoveredFromPriorAttempt(previous.Status, last.Status)
                ? BuildSourceProcessingBuildId(first.AnchorId, attemptNumber - 1)
                : string.Empty;

            await UpsertBackfilledProcessingAttemptAsync(
                connection,
                first.AnchorId,
                attemptId,
                buildId,
                attemptNumber,
                last.BuildVersion,
                first.CreatedAt,
                last,
                attemptEvents.Count,
                recoveredFromAttemptId,
                recoveredFromBuildId,
                cancellationToken);
            foreach (var processingEvent in attemptEvents)
            {
                await UpdateBackfilledProcessingEventAsync(
                    connection,
                    processingEvent.EventId,
                    attemptId,
                    buildId,
                    attemptNumber,
                    last.BuildVersion,
                    cancellationToken);
            }
        }
    }

    private static async ValueTask UpsertBackfilledProcessingAttemptAsync(
        SqliteConnection connection,
        long anchorId,
        string attemptId,
        string buildId,
        int attemptNumber,
        string buildVersion,
        DateTimeOffset startedAt,
        ReferenceProcessingEventBackfillRow last,
        int eventCount,
        string recoveredFromAttemptId,
        string recoveredFromBuildId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reference_anchor_processing_attempts
              (attempt_id, anchor_id, build_id, attempt_number, build_version, stage, status,
               started_at, updated_at, completed_at, event_count, source_segment_count,
               material_count, slot_count, vector_count, recovered_from_attempt_id,
               recovered_from_build_id, blocked_reason)
            VALUES
              ($attempt_id, $anchor_id, $build_id, $attempt_number, $build_version, $stage, $status,
               $started_at, $updated_at, $completed_at, $event_count, $source_segment_count,
               $material_count, $slot_count, $vector_count, $recovered_from_attempt_id,
               $recovered_from_build_id, $blocked_reason)
            ON CONFLICT(attempt_id) DO UPDATE SET
              build_version = excluded.build_version,
              stage = excluded.stage,
              status = excluded.status,
              started_at = excluded.started_at,
              updated_at = excluded.updated_at,
              completed_at = excluded.completed_at,
              event_count = excluded.event_count,
              source_segment_count = excluded.source_segment_count,
              material_count = excluded.material_count,
              slot_count = excluded.slot_count,
              vector_count = excluded.vector_count,
              recovered_from_attempt_id = excluded.recovered_from_attempt_id,
              recovered_from_build_id = excluded.recovered_from_build_id,
              blocked_reason = excluded.blocked_reason;
            """;
        command.Parameters.AddWithValue("$attempt_id", attemptId);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$build_id", buildId);
        command.Parameters.AddWithValue("$attempt_number", attemptNumber);
        command.Parameters.AddWithValue("$build_version", buildVersion);
        command.Parameters.AddWithValue("$stage", last.Stage);
        command.Parameters.AddWithValue("$status", last.Status);
        command.Parameters.AddWithValue("$started_at", FormatTimestamp(startedAt));
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(last.CreatedAt));
        command.Parameters.AddWithValue("$completed_at", IsTerminalBuildStatus(last.Status) ? FormatTimestamp(last.CreatedAt) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$event_count", eventCount);
        command.Parameters.AddWithValue("$source_segment_count", last.SourceSegmentCount);
        command.Parameters.AddWithValue("$material_count", last.MaterialCount);
        command.Parameters.AddWithValue("$slot_count", last.SlotCount);
        command.Parameters.AddWithValue("$vector_count", last.VectorCount);
        command.Parameters.AddWithValue("$recovered_from_attempt_id", recoveredFromAttemptId);
        command.Parameters.AddWithValue("$recovered_from_build_id", recoveredFromBuildId);
        command.Parameters.AddWithValue("$blocked_reason", BuildAttemptBlockedReason(last.Status, last.LastError));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask UpdateBackfilledProcessingEventAsync(
        SqliteConnection connection,
        string eventId,
        string attemptId,
        string buildId,
        int attemptNumber,
        string buildVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_anchor_processing_events
            SET attempt_id = $attempt_id,
                build_id = $build_id,
                attempt_number = $attempt_number,
                build_version = $build_version
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$attempt_id", attemptId);
        command.Parameters.AddWithValue("$build_id", buildId);
        command.Parameters.AddWithValue("$attempt_number", attemptNumber);
        command.Parameters.AddWithValue("$build_version", buildVersion);
        command.Parameters.AddWithValue("$event_id", eventId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask EnsureImportIdentityUniqueIndexAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await QuarantineLegacyDuplicateImportIdentitiesAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS idx_reference_anchors_import_identity_unique
              ON reference_anchors(
                corpus_visibility,
                COALESCE(novel_id, 0),
                source_path,
                source_kind,
                source_file_hash
              );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask QuarantineLegacyDuplicateImportIdentitiesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_anchors
            SET source_file_hash = 'legacy-duplicate:' || anchor_id || ':' || source_file_hash
            WHERE source_file_hash NOT LIKE 'legacy-duplicate:%'
              AND EXISTS (
                SELECT 1
                FROM reference_anchors canonical
                WHERE canonical.anchor_id < reference_anchors.anchor_id
                  AND canonical.corpus_visibility = reference_anchors.corpus_visibility
                  AND COALESCE(canonical.novel_id, 0) = COALESCE(reference_anchors.novel_id, 0)
                  AND canonical.source_path = reference_anchors.source_path
                  AND canonical.source_kind = reference_anchors.source_kind
                  AND canonical.source_file_hash = reference_anchors.source_file_hash
              );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask EnsureNullableAnchorNovelIdAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(reference_anchors);";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), "novel_id", StringComparison.OrdinalIgnoreCase))
                {
                    if (reader.GetInt32(3) == 0)
                    {
                        return;
                    }

                    break;
                }
            }
        }

        await using (var foreignKeysOff = connection.CreateCommand())
        {
            foreignKeysOff.CommandText = """
                PRAGMA foreign_keys = OFF;
                PRAGMA legacy_alter_table = ON;
                """;
            await foreignKeysOff.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var rebuild = connection.CreateCommand())
            {
                rebuild.Transaction = transaction;
                rebuild.CommandText = """
                    ALTER TABLE reference_anchors RENAME TO reference_anchors_old;

                    CREATE TABLE reference_anchors (
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

                    INSERT INTO reference_anchors (
                      anchor_id, novel_id, title, author, source_path, source_kind, license_status,
                      source_file_hash, build_version, status, created_at, updated_at,
                      corpus_visibility, source_trust, user_tags_json
                    )
                    SELECT
                      anchor_id, novel_id, title, author, source_path, source_kind, license_status,
                      source_file_hash, build_version, status, created_at, updated_at,
                      corpus_visibility, source_trust, user_tags_json
                    FROM reference_anchors_old;

                    DROP TABLE reference_anchors_old;
                    """;
                await rebuild.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            await using var restorePragmas = connection.CreateCommand();
            restorePragmas.CommandText = """
                PRAGMA legacy_alter_table = OFF;
                PRAGMA foreign_keys = ON;
                """;
            await restorePragmas.ExecuteNonQueryAsync(cancellationToken);
        }
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

    private static async ValueTask PromoteLegacyOwnedWorkspaceCorpusRowsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_anchors
            SET novel_id = NULL
            WHERE novel_id IS NOT NULL
              AND novel_id <> $workspace_corpus_novel_id
              AND corpus_visibility = $workspace_visibility;
            """;
        command.Parameters.AddWithValue("$workspace_corpus_novel_id", WorkspaceCorpusNovelId);
        command.Parameters.AddWithValue("$workspace_visibility", ReferenceCorpusVisibilities.Workspace);
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

    private static bool IsSqliteConstraintViolation(SqliteException exception)
    {
        return exception.SqliteErrorCode == SqliteConstraintErrorCode;
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

        AppendAdvancedSegments(anchorId, sourceText, chapters, segments);
        return segments;
    }

    private static void AppendAdvancedSegments(
        long anchorId,
        string sourceText,
        IReadOnlyList<TextSpan> chapters,
        List<ReferenceSourceSegment> segments)
    {
        var sceneIndex = 0;
        var beatIndex = 0;
        var dialogueExchangeIndex = 0;
        var actionAfterbeatIndex = 0;
        var imageMotifIndex = 0;
        var hookIndex = 0;
        var payoffIndex = 0;
        var transitionIndex = 0;

        for (var chapterOffset = 0; chapterOffset < chapters.Count; chapterOffset++)
        {
            var chapter = chapters[chapterOffset];
            var chapterIndex = chapterOffset + 1;
            var chapterHash = HashText(chapter.Text);
            var chapterId = BuildSegmentId(anchorId, "chapter", chapterIndex, 0, chapterHash);
            var paragraphs = SplitParagraphs(chapter.Text, chapter.StartOffset).ToArray();
            if (paragraphs.Length == 0)
            {
                continue;
            }

            for (var sceneStart = 0; sceneStart < paragraphs.Length;)
            {
                var sceneEnd = FindSceneEnd(paragraphs, sceneStart);
                var sceneSpan = BuildAbsoluteSpan(
                    sourceText,
                    paragraphs[sceneStart].StartOffset,
                    paragraphs[sceneEnd - 1].EndOffset);
                var sceneId = AddAdvancedSegment(
                    segments,
                    anchorId,
                    chapterIndex,
                    chapter.Title,
                    ReferenceMaterialTypes.Scene,
                    ref sceneIndex,
                    chapterId,
                    sceneSpan);
                if (sceneId.Length == 0)
                {
                    sceneStart = sceneEnd;
                    continue;
                }

                for (var paragraphOffset = sceneStart; paragraphOffset < sceneEnd; paragraphOffset++)
                {
                    var beat = paragraphs[paragraphOffset];
                    var beatId = AddAdvancedSegment(
                        segments,
                        anchorId,
                        chapterIndex,
                        chapter.Title,
                        ReferenceMaterialTypes.Beat,
                        ref beatIndex,
                        sceneId,
                        beat);
                    if (beatId.Length == 0)
                    {
                        continue;
                    }

                    AddAdvancedChildSegments(
                        segments,
                        anchorId,
                        chapterIndex,
                        chapter.Title,
                        beatId,
                        beat,
                        isLastBeatInChapter: paragraphOffset == paragraphs.Length - 1,
                        ref dialogueExchangeIndex,
                        ref actionAfterbeatIndex,
                        ref imageMotifIndex,
                        ref hookIndex,
                        ref payoffIndex,
                        ref transitionIndex);
                }

                sceneStart = sceneEnd;
            }
        }
    }

    private static void AddAdvancedChildSegments(
        List<ReferenceSourceSegment> segments,
        long anchorId,
        int chapterIndex,
        string chapterTitle,
        string beatId,
        TextSpan beat,
        bool isLastBeatInChapter,
        ref int dialogueExchangeIndex,
        ref int actionAfterbeatIndex,
        ref int imageMotifIndex,
        ref int hookIndex,
        ref int payoffIndex,
        ref int transitionIndex)
    {
        if (ContainsAny(beat.Text, DialogueMarkers))
        {
            AddAdvancedSegment(
                segments,
                anchorId,
                chapterIndex,
                chapterTitle,
                ReferenceMaterialTypes.DialogueExchange,
                ref dialogueExchangeIndex,
                beatId,
                BuildEvidenceWindow(beat, DialogueMarkers));
        }

        if (IsActionAfterbeatCandidate(beat.Text))
        {
            AddAdvancedSegment(
                segments,
                anchorId,
                chapterIndex,
                chapterTitle,
                ReferenceMaterialTypes.ActionAfterbeat,
                ref actionAfterbeatIndex,
                beatId,
                BuildEvidenceWindow(beat, ActionAfterbeatEvidenceMarkers));
        }

        if (ContainsAny(beat.Text, SensoryMarkers))
        {
            AddAdvancedSegment(
                segments,
                anchorId,
                chapterIndex,
                chapterTitle,
                ReferenceMaterialTypes.ImageMotif,
                ref imageMotifIndex,
                beatId,
                BuildEvidenceWindow(beat, SensoryMarkers));
        }

        if (IsHookCandidate(beat.Text, isLastBeatInChapter))
        {
            AddAdvancedSegment(
                segments,
                anchorId,
                chapterIndex,
                chapterTitle,
                ReferenceMaterialTypes.Hook,
                ref hookIndex,
                beatId,
                BuildEvidenceWindow(beat, HookMarkers, preferTail: true));
        }

        if (ContainsAny(beat.Text, PayoffMarkers))
        {
            AddAdvancedSegment(
                segments,
                anchorId,
                chapterIndex,
                chapterTitle,
                ReferenceMaterialTypes.Payoff,
                ref payoffIndex,
                beatId,
                BuildEvidenceWindow(beat, PayoffMarkers));
        }

        if (ContainsAny(beat.Text, TransitionMarkers))
        {
            AddAdvancedSegment(
                segments,
                anchorId,
                chapterIndex,
                chapterTitle,
                ReferenceMaterialTypes.Transition,
                ref transitionIndex,
                beatId,
                BuildEvidenceWindow(beat, TransitionMarkers));
        }
    }

    private static string AddAdvancedSegment(
        List<ReferenceSourceSegment> segments,
        long anchorId,
        int chapterIndex,
        string chapterTitle,
        string segmentType,
        ref int segmentIndex,
        string parentSegmentId,
        TextSpan span)
    {
        var text = span.Text.Trim();
        if (text.Length == 0 || span.EndOffset <= span.StartOffset)
        {
            return string.Empty;
        }

        segmentIndex++;
        var hash = HashText(text);
        var segmentId = BuildSegmentId(anchorId, segmentType, chapterIndex, segmentIndex, hash);
        segments.Add(new ReferenceSourceSegment(
            segmentId,
            chapterIndex,
            chapterTitle,
            segmentType,
            segmentIndex,
            parentSegmentId,
            span.StartOffset,
            span.EndOffset,
            text,
            hash));
        return segmentId;
    }

    private static int FindSceneEnd(IReadOnlyList<TextSpan> paragraphs, int sceneStart)
    {
        var sceneEnd = sceneStart + 1;
        while (sceneEnd < paragraphs.Count && sceneEnd - sceneStart < AdvancedSceneMaxParagraphs)
        {
            if (StartsSceneBoundary(paragraphs[sceneEnd].Text) ||
                IsHookCandidate(paragraphs[sceneEnd - 1].Text, isLastBeatInChapter: false))
            {
                break;
            }

            sceneEnd++;
        }

        return sceneEnd;
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
                "scene" => ReferenceMaterialTypes.Scene,
                "beat" => ReferenceMaterialTypes.Beat,
                "dialogue_exchange" => ReferenceMaterialTypes.DialogueExchange,
                "action_afterbeat" => ReferenceMaterialTypes.ActionAfterbeat,
                "image_motif" => ReferenceMaterialTypes.ImageMotif,
                "hook" => ReferenceMaterialTypes.Hook,
                "payoff" => ReferenceMaterialTypes.Payoff,
                "transition" => ReferenceMaterialTypes.Transition,
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

            var tags = ApplyAdvancedMaterialTagOverrides(materialType, ClassifyMaterial(text));
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

    private static ReferenceProcessingAffectedIds BuildAffectedProcessingIds(
        IReadOnlyList<ReferenceMaterialPayload> materials)
    {
        return BuildAffectedProcessingIds(materials, null);
    }

    private static ReferenceProcessingAffectedIds BuildAffectedProcessingIds(
        IReadOnlyList<ReferenceSourceSegment> segments)
    {
        var segment = segments.FirstOrDefault();
        return segment is null
            ? ReferenceProcessingAffectedIds.Empty
            : new ReferenceProcessingAffectedIds(string.Empty, segment.SegmentId, string.Empty);
    }

    private static ReferenceProcessingAffectedIds BuildAffectedProcessingIds(
        IReadOnlyList<ReferenceMaterialPayload> materials,
        IReadOnlyDictionary<string, IReadOnlyList<ReferenceMaterialSlot>>? slotsByMaterial)
    {
        return BuildAffectedProcessingIds(materials, null, slotsByMaterial);
    }

    private static ReferenceProcessingAffectedIds BuildAffectedProcessingIds(
        IReadOnlyList<ReferenceMaterialPayload> materials,
        IReadOnlyList<ReferenceMaterialPayload>? slotFallbackMaterials,
        IReadOnlyDictionary<string, IReadOnlyList<ReferenceMaterialSlot>>? slotsByMaterial)
    {
        var material = slotsByMaterial is null
            ? materials.FirstOrDefault()
            : FindFirstMaterialWithSlot(materials, slotsByMaterial)
                ?? (slotFallbackMaterials is null ? null : FindFirstMaterialWithSlot(slotFallbackMaterials, slotsByMaterial))
                ?? materials.FirstOrDefault()
                ?? slotFallbackMaterials?.FirstOrDefault();
        if (material is null)
        {
            return ReferenceProcessingAffectedIds.Empty;
        }

        var slotId = slotsByMaterial is not null &&
            slotsByMaterial.TryGetValue(material.MaterialId, out var slots)
            ? slots.FirstOrDefault()?.SlotId ?? string.Empty
            : string.Empty;
        return new ReferenceProcessingAffectedIds(
            material.MaterialId,
            material.SourceSegmentId,
            slotId);
    }

    private static ReferenceMaterialPayload? FindFirstMaterialWithSlot(
        IReadOnlyList<ReferenceMaterialPayload> materials,
        IReadOnlyDictionary<string, IReadOnlyList<ReferenceMaterialSlot>> slotsByMaterial)
    {
        return materials.FirstOrDefault(item =>
            slotsByMaterial.TryGetValue(item.MaterialId, out var materialSlots) &&
            materialSlots.Count > 0);
    }

    private static ReferenceProcessingAffectedIds BuildAffectedProcessingIds(ReferenceAnchorCorpusSnapshot corpus)
    {
        return corpus.Materials.Count > 0
            ? BuildAffectedProcessingIds(corpus.Materials, corpus.SlotsByMaterial)
            : BuildAffectedProcessingIds(corpus.Segments);
    }

    private static TextSpan BuildAbsoluteSpan(string sourceText, int startOffset, int endOffset)
    {
        var start = Math.Clamp(startOffset, 0, sourceText.Length);
        var end = Math.Clamp(endOffset, start, sourceText.Length);
        return TrimSpan(sourceText, start, end);
    }

    private static TextSpan BuildEvidenceWindow(
        TextSpan container,
        IReadOnlyList<string> markers,
        bool preferTail = false)
    {
        if (container.Text.Length <= AdvancedEvidenceWindowChars)
        {
            return container;
        }

        var anchorIndex = preferTail
            ? container.Text.Length - 1
            : FindFirstMarkerIndex(container.Text, markers);
        if (anchorIndex < 0)
        {
            anchorIndex = 0;
        }

        var maxStart = Math.Max(0, container.Text.Length - AdvancedEvidenceWindowChars);
        var start = preferTail
            ? maxStart
            : Math.Clamp(anchorIndex - AdvancedEvidenceWindowChars / 3, 0, maxStart);
        var end = Math.Min(container.Text.Length, start + AdvancedEvidenceWindowChars);
        var trimmed = TrimSpan(container.Text, start, end);
        return trimmed with
        {
            StartOffset = container.StartOffset + trimmed.StartOffset,
            EndOffset = container.StartOffset + trimmed.EndOffset
        };
    }

    private static int FindFirstMarkerIndex(string text, IReadOnlyList<string> markers)
    {
        var best = -1;
        foreach (var marker in markers.Where(marker => marker.Length > 0))
        {
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
            }
        }

        return best;
    }

    private static bool StartsSceneBoundary(string text)
    {
        var trimmed = text.TrimStart();
        return TransitionMarkers.Any(marker => trimmed.StartsWith(marker, StringComparison.Ordinal));
    }

    private static bool IsActionAfterbeatCandidate(string text)
    {
        return ContainsAny(text, AfterbeatMarkers) ||
            (ContainsAny(text, ActionMarkers) &&
                (ContainsAny(text, EmotionEvidenceMarkers) || ContainsAny(text, RestrainedEmotionMarkers)));
    }

    private static bool IsHookCandidate(string text, bool isLastBeatInChapter)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var last = trimmed[^1];
        return last is '？' or '?' or '！' or '!' ||
            ContainsAny(trimmed, HookMarkers) ||
            (isLastBeatInChapter && ContainsAny(trimmed, PayoffMarkers));
    }

    private static MaterialTags ApplyAdvancedMaterialTagOverrides(string materialType, MaterialTags tags)
    {
        return materialType switch
        {
            ReferenceMaterialTypes.Scene => tags with
            {
                FunctionTag = "scene",
                SceneTag = "scene",
                TechniqueTag = tags.TechniqueTag == "plain" ? "scene_structure" : tags.TechniqueTag,
                FunctionConfidence = Math.Max(tags.FunctionConfidence, 0.75)
            },
            ReferenceMaterialTypes.Beat => tags with
            {
                FunctionTag = "beat",
                SceneTag = "beat",
                TechniqueTag = tags.TechniqueTag == "plain" ? "beat_structure" : tags.TechniqueTag,
                FunctionConfidence = Math.Max(tags.FunctionConfidence, 0.75)
            },
            ReferenceMaterialTypes.DialogueExchange => tags with
            {
                FunctionTag = "dialogue",
                SceneTag = "conversation",
                TechniqueTag = "dialogue_exchange",
                FunctionConfidence = Math.Max(tags.FunctionConfidence, 0.9),
                EmotionConfidence = Math.Max(tags.EmotionConfidence, 0.75)
            },
            ReferenceMaterialTypes.ActionAfterbeat => tags with
            {
                FunctionTag = tags.FunctionTag == "narration" ? "emotion_evidence" : tags.FunctionTag,
                SceneTag = "afterbeat",
                TechniqueTag = "afterbeat",
                FunctionConfidence = Math.Max(tags.FunctionConfidence, 0.85),
                EmotionConfidence = Math.Max(tags.EmotionConfidence, 0.75)
            },
            ReferenceMaterialTypes.ImageMotif => tags with
            {
                FunctionTag = "environment",
                SceneTag = "image_motif",
                TechniqueTag = "sensory_detail",
                FunctionConfidence = Math.Max(tags.FunctionConfidence, 0.9)
            },
            ReferenceMaterialTypes.Hook => tags with
            {
                FunctionTag = "hook",
                EmotionTag = tags.EmotionTag == "neutral" ? "uncertain" : tags.EmotionTag,
                SceneTag = "tension",
                TechniqueTag = "hook",
                FunctionConfidence = Math.Max(tags.FunctionConfidence, 0.9),
                EmotionConfidence = Math.Max(tags.EmotionConfidence, 0.75)
            },
            ReferenceMaterialTypes.Payoff => tags with
            {
                FunctionTag = "payoff",
                SceneTag = "reveal",
                TechniqueTag = "payoff",
                FunctionConfidence = Math.Max(tags.FunctionConfidence, 0.9)
            },
            ReferenceMaterialTypes.Transition => tags with
            {
                FunctionTag = "transition",
                SceneTag = "transition",
                TechniqueTag = "transition",
                FunctionConfidence = Math.Max(tags.FunctionConfidence, 0.9)
            },
            _ => tags
        };
    }

    private static MaterialTags ClassifyMaterial(string text)
    {
        var isDialogue = ContainsAny(text, DialogueMarkers);
        var hasSensory = ContainsAny(text, SensoryMarkers);
        var hasInteriority = ContainsAny(text, InteriorityMarkers);
        var hasAction = ContainsAny(text, ActionMarkers);
        var hasTransition = ContainsAny(text, TransitionMarkers);
        var hasEmotionEvidence = ContainsAny(text, EmotionEvidenceMarkers);
        var hasLimitedPov = ContainsAny(text, LimitedPovMarkers);
        var hasAfterbeat = ContainsAny(text, AfterbeatMarkers);

        var functionTag = isDialogue
            ? "dialogue"
            : hasInteriority
                ? "interiority"
                : hasAfterbeat && hasAction
                    ? "action"
                    : hasEmotionEvidence
                        ? "emotion_evidence"
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
                : hasLimitedPov
                    ? "limited_pov"
                    : hasAfterbeat
                        ? "afterbeat"
                        : hasEmotionEvidence
                            ? "external_evidence"
                            : hasSensory
                                ? "sensory_detail"
                                : hasTransition
                                    ? "transition"
                                    : "plain";
        var emotionTag = hasInteriority
            ? "reflective"
            : ContainsAny(text, RestrainedEmotionMarkers)
                ? "restrained"
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
            hasLimitedPov ? "limited" : hasInteriority ? "close" : "unknown",
            techniqueTag,
            functionConfidence,
            emotionConfidence,
            hasLimitedPov ? 0.7 : povConfidence);
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
            MatchesAnyFilter(material.TechniqueTag, input.TechniqueTags) &&
            MatchesNarrativeDutyFilters(material, input.NarrativeDuties) &&
            MatchesEmotionTransitionFilters(material, input.EmotionTransitions) &&
            MatchesProseDutyFilters(material, input.ProseDuties);
    }

    private static bool MatchesAnyFilter(string value, IReadOnlyList<string>? filters)
    {
        return filters is null ||
            filters.Count == 0 ||
            filters.Any(filter => string.Equals(value, filter, StringComparison.OrdinalIgnoreCase));
    }

    private static StyleSearchOptions NormalizeStyleSearchOptions(SearchReferenceMaterialsPayload input)
    {
        var profileIds = NormalizeStyleProfileIds(input.StyleProfileIds);
        var dimensions = NormalizeStyleDimensions(input.StyleDimensions);
        var intensity = string.IsNullOrWhiteSpace(input.ImitationIntensity)
            ? ReferenceStyleImitationIntensities.Moderate
            : ValidateAllowedText(input.ImitationIntensity, nameof(input.ImitationIntensity), AllowedStyleImitationIntensities);
        return new StyleSearchOptions(
            profileIds,
            dimensions,
            intensity,
            StyleFitWeight(intensity),
            SourceRiskPenalty(intensity));
    }

    private static IReadOnlyList<long> NormalizeStyleProfileIds(IReadOnlyList<long>? profileIds)
    {
        if (profileIds is null || profileIds.Count == 0)
        {
            return [];
        }

        var normalized = new List<long>(profileIds.Count);
        var seen = new HashSet<long>();
        foreach (var profileId in profileIds)
        {
            if (profileId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(profileIds), profileId, "Reference style profile id must be positive.");
            }

            if (seen.Add(profileId))
            {
                normalized.Add(profileId);
            }
        }

        if (normalized.Count > MaxStyleProfileFilters)
        {
            throw new ArgumentException($"At most {MaxStyleProfileFilters} style profiles can be used for one material search.", nameof(profileIds));
        }

        return normalized;
    }

    private static IReadOnlyList<string> NormalizeStyleDimensions(IReadOnlyList<string>? dimensions)
    {
        if (dimensions is null || dimensions.Count == 0)
        {
            return [];
        }

        var normalized = dimensions
            .Select(value => NormalizeOptionalText(value, nameof(dimensions), maxLength: 128))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length > MaxStyleDimensionFilters)
        {
            throw new ArgumentException($"At most {MaxStyleDimensionFilters} style dimensions can be used for one material search.", nameof(dimensions));
        }

        return normalized;
    }

    private static double StyleFitWeight(string intensity)
    {
        return intensity switch
        {
            ReferenceStyleImitationIntensities.DiagnosticOnly => 0,
            ReferenceStyleImitationIntensities.Loose => 1.5,
            ReferenceStyleImitationIntensities.Strong => 4.0,
            _ => 2.5
        };
    }

    private static double SourceRiskPenalty(string intensity)
    {
        return intensity switch
        {
            ReferenceStyleImitationIntensities.Strong => -0.45,
            ReferenceStyleImitationIntensities.Moderate => -0.2,
            _ => 0
        };
    }

    private static async ValueTask<StyleSearchContext> ReadStyleSearchContextAsync(
        SqliteConnection connection,
        long novelId,
        IReadOnlyList<long> anchorIds,
        StyleSearchOptions options,
        CancellationToken cancellationToken)
    {
        if (options.ProfileIds.Count == 0)
        {
            return StyleSearchContext.Empty;
        }

        if (!await TableExistsAsync(connection, "reference_style_profiles", cancellationToken) ||
            !await TableExistsAsync(connection, "reference_material_style_tags", cancellationToken))
        {
            throw new ArgumentException("Reference style profile does not exist for this novel.", nameof(options));
        }

        await using var command = connection.CreateCommand();
        var profileParameters = AddLongParameters(command, "$style_profile_id_", options.ProfileIds);
        command.CommandText = $$"""
            SELECT profile_id, anchor_ids_json, aggregate_confidence
            FROM reference_style_profiles
            WHERE novel_id = $novel_id
              AND status = $status
              AND archived_at IS NULL
              AND profile_id IN ({{string.Join(", ", profileParameters)}});
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$status", ReferenceStyleProfileStatuses.Active);

        var profileConfidences = new Dictionary<long, double>();
        var sourceAnchorIds = new HashSet<long>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var profileId = reader.GetInt64(0);
                profileConfidences[profileId] = reader.GetDouble(2);
                foreach (var sourceAnchorId in ReadLongList(reader.GetString(1)))
                {
                    sourceAnchorIds.Add(sourceAnchorId);
                }
            }
        }

        if (profileConfidences.Count != options.ProfileIds.Count)
        {
            throw new ArgumentException("Reference style profile does not exist or is archived for this novel.", nameof(options));
        }

        var fitScores = await ReadStyleFitScoresAsync(
            connection,
            anchorIds,
            options,
            profileConfidences,
            cancellationToken);
        return new StyleSearchContext(fitScores, sourceAnchorIds, options.SourceRiskPenalty);
    }

    private static async ValueTask<IReadOnlyDictionary<string, double>> ReadStyleFitScoresAsync(
        SqliteConnection connection,
        IReadOnlyList<long> anchorIds,
        StyleSearchOptions options,
        IReadOnlyDictionary<long, double> profileConfidences,
        CancellationToken cancellationToken)
    {
        if (anchorIds.Count == 0 || options.FitWeight <= 0)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        await using var command = connection.CreateCommand();
        var anchorParameters = AddLongParameters(command, "$style_anchor_id_", anchorIds);
        var profileParameters = AddLongParameters(command, "$style_tag_profile_id_", options.ProfileIds);
        var dimensionPredicate = string.Empty;
        if (options.Dimensions.Count > 0)
        {
            var dimensionParameters = AddStringParameters(command, "$style_dimension_", options.Dimensions);
            dimensionPredicate = $"AND t.tag_key IN ({string.Join(", ", dimensionParameters)})";
        }

        command.CommandText = $$"""
            SELECT t.material_id, t.profile_id, t.tag_key, MAX(t.confidence)
            FROM reference_material_style_tags t
            INNER JOIN reference_materials m ON m.material_id = t.material_id
            WHERE t.profile_id IN ({{string.Join(", ", profileParameters)}})
              AND m.anchor_id IN ({{string.Join(", ", anchorParameters)}})
              AND m.archived_at IS NULL
              {{dimensionPredicate}}
            GROUP BY t.material_id, t.profile_id, t.tag_key;
            """;

        var rawScores = new Dictionary<string, double>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var materialId = reader.GetString(0);
            var profileId = reader.GetInt64(1);
            var confidence = Math.Clamp(reader.GetDouble(3), 0, 1);
            var profileConfidence = profileConfidences.TryGetValue(profileId, out var value)
                ? Math.Clamp(value, 0, 1)
                : 0;
            var contribution = confidence * profileConfidence;
            rawScores[materialId] = rawScores.TryGetValue(materialId, out var existing)
                ? existing + contribution
                : contribution;
        }

        var denominator = Math.Max(1, options.Dimensions.Count);
        return rawScores.ToDictionary(
            item => item.Key,
            item => Math.Round(Math.Min(1.0, item.Value / denominator) * options.FitWeight, 4),
            StringComparer.Ordinal);
    }

    private static async ValueTask<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table'
              AND name = $table_name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$table_name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async ValueTask<IReadOnlyDictionary<string, double>> TryBuildEmbeddingScoresAsync(
        string databasePath,
        SqliteConnection connection,
        SearchReferenceMaterialsPayload input,
        IReadOnlyList<long> anchorIds,
        int materialCount,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = (input.Query ?? string.Empty).Trim();
        if (normalizedQuery.Length == 0 || materialCount == 0)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        var readyAnchorIds = await ReadReadyVectorAnchorIdsAsync(connection, anchorIds, cancellationToken);
        if (readyAnchorIds.Count == 0)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        var embeddingOptions = await _embeddingConfiguration.GetActiveEmbeddingOptionsAsync(cancellationToken);
        if (embeddingOptions is null)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        EmbeddingBatchResult queryEmbedding;
        try
        {
            queryEmbedding = await _embeddings.EmbedAsync(
                [normalizedQuery],
                embeddingOptions with { InputKind = BuiltinOnnxEmbeddingModel.QueryInputKind },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        if (queryEmbedding.Items.Count != 1)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        var queryVector = queryEmbedding.Items[0].Vector;
        var dimensions = queryEmbedding.Dimensions > 0 ? queryEmbedding.Dimensions : queryVector.Count;
        if (dimensions <= 0 || queryVector.Count != dimensions)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        var rowIds = await ReadMaterialRowIdsAsync(connection, readyAnchorIds, cancellationToken);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var topK = Math.Clamp(materialCount, 1, 100);
        foreach (var anchorId in readyAnchorIds)
        {
            if (!rowIds.TryGetValue(anchorId, out var anchorRowIds) || anchorRowIds.Count == 0)
            {
                continue;
            }

            var tableName = SqliteVecTableProvisioner.BuildReferenceAnchorVectorTableName(anchorId, dimensions);
            IReadOnlyList<SqliteVecSearchRecord> vectorResults;
            try
            {
                vectorResults = await _vecQuery.SearchAsync(
                    databasePath,
                    new SqliteVecSearchRequest(tableName, dimensions, queryVector, topK),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidOperationException or SqliteException)
            {
                continue;
            }

            foreach (var result in vectorResults)
            {
                if (!anchorRowIds.TryGetValue(result.RowId, out var materialId))
                {
                    continue;
                }

                var score = EmbeddingDistanceScore(result.Distance);
                if (score <= 0)
                {
                    continue;
                }

                scores[materialId] = scores.TryGetValue(materialId, out var existing)
                    ? Math.Max(existing, score)
                    : score;
            }
        }

        return scores;
    }

    private static IReadOnlyDictionary<string, double> ScoreMaterialComponents(
        ReferenceMaterialPayload material,
        SearchReferenceMaterialsPayload input,
        double embeddingScore,
        bool acceptedFeedback,
        double styleFitScore,
        double sourceRiskPenalty)
    {
        var components = new Dictionary<string, double>(StringComparer.Ordinal);
        var normalizedQuery = (input.Query ?? string.Empty).Trim();
        if (normalizedQuery.Length > 0)
        {
            var firstIndex = material.Text.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase);
            if (firstIndex >= 0)
            {
                AddScore(components, "lexical", 10.0 + Math.Max(0, 2.0 - firstIndex / 20.0));
            }
        }

        AddScore(components, "material_type", material.MaterialType == ReferenceMaterialTypes.Sentence ? 1.5 : 0.8);
        AddScore(components, "tag", SearchTagScore(material, input));
        AddScore(components, "narrative_duty", NarrativeDutyScore(material, input.NarrativeDuties));
        AddScore(components, "emotion_transition", EmotionTransitionScore(material, input.EmotionTransitions));
        AddScore(components, "prose_duty", ProseDutyScore(material, input.ProseDuties));
        AddScore(components, "style_fit", styleFitScore);
        AddScore(components, "embedding", embeddingScore);
        AddScore(components, "accepted_feedback", acceptedFeedback ? 4.0 : 0);
        AddScoreComponent(components, "source_risk_penalty", sourceRiskPenalty);
        AddScore(components, "confidence", material.FunctionConfidence + material.EmotionConfidence * 0.2 + material.PovConfidence * 0.1);
        AddScore(components, "length", Math.Max(0, 1.0 - material.Text.Length / 500.0));
        return components;
    }

    private static double EmbeddingDistanceScore(double distance)
    {
        if (double.IsNaN(distance) || double.IsInfinity(distance) || distance < 0)
        {
            return 0;
        }

        return Math.Round(Math.Max(0, 4.0 - distance * 4.0), 4);
    }

    private static double SearchTagScore(
        ReferenceMaterialPayload material,
        SearchReferenceMaterialsPayload input)
    {
        var score = 0.0;
        score += MatchesNonEmptyFilter(material.MaterialType, input.MaterialTypes) ? 1.0 : 0;
        score += MatchesNonEmptyFilter(material.EmotionTag, input.EmotionTags) ? 1.0 : 0;
        score += MatchesNonEmptyFilter(material.FunctionTag, input.FunctionTags) ? 1.0 : 0;
        score += MatchesNonEmptyFilter(material.PovTag, input.PovTags) ? 1.0 : 0;
        score += MatchesNonEmptyFilter(material.TechniqueTag, input.TechniqueTags) ? 1.0 : 0;
        return score;
    }

    private static bool MatchesNarrativeDutyFilters(
        ReferenceMaterialPayload material,
        IReadOnlyList<string>? narrativeDuties)
    {
        return narrativeDuties is null ||
            narrativeDuties.Count == 0 ||
            narrativeDuties.Any(duty => MatchesNarrativeDuty(material, duty));
    }

    private static double NarrativeDutyScore(
        ReferenceMaterialPayload material,
        IReadOnlyList<string>? narrativeDuties)
    {
        return narrativeDuties is null
            ? 0
            : narrativeDuties.Count(duty => MatchesNarrativeDuty(material, duty));
    }

    private static bool MatchesNarrativeDuty(ReferenceMaterialPayload material, string duty)
    {
        return NormalizeFilterToken(duty) switch
        {
            "" => false,
            "interiority" => IsTag(material.FunctionTag, "interiority"),
            "external_evidence" => IsTag(material.FunctionTag, "action") ||
                IsTag(material.FunctionTag, "environment") ||
                IsTag(material.FunctionTag, "emotion_evidence") ||
                IsTag(material.TechniqueTag, "external_evidence"),
            "transition" => IsTag(material.FunctionTag, "transition"),
            "sensory" or "sensory_anchor" => IsTag(material.TechniqueTag, "sensory_detail"),
            "causality" => !IsTag(material.FunctionTag, "dialogue"),
            "subtext" => IsTag(material.FunctionTag, "dialogue") ||
                IsTag(material.FunctionTag, "interiority") ||
                IsTag(material.FunctionTag, "emotion_evidence"),
            "source_detail" or "source_backed_detail" => IsTag(material.FunctionTag, "environment") || IsTag(material.TechniqueTag, "sensory_detail"),
            "physical_afterbeat" or "afterbeat" => IsTag(material.TechniqueTag, "afterbeat") || IsTag(material.FunctionTag, "action"),
            var normalized => IsTag(material.FunctionTag, normalized) ||
                IsTag(material.TechniqueTag, normalized) ||
                IsTag(material.SceneTag, normalized)
        };
    }

    private static bool MatchesEmotionTransitionFilters(
        ReferenceMaterialPayload material,
        IReadOnlyList<string>? emotionTransitions)
    {
        return emotionTransitions is null ||
            emotionTransitions.Count == 0 ||
            emotionTransitions.Any(transition => MatchesEmotionTransition(material, transition));
    }

    private static double EmotionTransitionScore(
        ReferenceMaterialPayload material,
        IReadOnlyList<string>? emotionTransitions)
    {
        return emotionTransitions is null
            ? 0
            : emotionTransitions.Count(transition => MatchesEmotionTransition(material, transition));
    }

    private static bool MatchesEmotionTransition(ReferenceMaterialPayload material, string transition)
    {
        var emotionTag = NormalizeFilterToken(material.EmotionTag);
        var normalizedTransition = NormalizeFilterToken(transition);
        return emotionTag.Length > 0 &&
            normalizedTransition.Length > 0 &&
            normalizedTransition.Contains(emotionTag, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesProseDutyFilters(
        ReferenceMaterialPayload material,
        IReadOnlyList<string>? proseDuties)
    {
        return proseDuties is null ||
            proseDuties.Count == 0 ||
            proseDuties.Any(duty => MatchesProseDuty(material, duty));
    }

    private static double ProseDutyScore(
        ReferenceMaterialPayload material,
        IReadOnlyList<string>? proseDuties)
    {
        return proseDuties is null
            ? 0
            : proseDuties.Count(duty => MatchesProseDuty(material, duty));
    }

    private static bool MatchesProseDuty(ReferenceMaterialPayload material, string duty)
    {
        return NormalizeFilterToken(duty) switch
        {
            "" => false,
            "source_backed_detail" or "source_detail" => IsTag(material.FunctionTag, "environment") ||
                IsTag(material.TechniqueTag, "sensory_detail"),
            "external_evidence" => IsTag(material.FunctionTag, "action") ||
                IsTag(material.FunctionTag, "environment") ||
                IsTag(material.FunctionTag, "emotion_evidence") ||
                IsTag(material.TechniqueTag, "external_evidence"),
            "interiority" => IsTag(material.FunctionTag, "interiority"),
            "subtext" => IsTag(material.FunctionTag, "dialogue") ||
                IsTag(material.FunctionTag, "interiority") ||
                IsTag(material.FunctionTag, "emotion_evidence") ||
                IsTag(material.TechniqueTag, "external_evidence"),
            "transition" => IsTag(material.FunctionTag, "transition"),
            "delayed_reaction" or "physical_afterbeat" or "afterbeat" => IsTag(material.TechniqueTag, "afterbeat") ||
                IsTag(material.FunctionTag, "action") ||
                IsTag(material.FunctionTag, "emotion_evidence"),
            "sensory_anchor" or "sensory" => IsTag(material.TechniqueTag, "sensory_detail"),
            "anti_screenplay" or "anti_screenplay_duty" => !IsTag(material.FunctionTag, "dialogue"),
            var normalized => IsTag(material.FunctionTag, normalized) ||
                IsTag(material.TechniqueTag, normalized) ||
                IsTag(material.SceneTag, normalized)
        };
    }

    private static bool MatchesNonEmptyFilter(string value, IReadOnlyList<string>? filters)
    {
        return filters is not null &&
            filters.Count > 0 &&
            filters.Any(filter => string.Equals(value, filter, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTag(string value, string tag)
    {
        return string.Equals(value, tag, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFilterToken(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static void AddScore(IDictionary<string, double> components, string name, double value)
    {
        if (value > 0)
        {
            components[name] = Math.Round(value, 4);
        }
    }

    private static void AddScoreComponent(IDictionary<string, double> components, string name, double value)
    {
        if (Math.Abs(value) > 0.000001)
        {
            components[name] = Math.Round(value, 4);
        }
    }

    private static string TruncateUnknownLicensePreview(string text)
    {
        var normalized = (text ?? string.Empty).Trim();
        return normalized.Length <= UnknownLicensePreviewMaxChars
            ? normalized
            : normalized[..UnknownLicensePreviewMaxChars].TrimEnd() + "...";
    }

    private static bool IsUnknownLicense(string licenseStatus)
    {
        return string.Equals(licenseStatus, "unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static ReferenceMaterialTagReviewItemPayload BuildMaterialTagReviewItem(
        ReferenceMaterialSummaryPayload material)
    {
        return new ReferenceMaterialTagReviewItemPayload(
            material,
            BuildMaterialTagReviewIssues(material));
    }

    private static IReadOnlyList<ReferenceMaterialTagReviewIssuePayload> BuildMaterialTagReviewIssues(
        ReferenceMaterialSummaryPayload material)
    {
        var issues = new List<ReferenceMaterialTagReviewIssuePayload>();
        if (!material.UserVerified)
        {
            issues.Add(new ReferenceMaterialTagReviewIssuePayload(
                ReferenceMaterialTagReviewIssueCodes.Unverified,
                "未校正",
                "review"));
        }

        var lowConfidence = new List<string>();
        AddLowConfidenceLabel(lowConfidence, "功能", material.FunctionConfidence);
        AddLowConfidenceLabel(lowConfidence, "情绪", material.EmotionConfidence);
        AddLowConfidenceLabel(lowConfidence, "POV", material.PovConfidence);
        if (lowConfidence.Count > 0)
        {
            issues.Add(new ReferenceMaterialTagReviewIssuePayload(
                ReferenceMaterialTagReviewIssueCodes.LowConfidence,
                $"低置信 {string.Join(" / ", lowConfidence)}",
                "warning"));
        }

        var unknownTags = new List<string>();
        AddUnknownTagLabel(unknownTags, "功能", material.FunctionTag);
        AddUnknownTagLabel(unknownTags, "情绪", material.EmotionTag);
        AddUnknownTagLabel(unknownTags, "场景", material.SceneTag);
        AddUnknownTagLabel(unknownTags, "POV", material.PovTag);
        AddUnknownTagLabel(unknownTags, "技法", material.TechniqueTag);
        if (unknownTags.Count > 0)
        {
            issues.Add(new ReferenceMaterialTagReviewIssuePayload(
                ReferenceMaterialTagReviewIssueCodes.UnknownTag,
                $"unknown 标签 {string.Join(" / ", unknownTags)}",
                "review"));
        }

        return issues;
    }

    private static void AddLowConfidenceLabel(ICollection<string> labels, string label, double confidence)
    {
        if (double.IsFinite(confidence) && confidence < MaterialReviewConfidenceThreshold)
        {
            labels.Add(string.Create(CultureInfo.InvariantCulture, $"{label} {confidence:0.00}"));
        }
    }

    private static void AddUnknownTagLabel(ICollection<string> labels, string label, string value)
    {
        if (UnknownMaterialTags.Contains(NormalizeFilterToken(value)))
        {
            labels.Add(label);
        }
    }

    private static ReferenceMaterialSummaryPayload ToMaterialSummary(
        ReferenceMaterialPayload material,
        int previewMaxChars,
        DateTimeOffset? archivedAt)
    {
        var preview = BuildPreview(material.Text, previewMaxChars);
        var archiveState = archivedAt.HasValue
            ? ReferenceMaterialArchiveFilters.Archived
            : ReferenceMaterialArchiveFilters.Active;
        return new ReferenceMaterialSummaryPayload(
            material.MaterialId,
            material.AnchorId,
            material.SourceSegmentId,
            material.MaterialType,
            material.FunctionTag,
            material.EmotionTag,
            material.SceneTag,
            material.PovTag,
            material.TechniqueTag,
            material.FunctionConfidence,
            material.EmotionConfidence,
            material.PovConfidence,
            preview.Text,
            preview.Truncated,
            material.SourceHash,
            material.ExtractorVersion,
            material.UserVerified,
            material.CreatedAt,
            archiveState,
            archivedAt,
            material.ScoreComponents);
    }

    private static ReferenceMaterialSourceSummaryPayload ToSourceSummary(ReferenceAnchorPayload anchor)
    {
        return new ReferenceMaterialSourceSummaryPayload(
            anchor.AnchorId,
            anchor.NovelId,
            anchor.Title,
            anchor.Author,
            anchor.SourceKind,
            anchor.LicenseStatus,
            anchor.SourceFileHash,
            anchor.BuildVersion,
            anchor.Status,
            anchor.Visibility,
            anchor.SourceTrust,
            anchor.UserTags,
            anchor.OwnerScope,
            anchor.OwnerNovelId);
    }

    private static string BuildProcessingNoteMessage(ReferenceAnchorBuildStatusPayload status)
    {
        return BuildProcessingNoteMessage(
            status.SourceSegmentCount,
            status.MaterialCount,
            status.SlotCount,
            status.VectorCount,
            status.LastError);
    }

    private static string BuildProcessingNoteMessage(
        int sourceSegmentCount,
        int materialCount,
        int slotCount,
        int vectorCount,
        string lastError)
    {
        if (!string.IsNullOrWhiteSpace(lastError))
        {
            return RedactError(lastError);
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"segments={sourceSegmentCount}; materials={materialCount}; slots={slotCount}; vectors={vectorCount}");
    }

    private static TextPreview BuildPreview(string? text, int maxLength)
    {
        var normalized = NormalizePreviewText(text);
        if (normalized.Length <= maxLength)
        {
            return new TextPreview(normalized, false);
        }

        return new TextPreview(normalized[..maxLength].TrimEnd() + "...", true);
    }

    private static string NormalizePreviewText(string? text)
    {
        return Regex.Replace((text ?? string.Empty).Trim(), @"\s+", " ");
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
        IReadOnlyList<string> nonSlotEdits,
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

        var sourceLeak = ReferenceSourceLeakAuditor.Analyze(material.Text, candidateText, rewriteLevel);
        if (sourceLeak.ShouldFail)
        {
            foreach (var finding in sourceLeak.Findings)
            {
                requiredFixes.Add($"Source-leak risk: {finding}");
            }
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
            nonSlotEdits,
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

    private static void ValidateCreateAnchorsInput(CreateReferenceAnchorsPayload input)
    {
        if (input.Anchors is null || input.Anchors.Count == 0)
        {
            throw new ArgumentException("At least one reference anchor is required.", nameof(input));
        }

        if (input.Anchors.Count > 50)
        {
            throw new ArgumentException("At most 50 reference anchors can be imported at once.", nameof(input));
        }
    }

    private static CreateReferenceAnchorFailurePayload CreateFailurePayload(
        int index,
        CreateReferenceAnchorPayload? input,
        Exception exception)
    {
        return CreateFailurePayload(index, input, RedactError(exception.Message));
    }

    private static CreateReferenceAnchorFailurePayload CreateFailurePayload(
        int index,
        CreateReferenceAnchorPayload? input,
        string diagnostic)
    {
        return new CreateReferenceAnchorFailurePayload(
            index,
            NormalizeFailureDisplayText(input?.Title, "Untitled reference source"),
            NormalizeFailureDisplayText(input?.SourceKind, string.Empty),
            BuildFailureSourceIdentity(input),
            RedactError(diagnostic),
            RetryAvailable: true);
    }

    private static string BuildFailureSourceIdentity(CreateReferenceAnchorPayload? input)
    {
        var visibility = string.IsNullOrWhiteSpace(input?.Visibility)
            ? ReferenceCorpusVisibilities.Private
            : NormalizeFailureDisplayText(input?.Visibility, ReferenceCorpusVisibilities.Private);
        var scope = string.Equals(visibility, ReferenceCorpusVisibilities.Workspace, StringComparison.Ordinal)
            ? "workspace"
            : (input?.NovelId ?? 0).ToString(CultureInfo.InvariantCulture);
        var sourceKind = NormalizeFailureDisplayText(input?.SourceKind, string.Empty);
        var normalizedPath = NormalizeFailureSourcePath(input?.SourcePath);
        return "source:" + HashText(string.Join('\u001F', scope, visibility, normalizedPath, sourceKind));
    }

    private static string BuildUnavailableSourceHash(
        long? storedNovelId,
        string visibility,
        string sourcePath,
        string sourceKind)
    {
        var scope = storedNovelId.HasValue
            ? storedNovelId.Value.ToString(CultureInfo.InvariantCulture)
            : "workspace";
        return "unavailable:" + HashText(string.Join('\u001F', scope, visibility, NormalizeLockPath(sourcePath), sourceKind));
    }

    private static string NormalizeFailureSourcePath(string? sourcePath)
    {
        try
        {
            var normalized = NormalizeOptionalText(sourcePath, nameof(sourcePath), maxLength: 1024);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "<empty>";
            }

            return NormalizeLockPath(normalized);
        }
        catch
        {
            return "<invalid:" + HashText(sourcePath ?? string.Empty) + ">";
        }
    }

    private static string NormalizeFailureDisplayText(string? value, string fallback)
    {
        var normalized = new string((value ?? string.Empty)
            .Trim()
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray())
            .Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return fallback;
        }

        return normalized.Length <= 200 ? normalized : normalized[..200].TrimEnd() + "...";
    }

    private static string BuildImportIdentityLockKey(
        string databasePath,
        long? storedNovelId,
        string visibility,
        string sourcePath,
        string sourceKind,
        string sourceFileHash)
    {
        var scope = storedNovelId.HasValue
            ? storedNovelId.Value.ToString(CultureInfo.InvariantCulture)
            : "workspace";
        return string.Join(
            '\u001F',
            NormalizeLockPath(databasePath),
            visibility,
            scope,
            NormalizeLockPath(sourcePath),
            sourceKind,
            sourceFileHash);
    }

    private static string NormalizeLockPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return OperatingSystem.IsWindows()
            ? fullPath.ToUpperInvariant()
            : fullPath;
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

    private static string NormalizeFeedbackEditedText(string? value, string name, int maxLength)
    {
        var normalized = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(name, normalized.Length, $"Value must be at most {maxLength} characters.");
        }

        if (normalized.Any(value => char.IsControl(value) && value is not '\n' and not '\t'))
        {
            throw new ArgumentException("Value must not contain unsupported control characters.", name);
        }

        return normalized;
    }

    private static string BuildSegmentId(long anchorId, string type, int chapterIndex, int segmentIndex, string hash)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{anchorId}:{chapterIndex}:{type}:{segmentIndex}:{hash[..16]}");
    }

    private static string BuildTextNodeId(string segmentId)
    {
        return "node-" + HashText(segmentId);
    }

    private static string BuildStage1RunId(long anchorId, string sourceHash)
    {
        return "stage1-" + anchorId.ToString(CultureInfo.InvariantCulture) + "-" + sourceHash[..16];
    }

    private static string BuildDefaultCorpusLibraryId(long? storedNovelId, string visibility)
    {
        return string.Equals(visibility, ReferenceCorpusVisibilities.Workspace, StringComparison.Ordinal)
            ? "global:workspace"
            : "project:" + (storedNovelId ?? 0).ToString(CultureInfo.InvariantCulture) + ":default";
    }

    private static CorpusLicenseGateMapping MapLicenseStatus(string licenseStatus)
    {
        return licenseStatus.Trim().ToLowerInvariant() switch
        {
            "public_domain" => new CorpusLicenseGateMapping(
                ReferenceCorpusLicenseStates.PublicDomain,
                ReferenceCorpusReusePolicies.VerbatimOk,
                0.90,
                true),
            "licensed" => new CorpusLicenseGateMapping(
                ReferenceCorpusLicenseStates.Authorized,
                ReferenceCorpusReusePolicies.AdaptedOnly,
                0.42,
                true),
            "user_provided" => new CorpusLicenseGateMapping(
                ReferenceCorpusLicenseStates.Authorized,
                ReferenceCorpusReusePolicies.AdaptedOnly,
                0.42,
                true),
            _ => new CorpusLicenseGateMapping(
                ReferenceCorpusLicenseStates.Unknown,
                ReferenceCorpusReusePolicies.ReferenceOnly,
                null,
                false)
        };
    }

    private static string MapSourceQuality(string sourceTrust)
    {
        return sourceTrust.Trim().ToLowerInvariant() switch
        {
            ReferenceSourceTrustLevels.UserVerified => "trusted",
            ReferenceSourceTrustLevels.Imported => "normal",
            ReferenceSourceTrustLevels.Unverified => "low",
            _ => "normal"
        };
    }

    private static string MapTextNodeType(string segmentType)
    {
        return segmentType switch
        {
            "chapter" => ReferenceCorpusNodeTypes.Chapter,
            "scene" => ReferenceCorpusNodeTypes.Scene,
            "sentence" => ReferenceCorpusNodeTypes.Sentence,
            "paragraph" => ReferenceCorpusNodeTypes.Passage,
            "beat" => ReferenceCorpusNodeTypes.Passage,
            "dialogue_exchange" => ReferenceCorpusNodeTypes.Passage,
            "action_afterbeat" => ReferenceCorpusNodeTypes.Passage,
            "image_motif" => ReferenceCorpusNodeTypes.Passage,
            "hook" => ReferenceCorpusNodeTypes.Passage,
            "payoff" => ReferenceCorpusNodeTypes.Passage,
            "transition" => ReferenceCorpusNodeTypes.Passage,
            _ => ReferenceCorpusNodeTypes.Passage
        };
    }

    private static IReadOnlyDictionary<string, int> BuildSegmentDepths(IReadOnlyList<ReferenceSourceSegment> segments)
    {
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        var byId = segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            _ = ResolveSegmentDepth(segment.SegmentId, byId, depths, []);
        }

        return depths;
    }

    private static int ResolveSegmentDepth(
        string segmentId,
        IReadOnlyDictionary<string, ReferenceSourceSegment> byId,
        Dictionary<string, int> depths,
        HashSet<string> visiting)
    {
        if (depths.TryGetValue(segmentId, out var existing))
        {
            return existing;
        }

        if (!byId.TryGetValue(segmentId, out var segment) ||
            string.IsNullOrWhiteSpace(segment.ParentSegmentId) ||
            !byId.ContainsKey(segment.ParentSegmentId) ||
            !visiting.Add(segmentId))
        {
            depths[segmentId] = 0;
            return 0;
        }

        var depth = ResolveSegmentDepth(segment.ParentSegmentId, byId, depths, visiting) + 1;
        visiting.Remove(segmentId);
        depths[segmentId] = depth;
        return depth;
    }

    private static IReadOnlyList<SensoryDetection> DetectSensory(string text)
    {
        var detections = new List<SensoryDetection>();
        if (ContainsAny(text, ["声", "响", "听", "雨声", "风声"]))
        {
            detections.Add(new SensoryDetection("auditory", 0.72));
        }

        if (ContainsAny(text, ["光", "影", "灯", "黑", "白", "看", "眼"]))
        {
            detections.Add(new SensoryDetection("visual", 0.62));
        }

        if (ContainsAny(text, ["指尖", "掌心", "捏", "握", "扣", "贴", "挤"]))
        {
            detections.Add(new SensoryDetection("tactile", 0.66));
        }

        if (ContainsAny(text, ["冷", "热", "烫", "凉", "寒"]))
        {
            detections.Add(new SensoryDetection("temperature", 0.64));
        }

        return detections
            .GroupBy(item => item.Sense, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Intensity).First())
            .ToArray();
    }

    private static DeterministicRhythmObservation BuildDeterministicRhythm(string text)
    {
        var charCount = text.Length;
        var label = charCount switch
        {
            <= 12 => "short",
            <= 32 => "medium",
            _ => "long"
        };
        var pauseCount = text.Count(static value => value is '，' or '、' or '；' or ';' or ',' or '。' or '！' or '？' or '!' or '?');
        var cadence = pauseCount >= 3
            ? "staccato"
            : text.Contains('，', StringComparison.Ordinal) || text.Contains('、', StringComparison.Ordinal)
                ? "flowing"
                : "steady";
        return new DeterministicRhythmObservation(label, charCount, cadence);
    }

    private static string DetectEmotion(string text)
    {
        if (ContainsAny(text, ["没有立刻开口", "没有说话", "不开口", "沉默"]))
        {
            return "withheld_answer";
        }

        if (ContainsAny(text, ["攥", "咬", "捏", "拳", "扣在掌心"]))
        {
            return "restrained_pressure";
        }

        if (ContainsAny(text, ["笑", "松了口气", "放心"]))
        {
            return "surface_relief";
        }

        return string.Empty;
    }

    private static DeterministicEmotionObservation MapDeterministicEmotion(string marker)
    {
        return marker switch
        {
            "restrained_pressure" => new DeterministicEmotionObservation("anger", "restrained", "rising", "suppressed", 6.0),
            "surface_relief" => new DeterministicEmotionObservation("relief", "masking", "falling", "indirect", 4.0),
            _ => new DeterministicEmotionObservation("calm", "restrained", "stable", "suppressed", 5.0)
        };
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
        var storedNovelId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);

        return new ReferenceAnchorPayload(
            reader.GetInt64(0),
            storedNovelId ?? WorkspaceCorpusNovelId,
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            ParseTimestamp(reader.GetString(10)),
            ParseTimestamp(reader.GetString(11)),
            reader.GetString(12),
            reader.GetString(13),
            ReadStringList(reader.GetString(14)));
    }

    private static ReferenceAnchorBuildStatusPayload ReadBuildStatus(SqliteDataReader reader)
    {
        return new ReferenceAnchorBuildStatusPayload(
            reader.IsDBNull(0) ? WorkspaceCorpusNovelId : reader.GetInt64(0),
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

    private static ReferenceUserFeedbackPayload ReadUserFeedback(SqliteDataReader reader)
    {
        return new ReferenceUserFeedbackPayload(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetString(8),
            ReadStringList(reader.GetString(9)),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            ParseTimestamp(reader.GetString(13)));
    }

    private static ReferenceAnchorBuildStatusPayload BuildStatus(
        ReferenceAnchorPayload anchor,
        string status,
        string stage,
        int sourceSegmentCount,
        int materialCount,
        int slotCount,
        string lastError,
        DateTimeOffset updatedAt,
        int vectorCount = 0)
    {
        return new ReferenceAnchorBuildStatusPayload(
            anchor.NovelId,
            anchor.AnchorId,
            status,
            stage,
            sourceSegmentCount,
            materialCount,
            slotCount,
            vectorCount,
            lastError,
            updatedAt);
    }

    private static IReadOnlyList<string> NormalizeFeedbackTags(IReadOnlyList<string>? tags)
    {
        return NormalizeUserTags(tags);
    }

    private static IReadOnlyList<string> NormalizeUserTags(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return [];
        }

        return tags
            .Select(tag => NormalizeOptionalText(tag, nameof(tags), maxLength: 128))
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadStringList(string json)
    {
        return JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? [];
    }

    private static IReadOnlyList<long> ReadLongList(string json)
    {
        return JsonSerializer.Deserialize<IReadOnlyList<long>>(json, JsonOptions) ?? [];
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

    private static IReadOnlyList<string> AddStringParameters(
        SqliteCommand command,
        string prefix,
        IReadOnlyList<string> values)
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
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Reference anchor import failed.";
        }

        var redacted = value.Trim();
        redacted = SensitiveFieldPattern.Replace(redacted, "[REDACTED_FIELD]");
        redacted = SecretPattern.Replace(redacted, "[REDACTED_SECRET]");
        redacted = FileUriPattern.Replace(redacted, "[REDACTED_PATH]");
        redacted = UncPathPattern.Replace(redacted, "[REDACTED_PATH]");
        redacted = WindowsPathPattern.Replace(redacted, "[REDACTED_PATH]");
        redacted = UnixPathPattern.Replace(redacted, "[REDACTED_PATH]");
        return redacted.Length <= 1_000 ? redacted : redacted[..1_000].TrimEnd() + "...";
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

    private sealed record ArchivedReferenceMaterialMarker(
        string MaterialId,
        ReferenceMaterialHashKey HashKey,
        string ArchivedAt);

    private sealed record ReferenceAnchorCorpusSnapshot(
        IReadOnlyList<ReferenceSourceSegment> Segments,
        IReadOnlyList<ReferenceMaterialPayload> Materials,
        IReadOnlyDictionary<string, IReadOnlyList<ReferenceMaterialSlot>> SlotsByMaterial,
        IReadOnlyDictionary<string, string> ArchivedMaterialTimestamps);

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

    private sealed record ReferenceMaterialHashKey(string MaterialType, string SourceHash);

    private sealed record CorpusLicenseGateMapping(
        string LicenseState,
        string ReusePolicy,
        double? MaxVerbatimRatio,
        bool ClearedForInsertion);

    private sealed record DeterministicRhythmObservation(
        string Label,
        int CharCount,
        string Cadence);

    private sealed record DeterministicEmotionObservation(
        string Surface,
        string Subtext,
        string Direction,
        string Mode,
        double Intensity);

    private sealed record SensoryDetection(string Sense, double Intensity);

    private sealed record ReferenceProcessingAffectedIds(
        string MaterialId,
        string SegmentId,
        string SlotId)
    {
        public static ReferenceProcessingAffectedIds Empty { get; } = new(string.Empty, string.Empty, string.Empty);
    }

    private sealed record ReferenceProcessingAttemptRead(
        string AttemptId,
        string BuildId,
        int AttemptNumber,
        string BuildVersion,
        string Status,
        DateTimeOffset StartedAt,
        string RecoveredFromAttemptId,
        string RecoveredFromBuildId);

    private sealed record ReferenceProcessingAttemptWrite(
        long AnchorId,
        string AttemptId,
        string BuildId,
        int AttemptNumber,
        string BuildVersion,
        DateTimeOffset StartedAt,
        string RecoveredFromAttemptId,
        string RecoveredFromBuildId);

    private sealed record ReferenceProcessingEventBackfillRow(
        string EventId,
        long AnchorId,
        string BuildVersion,
        string Stage,
        string Status,
        int SourceSegmentCount,
        int MaterialCount,
        int SlotCount,
        int VectorCount,
        string LastError,
        DateTimeOffset CreatedAt);

    private sealed record ReferenceAnchorBuildCompletion(
        ReferenceAnchorPayload Anchor,
        string Stage,
        int VectorCount,
        string LastError);

    private sealed record ReferenceMaterialEmbedding(
        ReferenceMaterialPayload Material,
        IReadOnlyList<float> Vector);

    private sealed record ReferenceMaterialDetailRow(
        ReferenceMaterialPayload Material,
        ReferenceAnchorPayload Anchor,
        DateTimeOffset? ArchivedAt);

    private sealed record TextPreview(
        string Text,
        bool Truncated);

    private sealed record AdaptedMaterial(
        string Text,
        IReadOnlyList<ReferenceSlotValuePayload> ChangedSlots);

    private sealed record StyleSearchOptions(
        IReadOnlyList<long> ProfileIds,
        IReadOnlyList<string> Dimensions,
        string Intensity,
        double FitWeight,
        double SourceRiskPenalty);

    private sealed record StyleSearchContext(
        IReadOnlyDictionary<string, double> FitScores,
        IReadOnlySet<long> SourceAnchorIds,
        double SourceRiskPenalty)
    {
        public static StyleSearchContext Empty { get; } = new(
            new Dictionary<string, double>(StringComparer.Ordinal),
            new HashSet<long>(),
            0);
    }

    private sealed record ScoredSearchMaterial(
        ReferenceMaterialPayload Material,
        IReadOnlyDictionary<string, double> ScoreComponents)
    {
        public double Score => ScoreComponents.Values.Sum();
    }
}
