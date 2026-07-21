using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class SqliteReferenceWritingService : IReferenceWritingService
{
    private const int MaximumGoalCharacters = 2_000;
    private const int MaximumBlueprints = 3;
    private const int MaximumMaterialsPerBlueprint = 6;
    private static readonly string[] Strategies = ["progressive", "contrast", "focused"];
    private static readonly JsonSerializerOptions JsonOptions = BridgeJson.SerializerOptions;

    private readonly IReferenceCorpusDatabasePathResolver _databasePathResolver;
    private readonly IReferenceMaterialSearch _materials;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqliteReferenceWritingService(
        AppInitializationOptions? options = null,
        IReferenceMaterialSearch? materials = null,
        IReferenceCorpusDatabasePathResolver? databasePathResolver = null)
    {
        var initializationOptions = options ?? new AppInitializationOptions();
        _databasePathResolver = databasePathResolver ?? new ReferenceCorpusDatabasePathResolver(initializationOptions);
        _materials = materials ?? new SqliteReferenceMaterialSearch(initializationOptions);
    }

    public async ValueTask<ReferenceWritingSessionPayload> GenerateBlueprintsAsync(
        GenerateReferenceBlueprintsPayload input,
        CancellationToken cancellationToken)
    {
        var request = Validate(input);
        var hits = await _materials.SearchAsync(
            new ReferenceMaterialSearchRequest(
                request.Goal,
                MaximumBlueprints * MaximumMaterialsPerBlueprint,
                SessionId: BuildLibrarySessionId(request.NovelId)),
            cancellationToken);
        ValidateHits(hits);
        if (hits.Count == 0)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.BlueprintNoRelevantMaterial,
                "No active reference material is available for this chapter goal.");
        }

        var blueprints = BuildBlueprints(request.Goal, hits, request.RequestedCount);
        if (blueprints.Count == 0)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.BlueprintNoRelevantMaterial,
                "Active reference material could not form a chapter blueprint.");
        }

        foreach (var blueprint in blueprints)
        {
            await ReadBlueprintMaterialsAsync(request.NovelId, blueprint, cancellationToken);
        }

        var session = new ReferenceWritingSessionPayload(
            request.SessionId,
            request.NovelId,
            request.ChapterNumber,
            request.Goal,
            blueprints,
            string.Empty,
            DateTimeOffset.UtcNow);
        await PersistAsync(session, cancellationToken);
        return session;
    }

    public async ValueTask<ReferenceWritingSessionPayload?> GetSessionAsync(
        GetReferenceWritingSessionPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateIdentity(input.NovelId, input.ChapterNumber, input.SessionId);
        ReferenceWritingSessionPayload? session;
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await _databasePathResolver.ResolveAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT payload_json
                FROM reference_writing_sessions
                WHERE session_id = $session_id
                  AND novel_id = $novel_id
                  AND chapter_number = $chapter_number;
                """;
            command.Parameters.AddWithValue("$session_id", input.SessionId.Trim());
            command.Parameters.AddWithValue("$novel_id", input.NovelId);
            command.Parameters.AddWithValue("$chapter_number", input.ChapterNumber);
            var json = await command.ExecuteScalarAsync(cancellationToken) as string;
            session = json is null
                ? null
                : JsonSerializer.Deserialize<ReferenceWritingSessionPayload>(json, JsonOptions)
                    ?? throw new InvalidOperationException("Stored reference writing session is invalid.");
        }
        finally
        {
            _mutex.Release();
        }

        if (session is not null)
        {
            foreach (var blueprint in session.Blueprints)
            {
                await ReadBlueprintMaterialsAsync(session.NovelId, blueprint, cancellationToken);
            }
        }

        return session;
    }

    public async ValueTask<ReferenceWritingSessionPayload> SelectBlueprintAsync(
        SelectReferenceBlueprintPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateIdentity(input.NovelId, input.ChapterNumber, input.SessionId);
        var blueprintId = NormalizeIdentifier(input.BlueprintId, "blueprint id");
        var session = await GetRequiredSessionAsync(
            input.NovelId,
            input.ChapterNumber,
            input.SessionId,
            cancellationToken);
        var blueprint = session.Blueprints.FirstOrDefault(candidate =>
            string.Equals(candidate.BlueprintId, blueprintId, StringComparison.Ordinal))
            ?? throw new ArgumentException("Selected reference blueprint does not belong to this session.", nameof(input));
        await ReadBlueprintMaterialsAsync(input.NovelId, blueprint, cancellationToken);
        var selected = session with
        {
            SelectedBlueprintId = blueprint.BlueprintId,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await PersistAsync(selected, cancellationToken);
        return selected;
    }

    public async ValueTask<ReferenceWritingDraftCandidatesPayload> GenerateDraftCandidatesAsync(
        GenerateReferenceDraftCandidatesPayload input,
        CancellationToken cancellationToken)
    {
        var request = Validate(input);
        var session = await GetRequiredSessionAsync(
            request.NovelId,
            request.ChapterNumber,
            request.SessionId,
            cancellationToken);
        if (!string.Equals(session.SelectedBlueprintId, request.BlueprintId, StringComparison.Ordinal))
        {
            throw new ReferenceWritingException(
                ReferenceWritingErrorCodes.BlueprintNotSelected,
                "Select this reference blueprint before generating draft candidates.");
        }

        var blueprint = session.Blueprints.FirstOrDefault(candidate =>
            string.Equals(candidate.BlueprintId, request.BlueprintId, StringComparison.Ordinal))
            ?? throw new ReferenceWritingException(
                ReferenceWritingErrorCodes.BlueprintNotSelected,
                "The selected reference blueprint no longer belongs to this session.");
        var materials = await ReadBlueprintMaterialsAsync(request.NovelId, blueprint, cancellationToken);
        var candidates = BuildDraftCandidates(request, blueprint, materials);
        if (candidates.Count == 0)
        {
            throw new ReferenceWritingException(
                ReferenceWritingErrorCodes.MaterialNotInsertable,
                "The selected reference blueprint cannot produce a distinct draft candidate.");
        }

        return new ReferenceWritingDraftCandidatesPayload(
            session.SessionId,
            blueprint.BlueprintId,
            candidates);
    }

    private async ValueTask PersistAsync(
        ReferenceWritingSessionPayload session,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await _databasePathResolver.ResolveAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO reference_writing_sessions (
                  session_id, novel_id, chapter_number, goal, payload_json, updated_at)
                VALUES (
                  $session_id, $novel_id, $chapter_number, $goal, $payload_json, $updated_at)
                ON CONFLICT(novel_id, chapter_number, session_id) DO UPDATE SET
                  goal = excluded.goal,
                  payload_json = excluded.payload_json,
                  updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$session_id", session.SessionId);
            command.Parameters.AddWithValue("$novel_id", session.NovelId);
            command.Parameters.AddWithValue("$chapter_number", session.ChapterNumber);
            command.Parameters.AddWithValue("$goal", session.Goal);
            command.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(session, JsonOptions));
            command.Parameters.AddWithValue("$updated_at", session.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<ReferenceWritingSessionPayload> GetRequiredSessionAsync(
        long novelId,
        int chapterNumber,
        string sessionId,
        CancellationToken cancellationToken) =>
        await GetSessionAsync(
            new GetReferenceWritingSessionPayload(novelId, chapterNumber, sessionId),
            cancellationToken)
        ?? throw new ReferenceWritingException(
            ReferenceWritingErrorCodes.SessionNotFound,
            "Reference writing session does not exist.");

    private async ValueTask<IReadOnlyDictionary<string, MaterialSnapshot>> ReadBlueprintMaterialsAsync(
        long novelId,
        ReferenceWritingBlueprintPayload blueprint,
        CancellationToken cancellationToken)
    {
        var identities = blueprint.Beats
            .SelectMany(beat => beat.Materials)
            .Distinct()
            .ToArray();
        if (identities.Length == 0)
        {
            throw new ReferenceWritingException(
                ReferenceWritingErrorCodes.MaterialMissing,
                "Reference blueprint contains no material sources.");
        }

        var databasePath = await _databasePathResolver.ResolveAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        var materials = new Dictionary<string, MaterialSnapshot>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            var snapshot = await ReadMaterialAsync(
                connection,
                novelId,
                identity,
                cancellationToken);
            materials.Add(MaterialKey(identity.MaterialId, identity.GenerationId), snapshot);
        }

        foreach (var snapshot in materials.Values)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT active_generation_id
                FROM reference_anchor_materialization_state
                WHERE anchor_id = $anchor_id;
                """;
            command.Parameters.AddWithValue("$anchor_id", snapshot.AnchorId);
            var generationId = await command.ExecuteScalarAsync(cancellationToken) as string;
            if (!string.Equals(generationId, snapshot.GenerationId, StringComparison.Ordinal))
            {
                throw new ReferenceWritingException(
                    ReferenceWritingErrorCodes.BlueprintStale,
                    "A reference material generation changed after this blueprint was created.");
            }
        }

        return materials;
    }

    private static async ValueTask<MaterialSnapshot> ReadMaterialAsync(
        SqliteConnection connection,
        long novelId,
        ReferenceMaterialIdentityPayload identity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT material.material_id, material.generation_id, material.anchor_id,
                   material.chapter_index, material.text, material.text_hash,
                   license.license_state, license.reuse_policy
            FROM reference_materials material
            JOIN reference_anchor_materialization_state state
              ON state.anchor_id = material.anchor_id
             AND state.active_generation_id = material.generation_id
            JOIN reference_source_license license
              ON license.anchor_id = material.anchor_id
            WHERE material.material_id = $material_id
              AND material.generation_id = $generation_id
              AND license.license_state IN ($public_domain, $creative_commons, $authorized)
              AND license.reuse_policy <> $forbidden
              AND license.cleared_for_insertion = 1
              AND EXISTS (
                SELECT 1
                FROM reference_library_members member
                JOIN reference_session_library_binding binding
                  ON binding.library_id = member.library_id
                WHERE member.anchor_id = material.anchor_id
                  AND member.enabled = 1
                  AND binding.session_id = $session_id
              );
            """;
        command.Parameters.AddWithValue("$material_id", identity.MaterialId);
        command.Parameters.AddWithValue("$generation_id", identity.GenerationId);
        command.Parameters.AddWithValue("$public_domain", ReferenceCorpusLicenseStates.PublicDomain);
        command.Parameters.AddWithValue("$creative_commons", ReferenceCorpusLicenseStates.CreativeCommons);
        command.Parameters.AddWithValue("$authorized", ReferenceCorpusLicenseStates.Authorized);
        command.Parameters.AddWithValue("$forbidden", ReferenceCorpusReusePolicies.Forbidden);
        command.Parameters.AddWithValue("$session_id", BuildLibrarySessionId(novelId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await ThrowUnavailableMaterialAsync(connection, identity, cancellationToken);
            throw new InvalidOperationException("Unavailable material diagnosis did not throw.");
        }

        var snapshot = new MaterialSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new ReferenceWritingException(
                ReferenceWritingErrorCodes.MaterialMissing,
                "Reference material identity resolved to multiple source rows.");
        }

        if (!string.Equals(Hash(snapshot.Text), snapshot.TextHash, StringComparison.Ordinal))
        {
            throw new ReferenceWritingException(
                ReferenceWritingErrorCodes.MaterialTextMismatch,
                "Reference material text no longer matches its frozen hash.");
        }

        return snapshot;
    }

    private static async ValueTask ThrowUnavailableMaterialAsync(
        SqliteConnection connection,
        ReferenceMaterialIdentityPayload identity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT material.generation_id, state.active_generation_id
            FROM reference_materials material
            LEFT JOIN reference_anchor_materialization_state state
              ON state.anchor_id = material.anchor_id
            WHERE material.material_id = $material_id;
            """;
        command.Parameters.AddWithValue("$material_id", identity.MaterialId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ReferenceWritingException(
                ReferenceWritingErrorCodes.MaterialMissing,
                "A material referenced by the selected blueprint no longer exists.");
        }

        var storedGeneration = reader.GetString(0);
        var activeGeneration = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (!string.Equals(storedGeneration, identity.GenerationId, StringComparison.Ordinal) ||
            !string.Equals(activeGeneration, identity.GenerationId, StringComparison.Ordinal))
        {
            throw new ReferenceWritingException(
                ReferenceWritingErrorCodes.BlueprintStale,
                "A material generation referenced by the selected blueprint is no longer active.");
        }

        throw new ReferenceWritingException(
            ReferenceWritingErrorCodes.MaterialNotInsertable,
            "A material referenced by the selected blueprint is not authorized for insertion.");
    }

    private static IReadOnlyList<ReferenceWritingDraftCandidatePayload> BuildDraftCandidates(
        DraftRequest request,
        ReferenceWritingBlueprintPayload blueprint,
        IReadOnlyDictionary<string, MaterialSnapshot> materials)
    {
        var candidates = new List<ReferenceWritingDraftCandidatePayload>(request.RequestedCount);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var candidateIndex = 0; candidateIndex < request.RequestedCount; candidateIndex++)
        {
            var selected = blueprint.Beats
                .Select(beat => (Beat: beat, Identity: beat.Materials[candidateIndex % beat.Materials.Count]))
                .ToArray();
            var identityKey = string.Join(
                '|',
                selected.Select(item => item.Identity.MaterialId + "@" + item.Identity.GenerationId));
            if (!seen.Add(identityKey))
            {
                continue;
            }

            var texts = new List<string>(selected.Length);
            var sources = new List<ReferenceWritingDraftSourcePayload>(selected.Length);
            foreach (var item in selected)
            {
                var material = materials[MaterialKey(
                    item.Identity.MaterialId,
                    item.Identity.GenerationId)];
                var output = ApplySlots(material.Text, request.SlotValues);
                if (string.Equals(material.ReusePolicy, ReferenceCorpusReusePolicies.ReferenceOnly, StringComparison.Ordinal) ||
                    string.Equals(material.ReusePolicy, ReferenceCorpusReusePolicies.Forbidden, StringComparison.Ordinal) ||
                    (string.Equals(material.ReusePolicy, ReferenceCorpusReusePolicies.AdaptedOnly, StringComparison.Ordinal) &&
                     string.Equals(output, material.Text, StringComparison.Ordinal)))
                {
                    throw new ReferenceWritingException(
                        ReferenceWritingErrorCodes.MaterialNotInsertable,
                        "The selected reference material requires an explicit, effective adaptation.");
                }

                texts.Add(output);
                sources.Add(new ReferenceWritingDraftSourcePayload(
                    item.Beat.BeatId,
                    material.MaterialId,
                    material.GenerationId,
                    material.AnchorId,
                    material.ChapterIndex,
                    material.TextHash,
                    material.LicenseState,
                    material.ReusePolicy));
            }

            var text = string.Join("\n\n", texts);
            var candidateId = "draft-" + Hash(blueprint.BlueprintId + "|" + identityKey + "|" + text)[..24];
            candidates.Add(new ReferenceWritingDraftCandidatePayload(
                candidateId,
                blueprint.BlueprintId,
                text,
                InsertAt(request.CurrentDraftText, request.InsertionOffset, text),
                sources,
                new ReferenceWritingDraftAuditPayload(true, [])));
        }

        return candidates;
    }

    private static string ApplySlots(
        string text,
        IReadOnlyDictionary<string, string> slotValues)
    {
        var output = text;
        foreach (var slot in slotValues.OrderByDescending(item => item.Key.Length))
        {
            output = output.Replace(slot.Key, slot.Value, StringComparison.Ordinal);
        }

        return output;
    }

    private static string InsertAt(string currentText, int insertionOffset, string text) =>
        currentText.Insert(insertionOffset, text);

    private static IReadOnlyList<ReferenceWritingBlueprintPayload> BuildBlueprints(
        string goal,
        IReadOnlyList<ReferenceMaterialSearchHit> hits,
        int requestedCount)
    {
        var blueprints = new List<ReferenceWritingBlueprintPayload>(requestedCount);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var candidateIndex = 0; candidateIndex < requestedCount; candidateIndex++)
        {
            var selected = Rotate(hits, candidateIndex)
                .Take(Math.Min(MaximumMaterialsPerBlueprint, hits.Count))
                .ToArray();
            var identityKey = string.Join(
                '|',
                selected.Select(hit => hit.MaterialId + "@" + hit.GenerationId));
            if (!seen.Add(identityKey))
            {
                continue;
            }

            var blueprintId = "blueprint-" + Hash(goal + "|" + candidateIndex + "|" + identityKey)[..24];
            var beats = selected
                .Chunk(2)
                .Select((chunk, beatIndex) => new ReferenceWritingBlueprintBeatPayload(
                    "beat-" + Hash(blueprintId + "|" + beatIndex)[..20],
                    beatIndex,
                    chunk[0].Description,
                    chunk[0].MaterialType,
                    chunk.Select(hit => new ReferenceMaterialIdentityPayload(
                        hit.MaterialId,
                        hit.GenerationId)).ToArray()))
                .ToArray();
            blueprints.Add(new ReferenceWritingBlueprintPayload(
                blueprintId,
                Strategies[candidateIndex],
                beats));
        }

        return blueprints;
    }

    private static IEnumerable<ReferenceMaterialSearchHit> Rotate(
        IReadOnlyList<ReferenceMaterialSearchHit> hits,
        int offset)
    {
        for (var index = 0; index < hits.Count; index++)
        {
            yield return hits[(index + offset) % hits.Count];
        }
    }

    private static void ValidateHits(IReadOnlyList<ReferenceMaterialSearchHit> hits)
    {
        if (hits is null ||
            hits.Any(hit =>
                string.IsNullOrWhiteSpace(hit.MaterialId) ||
                string.IsNullOrWhiteSpace(hit.GenerationId) ||
                hit.AnchorId <= 0 ||
                string.IsNullOrWhiteSpace(hit.MaterialType) ||
                string.IsNullOrWhiteSpace(hit.Description) ||
                string.IsNullOrWhiteSpace(hit.Text) ||
                string.IsNullOrWhiteSpace(hit.TextHash) ||
                !double.IsFinite(hit.VectorDistance)) ||
            hits.Select(hit => hit.MaterialId).Distinct(StringComparer.Ordinal).Count() != hits.Count)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "Reference material search returned invalid blueprint sources.");
        }
    }

    private static BlueprintRequest Validate(GenerateReferenceBlueprintsPayload input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateIdentity(input.NovelId, input.ChapterNumber, input.SessionId);
        var goal = input.Goal?.Trim() ?? string.Empty;
        if (goal.Length is 0 or > MaximumGoalCharacters || goal.Any(char.IsControl))
        {
            throw new ArgumentException("Reference blueprint goal is invalid.", nameof(input));
        }

        if (input.RequestedCount is < 1 or > MaximumBlueprints)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                $"Reference blueprint count must be between 1 and {MaximumBlueprints}.");
        }

        return new BlueprintRequest(
            input.NovelId,
            input.ChapterNumber,
            input.SessionId.Trim(),
            goal,
            input.RequestedCount);
    }

    private static DraftRequest Validate(GenerateReferenceDraftCandidatesPayload input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateIdentity(input.NovelId, input.ChapterNumber, input.SessionId);
        var blueprintId = NormalizeIdentifier(input.BlueprintId, "blueprint id");
        var currentDraftText = input.CurrentDraftText
            ?? throw new ArgumentException("Current draft text is required.", nameof(input));
        if (input.InsertionOffset < 0 || input.InsertionOffset > currentDraftText.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Insertion offset is outside the current draft.");
        }

        if (input.RequestedCount is < 1 or > MaximumBlueprints)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                $"Reference draft candidate count must be between 1 and {MaximumBlueprints}.");
        }

        var slotValues = (input.SlotValues ?? throw new ArgumentException(
                "Reference draft slot values are required.",
                nameof(input)))
            .ToDictionary(
                item => NormalizeSlotValue(item.Key, "slot source"),
                item => NormalizeSlotValue(item.Value, "slot replacement"),
                StringComparer.Ordinal);
        return new DraftRequest(
            input.NovelId,
            input.ChapterNumber,
            input.SessionId.Trim(),
            blueprintId,
            currentDraftText,
            input.InsertionOffset,
            slotValues,
            input.RequestedCount);
    }

    private static void ValidateIdentity(long novelId, int chapterNumber, string? sessionId)
    {
        if (novelId <= 0 ||
            chapterNumber <= 0 ||
            string.IsNullOrWhiteSpace(sessionId) ||
            sessionId.Length > 256 ||
            sessionId.Any(char.IsControl))
        {
            throw new ArgumentException("Reference writing session identity is invalid.");
        }
    }

    private static string BuildLibrarySessionId(long novelId) => $"project:{novelId}:default";

    private static string NormalizeIdentifier(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 256 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException($"Reference writing {fieldName} is invalid.");
        }

        return normalized;
    }

    private static string NormalizeSlotValue(string? value, string fieldName)
    {
        var normalized = value ?? string.Empty;
        if (normalized.Length is 0 or > 500 ||
            normalized.Contains('\0') ||
            !string.Equals(normalized, normalized.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"Reference writing {fieldName} is invalid.");
        }

        return normalized;
    }

    private static string MaterialKey(string materialId, string generationId) =>
        materialId + "\u001f" + generationId;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async ValueTask EnsureSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS reference_writing_sessions (
              session_id TEXT NOT NULL,
              novel_id INTEGER NOT NULL,
              chapter_number INTEGER NOT NULL,
              goal TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              PRIMARY KEY(novel_id, chapter_number, session_id)
            );

            CREATE INDEX IF NOT EXISTS idx_reference_writing_sessions_chapter
              ON reference_writing_sessions(novel_id, chapter_number, updated_at DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private sealed record BlueprintRequest(
        long NovelId,
        int ChapterNumber,
        string SessionId,
        string Goal,
        int RequestedCount);

    private sealed record DraftRequest(
        long NovelId,
        int ChapterNumber,
        string SessionId,
        string BlueprintId,
        string CurrentDraftText,
        int InsertionOffset,
        IReadOnlyDictionary<string, string> SlotValues,
        int RequestedCount);

    private sealed record MaterialSnapshot(
        string MaterialId,
        string GenerationId,
        long AnchorId,
        int ChapterIndex,
        string Text,
        string TextHash,
        string LicenseState,
        string ReusePolicy);
}
