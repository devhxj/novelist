using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class LegacyDataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InitializeCopiesLegacyDataImportsSqliteStoresAndWritesManifest()
    {
        var oldConfig = Path.Combine(_root, "old-config");
        var oldData = Path.Combine(_root, "old-data");
        var newConfig = Path.Combine(_root, "new-config");
        var newData = Path.Combine(_root, "new-data");
        Directory.CreateDirectory(oldConfig);
        Directory.CreateDirectory(oldData);
        await File.WriteAllTextAsync(
            Path.Combine(oldConfig, "config.json"),
            JsonSerializer.Serialize(new { data_dir = oldData }));

        await CreateLegacyWorkspaceAsync(oldConfig, oldData);
        await CreateLegacyDatabaseAsync(Path.Combine(oldData, "novel-agent.db"));
        var sourceHashBefore = await HashFileAsync(Path.Combine(oldData, "novels", "1", "chapters", "001.md"));
        var skillHashBefore = await HashFileAsync(Path.Combine(oldConfig, "skills", "legacy-style.md"));

        var options = new AppInitializationOptions
        {
            ConfigDirectory = newConfig,
            DefaultDataDirectory = newData,
            EnableLegacyMigration = true,
            LegacyConfigDirectory = oldConfig,
            LegacyDataDirectory = oldData
        };

        await new FileSystemAppInitializationService(options).InitializeAsync(newData, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(newData, "migration_manifest.json")));
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(newData, "migration_manifest.json"))))
        {
            var status = manifest.RootElement.GetProperty("status").GetString();
            Assert.Contains(status, new[] { "completed", "completed_with_warnings" });
            Assert.Contains(
                manifest.RootElement.GetProperty("operations").EnumerateArray(),
                item => item.GetProperty("name").GetString() == "import_novels");
        }

        Assert.Equal(sourceHashBefore, await HashFileAsync(Path.Combine(oldData, "novels", "1", "chapters", "001.md")));
        Assert.Equal(skillHashBefore, await HashFileAsync(Path.Combine(oldConfig, "skills", "legacy-style.md")));
        Assert.True(File.Exists(Path.Combine(newData, "novels", "1", "chapters", "001.md")));
        Assert.Equal("旧故事状态", await File.ReadAllTextAsync(Path.Combine(newData, "novels", "1", "novelist.md")));
        Assert.False(File.Exists(Path.Combine(newData, "novels", "1", "story-state.md")));
        Assert.True(File.Exists(Path.Combine(newData, "skills", "legacy-style.md")));

        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = Assert.Single(await novelService.GetNovelsAsync(CancellationToken.None));
        Assert.Equal(1, novel.Id);
        Assert.Equal("旧城档案", novel.Title);
        Assert.Equal("悬疑", novel.Genre);

        var settingsPayload = await settings.GetSettingsAsync(CancellationToken.None);
        Assert.Equal(1, settingsPayload.LastNovelId);
        Assert.Equal("deepseek/deepseek-v4-pro", settingsPayload.SelectedModelKey);
        Assert.Equal("session_legacy", settingsPayload.LastSessionId);
        Assert.Equal("旧作者", settingsPayload.UserName);

        var chapters = new FileSystemChapterContentService(options, novelService);
        var chapter = Assert.Single(await chapters.GetChaptersAsync(1, CancellationToken.None));
        Assert.Equal(101, chapter.Id);
        Assert.Equal("第一封信", chapter.Title);
        Assert.Equal("chapters/001.md", chapter.FilePath);
        Assert.Equal("旧章节正文", await chapters.GetContentAsync(1, "chapters/001.md", CancellationToken.None));

        var preferences = await new FileSystemPreferenceService(options, novelService).GetPreferencesAsync(1, CancellationToken.None);
        Assert.Single(preferences.Global);
        Assert.Equal("节奏偏好", preferences.Global[0].Category);

        var world = new FileSystemWorldEntityService(options, novelService);
        Assert.Equal(["林岚", "阿七"], (await world.GetCharactersAsync(1, CancellationToken.None)).Select(item => item.Name));
        Assert.Single(await world.GetCharacterRelationsAsync(1, CancellationToken.None));
        Assert.Equal(["旧城", "钟楼"], (await world.GetLocationsAsync(1, CancellationToken.None)).Select(item => item.Name));
        Assert.Single(await world.GetLocationRelationsAsync(1, CancellationToken.None));

        var planning = new FileSystemPlanningService(options, novelService);
        Assert.Contains(await planning.GetChapterPlansAsync(1, CancellationToken.None), item => item.Scope == "next" && item.Content == "下一章旧计划");
        Assert.Single(await planning.GetTimelineEntriesAsync(1, 1, 10, CancellationToken.None));
        Assert.Single(await planning.GetStoryArcsAsync(1, CancellationToken.None));
        Assert.Single(await planning.GetArcNodesAsync(1, 1, 10, CancellationToken.None));
        Assert.Single(await planning.GetReaderPerspectivesAsync(1, CancellationToken.None));

        var llm = await new FileSystemLlmConfigurationService(options).GetConfigAsync(CancellationToken.None);
        Assert.Equal("legacy-secret", llm.Providers.Single(item => item.Key == "deepseek").ApiKey);

        var chatService = new FileSystemChatSessionService(
            options,
            novelService,
            settings,
            new FileSystemLlmConfigurationService(options),
            new ThrowingCompletionClient());
        var session = await chatService.GetSessionAsync("session_legacy", CancellationToken.None);
        Assert.Equal(1, session.NovelId);
        Assert.Equal("旧会话", session.Title);
        var messages = await chatService.GetSessionMessagesAsync("session_legacy", CancellationToken.None);
        Assert.Equal("旧消息", Assert.Single(messages).Content);

        var writing = await new FileSystemWritingStatisticsService(options, novelService)
            .GetWritingActivityAsync(12, CancellationToken.None);
        Assert.Contains(writing, item => item.Date == "2026-07-01" && item.Words == 128);

        var gitMetadata = Path.Combine(newData, "novels", "1", ".git");
        Assert.True(Directory.Exists(gitMetadata) || File.Exists(gitMetadata));
        var log = await new GitVersionControlService(options).GetLogAsync(1, null, 10, CancellationToken.None);
        Assert.NotEmpty(log);
    }

    [Fact]
    public async Task MigrationPreservesExistingPhase15StoresAndTargetAppData()
    {
        var oldConfig = Path.Combine(_root, "old-config-phase15");
        var oldData = Path.Combine(_root, "old-data-phase15");
        var newConfig = Path.Combine(_root, "new-config-phase15");
        var newData = Path.Combine(_root, "new-data-phase15");
        Directory.CreateDirectory(oldConfig);
        Directory.CreateDirectory(oldData);
        Directory.CreateDirectory(newData);
        await File.WriteAllTextAsync(
            Path.Combine(oldConfig, "config.json"),
            JsonSerializer.Serialize(new { data_dir = oldData }));
        await CreateLegacyWorkspaceAsync(oldConfig, oldData);
        await CreateLegacyDatabaseAsync(Path.Combine(oldData, "novel-agent.db"));

        await WriteExistingPhase15TargetDataAsync(newData);
        var preservedPaths = new[]
        {
            Path.Combine(newData, "app_settings.json"),
            Path.Combine(newData, "novels", "index.json"),
            Path.Combine(newData, "novels", "1", "chapters", "001.md"),
            Path.Combine(newData, "style_samples", "index.json"),
            Path.Combine(newData, "narrative_patterns", "runs.json"),
            Path.Combine(newData, "novel_imports", "runs.json")
        };
        var hashesBefore = await HashFilesAsync(preservedPaths);

        var options = new AppInitializationOptions
        {
            ConfigDirectory = newConfig,
            DefaultDataDirectory = newData,
            EnableLegacyMigration = true,
            LegacyConfigDirectory = oldConfig,
            LegacyDataDirectory = oldData
        };

        await new FileSystemAppInitializationService(options).InitializeAsync(newData, CancellationToken.None);

        Assert.Equal(hashesBefore, await HashFilesAsync(preservedPaths));
        Assert.Equal("目标章节正文", await File.ReadAllTextAsync(Path.Combine(newData, "novels", "1", "chapters", "001.md")));
        Assert.True(File.Exists(Path.Combine(newData, "skills", "legacy-style.md")));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(newData, "migration_manifest.json")));
        var operations = manifest.RootElement.GetProperty("operations").EnumerateArray().ToArray();
        Assert.Contains(operations, item =>
            item.GetProperty("name").GetString() == "import_novels" &&
            item.GetProperty("status").GetString() == "skipped");
        Assert.Contains(operations, item =>
            item.GetProperty("name").GetString() == "import_app_settings" &&
            item.GetProperty("status").GetString() == "skipped");
        Assert.Contains(operations, item =>
            item.GetProperty("name").GetString() == "copy_novel_workspaces" &&
            item.GetProperty("status").GetString() == "completed_with_warnings");
    }

    [Fact]
    public async Task MigrationPreservesPartiallyPopulatedPhase14ReferenceStyleDatabase()
    {
        var oldConfig = Path.Combine(_root, "old-config-style");
        var oldData = Path.Combine(_root, "old-data-style");
        var newConfig = Path.Combine(_root, "new-config-style");
        var newData = Path.Combine(_root, "new-data-style");
        Directory.CreateDirectory(oldConfig);
        Directory.CreateDirectory(oldData);
        Directory.CreateDirectory(newData);
        await File.WriteAllTextAsync(
            Path.Combine(oldConfig, "config.json"),
            JsonSerializer.Serialize(new { data_dir = oldData }));
        await CreateLegacyWorkspaceAsync(oldConfig, oldData);
        await CreateLegacyDatabaseAsync(Path.Combine(oldData, "novel-agent.db"));
        await CreatePartialReferenceStyleDatabaseAsync(Path.Combine(newData, "reference-anchor", "index.sqlite"));
        SqliteConnection.ClearAllPools();

        var options = new AppInitializationOptions
        {
            ConfigDirectory = newConfig,
            DefaultDataDirectory = newData,
            EnableLegacyMigration = true,
            LegacyConfigDirectory = oldConfig,
            LegacyDataDirectory = oldData
        };

        await new FileSystemAppInitializationService(options).InitializeAsync(newData, CancellationToken.None);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(newData, "reference-anchor", "index.sqlite"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();

        await using var profileCommand = connection.CreateCommand();
        profileCommand.CommandText = "SELECT title FROM reference_style_profiles WHERE profile_id = 7;";
        Assert.Equal("既有 Phase14 画像", (string?)await profileCommand.ExecuteScalarAsync());

        await using var evidenceCommand = connection.CreateCommand();
        evidenceCommand.CommandText = "SELECT COUNT(*) FROM reference_style_profile_evidence WHERE profile_id = 7;";
        Assert.Equal(1L, (long)(await evidenceCommand.ExecuteScalarAsync())!);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            ClearReadOnlyAttributes(_root);
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async ValueTask CreateLegacyWorkspaceAsync(string oldConfig, string oldData)
    {
        var novel = Path.Combine(oldData, "novels", "1");
        Directory.CreateDirectory(Path.Combine(novel, "chapters"));
        Directory.CreateDirectory(Path.Combine(novel, "plans"));
        await File.WriteAllTextAsync(Path.Combine(novel, "story-state.md"), "旧故事状态");
        await File.WriteAllTextAsync(Path.Combine(novel, "chapters", "001.md"), "旧章节正文");
        await File.WriteAllTextAsync(Path.Combine(novel, "plans", "next.md"), "下一章旧计划");

        Directory.CreateDirectory(Path.Combine(oldConfig, "skills"));
        await File.WriteAllTextAsync(
            Path.Combine(oldConfig, "skills", "legacy-style.md"),
            """
            ---
            name: legacy-style
            description: old style
            mode: auto
            ---
            旧风格技能
            """);

        await File.WriteAllBytesAsync(
            Path.Combine(oldConfig, "llm_config.enc"),
            EncryptLegacyLlmConfig("""
            {
              "providers": [
                {
                  "name": "deepseek",
                  "chat_url": "",
                  "api_key": "legacy-secret",
                  "models": [
                    {
                      "id": "legacy-custom",
                      "name": "Legacy Custom",
                      "context_window": 4096,
                      "max_output_tokens": 1024,
                      "supports_thinking": false,
                      "supports_vision": false
                    }
                  ],
                  "temperature": 0.7
                }
              ]
            }
            """));
    }

    private static async ValueTask CreateLegacyDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE TABLE novels (id INTEGER PRIMARY KEY, title TEXT, genre TEXT, description TEXT, created_at TEXT, updated_at TEXT)");
        await ExecuteAsync(connection, "CREATE TABLE chapters (id INTEGER PRIMARY KEY, novel_id INTEGER, chapter_number INTEGER, title TEXT, summary TEXT, word_count INTEGER, created_at TEXT, updated_at TEXT)");
        await ExecuteAsync(connection, "CREATE TABLE preference_items (id INTEGER PRIMARY KEY, novel_id INTEGER, is_global INTEGER, category TEXT, content TEXT, created_at TEXT)");
        await ExecuteAsync(connection, "CREATE TABLE characters (id INTEGER PRIMARY KEY, novel_id INTEGER, name TEXT, description TEXT, personality TEXT, abilities TEXT, created_at TEXT, updated_at TEXT)");
        await ExecuteAsync(connection, "CREATE TABLE character_relations (id INTEGER PRIMARY KEY, novel_id INTEGER, source_character_id INTEGER, target_character_id INTEGER, relation_describe TEXT, description TEXT, chapter_id INTEGER, is_current INTEGER, created_at TEXT)");
        await ExecuteAsync(connection, "CREATE TABLE locations (id INTEGER PRIMARY KEY, novel_id INTEGER, name TEXT, location_type TEXT, description TEXT, detail_json TEXT, parent_location_id INTEGER, tags TEXT, created_at TEXT, updated_at TEXT)");
        await ExecuteAsync(connection, "CREATE TABLE location_relations (id INTEGER PRIMARY KEY, novel_id INTEGER, location_a INTEGER, location_b INTEGER, relation_type TEXT, description TEXT, created_at TEXT, updated_at TEXT)");
        await ExecuteAsync(connection, "CREATE TABLE time_entries (id INTEGER PRIMARY KEY, novel_id INTEGER, category TEXT, status TEXT, title TEXT, content TEXT, detail_json TEXT, target_chapter INTEGER, importance INTEGER, source_chapter_id INTEGER, source TEXT, resolved_chapter_id INTEGER, created_at TEXT, updated_at TEXT)");
        await ExecuteAsync(connection, "CREATE TABLE story_arcs (id INTEGER PRIMARY KEY, novel_id INTEGER, name TEXT, description TEXT, arc_type TEXT, importance INTEGER, status TEXT, reactivate_at TEXT, created_at TEXT, updated_at TEXT)");
        await ExecuteAsync(connection, "CREATE TABLE arc_nodes (id INTEGER PRIMARY KEY, novel_id INTEGER, story_arc_id INTEGER, title TEXT, description TEXT, target_chapter INTEGER, actual_chapter INTEGER, status TEXT, created_at TEXT, updated_at TEXT)");
        await ExecuteAsync(connection, "CREATE TABLE reader_perspectives (id INTEGER PRIMARY KEY, novel_id INTEGER, type TEXT, content TEXT, related_truth TEXT, planted_chapter INTEGER, revealed_chapter INTEGER, created_at TEXT)");
        await ExecuteAsync(connection, "CREATE TABLE app_config (id INTEGER PRIMARY KEY, last_novel_id INTEGER, selected_model_key TEXT, reasoning_effort TEXT, approval_mode TEXT, chat_panel_width INTEGER, last_session_id TEXT, user_name TEXT)");
        await ExecuteAsync(connection, "CREATE TABLE sessions (session_id TEXT PRIMARY KEY, novel_id INTEGER, title TEXT, model TEXT, reasoning_effort TEXT, summary TEXT, pending_changes TEXT, extra_metadata TEXT, active_version INTEGER, last_turn_id INTEGER, usage TEXT, created_at TEXT, updated_at TEXT)");
        await ExecuteAsync(connection, "CREATE TABLE messages (id INTEGER PRIMARY KEY, session_id TEXT, turn_id INTEGER, role TEXT, content TEXT, thinking_content TEXT, token_count INTEGER, extra_metadata TEXT, version INTEGER, to_api INTEGER, to_frontend INTEGER, event_type TEXT, agent_type TEXT, sub_task_id TEXT, created_at TEXT)");
        await ExecuteAsync(connection, "CREATE TABLE writing_log (id INTEGER PRIMARY KEY, date TEXT, novel_id INTEGER, chapter_id INTEGER, word_delta INTEGER, created_at TEXT)");

        await ExecuteAsync(connection, "INSERT INTO novels VALUES (1, '旧城档案', '悬疑', '旧描述', '2026-07-01T00:00:00Z', '2026-07-02T00:00:00Z')");
        await ExecuteAsync(connection, "INSERT INTO chapters VALUES (101, 1, 1, '第一封信', '旧摘要', 5, '2026-07-01T00:00:00Z', '2026-07-02T00:00:00Z')");
        await ExecuteAsync(connection, "INSERT INTO preference_items VALUES (201, 1, 1, '节奏偏好', '慢热但每章有钩子', '2026-07-01T00:00:00Z')");
        await ExecuteAsync(connection, "INSERT INTO characters VALUES (301, 1, '林岚', '记者', '{}', '[\"速记\"]', '2026-07-01T00:00:00Z', '2026-07-02T00:00:00Z')");
        await ExecuteAsync(connection, "INSERT INTO characters VALUES (302, 1, '阿七', '线人', '{}', '[]', '2026-07-01T00:00:00Z', '2026-07-02T00:00:00Z')");
        await ExecuteAsync(connection, "INSERT INTO character_relations VALUES (303, 1, 301, 302, '互相试探', '共享线索', 101, 1, '2026-07-02T00:00:00Z')");
        await ExecuteAsync(connection, "INSERT INTO locations VALUES (401, 1, '旧城', '城市', '雨夜旧城', '{}', NULL, '[\"雨\"]', '2026-07-01T00:00:00Z', '2026-07-02T00:00:00Z')");
        await ExecuteAsync(connection, "INSERT INTO locations VALUES (402, 1, '钟楼', '建筑', '城中钟楼', '{}', 401, '[]', '2026-07-01T00:00:00Z', '2026-07-02T00:00:00Z')");
        await ExecuteAsync(connection, "INSERT INTO location_relations VALUES (403, 1, 401, 402, '包含', '旧城中心', '2026-07-01T00:00:00Z', '2026-07-02T00:00:00Z')");
        await ExecuteAsync(connection, "INSERT INTO time_entries VALUES (501, 1, 'foreshadowing', 'pending', '钟声异常', '午夜钟声多响一次', '{}', 3, 4, 101, 'ai', 0, '2026-07-01T00:00:00Z', '2026-07-02T00:00:00Z')");
        await ExecuteAsync(connection, "INSERT INTO story_arcs VALUES (601, 1, '钟楼线', '追查钟楼秘密', 'main', 5, 'active', '', '2026-07-01T00:00:00Z', '2026-07-02T00:00:00Z')");
        await ExecuteAsync(connection, "INSERT INTO arc_nodes VALUES (602, 1, 601, '发现钟表匠', '找到旧钥匙', 4, 0, 'pending', '2026-07-01T00:00:00Z', '2026-07-02T00:00:00Z')");
        await ExecuteAsync(connection, "INSERT INTO reader_perspectives VALUES (701, 1, 'suspense', '谁改了钟声', '钟表匠被胁迫', 1, 0, '2026-07-01T00:00:00Z')");
        await ExecuteAsync(connection, "INSERT INTO app_config VALUES (1, 1, 'deepseek/deepseek-v4-pro', 'high', 'manual', 420, 'session_legacy', '旧作者')");
        await ExecuteAsync(connection, "INSERT INTO sessions VALUES ('session_legacy', 1, '旧会话', 'deepseek-v4-pro', 'high', '', '', '', 1, 1, '{\"total_tokens\":12}', '2026-07-01T00:00:00Z', '2026-07-02T00:00:00Z')");
        await ExecuteAsync(connection, "INSERT INTO messages VALUES (801, 'session_legacy', 1, 'user', '旧消息', '', 3, '', 1, 1, 1, '', 'main', '', '2026-07-02T00:00:00Z')");
        await ExecuteAsync(connection, "INSERT INTO writing_log VALUES (901, '2026-07-01', 1, 101, 128, '2026-07-01T00:00:00Z')");
    }

    private static async ValueTask WriteExistingPhase15TargetDataAsync(string newData)
    {
        Directory.CreateDirectory(Path.Combine(newData, "novels", "1", "chapters"));
        Directory.CreateDirectory(Path.Combine(newData, "style_samples"));
        Directory.CreateDirectory(Path.Combine(newData, "narrative_patterns"));
        Directory.CreateDirectory(Path.Combine(newData, "novel_imports"));

        await File.WriteAllTextAsync(
            Path.Combine(newData, "app_settings.json"),
            """
            {
              "ID": 1,
              "last_novel_id": 1,
              "selected_model_key": "target/provider",
              "reasoning_effort": "target",
              "approval_mode": "auto",
              "chat_panel_width": 444,
              "last_session_id": "target_session",
              "user_name": "目标作者",
              "git_author_name": "Target Git",
              "git_author_email": "target@example.com",
              "update_check_enabled": true,
              "update_check_endpoint_url": "https://updates.example.test/releases.json",
              "update_check_dismissed_version": "9.9.9",
              "sidebar_width": 300,
              "metadata_panel_width": 360,
              "window_width": 1400,
              "window_height": 900,
              "window_maximized": true
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(newData, "novels", "index.json"),
            """
            {
              "version": 1,
              "next_id": 2,
              "items": [
                {
                  "id": 1,
                  "title": "目标小说",
                  "genre": "目标类型",
                  "description": "目标描述",
                  "created_at": "2026-07-07T00:00:00Z",
                  "updated_at": "2026-07-07T00:00:00Z"
                }
              ]
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(newData, "novels", "1", "chapters", "001.md"), "目标章节正文");
        await File.WriteAllTextAsync(
            Path.Combine(newData, "style_samples", "index.json"),
            """
            {
              "version": 1,
              "next_id": 2,
              "items": [
                {
                  "sample_id": 1,
                  "novel_id": 1,
                  "is_global": false,
                  "name": "目标风格样本",
                  "content": "目标样本文本。",
                  "preview": "目标样本文本。",
                  "tags": ["target"],
                  "stats_schema_version": "style_sample_stats_v1",
                  "stats": {
                    "character_count": 7,
                    "sentence_count": 1,
                    "average_sentence_chars": 7,
                    "dialogue_ratio": 0,
                    "interiority_ratio": 0,
                    "sensory_ratio": 0,
                    "punctuation_per_100_chars": 14.2857
                  },
                  "source_metadata": null,
                  "created_at": "2026-07-07T00:00:00Z",
                  "updated_at": "2026-07-07T00:00:00Z"
                }
              ]
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(newData, "narrative_patterns", "runs.json"),
            """
            {
              "version": 1,
              "runs": [
                {
                  "task_id": "target-pattern",
                  "novel_id": 1,
                  "status": "completed",
                  "stage": "skill_preview",
                  "progress_completed": 1,
                  "progress_total": 1,
                  "chapter_ranges": [{ "start_chapter": 1, "end_chapter": 1 }],
                  "selected_chapter_ids": [],
                  "provider_name": "target-provider",
                  "model_id": "target-model",
                  "reasoning_effort": "low",
                  "skill_name": "目标叙事模式",
                  "skill_preview": "目标模式",
                  "generated_skill": {
                    "name": "目标叙事模式",
                    "preview": "目标模式",
                    "status": "preview_ready",
                    "updated_at": "2026-07-07T00:00:00Z"
                  },
                  "diagnostics": [],
                  "error": null,
                  "trace": [],
                  "created_at": "2026-07-07T00:00:00Z",
                  "updated_at": "2026-07-07T00:00:00Z",
                  "completed_at": "2026-07-07T00:00:00Z",
                  "cancelled_at": null,
                  "failed_at": null
                }
              ]
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(newData, "novel_imports", "runs.json"),
            """
            {
              "version": 1,
              "runs": [
                {
                  "task_id": "target-import",
                  "state": "completed",
                  "stage": "completed",
                  "source_display_name": "target.txt",
                  "source_path_hash": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "parser_type": "txt",
                  "requested_title": "目标导入",
                  "commit_message": "import target",
                  "created_novel_id": 1,
                  "created_file_roots": ["novels/1"],
                  "skipped_chapters": [],
                  "diagnostics": [],
                  "warnings": [],
                  "error": null,
                  "cleanup_state": "not_started",
                  "warning_state": "none",
                  "started_at": "2026-07-07T00:00:00Z",
                  "updated_at": "2026-07-07T00:00:00Z",
                  "completed_at": "2026-07-07T00:00:00Z"
                }
              ]
            }
            """);
    }

    private static async ValueTask CreatePartialReferenceStyleDatabaseAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
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
            """);
        await ExecuteAsync(connection, """
            CREATE TABLE reference_source_segments (
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
              text_hash TEXT NOT NULL
            );
            """);
        await ExecuteAsync(connection, """
            CREATE TABLE reference_materials (
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
              archived_at TEXT
            );
            """);
        await ExecuteAsync(connection, """
            CREATE TABLE reference_style_profiles (
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
            """);
        await ExecuteAsync(connection, """
            CREATE TABLE reference_style_profile_evidence (
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
              created_at TEXT NOT NULL
            );
            """);
        await ExecuteAsync(connection, "INSERT INTO reference_anchors VALUES (5, 1, '既有锚点', '', 'source.md', 'markdown', 'user_provided', 'hash-source', 'reference-anchor-v1', 'ready', '2026-07-07T00:00:00Z', '2026-07-07T00:00:00Z', 'private', 'user_verified', '[]')");
        await ExecuteAsync(connection, "INSERT INTO reference_source_segments VALUES ('seg-5-1', 5, 1, '第一章', 'chapter', 1, '', 0, 10, '既有片段', 'hash-segment')");
        await ExecuteAsync(connection, "INSERT INTO reference_materials VALUES ('mat-5-1', 5, 'seg-5-1', 'scene', 'setup', 'restraint', 'rain', 'limited', 'contrast', 0.9, 0.8, 0.7, '既有材料', 'hash-material', 'reference-anchor-v1', 1, '2026-07-07T00:00:00Z', NULL)");
        await ExecuteAsync(connection, """
            INSERT INTO reference_style_profiles
            VALUES (
              7,
              1,
              '既有 Phase14 画像',
              '',
              'ready',
              'reference-style-deterministic-v1',
              'reference-style-feature-v1',
              'deterministic',
              '[5]',
              '["hash-source"]',
              '["user_provided"]',
              '["user_verified"]',
              '{"numeric_features":[],"distribution_features":[],"categorical_features":[]}',
              0.75,
              '2026-07-07T00:00:00Z',
              '2026-07-07T00:00:00Z',
              NULL
            )
            """);
        await ExecuteAsync(connection, "INSERT INTO reference_style_profile_evidence VALUES ('ev-7-1', 7, 5, 'seg-5-1', 'mat-5-1', 'sentence_length', 'short', 0, 4, 'hash-evidence', 0.8, 'deterministic', '2026-07-07T00:00:00Z')");
    }

    private static async ValueTask ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private static async ValueTask<IReadOnlyDictionary<string, string>> HashFilesAsync(IEnumerable<string> paths)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            result[path] = await HashFileAsync(path);
        }

        return result;
    }

    private static byte[] EncryptLegacyLlmConfig(string json)
    {
        var key = new byte[]
        {
            0x7a, 0x3f, 0x71, 0xe2, 0x5c, 0x9d, 0x0b, 0x46,
            0x1a, 0x5f, 0x33, 0xc8, 0x6e, 0x22, 0x4d, 0x0f,
            0x85, 0xce, 0x1c, 0x29, 0x3f, 0xa7, 0x80, 0xf4,
            0x2e, 0x9c, 0x17, 0xd5, 0x4a, 0x8e, 0xd2, 0x06
        };
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(json);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, ciphertext, tag);
        return nonce.Concat(ciphertext).Concat(tag).ToArray();
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    private sealed class ThrowingCompletionClient : IChatCompletionClient
    {
        public IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("This test only reads imported chat state.");
        }

        public ValueTask<string> GenerateTextAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("This test only reads imported chat state.");
        }
    }
}
