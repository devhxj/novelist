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
        ReferenceMaterializationBatchSizes.Validate(input.ChapterBatchSize);
        var splitProfileId = NormalizeProfileId(input.SplitProfileId);
        await EnsureConfirmedProfileMatchesCurrentSourceAsync(
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
                "candidate-window-v1",
                "material-qualifier-v1",
                models.Llm,
                models.Embedding,
                input.ChapterBatchSize,
                now),
            cancellationToken);
    }

    public async ValueTask<ReferenceMaterializationStatusPayload?> GetMaterializationStatusAsync(
        GetReferenceMaterializationStatusPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateReferenceInput(input.NovelId, input.AnchorId);
        await EnsureAnchorAccessibleAsync(input.NovelId, input.AnchorId, cancellationToken);
        var status = await _runStore.GetAsync(input.RunId, cancellationToken);
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
