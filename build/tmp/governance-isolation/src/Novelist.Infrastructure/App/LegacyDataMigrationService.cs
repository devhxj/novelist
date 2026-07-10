using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public interface ILegacyDataMigrationService
{
    ValueTask MigrateAsync(string targetDataDirectory, CancellationToken cancellationToken);
}

public sealed class LegacyDataMigrationService : ILegacyDataMigrationService
{
    private const int LlmNonceSize = 12;
    private const int LlmTagSize = 16;
    private const int MaxProviderKeyLength = 128;
    private const int MaxProviderDisplayNameLength = 200;
    private const int MaxProviderUrlLength = 2_048;
    private const int MaxApiKeyLength = 4_096;
    private const int MaxModelIdLength = 256;
    private const int MaxModelNameLength = 256;
    private const int MaxTokenLimit = 4_000_000;

    private static readonly byte[] LlmAppKey =
    [
        0x7a, 0x3f, 0x71, 0xe2, 0x5c, 0x9d, 0x0b, 0x46,
        0x1a, 0x5f, 0x33, 0xc8, 0x6e, 0x22, 0x4d, 0x0f,
        0x85, 0xce, 0x1c, 0x29, 0x3f, 0xa7, 0x80, 0xf4,
        0x2e, 0x9c, 0x17, 0xd5, 0x4a, 0x8e, 0xd2, 0x06
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly IVersionControlService _versionControl;

    public LegacyDataMigrationService(
        AppInitializationOptions? options = null,
        IVersionControlService? versionControl = null)
    {
        _options = options ?? new AppInitializationOptions();
        _versionControl = versionControl ?? new GitVersionControlService(_options);
    }

    public async ValueTask MigrateAsync(string targetDataDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetDataDirectory))
        {
            throw new ArgumentException("Target data directory is required.", nameof(targetDataDirectory));
        }

        var target = Path.GetFullPath(targetDataDirectory);
        var source = ResolveSource();
        if (source is null)
        {
            return;
        }

        EnsureCopyFirstTarget(source, target);
        Directory.CreateDirectory(target);

        var manifestPath = Path.Combine(target, "migration_manifest.json");
        if (IsCompletedManifest(manifestPath))
        {
            return;
        }

        var manifest = new MigrationManifest
        {
            StartedAt = DateTimeOffset.UtcNow,
            Status = "running",
            Source = new MigrationSourceManifest
            {
                ConfigDirectory = source.ConfigDirectory,
                DataDirectory = source.DataDirectory,
                DatabasePath = source.DatabasePath,
                NovelsDirectory = source.NovelsDirectory,
                SkillsDirectory = source.SkillsDirectory,
                LlmConfigPath = source.LlmConfigPath
            },
            Target = new MigrationTargetManifest
            {
                DataDirectory = target,
                ManifestPath = manifestPath
            }
        };

        await WriteManifestAsync(manifestPath, manifest, cancellationToken);

