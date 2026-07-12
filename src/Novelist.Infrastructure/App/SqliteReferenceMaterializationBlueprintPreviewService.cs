using System.Globalization;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class SqliteReferenceMaterializationBlueprintPreviewService : IReferenceMaterializationBlueprintPreviewService
{
    private const int MaximumSelectedSources = 10;
    private const int MaximumGoalCharacters = 800;
    private const int MaximumCandidates = 3;
    private const int MaximumMaterialsPerCandidate = 6;
    private const int MaximumSemanticResultsPerSource = 18;
    private const int MaximumPreviewCharacters = 320;

    private readonly IReferenceCorpusDatabasePathResolver _databasePathResolver;
    private readonly IReferenceMaterializationService _materialization;

    public SqliteReferenceMaterializationBlueprintPreviewService(
        AppInitializationOptions? options = null,
        IReferenceMaterializationService? materialization = null,
        IReferenceCorpusDatabasePathResolver? databasePathResolver = null)
    {
        var initializationOptions = options ?? new AppInitializationOptions();
        _databasePathResolver = databasePathResolver ?? new ReferenceCorpusDatabasePathResolver(initializationOptions);
        _materialization = materialization ?? new SqliteReferenceMaterializationService(initializationOptions);
    }

    public async ValueTask<ReferenceMaterializationBlueprintPreviewPayload> GenerateAsync(
        GenerateReferenceMaterializationBlueprintPreviewPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var request = ValidateGenerateInput(input);
        var sources = new List<PreviewSource>(request.AnchorIds.Count);
        var materials = new List<PreviewMaterial>();
        var seenMaterialIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var anchorId in request.AnchorIds)
        {
            var active = await _materialization.ListActiveMaterialsAsync(
                new ListActiveReferenceMaterializationMaterialsPayload(request.NovelId, anchorId, 1, 1),
                cancellationToken);
            var listedMaterials = active.Items ?? Array.Empty<ReferenceMaterializationMaterialPayload>();
            var firstMaterial = listedMaterials.FirstOrDefault();
            if (active.Total <= 0 || firstMaterial is null || string.IsNullOrWhiteSpace(firstMaterial.GenerationId))
            {
                throw MaterialNotReady(anchorId);
            }

            var generationId = NormalizeIdentifier(firstMaterial.GenerationId, "generation id");
            if (listedMaterials.Any(material =>
                    material.AnchorId != anchorId ||
                    !string.Equals(material.GenerationId, generationId, StringComparison.Ordinal)))
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.GenerationIncomplete,
                    "Active material listing returned material outside the selected generation.");
            }

            sources.Add(new PreviewSource(anchorId, generationId, active.Total));
            var hits = await _materialization.SearchActiveMaterialsAsync(
                new SearchActiveReferenceMaterializationMaterialsPayload(
                    request.NovelId,
                    anchorId,
                    request.Goal,
                    MaximumSemanticResultsPerSource),
                cancellationToken);
            foreach (var hit in hits ?? Array.Empty<ReferenceMaterializationSemanticSearchHitPayload>())
            {
                ValidateSemanticHit(hit, anchorId, generationId);
                if (seenMaterialIds.Add(hit.Material.MaterialId))
                {
                    materials.Add(new PreviewMaterial(hit.Material, Math.Clamp(hit.VectorScore, 0, 1)));
                }
            }
        }

        if (materials.Count == 0)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.BlueprintNoRelevantMaterial,
                "The selected material-ready references did not return semantic material for this blueprint goal.");
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var candidates = BuildCandidates(sessionId, materials, request.RequestedCount);
        var now = DateTimeOffset.UtcNow;
        var preview = new StoredPreview(
            sessionId,
            request.NovelId,
            ReferenceMaterializationBlueprintPreviewStatuses.Active,
            ReferenceMaterializationBlueprintPreviewNextActions.None,
            request.Goal,
            sources,
            candidates,
            now,
            now);
        await PersistAsync(preview, cancellationToken);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var staleAnchorIds = await ReadStaleAnchorIdsAsync(connection, preview.Sources, cancellationToken);
        if (staleAnchorIds.Count > 0)
        {
            var updatedAt = DateTimeOffset.UtcNow;
            await MarkStaleAsync(connection, preview.SessionId, updatedAt, cancellationToken);
            preview = preview with
            {
                Status = ReferenceMaterializationBlueprintPreviewStatuses.Stale,
                NextAction = ReferenceMaterializationBlueprintPreviewNextActions.Rebuild,
                UpdatedAt = updatedAt
            };
        }

        return ToPayload(preview, staleAnchorIds);
    }

    public async ValueTask<ReferenceMaterializationBlueprintPreviewPayload?> GetAsync(
        GetReferenceMaterializationBlueprintPreviewPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.NovelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Novel id must be positive.");
        }

        var sessionId = NormalizeIdentifier(input.SessionId, "session id");
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var preview = await ReadPreviewAsync(connection, input.NovelId, sessionId, cancellationToken);
        if (preview is null)
        {
            return null;
        }

        var staleAnchorIds = await ReadStaleAnchorIdsAsync(connection, preview.Sources, cancellationToken);
        if (staleAnchorIds.Count > 0 && preview.Status != ReferenceMaterializationBlueprintPreviewStatuses.Stale)
        {
            var updatedAt = DateTimeOffset.UtcNow;
            await MarkStaleAsync(connection, preview.SessionId, updatedAt, cancellationToken);
            preview = preview with
            {
                Status = ReferenceMaterializationBlueprintPreviewStatuses.Stale,
                NextAction = ReferenceMaterializationBlueprintPreviewNextActions.Rebuild,
                UpdatedAt = updatedAt
            };
        }

        var reportedStaleAnchorIds = staleAnchorIds.Count > 0
            ? staleAnchorIds
            : preview.Status == ReferenceMaterializationBlueprintPreviewStatuses.Stale
                ? preview.Sources.Select(source => source.AnchorId).ToArray()
                : Array.Empty<long>();
        return ToPayload(preview, reportedStaleAnchorIds);
    }

    private async ValueTask PersistAsync(StoredPreview preview, CancellationToken cancellationToken)
    {
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await InsertSessionAsync(connection, transaction, preview, cancellationToken);
        foreach (var source in preview.Sources)
        {
            await InsertSourceAsync(connection, transaction, preview.SessionId, source, cancellationToken);
        }

        foreach (var candidate in preview.Candidates)
        {
            await InsertCandidateAsync(connection, transaction, preview.SessionId, candidate, cancellationToken);
            foreach (var beat in candidate.Beats)
            {
                await InsertBeatAsync(connection, transaction, preview.SessionId, candidate.BlueprintId, beat, cancellationToken);
                foreach (var material in beat.Materials)
                {
                    await InsertMaterialLinkAsync(
                        connection,
                        transaction,
                        preview.SessionId,
                        candidate.BlueprintId,
                        beat.BeatId,
                        material,
                        cancellationToken);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask InsertSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredPreview preview,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_materialization_blueprint_preview_sessions (
                session_id, novel_id, goal, status, next_action, created_at, updated_at)
            VALUES ($session_id, $novel_id, $goal, $status, $next_action, $created_at, $updated_at);
            """;
        command.Parameters.AddWithValue("$session_id", preview.SessionId);
        command.Parameters.AddWithValue("$novel_id", preview.NovelId);
        command.Parameters.AddWithValue("$goal", preview.Goal);
        command.Parameters.AddWithValue("$status", preview.Status);
        command.Parameters.AddWithValue("$next_action", preview.NextAction);
        command.Parameters.AddWithValue("$created_at", Timestamp(preview.CreatedAt));
        command.Parameters.AddWithValue("$updated_at", Timestamp(preview.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask InsertSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        PreviewSource source,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_materialization_blueprint_preview_sources (
                session_id, anchor_id, generation_id, material_count)
            VALUES ($session_id, $anchor_id, $generation_id, $material_count);
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$anchor_id", source.AnchorId);
        command.Parameters.AddWithValue("$generation_id", source.GenerationId);
        command.Parameters.AddWithValue("$material_count", source.MaterialCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask InsertCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        PreviewCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_materialization_blueprint_preview_candidates (
                session_id, blueprint_id, candidate_index, strategy)
            VALUES ($session_id, $blueprint_id, $candidate_index, $strategy);
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$blueprint_id", candidate.BlueprintId);
        command.Parameters.AddWithValue("$candidate_index", candidate.CandidateIndex);
        command.Parameters.AddWithValue("$strategy", candidate.Strategy);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask InsertBeatAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string blueprintId,
        PreviewBeat beat,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_materialization_blueprint_preview_beats (
                session_id, blueprint_id, beat_id, beat_index, intent, narrative_function)
            VALUES ($session_id, $blueprint_id, $beat_id, $beat_index, $intent, $narrative_function);
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        command.Parameters.AddWithValue("$beat_id", beat.BeatId);
        command.Parameters.AddWithValue("$beat_index", beat.BeatIndex);
        command.Parameters.AddWithValue("$intent", beat.Intent);
        command.Parameters.AddWithValue("$narrative_function", beat.NarrativeFunction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask InsertMaterialLinkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string blueprintId,
        string beatId,
        PreviewLink link,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_materialization_blueprint_preview_material_links (
                session_id, blueprint_id, beat_id, material_id, anchor_id, generation_id,
                material_type, text_preview, quality_score, vector_score, fit_explanation, material_rank)
            VALUES (
                $session_id, $blueprint_id, $beat_id, $material_id, $anchor_id, $generation_id,
                $material_type, $text_preview, $quality_score, $vector_score, $fit_explanation, $material_rank);
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        command.Parameters.AddWithValue("$beat_id", beatId);
        command.Parameters.AddWithValue("$material_id", link.MaterialId);
        command.Parameters.AddWithValue("$anchor_id", link.AnchorId);
        command.Parameters.AddWithValue("$generation_id", link.GenerationId);
        command.Parameters.AddWithValue("$material_type", link.MaterialType);
        command.Parameters.AddWithValue("$text_preview", link.TextPreview);
        command.Parameters.AddWithValue("$quality_score", link.QualityScore);
        command.Parameters.AddWithValue("$vector_score", link.VectorScore);
        command.Parameters.AddWithValue("$fit_explanation", link.FitExplanation);
        command.Parameters.AddWithValue("$material_rank", link.MaterialRank);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<StoredPreview?> ReadPreviewAsync(
        SqliteConnection connection,
        long novelId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        StoredPreview? preview;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT session_id, novel_id, goal, status, next_action, created_at, updated_at
                FROM reference_materialization_blueprint_preview_sessions
                WHERE session_id = $session_id AND novel_id = $novel_id;
                """;
            command.Parameters.AddWithValue("$session_id", sessionId);
            command.Parameters.AddWithValue("$novel_id", novelId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            preview = new StoredPreview(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(2),
                [],
                [],
                ParseTimestamp(reader.GetString(5)),
                ParseTimestamp(reader.GetString(6)));
        }

        var sources = new List<PreviewSource>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT anchor_id, generation_id, material_count
                FROM reference_materialization_blueprint_preview_sources
                WHERE session_id = $session_id
                ORDER BY anchor_id;
                """;
            command.Parameters.AddWithValue("$session_id", sessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sources.Add(new PreviewSource(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }

        var candidates = new List<PreviewCandidate>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT candidate.blueprint_id, candidate.candidate_index, candidate.strategy,
                       beat.beat_id, beat.beat_index, beat.intent, beat.narrative_function,
                       link.material_id, link.anchor_id, link.generation_id, link.material_type,
                       link.text_preview, link.quality_score, link.vector_score, link.fit_explanation,
                       link.material_rank
                FROM reference_materialization_blueprint_preview_candidates candidate
                JOIN reference_materialization_blueprint_preview_beats beat
                  ON beat.session_id = candidate.session_id
                 AND beat.blueprint_id = candidate.blueprint_id
                JOIN reference_materialization_blueprint_preview_material_links link
                  ON link.session_id = beat.session_id
                 AND link.blueprint_id = beat.blueprint_id
                 AND link.beat_id = beat.beat_id
                WHERE candidate.session_id = $session_id
                ORDER BY candidate.candidate_index, beat.beat_index, link.material_rank, link.material_id;
                """;
            command.Parameters.AddWithValue("$session_id", sessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var candidatesById = new Dictionary<string, MutablePreviewCandidate>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                var blueprintId = reader.GetString(0);
                if (!candidatesById.TryGetValue(blueprintId, out var candidate))
                {
                    candidate = new MutablePreviewCandidate(blueprintId, reader.GetInt32(1), reader.GetString(2));
                    candidatesById.Add(blueprintId, candidate);
                }

                var beatId = reader.GetString(3);
                if (!candidate.BeatsById.TryGetValue(beatId, out var beat))
                {
                    beat = new MutablePreviewBeat(beatId, reader.GetInt32(4), reader.GetString(5), reader.GetString(6));
                    candidate.BeatsById.Add(beatId, beat);
                }

                beat.Materials.Add(new PreviewLink(
                    reader.GetString(7),
                    reader.GetInt64(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetDouble(12),
                    reader.GetDouble(13),
                    reader.GetString(14),
                    reader.GetInt32(15)));
            }

            candidates.AddRange(candidatesById.Values
                .OrderBy(candidate => candidate.CandidateIndex)
                .Select(candidate => new PreviewCandidate(
                    candidate.BlueprintId,
                    candidate.CandidateIndex,
                    candidate.Strategy,
                    candidate.BeatsById.Values
                        .OrderBy(beat => beat.BeatIndex)
                        .Select(beat => new PreviewBeat(
                            beat.BeatId,
                            beat.BeatIndex,
                            beat.Intent,
                            beat.NarrativeFunction,
                            beat.Materials.OrderBy(link => link.MaterialRank).ToArray()))
                        .ToArray())));
        }

        return preview with { Sources = sources, Candidates = candidates };
    }

    private static async ValueTask<IReadOnlyList<long>> ReadStaleAnchorIdsAsync(
        SqliteConnection connection,
        IReadOnlyList<PreviewSource> sources,
        CancellationToken cancellationToken)
    {
        var stale = new List<long>();
        foreach (var source in sources)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT active_generation_id
                FROM reference_anchor_materialization_state
                WHERE anchor_id = $anchor_id;
                """;
            command.Parameters.AddWithValue("$anchor_id", source.AnchorId);
            var currentGenerationId = (string?)await command.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(currentGenerationId, source.GenerationId, StringComparison.Ordinal))
            {
                stale.Add(source.AnchorId);
            }
        }

        return stale;
    }

    private static async ValueTask MarkStaleAsync(
        SqliteConnection connection,
        string sessionId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_materialization_blueprint_preview_sessions
            SET status = $status, next_action = $next_action, updated_at = $updated_at
            WHERE session_id = $session_id;
            """;
        command.Parameters.AddWithValue("$status", ReferenceMaterializationBlueprintPreviewStatuses.Stale);
        command.Parameters.AddWithValue("$next_action", ReferenceMaterializationBlueprintPreviewNextActions.Rebuild);
        command.Parameters.AddWithValue("$updated_at", Timestamp(updatedAt));
        command.Parameters.AddWithValue("$session_id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask<string> EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var databasePath = await _databasePathResolver.ResolveAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
        return databasePath;
    }

    private static IReadOnlyList<PreviewCandidate> BuildCandidates(
        string sessionId,
        IReadOnlyList<PreviewMaterial> materials,
        int requestedCount)
    {
        var strategies = new[] { "pressure_chain", "emotion_arc", "technique_focus" };
        return strategies
            .Take(requestedCount)
            .Select((strategy, index) => BuildCandidate(sessionId, index, strategy, OrderMaterials(materials, strategy)))
            .ToArray();
    }

    private static PreviewCandidate BuildCandidate(
        string sessionId,
        int candidateIndex,
        string strategy,
        IReadOnlyList<PreviewMaterial> orderedMaterials)
    {
        var selected = orderedMaterials.Take(MaximumMaterialsPerCandidate).ToArray();
        var beatCount = Math.Min(3, selected.Length);
        var groupSize = (int)Math.Ceiling(selected.Length / (double)beatCount);
        var blueprintId = sessionId + "-blueprint-" + (candidateIndex + 1).ToString(CultureInfo.InvariantCulture);
        var beats = selected
            .Chunk(groupSize)
            .Take(beatCount)
            .Select((group, beatIndex) => BuildBeat(blueprintId, beatIndex, group))
            .ToArray();
        return new PreviewCandidate(blueprintId, candidateIndex, strategy, beats);
    }

    private static PreviewBeat BuildBeat(string blueprintId, int beatIndex, IReadOnlyList<PreviewMaterial> materials)
    {
        var first = materials[0];
        var narrativeFunction = first.Material.Tags.NarrativeFunctions.FirstOrDefault() ?? first.Material.MaterialType;
        var intent = beatIndex switch
        {
            0 => "Establish pressure through " + narrativeFunction + ".",
            1 => "Turn the scene through " + narrativeFunction + ".",
            _ => "Leave a forward hook through " + narrativeFunction + "."
        };
        var links = materials
            .Select((item, rank) => new PreviewLink(
                item.Material.MaterialId,
                item.Material.AnchorId,
                item.Material.GenerationId,
                item.Material.MaterialType,
                BuildTextPreview(item.Material.Text),
                item.Material.QualityScore,
                item.VectorScore,
                BuildFitExplanation(item.Material, item.VectorScore),
                rank))
            .ToArray();
        return new PreviewBeat(
            blueprintId + "-beat-" + (beatIndex + 1).ToString(CultureInfo.InvariantCulture),
            beatIndex,
            intent,
            narrativeFunction,
            links);
    }

    private static IReadOnlyList<PreviewMaterial> OrderMaterials(
        IReadOnlyList<PreviewMaterial> materials,
        string strategy) => materials
        .OrderByDescending(material => StrategyScore(material, strategy))
        .ThenByDescending(material => material.VectorScore)
        .ThenByDescending(material => material.Material.QualityScore)
        .ThenBy(material => material.Material.MaterialId, StringComparer.Ordinal)
        .ToArray();

    private static double StrategyScore(PreviewMaterial material, string strategy)
    {
        var tags = material.Material.Tags;
        var bonus = strategy switch
        {
            "pressure_chain" when tags.NarrativeFunctions.Any(value => value is "conflict" or "turn" or "hook") => 0.2,
            "emotion_arc" when tags.EmotionMechanics.Count > 0 => 0.2,
            "technique_focus" when tags.Techniques.Count > 0 => 0.2,
            _ => 0
        };
        return material.Material.QualityScore * 0.5 + material.VectorScore * 0.5 + bonus;
    }

    private static string BuildTextPreview(string value)
    {
        var normalized = new string((value ?? string.Empty)
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray())
            .Trim();
        return normalized.Length <= MaximumPreviewCharacters
            ? normalized
            : normalized[..MaximumPreviewCharacters].TrimEnd() + "...";
    }

    private static string BuildFitExplanation(ReferenceMaterializationMaterialPayload material, double vectorScore)
    {
        var function = material.Tags.NarrativeFunctions.FirstOrDefault() ?? material.MaterialType;
        return "Semantic match " + vectorScore.ToString("0.000", CultureInfo.InvariantCulture) + "; supports " + function + ".";
    }

    private static void ValidateSemanticHit(
        ReferenceMaterializationSemanticSearchHitPayload hit,
        long anchorId,
        string generationId)
    {
        if (hit is null || hit.Material is null ||
            hit.Material.AnchorId != anchorId ||
            !string.Equals(hit.Material.GenerationId, generationId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(hit.Material.MaterialId) ||
            !double.IsFinite(hit.Material.QualityScore) ||
            !double.IsFinite(hit.VectorScore))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "Active semantic retrieval returned a material outside the selected generation.");
        }
    }

    private static GenerateRequest ValidateGenerateInput(GenerateReferenceMaterializationBlueprintPreviewPayload input)
    {
        if (input.NovelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Novel id must be positive.");
        }

        var anchorIds = (input.AnchorIds ?? Array.Empty<long>())
            .Distinct()
            .OrderBy(anchorId => anchorId)
            .ToArray();
        if (anchorIds.Length is 0 or > MaximumSelectedSources || anchorIds.Any(anchorId => anchorId <= 0))
        {
            throw new ArgumentException("Blueprint preview requires between one and ten distinct reference sources.", nameof(input));
        }

        var goal = input.Goal?.Trim() ?? string.Empty;
        if (goal.Length == 0 || goal.Length > MaximumGoalCharacters || goal.Any(char.IsControl))
        {
            throw new ArgumentException("Blueprint preview goal is invalid.", nameof(input));
        }

        if (input.RequestedCount is < 1 or > MaximumCandidates)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Blueprint preview requested count must be between one and three.");
        }

        return new GenerateRequest(input.NovelId, anchorIds, goal, input.RequestedCount);
    }

    private static ReferenceMaterializationException MaterialNotReady(long anchorId) => new(
        ReferenceMaterializationErrorCodes.BlueprintMaterialNotReady,
        "Reference source " + anchorId.ToString(CultureInfo.InvariantCulture) + " has no active material-ready generation. Rebuild materialization before previewing a blueprint.");

    private static string NormalizeIdentifier(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > 128 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Blueprint preview " + fieldName + " is invalid.", fieldName);
        }

        return normalized;
    }

    private static string Timestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static ReferenceMaterializationBlueprintPreviewPayload ToPayload(
        StoredPreview preview,
        IReadOnlyList<long> staleAnchorIds) => new(
            preview.SessionId,
            preview.Status,
            preview.NextAction,
            preview.Goal,
            preview.Sources
                .Select(source => new ReferenceMaterializationBlueprintPreviewSourcePayload(
                    source.AnchorId,
                    source.GenerationId,
                    source.MaterialCount))
                .ToArray(),
            preview.Candidates
                .Select(candidate => new ReferenceMaterializationBlueprintPreviewCandidatePayload(
                    candidate.BlueprintId,
                    candidate.Strategy,
                    candidate.Beats
                        .Select(beat => new ReferenceMaterializationBlueprintPreviewBeatPayload(
                            beat.BeatId,
                            beat.BeatIndex,
                            beat.Intent,
                            beat.NarrativeFunction,
                            beat.Materials
                                .Select(link => new ReferenceMaterializationBlueprintPreviewMaterialLinkPayload(
                                    link.MaterialId,
                                    link.AnchorId,
                                    link.GenerationId,
                                    link.MaterialType,
                                    link.TextPreview,
                                    link.QualityScore,
                                    link.VectorScore,
                                    link.FitExplanation))
                                .ToArray()))
                        .ToArray()))
                .ToArray(),
            staleAnchorIds.ToArray(),
            preview.CreatedAt,
            preview.UpdatedAt);

    private sealed record GenerateRequest(long NovelId, IReadOnlyList<long> AnchorIds, string Goal, int RequestedCount);

    private sealed record PreviewSource(long AnchorId, string GenerationId, long MaterialCount);

    private sealed record PreviewMaterial(ReferenceMaterializationMaterialPayload Material, double VectorScore);

    private sealed record PreviewLink(
        string MaterialId,
        long AnchorId,
        string GenerationId,
        string MaterialType,
        string TextPreview,
        double QualityScore,
        double VectorScore,
        string FitExplanation,
        int MaterialRank);

    private sealed record PreviewBeat(
        string BeatId,
        int BeatIndex,
        string Intent,
        string NarrativeFunction,
        IReadOnlyList<PreviewLink> Materials);

    private sealed record PreviewCandidate(
        string BlueprintId,
        int CandidateIndex,
        string Strategy,
        IReadOnlyList<PreviewBeat> Beats);

    private sealed record StoredPreview(
        string SessionId,
        long NovelId,
        string Status,
        string NextAction,
        string Goal,
        IReadOnlyList<PreviewSource> Sources,
        IReadOnlyList<PreviewCandidate> Candidates,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed class MutablePreviewCandidate
    {
        public MutablePreviewCandidate(string blueprintId, int candidateIndex, string strategy)
        {
            BlueprintId = blueprintId;
            CandidateIndex = candidateIndex;
            Strategy = strategy;
        }

        public string BlueprintId { get; }

        public int CandidateIndex { get; }

        public string Strategy { get; }

        public Dictionary<string, MutablePreviewBeat> BeatsById { get; } = new(StringComparer.Ordinal);
    }

    private sealed class MutablePreviewBeat
    {
        public MutablePreviewBeat(string beatId, int beatIndex, string intent, string narrativeFunction)
        {
            BeatId = beatId;
            BeatIndex = beatIndex;
            Intent = intent;
            NarrativeFunction = narrativeFunction;
        }

        public string BeatId { get; }

        public int BeatIndex { get; }

        public string Intent { get; }

        public string NarrativeFunction { get; }

        public List<PreviewLink> Materials { get; } = [];
    }
}