        try
        {
            await CopyDirectoryIfPresentAsync(
                source.NovelsDirectory,
                Path.Combine(target, "novels"),
                "copy_novel_workspaces",
                manifest,
                cancellationToken);
            await NormalizeStoryStateFileNamesAsync(target, manifest, cancellationToken);

            await CopyDirectoryIfPresentAsync(
                source.SkillsDirectory,
                Path.Combine(target, "skills"),
                "copy_user_skills",
                manifest,
                cancellationToken);

            await ConvertLegacyLlmConfigAsync(source, target, manifest, cancellationToken);

            var importState = await ImportSqliteMetadataAsync(source, target, manifest, cancellationToken);
            await EnsureNovelRepositoriesAsync(importState.NovelIds, manifest, cancellationToken);

            manifest.CompletedAt = DateTimeOffset.UtcNow;
            manifest.Status = HasWarnings(manifest) ? "completed_with_warnings" : "completed";
            await WriteManifestAsync(manifestPath, manifest, cancellationToken);
        }
        catch (Exception ex)
        {
            manifest.CompletedAt = DateTimeOffset.UtcNow;
            manifest.Status = "failed";
            manifest.Error = SanitizeError(ex.Message);
            await WriteManifestAsync(manifestPath, manifest, CancellationToken.None);
            throw;
        }
    }

    private async ValueTask<MigrationImportState> ImportSqliteMetadataAsync(
        LegacySource source,
        string target,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        var state = new MigrationImportState();
        if (string.IsNullOrWhiteSpace(source.DatabasePath) || !File.Exists(source.DatabasePath))
        {
            await ImportNovelIndexAsync([], source.NovelsDirectory, target, state, manifest, cancellationToken);
            await ImportChapterPlansFromFilesAsync(target, state.NovelIds, null, manifest, cancellationToken);
            return state;
        }

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = source.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync(cancellationToken);

        var novels = await ReadTableAsync(connection, "novels", cancellationToken);
        await ImportNovelIndexAsync(novels, source.NovelsDirectory, target, state, manifest, cancellationToken);
        await ImportAppSettingsAsync(connection, target, state, manifest, cancellationToken);
        await ImportChaptersAsync(connection, target, state, manifest, cancellationToken);
        await ImportPreferencesAsync(connection, target, state, manifest, cancellationToken);
        await ImportWorldEntitiesAsync(connection, target, state, manifest, cancellationToken);
        await ImportPlanningAsync(connection, target, state, manifest, cancellationToken);
        await ImportChatSessionsAsync(connection, target, state, manifest, cancellationToken);
        await ImportWritingLogAsync(connection, target, manifest, cancellationToken);
        return state;
    }

    private async ValueTask ImportNovelIndexAsync(
        IReadOnlyList<Row> rows,
        string? legacyNovelsDirectory,
        string target,
        MigrationImportState state,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(target, "novels", "index.json");
        var payloads = new SortedDictionary<long, NovelPayload>();
        var now = DateTimeOffset.UtcNow;

        foreach (var row in rows)
        {
            var id = ReadLong(row, "id");
            if (id <= 0)
            {
                AddOperation(manifest, "import_novels", "sqlite_import", status: "completed_with_warnings", skipped: 1, warning: "Skipped a novel row with an invalid id.");
                continue;
            }

            payloads[id] = new NovelPayload(
                id,
                ReadRequiredString(row, "title", $"Imported Novel {id.ToString(CultureInfo.InvariantCulture)}"),
                ReadString(row, "genre"),
                ReadString(row, "description"),
                ReadDate(row, "created_at", now),
                ReadDate(row, "updated_at", now));
        }

        foreach (var id in EnumerateNovelDirectoryIds(legacyNovelsDirectory))
        {
            payloads.TryAdd(id, new NovelPayload(
                id,
                $"Imported Novel {id.ToString(CultureInfo.InvariantCulture)}",
                string.Empty,
                string.Empty,
                now,
                now));
        }

        foreach (var id in payloads.Keys)
        {
            state.NovelIds.Add(id);
            var workspace = Path.Combine(target, "novels", id.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(workspace);
            var novelist = Path.Combine(workspace, "novelist.md");
            if (!File.Exists(novelist))
            {
                await File.WriteAllTextAsync(novelist, string.Empty, cancellationToken);
            }
        }

        if (File.Exists(targetPath))
        {
            state.NovelIds.Clear();
            foreach (var id in await ReadNovelIdsFromTargetAsync(targetPath, cancellationToken))
            {
                state.NovelIds.Add(id);
            }

            AddOperation(manifest, "import_novels", "sqlite_import", target: targetPath, status: "skipped", skipped: payloads.Count, warning: "Target novel index already exists; legacy novel metadata was not overwritten.");
            return;
        }

        if (payloads.Count == 0)
        {
            AddOperation(manifest, "import_novels", "sqlite_import", target: targetPath, status: "skipped");
            return;
        }

        await WriteJsonAtomicAsync(targetPath, new NovelStoreDocument
        {
            NextId = NextId(payloads.Keys),
            Items = payloads.Values.OrderBy(item => item.Id).ToList()
        }, cancellationToken);

        AddOperation(manifest, "import_novels", "sqlite_import", target: targetPath, imported: payloads.Count);
    }

    private async ValueTask ImportAppSettingsAsync(
        SqliteConnection connection,
        string target,
        MigrationImportState state,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(target, "app_settings.json");
        if (File.Exists(targetPath))
        {
            AddOperation(manifest, "import_app_settings", "sqlite_import", target: targetPath, status: "skipped", warning: "Target app settings already exist; legacy settings were not overwritten.");
            return;
        }

        var rows = await ReadTableAsync(connection, "app_config", cancellationToken);
        var row = rows.FirstOrDefault();
        if (row is null)
        {
            AddOperation(manifest, "import_app_settings", "sqlite_import", target: targetPath, status: "skipped");
            return;
        }

        var selectedModel = ReadString(row, "selected_model_key");
        if (!IsValidSelectedModelKey(selectedModel))
        {
            selectedModel = string.Empty;
        }

        var lastNovelId = ReadLong(row, "last_novel_id");
        if (lastNovelId < 0 || !state.NovelIds.Contains(lastNovelId))
        {
            lastNovelId = 0;
        }

        var approvalMode = ReadString(row, "approval_mode");
        if (approvalMode is not ("manual" or "auto"))
        {
            approvalMode = "manual";
        }

        var width = (int)ReadLong(row, "chat_panel_width", 360);
        if (width is < 240 or > 1200)
        {
            width = 360;
        }

        await WriteJsonAtomicAsync(targetPath, new AppSettingsPayload(
            Id: 1,
            LastNovelId: lastNovelId,
            SelectedModelKey: selectedModel,
            ReasoningEffort: ReadString(row, "reasoning_effort"),
            ApprovalMode: approvalMode,
            ChatPanelWidth: width,
            LastSessionId: ReadString(row, "last_session_id"),
            UserName: ReadString(row, "user_name")), cancellationToken);

        AddOperation(manifest, "import_app_settings", "sqlite_import", target: targetPath, imported: 1);
    }

    private async ValueTask ImportChaptersAsync(
        SqliteConnection connection,
        string target,
        MigrationImportState state,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        var rows = await ReadTableAsync(connection, "chapters", cancellationToken);
        var groups = new SortedDictionary<long, List<ChapterPayload>>();
        var now = DateTimeOffset.UtcNow;
        var skipped = 0;

        foreach (var row in rows)
        {
            var id = ReadLong(row, "id");
            var novelId = ReadLong(row, "novel_id");
            var number = (int)ReadLong(row, "chapter_number");
            if (id <= 0 || !state.NovelIds.Contains(novelId) || number <= 0 || number > 999_999)
            {
                skipped++;
                continue;
            }

            groups.TryAdd(novelId, []);
            groups[novelId].Add(new ChapterPayload(
                id,
                novelId,
                number,
                ReadString(row, "title"),
                ReadString(row, "summary"),
                (int)Math.Max(0, ReadLong(row, "word_count")),
                ReadDate(row, "created_at", now),
                ReadDate(row, "updated_at", now),
                ChapterPath(number)));
            state.ChapterIds.Add(id);
        }

        foreach (var novelId in state.NovelIds)
        {
            if (!groups.ContainsKey(novelId))
            {
                var synthesized = SynthesizeChaptersFromFiles(target, novelId, now);
                if (synthesized.Count > 0)
                {
                    groups[novelId] = synthesized;
                    foreach (var item in synthesized)
                    {
                        state.ChapterIds.Add(item.Id);
                    }
                }
            }
        }

        var imported = 0;
        foreach (var (novelId, chapters) in groups)
        {
            var targetPath = Path.Combine(target, "novels", novelId.ToString(CultureInfo.InvariantCulture), "metadata", "chapters.json");
            if (File.Exists(targetPath))
            {
                AddOperation(manifest, "import_chapters", "sqlite_import", target: targetPath, status: "skipped", skipped: chapters.Count, warning: "Target chapter metadata already exists; legacy chapters were not overwritten.");
                continue;
            }

            var ordered = chapters
                .GroupBy(item => item.ChapterNumber)
                .Select(group => group.OrderBy(item => item.Id).First())
                .OrderBy(item => item.ChapterNumber)
                .ToList();
            await WriteJsonAtomicAsync(targetPath, new ChapterStoreDocument
            {
                NextId = NextId(ordered.Select(item => item.Id)),
                Items = ordered
            }, cancellationToken);
            imported += ordered.Count;
        }

        AddOperation(manifest, "import_chapters", "sqlite_import", imported: imported, skipped: skipped, status: skipped > 0 ? "completed_with_warnings" : "completed", warning: skipped > 0 ? "Some chapter rows were skipped because they had invalid ids or chapter numbers." : null);
    }

    private async ValueTask ImportPreferencesAsync(
        SqliteConnection connection,
        string target,
        MigrationImportState state,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(target, "preferences", "index.json");
        if (File.Exists(targetPath))
        {
            AddOperation(manifest, "import_preferences", "sqlite_import", target: targetPath, status: "skipped", warning: "Target preferences already exist; legacy preferences were not overwritten.");
            return;
        }

        var rows = await ReadTableAsync(connection, "preference_items", cancellationToken);
        var fallbackNovelId = state.NovelIds.Count == 0 ? 0 : state.NovelIds.Min();
        var items = new List<PreferenceItemPayload>();
        var skipped = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var row in rows)
        {
            var id = ReadLong(row, "id");
            var isGlobal = ReadBool(row, "is_global");
            var novelId = ReadLong(row, "novel_id");
            if (isGlobal && novelId <= 0)
            {
                novelId = fallbackNovelId;
            }

            if (id <= 0 || novelId <= 0 || (!isGlobal && !state.NovelIds.Contains(novelId)))
            {
                skipped++;
                continue;
            }

            items.Add(new PreferenceItemPayload(
                id,
                novelId,
                isGlobal,
                ReadString(row, "category"),
                ReadString(row, "content"),
                ReadDate(row, "created_at", now)));
        }

        if (items.Count == 0)
        {
            AddOperation(manifest, "import_preferences", "sqlite_import", target: targetPath, status: "skipped", skipped: skipped);
            return;
        }

        await WriteJsonAtomicAsync(targetPath, new PreferenceStoreDocument
        {
            NextId = NextId(items.Select(item => item.Id)),
            Items = items.OrderBy(item => item.Id).ToList()
        }, cancellationToken);

        AddOperation(manifest, "import_preferences", "sqlite_import", target: targetPath, imported: items.Count, skipped: skipped, status: skipped > 0 ? "completed_with_warnings" : "completed", warning: skipped > 0 ? "Some preference rows were skipped because they referenced missing novels." : null);
    }

    private async ValueTask ImportWorldEntitiesAsync(
        SqliteConnection connection,
        string target,
        MigrationImportState state,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(target, "world", "index.json");
        if (File.Exists(targetPath))
        {
            AddOperation(manifest, "import_world_entities", "sqlite_import", target: targetPath, status: "skipped", warning: "Target world entity store already exists; legacy world metadata was not overwritten.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var skipped = 0;
        var characters = new List<CharacterPayload>();
        foreach (var row in await ReadTableAsync(connection, "characters", cancellationToken))
        {
            var id = ReadLong(row, "id");
            var novelId = ReadLong(row, "novel_id");
            if (id <= 0 || !state.NovelIds.Contains(novelId))
            {
                skipped++;
                continue;
            }

            characters.Add(new CharacterPayload(
                id,
                novelId,
                ReadString(row, "name"),
                ReadString(row, "description"),
                ReadString(row, "personality"),
                ReadString(row, "abilities"),
                ReadDate(row, "created_at", now),
                ReadDate(row, "updated_at", now)));
            state.CharacterIds.Add(id);
        }

        var characterRelations = new List<CharacterRelationPayload>();
        foreach (var row in await ReadTableAsync(connection, "character_relations", cancellationToken))
        {
            var id = ReadLong(row, "id");
            var novelId = ReadLong(row, "novel_id");
            var sourceId = ReadLong(row, "source_character_id");
            var targetId = ReadLong(row, "target_character_id");
            if (id <= 0 || !state.NovelIds.Contains(novelId) || !state.CharacterIds.Contains(sourceId) || !state.CharacterIds.Contains(targetId))
            {
                skipped++;
                continue;
            }

            characterRelations.Add(new CharacterRelationPayload(
                id,
                novelId,
                sourceId,
                targetId,
                ReadString(row, "relation_describe"),
                ReadString(row, "description"),
                Math.Max(0, ReadLong(row, "chapter_id")),
                ReadBool(row, "is_current", defaultValue: true),
                ReadDate(row, "created_at", now)));
        }

        var locations = new List<LocationPayload>();
        foreach (var row in await ReadTableAsync(connection, "locations", cancellationToken))
        {
            var id = ReadLong(row, "id");
            var novelId = ReadLong(row, "novel_id");
            if (id <= 0 || !state.NovelIds.Contains(novelId))
            {
                skipped++;
                continue;
            }

            locations.Add(new LocationPayload(
                id,
                novelId,
                ReadString(row, "name"),
                ReadString(row, "location_type"),
                ReadString(row, "description"),
                ReadString(row, "detail_json"),
                ReadNullablePositiveLong(row, "parent_location_id"),
                ReadString(row, "tags"),
                ReadDate(row, "created_at", now),
                ReadDate(row, "updated_at", now)));
            state.LocationIds.Add(id);
        }

        locations = locations
            .Select(location => location.ParentLocationId is not null && !state.LocationIds.Contains(location.ParentLocationId.Value)
                ? location with { ParentLocationId = null }
                : location)
            .ToList();

        var locationRelations = new List<LocationRelationPayload>();
        foreach (var row in await ReadTableAsync(connection, "location_relations", cancellationToken))
        {
            var id = ReadLong(row, "id");
            var novelId = ReadLong(row, "novel_id");
            var a = ReadLong(row, "location_a");
            var b = ReadLong(row, "location_b");
            if (id <= 0 || !state.NovelIds.Contains(novelId) || a == b || !state.LocationIds.Contains(a) || !state.LocationIds.Contains(b))
            {
                skipped++;
                continue;
            }

            if (a > b)
            {
                (a, b) = (b, a);
            }

            locationRelations.Add(new LocationRelationPayload(
                id,
                novelId,
                a,
                b,
                ReadString(row, "relation_type"),
                ReadString(row, "description"),
                ReadDate(row, "created_at", now),
                ReadDate(row, "updated_at", now)));
        }

        await WriteJsonAtomicAsync(targetPath, new WorldEntityStoreDocument
        {
            NextCharacterId = NextId(characters.Select(item => item.Id)),
            NextCharacterRelationId = NextId(characterRelations.Select(item => item.Id)),
            NextLocationId = NextId(locations.Select(item => item.Id)),
            NextLocationRelationId = NextId(locationRelations.Select(item => item.Id)),
            Characters = DeduplicateById(characters, item => item.Id),
            CharacterRelations = DeduplicateById(characterRelations, item => item.Id),
            Locations = DeduplicateById(locations, item => item.Id),
            LocationRelations = DeduplicateById(locationRelations, item => item.Id)
        }, cancellationToken);

        var imported = characters.Count + characterRelations.Count + locations.Count + locationRelations.Count;
        AddOperation(manifest, "import_world_entities", "sqlite_import", target: targetPath, imported: imported, skipped: skipped, status: skipped > 0 ? "completed_with_warnings" : "completed", warning: skipped > 0 ? "Some world entity rows were skipped because they had invalid ids or dangling references." : null);
    }

    private async ValueTask ImportPlanningAsync(
        SqliteConnection connection,
        string target,
        MigrationImportState state,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(target, "planning", "index.json");
        if (File.Exists(targetPath))
        {
            AddOperation(manifest, "import_planning", "sqlite_import", target: targetPath, status: "skipped", warning: "Target planning store already exists; legacy planning metadata was not overwritten.");
            return;
        }

        var store = new PlanningStoreDocument();
        var skipped = 0;
        var now = DateTimeOffset.UtcNow;

        await ImportChapterPlansFromDbAsync(connection, store, state, cancellationToken);
        await ImportChapterPlansFromFilesAsync(target, state.NovelIds, store, manifest, cancellationToken);

        foreach (var row in await ReadTableAsync(connection, "time_entries", cancellationToken))
        {
            var id = ReadLong(row, "id");
            var novelId = ReadLong(row, "novel_id");
            var category = ReadString(row, "category");
            if (id <= 0 || !state.NovelIds.Contains(novelId) || category is not ("foreshadowing" or "user_directive"))
            {
                skipped++;
                continue;
            }

            store.TimelineEntries.Add(new TimelineEntryPayload(
                id,
                novelId,
                category,
                NormalizeEnum(ReadString(row, "status"), "pending", ["pending", "resolved", "abandoned"]),
                ReadString(row, "title"),
                ReadString(row, "content"),
                ReadString(row, "detail_json"),
                NormalizeChapterNumber((int)ReadLong(row, "target_chapter")),
                NormalizeImportance((int)ReadLong(row, "importance", 3)),
                Math.Max(0, ReadLong(row, "source_chapter_id")),
                NormalizeEnum(ReadString(row, "source"), "user", ["ai", "user"]),
                Math.Max(0, ReadLong(row, "resolved_chapter_id")),
                ReadDate(row, "created_at", now),
                ReadDate(row, "updated_at", now)));
        }

        foreach (var row in await ReadTableAsync(connection, "story_arcs", cancellationToken))
        {
            var id = ReadLong(row, "id");
            var novelId = ReadLong(row, "novel_id");
            if (id <= 0 || !state.NovelIds.Contains(novelId))
            {
                skipped++;
                continue;
            }

            store.StoryArcs.Add(new StoryArcPayload(
                id,
                novelId,
                ReadString(row, "name"),
                ReadString(row, "description"),
                NormalizeEnum(ReadString(row, "arc_type"), "sub", ["main", "sub", "character", "background"]),
                NormalizeImportance((int)ReadLong(row, "importance", 1)),
                NormalizeEnum(ReadString(row, "status"), "active", ["active", "paused", "completed", "abandoned"]),
                ReadString(row, "reactivate_at"),
                ReadDate(row, "created_at", now),
                ReadDate(row, "updated_at", now)));
            state.StoryArcIds.Add(id);
        }

        foreach (var row in await ReadTableAsync(connection, "arc_nodes", cancellationToken))
        {
            var id = ReadLong(row, "id");
            var novelId = ReadLong(row, "novel_id");
            var arcId = ReadLong(row, "story_arc_id");
            if (id <= 0 || !state.NovelIds.Contains(novelId) || !state.StoryArcIds.Contains(arcId))
            {
                skipped++;
                continue;
            }

            store.ArcNodes.Add(new ArcNodePayload(
                id,
                novelId,
                arcId,
                ReadString(row, "title"),
                ReadString(row, "description"),
                NormalizeChapterNumber((int)ReadLong(row, "target_chapter")),
                NormalizeOptionalChapterNumber((int)ReadLong(row, "actual_chapter")),
                NormalizeEnum(ReadString(row, "status"), "pending", ["pending", "completed", "abandoned"]),
                ReadDate(row, "created_at", now),
                ReadDate(row, "updated_at", now)));
        }

        foreach (var row in await ReadTableAsync(connection, "reader_perspectives", cancellationToken))
        {
            var id = ReadLong(row, "id");
            var novelId = ReadLong(row, "novel_id");
            var type = ReadString(row, "type");
            if (id <= 0 || !state.NovelIds.Contains(novelId) || type is not ("known" or "suspense" or "misconception"))
            {
                skipped++;
                continue;
            }

            store.ReaderPerspectives.Add(new ReaderPerspectivePayload(
                id,
                novelId,
                type,
                ReadString(row, "content"),
                ReadString(row, "related_truth"),
                NormalizeChapterNumber((int)ReadLong(row, "planted_chapter")),
                NormalizeOptionalChapterNumber((int)ReadLong(row, "revealed_chapter")),
                ReadDate(row, "created_at", now)));
        }

        store.NextTimelineEntryId = NextId(store.TimelineEntries.Select(item => item.Id));
        store.NextStoryArcId = NextId(store.StoryArcs.Select(item => item.Id));
        store.NextArcNodeId = NextId(store.ArcNodes.Select(item => item.Id));
        store.NextReaderPerspectiveId = NextId(store.ReaderPerspectives.Select(item => item.Id));

        await WriteJsonAtomicAsync(targetPath, store, cancellationToken);
        var imported = store.ChapterPlans.Count + store.TimelineEntries.Count + store.StoryArcs.Count + store.ArcNodes.Count + store.ReaderPerspectives.Count;
        AddOperation(manifest, "import_planning", "sqlite_import", target: targetPath, imported: imported, skipped: skipped, status: skipped > 0 ? "completed_with_warnings" : "completed", warning: skipped > 0 ? "Some planning rows were skipped because they had invalid ids, enums, or dangling references." : null);
    }

    private static async ValueTask ImportChapterPlansFromDbAsync(
        SqliteConnection connection,
        PlanningStoreDocument store,
        MigrationImportState state,
        CancellationToken cancellationToken)
    {
        foreach (var row in await ReadTableAsync(connection, "chapter_plans", cancellationToken))
        {
            var novelId = ReadLong(row, "novel_id");
            var scope = ReadString(row, "scope");
            if (!state.NovelIds.Contains(novelId) || scope is not ("next" or "near" or "far"))
            {
                continue;
            }

            UpsertChapterPlan(store, new ChapterPlanPayload(novelId, scope, ReadString(row, "content")));
        }
    }

    private static async ValueTask ImportChapterPlansFromFilesAsync(
        string target,
        IReadOnlyCollection<long> novelIds,
        PlanningStoreDocument? store,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            AddOperation(manifest, "import_chapter_plans_from_files", "file_import", status: "skipped");
            return;
        }

        var imported = 0;
        foreach (var novelId in novelIds)
        {
            foreach (var scope in new[] { "next", "near", "far" })
            {
                var path = Path.Combine(target, "novels", novelId.ToString(CultureInfo.InvariantCulture), "plans", $"{scope}.md");
                if (!File.Exists(path))
                {
                    continue;
                }

                var content = await File.ReadAllTextAsync(path, cancellationToken);
                UpsertChapterPlan(store, new ChapterPlanPayload(novelId, scope, content));
                imported++;
            }
        }

        AddOperation(manifest, "import_chapter_plans_from_files", "file_import", imported: imported, status: imported == 0 ? "skipped" : "completed");
    }

    private async ValueTask ImportChatSessionsAsync(
        SqliteConnection connection,
        string target,
        MigrationImportState state,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(target, "sessions", "index.json");
        if (File.Exists(targetPath))
        {
            AddOperation(manifest, "import_chat_sessions", "sqlite_import", target: targetPath, status: "skipped", warning: "Target chat session store already exists; legacy sessions were not overwritten.");
            return;
        }

        var store = new ChatSessionStoreDocument();
        var validSessionIds = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var skipped = 0;

        foreach (var row in await ReadTableAsync(connection, "sessions", cancellationToken))
        {
            var sessionId = ReadString(row, "session_id");
            var novelId = ReadLong(row, "novel_id");
            if (!IsValidSessionId(sessionId) || !state.NovelIds.Contains(novelId))
            {
                skipped++;
                continue;
            }

            store.Sessions.Add(new ChatSessionDocument
            {
                SessionId = sessionId,
                NovelId = novelId,
                Title = ReadString(row, "title"),
                Model = ReadRequiredString(row, "model", "deepseek-v4-pro"),
                ReasoningEffort = ReadString(row, "reasoning_effort"),
                Summary = ReadString(row, "summary"),
                PendingChanges = ReadString(row, "pending_changes"),
                ExtraMetadata = ReadString(row, "extra_metadata"),
                ActiveVersion = Math.Max(1, (int)ReadLong(row, "active_version", 1)),
                LastTurnId = Math.Max(0, (int)ReadLong(row, "last_turn_id")),
                UsageJson = ReadString(row, "usage"),
                CreatedAt = ReadDate(row, "created_at", now),
                UpdatedAt = ReadDate(row, "updated_at", now)
            });
            validSessionIds.Add(sessionId);
        }

        foreach (var row in await ReadTableAsync(connection, "messages", cancellationToken))
        {
            var id = ReadLong(row, "id");
            var sessionId = ReadString(row, "session_id");
            var role = ReadString(row, "role");
            if (id <= 0 || !validSessionIds.Contains(sessionId) || role is not ("system" or "user" or "assistant" or "tool"))
            {
                skipped++;
                continue;
            }

            store.Messages.Add(new ChatMessageDocument
            {
                Id = id,
                SessionId = sessionId,
                TurnId = Math.Max(0, (int)ReadLong(row, "turn_id")),
                Role = role,
                Content = ReadString(row, "content"),
                ThinkingContent = NullIfEmpty(ReadString(row, "thinking_content")),
                TokenCount = Math.Max(0, (int)ReadLong(row, "token_count")),
                ExtraMetadata = NullIfEmpty(ReadString(row, "extra_metadata")),
                Version = Math.Max(1, (int)ReadLong(row, "version", 1)),
                ToApi = ReadBool(row, "to_api"),
                ToFrontend = ReadBool(row, "to_frontend"),
                EventType = NullIfEmpty(ReadString(row, "event_type")),
                AgentType = ReadRequiredString(row, "agent_type", "main"),
                SubTaskId = NullIfEmpty(ReadString(row, "sub_task_id")),
                CreatedAt = ReadDate(row, "created_at", now)
            });
        }

        if (store.Sessions.Count == 0 && store.Messages.Count == 0)
        {
            AddOperation(manifest, "import_chat_sessions", "sqlite_import", target: targetPath, status: "skipped", skipped: skipped);
            return;
        }

        store.NextMessageId = NextId(store.Messages.Select(item => item.Id));
        await WriteJsonAtomicAsync(targetPath, store, cancellationToken);
        AddOperation(manifest, "import_chat_sessions", "sqlite_import", target: targetPath, imported: store.Sessions.Count + store.Messages.Count, skipped: skipped, status: skipped > 0 ? "completed_with_warnings" : "completed", warning: skipped > 0 ? "Some chat rows were skipped because they were malformed or referenced missing sessions." : null);
    }

    private async ValueTask ImportWritingLogAsync(
        SqliteConnection connection,
        string target,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(target, "writing", "log.json");
        if (File.Exists(targetPath))
        {
            AddOperation(manifest, "import_writing_log", "sqlite_import", target: targetPath, status: "skipped", warning: "Target writing log already exists; legacy log was not overwritten.");
            return;
        }

        var rows = await ReadTableAsync(connection, "writing_log", cancellationToken);
        var items = new List<WritingLogRecord>();
        var skipped = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            var id = ReadLong(row, "id");
            var novelId = ReadLong(row, "novel_id");
            var chapterId = ReadLong(row, "chapter_id");
            if (id <= 0 || novelId <= 0 || chapterId <= 0)
            {
                skipped++;
                continue;
            }

            items.Add(new WritingLogRecord
            {
                Id = id,
                Date = ReadRequiredString(row, "date", ReadDate(row, "created_at", now).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                NovelId = novelId,
                ChapterId = chapterId,
                WordDelta = (int)ReadLong(row, "word_delta"),
                CreatedAt = ReadDate(row, "created_at", now)
            });
        }

        if (items.Count == 0)
        {
            AddOperation(manifest, "import_writing_log", "sqlite_import", target: targetPath, status: "skipped", skipped: skipped);
            return;
        }

        await WriteJsonAtomicAsync(targetPath, new WritingLogDocument
        {
            NextId = NextId(items.Select(item => item.Id)),
            Items = items.OrderBy(item => item.Id).ToList()
        }, cancellationToken);
        AddOperation(manifest, "import_writing_log", "sqlite_import", target: targetPath, imported: items.Count, skipped: skipped, status: skipped > 0 ? "completed_with_warnings" : "completed", warning: skipped > 0 ? "Some writing log rows were skipped because they had invalid ids." : null);
    }

    private async ValueTask EnsureNovelRepositoriesAsync(
        IReadOnlyCollection<long> novelIds,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        var repaired = 0;
        foreach (var novelId in novelIds.OrderBy(id => id))
        {
            await _versionControl.EnsureRepositoryAsync(novelId, cancellationToken);
            var commit = await _versionControl.CommitIfChangedAsync(novelId, "migrate legacy data", cancellationToken);
            if (commit.Committed)
            {
                repaired++;
            }
        }

        AddOperation(manifest, "ensure_novel_git_repositories", "git_repair", imported: repaired, skipped: Math.Max(0, novelIds.Count - repaired));
    }

    private async ValueTask ConvertLegacyLlmConfigAsync(
        LegacySource source,
        string target,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.LlmConfigPath) || !File.Exists(source.LlmConfigPath))
        {
            AddOperation(manifest, "convert_llm_config", "config_import", status: "skipped");
            return;
        }

        var targetPath = Path.Combine(target, "llm", "config.enc");
        if (File.Exists(targetPath))
        {
            AddOperation(manifest, "convert_llm_config", "config_import", source: source.LlmConfigPath, target: targetPath, status: "skipped", warning: "Target LLM config already exists; legacy encrypted config was not overwritten.");
            return;
        }

        var encrypted = await File.ReadAllBytesAsync(source.LlmConfigPath, cancellationToken);
        var legacy = JsonSerializer.Deserialize<LegacyLlmConfigDocument>(DecryptLlmConfig(encrypted), JsonOptions)
            ?? new LegacyLlmConfigDocument();
        var converted = new UserLlmConfigDocument();
        var skipped = 0;

        foreach (var provider in legacy.Providers ?? [])
        {
            if (string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                continue;
            }

            if (!TryNormalizeProviderKey(provider.Name, out var key) || !TryNormalizeApiKey(provider.ApiKey, out var apiKey))
            {
                skipped++;
                continue;
            }

            var models = new List<ModelInfoPayload>();
            foreach (var model in provider.Models ?? [])
            {
                if (TryNormalizeModel(model, out var normalized))
                {
                    models.Add(normalized);
                }
            }

            converted.Providers.Add(new UserProviderDocument
            {
                Key = key,
                DisplayName = NormalizeDisplayName(provider.DisplayName, key),
                ChatUrl = NormalizeOptionalChatUrl(provider.ChatUrl),
                ApiKey = apiKey,
                Temperature = NormalizeTemperature(provider.Temperature),
                Models = models
            });
        }

        if (converted.Providers.Count == 0)
        {
            AddOperation(manifest, "convert_llm_config", "config_import", source: source.LlmConfigPath, target: targetPath, status: "skipped", skipped: skipped);
            return;
        }

        await WriteEncryptedLlmConfigAsync(targetPath, converted, cancellationToken);
        AddOperation(manifest, "convert_llm_config", "config_import", source: source.LlmConfigPath, target: targetPath, imported: converted.Providers.Count, skipped: skipped, warning: skipped > 0 ? "Some legacy LLM providers were skipped because provider keys or API keys were invalid." : null, status: skipped > 0 ? "completed_with_warnings" : "completed");
    }

    private static async ValueTask CopyDirectoryIfPresentAsync(
        string? sourceDirectory,
        string targetDirectory,
        string name,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            AddOperation(manifest, name, "copy", source: sourceDirectory, target: targetDirectory, status: "skipped");
            return;
        }

        var result = new CopyResult();
        await CopyDirectoryRecursiveAsync(sourceDirectory, targetDirectory, result, cancellationToken);
        var status = result.Warnings.Count == 0 ? "completed" : "completed_with_warnings";
        AddOperation(
            manifest,
            name,
            "copy",
            source: sourceDirectory,
            target: targetDirectory,
            status: status,
            copied: result.Copied,
            skipped: result.Skipped,
            warning: result.Warnings.Count == 0 ? null : string.Join(" ", result.Warnings.Take(3)));
    }

    private static async ValueTask CopyDirectoryRecursiveAsync(
        string sourceDirectory,
        string targetDirectory,
        CopyResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsReparsePoint(sourceDirectory))
        {
            result.Skipped++;
            result.Warnings.Add($"Skipped reparse-point directory: {sourceDirectory}");
            return;
        }

        Directory.CreateDirectory(targetDirectory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(entry))
            {
                result.Skipped++;
                result.Warnings.Add($"Skipped reparse-point entry: {entry}");
                continue;
            }

            var target = Path.Combine(targetDirectory, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                await CopyDirectoryRecursiveAsync(entry, target, result, cancellationToken);
                continue;
            }

            if (!File.Exists(entry))
            {
                result.Skipped++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
            {
                File.Copy(entry, target, overwrite: false);
                result.Copied++;
                continue;
            }

            if (await FilesEqualAsync(entry, target, cancellationToken))
            {
                result.Skipped++;
                continue;
            }

            result.Skipped++;
            result.Warnings.Add($"Skipped conflicting existing file: {target}");
        }
    }

    private static async ValueTask NormalizeStoryStateFileNamesAsync(
        string target,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        var novelsDirectory = Path.Combine(target, "novels");
        if (!Directory.Exists(novelsDirectory))
        {
            AddOperation(manifest, "normalize_story_state_files", "file_rename", target: novelsDirectory, status: "skipped");
            return;
        }

        var renamed = 0;
        var skipped = 0;
        var warnings = new List<string>();
        foreach (var workspace in Directory.EnumerateDirectories(novelsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentPath = Path.Combine(workspace, "novelist.md");
            var rootMarkdownFiles = Directory
                .EnumerateFiles(workspace, "*.md", SearchOption.TopDirectoryOnly)
                .Where(path => !string.Equals(Path.GetFileName(path), "novelist.md", PathComparison()) &&
                    !Path.GetFileName(path).StartsWith("retired-story-state", PathComparison()))
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();

            if (rootMarkdownFiles.Length == 0)
            {
                continue;
            }

            if (!File.Exists(currentPath) && rootMarkdownFiles.Length == 1)
            {
                File.Move(rootMarkdownFiles[0], currentPath);
                renamed++;
                continue;
            }

            foreach (var retiredPath in rootMarkdownFiles)
            {
                if (File.Exists(currentPath) && await FilesEqualAsync(retiredPath, currentPath, cancellationToken))
                {
                    File.Delete(retiredPath);
                    skipped++;
                    continue;
                }

                var preservedPath = AllocateRetiredStoryStatePreservationPath(workspace);
                File.Move(retiredPath, preservedPath);
                skipped++;
                warnings.Add($"Preserved conflicting retired story-state file: {preservedPath}");
            }
        }

        AddOperation(
            manifest,
            "normalize_story_state_files",
            "file_rename",
            target: novelsDirectory,
            imported: renamed,
            skipped: skipped,
            status: warnings.Count == 0 ? "completed" : "completed_with_warnings",
            warning: warnings.Count == 0 ? null : string.Join(" ", warnings.Take(3)));
    }

    private static string AllocateRetiredStoryStatePreservationPath(string workspace)
    {
        var candidate = Path.Combine(workspace, "retired-story-state.md");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        for (var i = 2; i < 1_000; i++)
        {
            candidate = Path.Combine(workspace, $"retired-story-state-{i.ToString(CultureInfo.InvariantCulture)}.md");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Too many retired story-state files in the same workspace.");
    }

    private LegacySource? ResolveSource()
    {
        var legacyConfigDirectory = Path.GetFullPath(_options.LegacyConfigDirectory ?? DefaultLegacyConfigDirectory());
        var configPath = Path.Combine(legacyConfigDirectory, "config.json");
        var configuredDataDirectory = TryReadLegacyConfiguredDataDirectory(configPath);
        var dataCandidates = new List<string>();

        AddCandidate(_options.LegacyDataDirectory);
        AddCandidate(configuredDataDirectory);
        AddCandidate(AppContext.BaseDirectory);
        AddCandidate(DefaultLegacyDataDirectory());

        foreach (var candidate in dataCandidates.Distinct(PathComparer()))
        {
            var db = Path.Combine(candidate, "novel-agent.db");
            var novels = Path.Combine(candidate, "novels");
            if (File.Exists(db) || Directory.Exists(novels))
            {
                return new LegacySource(
                    legacyConfigDirectory,
                    candidate,
                    File.Exists(db) ? db : null,
                    Directory.Exists(novels) ? novels : null,
                    Directory.Exists(Path.Combine(legacyConfigDirectory, "skills")) ? Path.Combine(legacyConfigDirectory, "skills") : null,
                    File.Exists(Path.Combine(legacyConfigDirectory, "llm_config.enc")) ? Path.Combine(legacyConfigDirectory, "llm_config.enc") : null);
            }
        }

        var skills = Path.Combine(legacyConfigDirectory, "skills");
        var llm = Path.Combine(legacyConfigDirectory, "llm_config.enc");
        if (File.Exists(configPath) || Directory.Exists(skills) || File.Exists(llm))
        {
            var dataDirectory = dataCandidates.FirstOrDefault() ?? DefaultLegacyDataDirectory();
            return new LegacySource(
                legacyConfigDirectory,
                dataDirectory,
                File.Exists(Path.Combine(dataDirectory, "novel-agent.db")) ? Path.Combine(dataDirectory, "novel-agent.db") : null,
                Directory.Exists(Path.Combine(dataDirectory, "novels")) ? Path.Combine(dataDirectory, "novels") : null,
                Directory.Exists(skills) ? skills : null,
                File.Exists(llm) ? llm : null);
        }

        return null;

        void AddCandidate(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                dataCandidates.Add(Path.GetFullPath(ExpandTilde(path)));
            }
        }
    }

    private static async ValueTask<IReadOnlyList<Row>> ReadTableAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, tableName, cancellationToken))
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {QuoteIdentifier(tableName)}";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<Row>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Row();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static async ValueTask<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static void UpsertChapterPlan(PlanningStoreDocument store, ChapterPlanPayload plan)
    {
        var index = store.ChapterPlans.FindIndex(item => item.NovelId == plan.NovelId && string.Equals(item.Scope, plan.Scope, StringComparison.Ordinal));
        if (index < 0)
        {
            store.ChapterPlans.Add(plan);
        }
        else
        {
            store.ChapterPlans[index] = plan;
        }
    }

    private static List<ChapterPayload> SynthesizeChaptersFromFiles(string target, long novelId, DateTimeOffset now)
    {
        var chapterDirectory = Path.Combine(target, "novels", novelId.ToString(CultureInfo.InvariantCulture), "chapters");
        if (!Directory.Exists(chapterDirectory))
        {
            return [];
        }

        var result = new List<ChapterPayload>();
        foreach (var file in Directory.EnumerateFiles(chapterDirectory, "*.md", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0 || number > 999_999)
            {
                continue;
            }

            var info = new FileInfo(file);
            result.Add(new ChapterPayload(
                number,
                novelId,
                number,
                $"Chapter {number.ToString(CultureInfo.InvariantCulture)}",
                string.Empty,
                0,
                new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                ChapterPath(number)));
        }

        return result.OrderBy(item => item.ChapterNumber).ToList();
    }

    private static IEnumerable<long> EnumerateNovelDirectoryIds(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (long.TryParse(Path.GetFileName(child), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            {
                yield return id;
            }
        }
    }

    private static async ValueTask<IReadOnlyList<long>> ReadNovelIdsFromTargetAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(targetPath);
        var store = await JsonSerializer.DeserializeAsync<NovelStoreDocument>(stream, JsonOptions, cancellationToken);
        return store?.Items.Where(item => item.Id > 0).Select(item => item.Id).ToArray() ?? [];
    }

    private static string ChapterPath(int number)
    {
        return $"chapters/{number.ToString("D3", CultureInfo.InvariantCulture)}.md";
    }

    private static async ValueTask WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static async ValueTask WriteManifestAsync(
        string path,
        MigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        await WriteJsonAtomicAsync(path, manifest, cancellationToken);
    }

    private static async ValueTask WriteEncryptedLlmConfigAsync(
        string path,
        UserLlmConfigDocument config,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions);
        var encrypted = EncryptLlmConfig(plain);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, encrypted, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static byte[] DecryptLlmConfig(byte[] encrypted)
    {
        if (encrypted.Length < LlmNonceSize + LlmTagSize)
        {
            throw new InvalidOperationException("Legacy LLM config ciphertext is too short.");
        }

        var nonce = encrypted[..LlmNonceSize];
        var ciphertext = encrypted[LlmNonceSize..^LlmTagSize];
        var tag = encrypted[^LlmTagSize..];
        var plain = new byte[ciphertext.Length];
        using var aes = new AesGcm(LlmAppKey, LlmTagSize);
        aes.Decrypt(nonce, ciphertext, tag, plain);
        return plain;
    }

    private static byte[] EncryptLlmConfig(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(LlmNonceSize);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[LlmTagSize];
        using var aes = new AesGcm(LlmAppKey, LlmTagSize);
        aes.Encrypt(nonce, plain, ciphertext, tag);

        var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length + ciphertext.Length, tag.Length);
        return result;
    }

    private static async ValueTask<bool> FilesEqualAsync(
        string left,
        string right,
        CancellationToken cancellationToken)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        var leftHash = await HashFileAsync(left, cancellationToken);
        var rightHash = await HashFileAsync(right, cancellationToken);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }

    private static async ValueTask<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsCompletedManifest(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var status = document.RootElement.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : string.Empty;
            return status is "completed" or "completed_with_warnings";
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureCopyFirstTarget(LegacySource source, string target)
    {
        var targetFull = Path.GetFullPath(target);
        foreach (var sourceDirectory in new[] { source.DataDirectory, source.ConfigDirectory }.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            if (IsSameOrChildPath(targetFull, sourceDirectory!))
            {
                throw new InvalidOperationException("Legacy source and Novelist target directories must be different for copy-first migration.");
            }
        }
    }

    private static bool IsSameOrChildPath(string child, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullChild = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullChild, fullParent, comparison) ||
            fullChild.StartsWith(fullParent + Path.DirectorySeparatorChar, comparison);
    }

    private static string? TryReadLegacyConfiguredDataDirectory(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(configPath));
            return document.RootElement.TryGetProperty("data_dir", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ExpandTilde(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        return path;
    }

    private static string DefaultLegacyConfigDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".novelist");
    }

    private static string DefaultLegacyDataDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Novelist");
    }

    private static IEqualityComparer<string> PathComparer()
    {
        return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }

    private static StringComparison PathComparison()
    {
        return OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    private static string QuoteIdentifier(string tableName)
    {
        if (tableName.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '_')))
        {
            throw new ArgumentException("Unexpected SQLite table name.", nameof(tableName));
        }

        return $"\"{tableName}\"";
    }

    private static long ReadLong(Row row, string name, long defaultValue = 0)
    {
        if (!row.TryGetValue(name, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            double d when !double.IsNaN(d) && !double.IsInfinity(d) => (long)d,
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private static long? ReadNullablePositiveLong(Row row, string name)
    {
        var value = ReadLong(row, name);
        return value > 0 ? value : null;
    }

    private static bool ReadBool(Row row, string name, bool defaultValue = false)
    {
        if (!row.TryGetValue(name, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            bool b => b,
            long l => l != 0,
            int i => i != 0,
            string text when bool.TryParse(text, out var parsed) => parsed,
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) => numeric != 0,
            _ => defaultValue
        };
    }

    private static string ReadString(Row row, string name)
    {
        if (!row.TryGetValue(name, out var value) || value is null)
        {
            return string.Empty;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    }

    private static string ReadRequiredString(Row row, string name, string fallback)
    {
        var value = ReadString(row, name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static DateTimeOffset ReadDate(Row row, string name, DateTimeOffset fallback)
    {
        if (!row.TryGetValue(name, out var value) || value is null)
        {
            return fallback;
        }

        if (value is DateTime dateTime)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));
        }

        if (value is string text)
        {
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var offset))
            {
                return offset.ToUniversalTime();
            }

            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDateTime))
            {
                return new DateTimeOffset(DateTime.SpecifyKind(parsedDateTime, DateTimeKind.Utc));
            }
        }

        if (ReadLong(row, name, long.MinValue) is var unix && unix > 0 && unix < 4_102_444_800)
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        return fallback;
    }

    private static long NextId(IEnumerable<long> ids)
    {
        var max = ids.DefaultIfEmpty(0).Max();
        return max <= 0 ? 1 : checked(max + 1);
    }

    private static int NormalizeImportance(int value)
    {
        return value is >= 1 and <= 5 ? value : 3;
    }

    private static int NormalizeChapterNumber(int value)
    {
        return value is >= 1 and <= 999_999 ? value : 1;
    }

    private static int NormalizeOptionalChapterNumber(int value)
    {
        return value is >= 0 and <= 999_999 ? value : 0;
    }

    private static string NormalizeEnum(string value, string fallback, IReadOnlyCollection<string> allowed)
    {
        return allowed.Contains(value, StringComparer.Ordinal) ? value : fallback;
    }

    private static bool IsValidSelectedModelKey(string value)
    {
        var parts = value.Split('/', StringSplitOptions.None);
        return parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]);
    }

    private static bool IsValidSessionId(string value)
    {
        return value.Length > 0 && value.Length <= 512 && !value.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch));
    }

    private static string? NullIfEmpty(string value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static List<T> DeduplicateById<T>(IEnumerable<T> items, Func<T, long> idSelector)
    {
        var seen = new HashSet<long>();
        var result = new List<T>();
        foreach (var item in items.OrderBy(idSelector))
        {
            if (seen.Add(idSelector(item)))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static bool TryNormalizeProviderKey(string? value, out string key)
    {
        key = (value ?? string.Empty).Trim().ToLowerInvariant();
        return key.Length is > 0 and <= MaxProviderKeyLength && key.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
    }

    private static bool TryNormalizeApiKey(string? value, out string apiKey)
    {
        apiKey = (value ?? string.Empty).Trim();
        return apiKey.Length is > 0 and <= MaxApiKeyLength && !apiKey.Any(char.IsControl);
    }

    private static string NormalizeDisplayName(string? value, string fallback)
    {
        var display = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return display.Length <= MaxProviderDisplayNameLength && !display.Any(char.IsControl) ? display : fallback;
    }

    private static string NormalizeOptionalChatUrl(string? value)
    {
        var url = (value ?? string.Empty).Trim();
        if (url.Length == 0)
        {
            return string.Empty;
        }

        if (url.Length > MaxProviderUrlLength)
        {
            return string.Empty;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        if (!url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            url = url.TrimEnd('/') + "/chat/completions";
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : string.Empty;
    }

    private static double? NormalizeTemperature(double? value)
    {
        return value is >= 0 and <= 2 ? Math.Round(value.Value, 2) : null;
    }

    private static bool TryNormalizeModel(LegacyModelInfoDocument model, out ModelInfoPayload normalized)
    {
        normalized = default!;
        var id = (model.Id ?? string.Empty).Trim();
        if (id.Length is <= 0 or > MaxModelIdLength || id.Any(char.IsControl))
        {
            return false;
        }

        var name = string.IsNullOrWhiteSpace(model.Name) ? id : model.Name.Trim();
        if (name.Length > MaxModelNameLength || name.Any(char.IsControl))
        {
            name = id;
        }

        normalized = new ModelInfoPayload(
            id,
            name,
            model.ContextWindow is >= 0 and <= MaxTokenLimit ? model.ContextWindow : 0,
            model.MaxOutputTokens is >= 0 and <= MaxTokenLimit ? model.MaxOutputTokens : 0,
            model.SupportsThinking,
            model.ReasoningLevels?.Where(level => !string.IsNullOrWhiteSpace(level)).Select(level => level.Trim()).ToArray(),
            model.SupportsVision);
        return true;
    }

    private static void AddOperation(
        MigrationManifest manifest,
        string name,
        string kind,
        string? source = null,
        string? target = null,
        string status = "completed",
        int copied = 0,
        int imported = 0,
        int skipped = 0,
        string? warning = null,
        string? error = null)
    {
        manifest.Operations.Add(new MigrationOperation
        {
            Name = name,
            Kind = kind,
            Source = source,
            Target = target,
            Status = status,
            ItemsCopied = copied,
            ItemsImported = imported,
            ItemsSkipped = skipped,
            Warning = warning,
            Error = error
        });
    }

    private static bool HasWarnings(MigrationManifest manifest)
    {
        return manifest.Operations.Any(operation =>
            !string.IsNullOrWhiteSpace(operation.Warning) ||
            string.Equals(operation.Status, "completed_with_warnings", StringComparison.Ordinal));
    }

    private static string SanitizeError(string value)
    {
        var sanitized = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return sanitized.Length <= 2_000 ? sanitized : sanitized[..2_000];
    }

    private sealed record LegacySource(
        string ConfigDirectory,
        string DataDirectory,
        string? DatabasePath,
        string? NovelsDirectory,
        string? SkillsDirectory,
        string? LlmConfigPath);

    private sealed class MigrationImportState
    {
        public SortedSet<long> NovelIds { get; } = [];

        public HashSet<long> ChapterIds { get; } = [];

        public HashSet<long> CharacterIds { get; } = [];

        public HashSet<long> LocationIds { get; } = [];

        public HashSet<long> StoryArcIds { get; } = [];
    }

    private sealed class CopyResult
    {
        public int Copied { get; set; }

        public int Skipped { get; set; }

        public List<string> Warnings { get; } = [];
    }

    private sealed class Row : Dictionary<string, object?>
    {
        public Row()
            : base(StringComparer.OrdinalIgnoreCase)
        {
        }
    }

    private sealed class MigrationManifest
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("migration")]
        public string Migration { get; set; } = "legacy-to-novelist";

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("started_at")]
        public DateTimeOffset StartedAt { get; set; }

        [JsonPropertyName("completed_at")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? CompletedAt { get; set; }

        [JsonPropertyName("source")]
        public MigrationSourceManifest Source { get; set; } = new();

        [JsonPropertyName("target")]
        public MigrationTargetManifest Target { get; set; } = new();

        [JsonPropertyName("operations")]
        public List<MigrationOperation> Operations { get; set; } = [];

        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Error { get; set; }
    }

    private sealed class MigrationSourceManifest
    {
        [JsonPropertyName("config_directory")]
        public string? ConfigDirectory { get; set; }

        [JsonPropertyName("data_directory")]
        public string? DataDirectory { get; set; }

        [JsonPropertyName("database_path")]
        public string? DatabasePath { get; set; }

        [JsonPropertyName("novels_directory")]
        public string? NovelsDirectory { get; set; }

        [JsonPropertyName("skills_directory")]
        public string? SkillsDirectory { get; set; }

        [JsonPropertyName("llm_config_path")]
        public string? LlmConfigPath { get; set; }
    }

    private sealed class MigrationTargetManifest
    {
        [JsonPropertyName("data_directory")]
        public string? DataDirectory { get; set; }

        [JsonPropertyName("manifest_path")]
        public string? ManifestPath { get; set; }
    }

    private sealed class MigrationOperation
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Source { get; set; }

        [JsonPropertyName("target")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Target { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("items_copied")]
        public int ItemsCopied { get; set; }

        [JsonPropertyName("items_imported")]
        public int ItemsImported { get; set; }

        [JsonPropertyName("items_skipped")]
        public int ItemsSkipped { get; set; }

        [JsonPropertyName("warning")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Warning { get; set; }

        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Error { get; set; }
    }

    private sealed class NovelStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_id")]
        public long NextId { get; set; } = 1;

        [JsonPropertyName("items")]
        public List<NovelPayload> Items { get; set; } = [];
    }

    private sealed class ChapterStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_id")]
        public long NextId { get; set; } = 1;

        [JsonPropertyName("items")]
        public List<ChapterPayload> Items { get; set; } = [];
    }

    private sealed class PreferenceStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_id")]
        public long NextId { get; set; } = 1;

        [JsonPropertyName("items")]
        public List<PreferenceItemPayload> Items { get; set; } = [];
    }

    private sealed class WorldEntityStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_character_id")]
        public long NextCharacterId { get; set; } = 1;

        [JsonPropertyName("next_character_relation_id")]
        public long NextCharacterRelationId { get; set; } = 1;

        [JsonPropertyName("next_location_id")]
        public long NextLocationId { get; set; } = 1;

        [JsonPropertyName("next_location_relation_id")]
        public long NextLocationRelationId { get; set; } = 1;

        [JsonPropertyName("characters")]
        public List<CharacterPayload> Characters { get; set; } = [];

        [JsonPropertyName("character_relations")]
        public List<CharacterRelationPayload> CharacterRelations { get; set; } = [];

        [JsonPropertyName("locations")]
        public List<LocationPayload> Locations { get; set; } = [];

        [JsonPropertyName("location_relations")]
        public List<LocationRelationPayload> LocationRelations { get; set; } = [];
    }

    private sealed class PlanningStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_timeline_entry_id")]
        public long NextTimelineEntryId { get; set; } = 1;

        [JsonPropertyName("next_story_arc_id")]
        public long NextStoryArcId { get; set; } = 1;

        [JsonPropertyName("next_arc_node_id")]
        public long NextArcNodeId { get; set; } = 1;

        [JsonPropertyName("next_reader_perspective_id")]
        public long NextReaderPerspectiveId { get; set; } = 1;

        [JsonPropertyName("chapter_plans")]
        public List<ChapterPlanPayload> ChapterPlans { get; set; } = [];

        [JsonPropertyName("timeline_entries")]
        public List<TimelineEntryPayload> TimelineEntries { get; set; } = [];

        [JsonPropertyName("story_arcs")]
        public List<StoryArcPayload> StoryArcs { get; set; } = [];

        [JsonPropertyName("arc_nodes")]
        public List<ArcNodePayload> ArcNodes { get; set; } = [];

        [JsonPropertyName("reader_perspectives")]
        public List<ReaderPerspectivePayload> ReaderPerspectives { get; set; } = [];
    }

    private sealed class ChatSessionStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_message_id")]
        public long NextMessageId { get; set; } = 1;

        [JsonPropertyName("sessions")]
        public List<ChatSessionDocument> Sessions { get; set; } = [];

        [JsonPropertyName("messages")]
        public List<ChatMessageDocument> Messages { get; set; } = [];
    }

    private sealed class ChatSessionDocument
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("novel_id")]
        public long NovelId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("reasoning_effort")]
        public string ReasoningEffort { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("pending_changes")]
        public string PendingChanges { get; set; } = string.Empty;

        [JsonPropertyName("extra_metadata")]
        public string ExtraMetadata { get; set; } = string.Empty;

        [JsonPropertyName("active_version")]
        public int ActiveVersion { get; set; } = 1;

        [JsonPropertyName("last_turn_id")]
        public int LastTurnId { get; set; }

        [JsonPropertyName("usage")]
        public string UsageJson { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class ChatMessageDocument
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("turn_id")]
        public int TurnId { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("thinking_content")]
        public string? ThinkingContent { get; set; }

        [JsonPropertyName("token_count")]
        public int TokenCount { get; set; }

        [JsonPropertyName("extra_metadata")]
        public string? ExtraMetadata { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("to_api")]
        public bool ToApi { get; set; }

        [JsonPropertyName("to_frontend")]
        public bool ToFrontend { get; set; }

        [JsonPropertyName("event_type")]
        public string? EventType { get; set; }

        [JsonPropertyName("agent_type")]
        public string AgentType { get; set; } = "main";

        [JsonPropertyName("sub_task_id")]
        public string? SubTaskId { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class WritingLogDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_id")]
        public long NextId { get; set; } = 1;

        [JsonPropertyName("items")]
        public List<WritingLogRecord> Items { get; set; } = [];
    }

    private sealed class WritingLogRecord
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("date")]
        public string Date { get; set; } = string.Empty;

        [JsonPropertyName("novel_id")]
        public long NovelId { get; set; }

        [JsonPropertyName("chapter_id")]
        public long ChapterId { get; set; }

        [JsonPropertyName("word_delta")]
        public int WordDelta { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class LegacyLlmConfigDocument
    {
        [JsonPropertyName("providers")]
        public List<LegacyProviderDocument> Providers { get; set; } = [];
    }

    private sealed class LegacyProviderDocument
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("chat_url")]
        public string ChatUrl { get; set; } = string.Empty;

        [JsonPropertyName("api_key")]
        public string ApiKey { get; set; } = string.Empty;

        [JsonPropertyName("models")]
        public List<LegacyModelInfoDocument> Models { get; set; } = [];

        [JsonPropertyName("temperature")]
        public double? Temperature { get; set; }

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;
    }

    private sealed class LegacyModelInfoDocument
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("context_window")]
        public int ContextWindow { get; set; }

        [JsonPropertyName("max_output_tokens")]
        public int MaxOutputTokens { get; set; }

        [JsonPropertyName("supports_thinking")]
        public bool SupportsThinking { get; set; }

        [JsonPropertyName("reasoning_levels")]
        public List<string>? ReasoningLevels { get; set; }

        [JsonPropertyName("supports_vision")]
        public bool SupportsVision { get; set; }
    }

    private sealed class UserLlmConfigDocument
    {
        [JsonPropertyName("providers")]
        public List<UserProviderDocument> Providers { get; set; } = [];
    }

    private sealed class UserProviderDocument
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("chat_url")]
        public string ChatUrl { get; set; } = string.Empty;

        [JsonPropertyName("api_key")]
        public string ApiKey { get; set; } = string.Empty;

        [JsonPropertyName("temperature")]
        public double? Temperature { get; set; }

        [JsonPropertyName("models")]
        public IReadOnlyList<ModelInfoPayload> Models { get; set; } = [];
    }
}
