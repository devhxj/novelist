using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Agent;

public sealed partial class NovelistMafToolRegistry
{
    private partial void AddStructuredTools(List<AIFunction> tools, NovelistMafToolContext context)
    {
        var structured = new StructuredMafTools(
            _chapterContent,
            _preferences,
            _world,
            _planning,
            _approvals,
            context,
            _serializerOptions);

        structured.AddAvailableTools(tools);
    }

    private sealed class StructuredMafTools
    {
        private const string GetChapterListToolName = "get_chapter_list";
        private const string GetPreferencesToolName = "get_preferences";
        private const string CreatePreferenceToolName = "create_preference";
        private const string UpdatePreferenceToolName = "update_preference";
        private const string GetCharactersToolName = "get_characters";
        private const string GetCharacterRelationsToolName = "get_character_relations";
        private const string CreateCharacterToolName = "create_character";
        private const string UpdateCharacterToolName = "update_character";
        private const string UpdateCharacterRelationshipToolName = "update_character_relationship";
        private const string GetLocationsToolName = "get_locations";
        private const string CreateLocationToolName = "create_location";
        private const string UpdateLocationToolName = "update_location";
        private const string CreateLocationRelationToolName = "create_location_relation";
        private const string UpdateLocationRelationToolName = "update_location_relation";
        private const string GetTimelineToolName = "get_timeline";
        private const string CreateTimelineEntryToolName = "create_timeline_entry";
        private const string UpdateTimelineEntryToolName = "update_timeline_entry";
        private const string UpdateChapterPlanToolName = "update_chapter_plan";
        private const string GetStoryArcsToolName = "get_story_arcs";
        private const string CreateStoryArcToolName = "create_story_arc";
        private const string UpdateStoryArcToolName = "update_story_arc";
        private const string CreateArcNodeToolName = "create_arc_node";
        private const string UpdateArcNodeToolName = "update_arc_node";
        private const string GetReaderPerspectiveToolName = "get_reader_perspective";
        private const string CreateReaderPerspectiveEntryToolName = "create_reader_perspective_entry";
        private const string UpdateReaderPerspectiveEntryToolName = "update_reader_perspective_entry";
        private const string DeleteRecordToolName = "delete_record";

        private const int DefaultPageSize = 50;
        private const int MaxPageSize = 100;
        private const int MaxCharacterBatch = 10;
        private const int MaxLocationBatch = 10;
        private const int MaxLocationRelationBatch = 10;
        private const int MaxPreferenceBatch = 5;
        private const int MaxTimelineEntryBatch = 6;
        private const int MaxStoryArcBatch = 5;
        private const int MaxArcNodeBatch = 10;
        private const int MaxReaderPerspectiveBatch = 10;

        private const string GetChapterListDescription = "获取小说章节列表，支持分页。按章节号降序返回 id、章节号、标题、字数、摘要和时间戳。";
        private const string GetPreferencesDescription = "获取所有创作偏好，包括全局偏好和当前小说专属偏好。返回格式化文本，用于确认长期创作规则、风格约束和用户指令。";
        private const string CreatePreferenceDescription = "批量创建创作偏好（1-5个）。偏好按自由文本 category 归类；创建前应先读取并避免重复条目。";
        private const string UpdatePreferenceDescription = "更新已有创作偏好条目。PATCH 语义，只需传入要修改的字段；增量合并时先读取现有内容，再传入合并后的完整 content。";
        private const string GetCharactersDescription = "获取当前小说角色列表。支持按角色名搜索和分页；需要关系图时再调用 get_character_relations。";
        private const string GetCharacterRelationsDescription = "获取指定角色集合内部的当前关系边。只返回两端都在 character_ids 中的关系，不限方向。";
        private const string CreateCharacterDescription = "批量创建角色（1-10个）。name 必填；personality 和 abilities 可传字符串形式 JSON。";
        private const string UpdateCharacterDescription = "更新已有角色设定。只需传入要修改的字段；personality 和 abilities 会完全替换旧值，不做合并。";
        private const string UpdateCharacterRelationshipDescription = "更新角色关系。提供 relation_id 时编辑已有关系；提供 source_character_id + target_character_id 时创建新的当前关系，并把同向旧关系标记为历史。";
        private const string GetLocationsDescription = "获取地点信息。mode=list 返回分页列表；mode=detail 返回地点详情、子地点和相关连通边；mode=network 返回根节点大地图。";
        private const string CreateLocationDescription = "批量创建地点（1-10个）。name 必填；parent_location_id 可接入已有地点层级树。";
        private const string UpdateLocationDescription = "更新已有地点。只需传入要修改的字段；parent_location_id 传 null 可清除父地点。";
        private const string CreateLocationRelationDescription = "批量创建地点间无向空间关系（1-10个）。A-B 等价 B-A，已存在边会拒绝创建。";
        private const string UpdateLocationRelationDescription = "更新已有地点空间关系边。PATCH 语义，通过 relation_id 定位。";
        private const string GetTimelineDescription = "获取故事时间线总览。传 current_chapter 时返回章节计划、近期历史、异常和未来条目；不传时分页浏览条目。";
        private const string CreateTimelineEntryDescription = "批量创建伏笔或用户指令（1-6条）。category 为 foreshadowing 或 user_directive；默认 status=pending、importance=3、source=ai。";
        private const string UpdateTimelineEntryDescription = "更新伏笔或用户指令。常用于回收伏笔、调整目标章节、修改内容或状态。";
        private const string UpdateChapterPlanDescription = "更新章节创作计划。scope 为 next、near 或 far；同一 scope 重复调用会覆盖旧值。";
        private const string GetStoryArcsDescription = "获取叙事弧线和节点链。传 current_chapter 时按当前章节拆分活跃、暂停、归档弧线；不传时分页浏览完整弧线。";
        private const string CreateStoryArcDescription = "批量创建叙事弧线（1-5个）。arc_type 为 main/sub/character/background。";
        private const string UpdateStoryArcDescription = "更新叙事弧线元数据。常用于暂停、完成或废弃弧线。";
        private const string CreateArcNodeDescription = "批量向弧线添加节点（1-10个）。target_chapter 为预计发生章节。";
        private const string UpdateArcNodeDescription = "更新弧线节点。常用于标记完成、调整目标章节或废弃节点。";
        private const string GetReaderPerspectiveDescription = "获取读者认知状态：已知信息、活跃悬念和读者误知。每条 entry_id 可用于后续更新或回收。";
        private const string CreateReaderPerspectiveEntryDescription = "批量添加读者认知条目（1-10个）。type 为 known/suspense/misconception；misconception 必须提供 related_truth。";
        private const string UpdateReaderPerspectiveEntryDescription = "更新读者认知条目。常用于回收悬念或揭露误知，设置 revealed_chapter 后不再出现在活跃列表中。";
        private const string DeleteRecordDescription = "删除指定表中的单条记录。删除前会检查关联影响并请求用户审批；存在关联数据时拒绝删除。";

        private readonly IChapterContentService? _chapters;
        private readonly IPreferenceService? _preferences;
        private readonly IWorldEntityService? _world;
        private readonly IPlanningService? _planning;
        private readonly IApprovalCoordinator? _approvals;
        private readonly NovelistMafToolContext _context;
        private readonly JsonSerializerOptions _serializerOptions;

        public StructuredMafTools(
            IChapterContentService? chapters,
            IPreferenceService? preferences,
            IWorldEntityService? world,
            IPlanningService? planning,
            IApprovalCoordinator? approvals,
            NovelistMafToolContext context,
            JsonSerializerOptions serializerOptions)
        {
            _chapters = chapters;
            _preferences = preferences;
            _world = world;
            _planning = planning;
            _approvals = approvals;
            _context = context;
            _serializerOptions = serializerOptions;
        }

        public void AddAvailableTools(List<AIFunction> tools)
        {
            if (_chapters is not null)
            {
                tools.Add(CreateFunction(nameof(GetChapterListAsync), GetChapterListToolName, GetChapterListDescription));
            }

            if (_preferences is not null)
            {
                tools.Add(CreateFunction(nameof(GetPreferencesAsync), GetPreferencesToolName, GetPreferencesDescription));
                tools.Add(CreateFunction(nameof(CreatePreferenceAsync), CreatePreferenceToolName, CreatePreferenceDescription));
                tools.Add(CreateFunction(nameof(UpdatePreferenceAsync), UpdatePreferenceToolName, UpdatePreferenceDescription));
            }

            if (_world is not null)
            {
                tools.Add(CreateFunction(nameof(GetCharactersAsync), GetCharactersToolName, GetCharactersDescription));
                tools.Add(CreateFunction(nameof(GetCharacterRelationsAsync), GetCharacterRelationsToolName, GetCharacterRelationsDescription));
                tools.Add(CreateFunction(nameof(CreateCharacterAsync), CreateCharacterToolName, CreateCharacterDescription));
                tools.Add(CreateFunction(nameof(UpdateCharacterAsync), UpdateCharacterToolName, UpdateCharacterDescription));
                tools.Add(CreateFunction(nameof(UpdateCharacterRelationshipAsync), UpdateCharacterRelationshipToolName, UpdateCharacterRelationshipDescription));
                tools.Add(CreateFunction(nameof(GetLocationsAsync), GetLocationsToolName, GetLocationsDescription));
                tools.Add(CreateFunction(nameof(CreateLocationAsync), CreateLocationToolName, CreateLocationDescription));
                tools.Add(CreateFunction(nameof(UpdateLocationAsync), UpdateLocationToolName, UpdateLocationDescription));
                tools.Add(CreateFunction(nameof(CreateLocationRelationAsync), CreateLocationRelationToolName, CreateLocationRelationDescription));
                tools.Add(CreateFunction(nameof(UpdateLocationRelationAsync), UpdateLocationRelationToolName, UpdateLocationRelationDescription));
            }

            if (_planning is not null)
            {
                tools.Add(CreateFunction(nameof(GetTimelineAsync), GetTimelineToolName, GetTimelineDescription));
                tools.Add(CreateFunction(nameof(CreateTimelineEntryAsync), CreateTimelineEntryToolName, CreateTimelineEntryDescription));
                tools.Add(CreateFunction(nameof(UpdateTimelineEntryAsync), UpdateTimelineEntryToolName, UpdateTimelineEntryDescription));
                tools.Add(CreateFunction(nameof(UpdateChapterPlanAsync), UpdateChapterPlanToolName, UpdateChapterPlanDescription));
                tools.Add(CreateFunction(nameof(GetStoryArcsAsync), GetStoryArcsToolName, GetStoryArcsDescription));
                tools.Add(CreateFunction(nameof(CreateStoryArcAsync), CreateStoryArcToolName, CreateStoryArcDescription));
                tools.Add(CreateFunction(nameof(UpdateStoryArcAsync), UpdateStoryArcToolName, UpdateStoryArcDescription));
                tools.Add(CreateFunction(nameof(CreateArcNodeAsync), CreateArcNodeToolName, CreateArcNodeDescription));
                tools.Add(CreateFunction(nameof(UpdateArcNodeAsync), UpdateArcNodeToolName, UpdateArcNodeDescription));
                tools.Add(CreateFunction(nameof(GetReaderPerspectiveAsync), GetReaderPerspectiveToolName, GetReaderPerspectiveDescription));
                tools.Add(CreateFunction(nameof(CreateReaderPerspectiveEntryAsync), CreateReaderPerspectiveEntryToolName, CreateReaderPerspectiveEntryDescription));
                tools.Add(CreateFunction(nameof(UpdateReaderPerspectiveEntryAsync), UpdateReaderPerspectiveEntryToolName, UpdateReaderPerspectiveEntryDescription));
            }

            if (_world is not null && _planning is not null && _preferences is not null)
            {
                tools.Add(CreateFunction(nameof(DeleteRecordAsync), DeleteRecordToolName, DeleteRecordDescription));
            }
        }

        private AIFunction CreateFunction(string methodName, string toolName, string description)
        {
            var method = typeof(StructuredMafTools).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(StructuredMafTools).FullName, methodName);

            return AIFunctionFactory.Create(
                method,
                this,
                new AIFunctionFactoryOptions
                {
                    Name = toolName,
                    Description = description,
                    SerializerOptions = _serializerOptions
                });
        }

        [Description(GetChapterListDescription)]
        private async ValueTask<Dictionary<string, object?>> GetChapterListAsync(
            [Description("页码，默认 1")]
            int page = 0,
            [Description("每页数量，默认 50，范围 1-100")]
            int size = 0,
            CancellationToken cancellationToken = default)
        {
            var chapters = await RequireChapters().GetChaptersAsync(_context.NovelId, cancellationToken);
            var ordered = chapters
                .OrderByDescending(chapter => chapter.ChapterNumber)
                .Select(chapter => new ChapterListItem(
                    chapter.Id,
                    chapter.ChapterNumber,
                    chapter.Title,
                    chapter.WordCount,
                    chapter.Summary,
                    chapter.CreatedAt,
                    chapter.UpdatedAt))
                .ToArray();
            return PageResult("items", ordered, page, size);
        }

        [Description(GetPreferencesDescription)]
        private async ValueTask<ContentWithCountsResult> GetPreferencesAsync(CancellationToken cancellationToken = default)
        {
            var result = await RequirePreferences().GetPreferencesAsync(_context.NovelId, cancellationToken);
            return new ContentWithCountsResult(
                FormatPreferences(result.Global, result.Novel),
                new Dictionary<string, int>
                {
                    ["global"] = result.Global.Count,
                    ["novel"] = result.Novel.Count
                });
        }

        [Description(CreatePreferenceDescription)]
        private async ValueTask<BatchIdsResult> CreatePreferenceAsync(
            [Description("要创建的偏好列表（1-5个）")]
            CreatePreferenceItem[] preferences,
            CancellationToken cancellationToken = default)
        {
            EnsureBatch(preferences, 1, MaxPreferenceBatch, nameof(preferences));
            var service = RequirePreferences();
            List<long> ids = [];
            foreach (var preference in preferences)
            {
                var created = await service.CreatePreferenceAsync(
                    _context.NovelId,
                    new CreatePreferencePayload(preference.IsGlobal, preference.Category, preference.Content),
                    cancellationToken);
                ids.Add(created.Id);
            }

            return new BatchIdsResult(ids.ToArray(), ids.Count);
        }

        [Description(UpdatePreferenceDescription)]
        private async ValueTask<PreferenceIdResult> UpdatePreferenceAsync(
            [Description("偏好条目 ID")]
            long preference_id,
            [Description("新的分类标签")]
            string? category = null,
            [Description("新的偏好内容。增量合并时传合并后的完整内容")]
            string? content = null,
            [Description("是否改为全局偏好")]
            bool? is_global = null,
            CancellationToken cancellationToken = default)
        {
            if (category is null && content is null && is_global is null)
            {
                throw new ArgumentException("At least one preference field must be provided.", nameof(preference_id));
            }

            var updated = await RequirePreferences().UpdatePreferenceAsync(
                preference_id,
                new UpdatePreferencePayload(category, content, is_global),
                cancellationToken);
            EnsurePreferenceVisible(updated);
            return new PreferenceIdResult(updated.Id);
        }

        [Description(GetCharactersDescription)]
        private async ValueTask<Dictionary<string, object?>> GetCharactersAsync(
            [Description("角色名搜索，空表示不过滤")]
            string? search = null,
            [Description("页码，默认 1")]
            int page = 0,
            [Description("每页数量，默认 50，范围 1-100")]
            int size = 0,
            CancellationToken cancellationToken = default)
        {
            var query = NormalizeOptionalSearch(search);
            var characters = await RequireWorld().GetCharactersAsync(_context.NovelId, cancellationToken);
            var filtered = characters
                .Where(character => string.IsNullOrEmpty(query) || character.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(character => new CharacterToolItemResult(
                    character.Id,
                    character.Name,
                    character.Description,
                    ParseJsonField(character.Personality),
                    ParseJsonField(character.Abilities)))
                .ToArray();
            return PageResult("characters", filtered, page, size);
        }

        [Description(GetCharacterRelationsDescription)]
        private async ValueTask<ContentResult> GetCharacterRelationsAsync(
            [Description("角色 ID 列表，只返回这些角色之间的当前关系边")]
            long[] character_ids,
            CancellationToken cancellationToken = default)
        {
            var ids = NormalizeIds(character_ids, nameof(character_ids));
            var idSet = ids.ToHashSet();
            var world = RequireWorld();
            var characters = await world.GetCharactersAsync(_context.NovelId, cancellationToken);
            var nameMap = characters.ToDictionary(character => character.Id, character => character.Name);
            var relations = await world.GetCharacterRelationsAsync(_context.NovelId, cancellationToken);
            var filtered = relations
                .Where(relation => idSet.Contains(relation.SourceCharacterId) && idSet.Contains(relation.TargetCharacterId))
                .OrderBy(relation => relation.SourceCharacterId)
                .ThenBy(relation => relation.Id)
                .ToArray();
            return new ContentResult(FormatCharacterRelations(filtered, nameMap));
        }

        [Description(CreateCharacterDescription)]
        private async ValueTask<BatchIdsResult> CreateCharacterAsync(
            [Description("要创建的角色列表（1-10个）")]
            CreateCharacterItem[] characters,
            CancellationToken cancellationToken = default)
        {
            EnsureBatch(characters, 1, MaxCharacterBatch, nameof(characters));
            var world = RequireWorld();
            List<long> ids = [];
            foreach (var item in characters)
            {
                var created = await world.CreateCharacterAsync(
                    _context.NovelId,
                    new CreateCharacterPayload(item.Name, item.Description, item.Personality, item.Abilities),
                    cancellationToken);
                ids.Add(created.Id);
            }

            return new BatchIdsResult(ids.ToArray(), ids.Count);
        }

        [Description(UpdateCharacterDescription)]
        private async ValueTask<IdResult> UpdateCharacterAsync(
            [Description("角色 ID")]
            long character_id,
            [Description("新的角色名称")]
            string? name = null,
            [Description("新的自然语言描述")]
            string? description = null,
            [Description("新的性格/设定，字符串形式 JSON，完全替换旧值")]
            string? personality = null,
            [Description("新的能力列表，字符串形式 JSON，完全替换旧值")]
            string? abilities = null,
            CancellationToken cancellationToken = default)
        {
            if (IsBlank(name) && IsBlank(description) && IsBlank(personality) && IsBlank(abilities))
            {
                throw new ArgumentException("At least one character field must be provided.", nameof(character_id));
            }

            await RequireWorld().UpdateCharacterAsync(
                _context.NovelId,
                character_id,
                new UpdateCharacterPayload(name, description, personality, abilities),
                cancellationToken);
            return new IdResult(character_id);
        }

        [Description(UpdateCharacterRelationshipDescription)]
        private async ValueTask<IdActionResult> UpdateCharacterRelationshipAsync(
            [Description("编辑已有关系时提供此 ID")]
            long relation_id = 0,
            [Description("关系演变时的发出方角色 ID")]
            long source_character_id = 0,
            [Description("关系演变时的接收方角色 ID")]
            long target_character_id = 0,
            [Description("关系描述，如“师徒但暗中互相提防”")]
            string? relation_describe = null,
            [Description("当前关系阶段的详细描述")]
            string? description = null,
            [Description("此关系确立或变化的章节 ID")]
            long? chapter_id = null,
            CancellationToken cancellationToken = default)
        {
            var updated = await RequireWorld().UpdateCharacterRelationshipAsync(
                _context.NovelId,
                new UpdateCharacterRelationshipPayload(
                    relation_id,
                    source_character_id,
                    target_character_id,
                    relation_describe,
                    description,
                    chapter_id),
                cancellationToken);
            var action = relation_id > 0 ? "edit" : "evolve";
            return new IdActionResult(updated.Id, action);
        }

        [Description(GetLocationsDescription)]
        private async ValueTask<object> GetLocationsAsync(
            [Description("查询模式：list / detail / network")]
            string mode = "list",
            [Description("detail 模式必填的地点 ID")]
            long location_id = 0,
            [Description("list 模式按地点类型筛选")]
            string? location_type = null,
            [Description("list 模式按名称搜索")]
            string? search = null,
            [Description("页码，默认 1")]
            int page = 0,
            [Description("每页数量，默认 50，范围 1-100")]
            int size = 0,
            CancellationToken cancellationToken = default)
        {
            var normalizedMode = NormalizeMode(mode, ["list", "detail", "network"]);
            return normalizedMode switch
            {
                "detail" => await GetLocationDetailAsync(location_id, cancellationToken),
                "network" => await GetLocationNetworkAsync(cancellationToken),
                _ => await GetLocationListAsync(location_type, search, page, size, cancellationToken)
            };
        }

        [Description(CreateLocationDescription)]
        private async ValueTask<BatchIdsResult> CreateLocationAsync(
            [Description("要创建的地点列表（1-10个）")]
            CreateLocationItem[] locations,
            CancellationToken cancellationToken = default)
        {
            EnsureBatch(locations, 1, MaxLocationBatch, nameof(locations));
            var world = RequireWorld();
            List<long> ids = [];
            foreach (var item in locations)
            {
                var created = await world.CreateLocationAsync(
                    _context.NovelId,
                    new CreateLocationPayload(
                        item.Name,
                        item.LocationType,
                        item.Description,
                        item.DetailJson,
                        item.ParentLocationId,
                        item.Tags),
                    cancellationToken);
                ids.Add(created.Id);
            }

            return new BatchIdsResult(ids.ToArray(), ids.Count);
        }

        [Description(UpdateLocationDescription)]
        private async ValueTask<IdResult> UpdateLocationAsync(
            [Description("地点 ID")]
            long location_id,
            [Description("新的地点名称")]
            string? name = null,
            [Description("新的地点类型")]
            string? location_type = null,
            [Description("新的地点描述")]
            string? description = null,
            [Description("新的结构化信息，字符串形式 JSON，完全替换旧值")]
            string? detail_json = null,
            [Description("新的标签，字符串形式 JSON 数组，完全替换旧值")]
            string? tags = null,
            [Description("新的父地点 ID；传 null 可清除父地点")]
            long? parent_location_id = null,
            CancellationToken cancellationToken = default)
        {
            var hasParent = RawArgumentsHasProperty("parent_location_id");
            if (IsBlank(name) && IsBlank(location_type) && IsBlank(description) && IsBlank(detail_json) && IsBlank(tags) && !hasParent)
            {
                throw new ArgumentException("At least one location field must be provided.", nameof(location_id));
            }

            await RequireWorld().UpdateLocationAsync(
                _context.NovelId,
                location_id,
                new UpdateLocationPayload(
                    name,
                    location_type,
                    description,
                    detail_json,
                    hasParent ? parent_location_id : null,
                    tags,
                    ClearParent: hasParent && parent_location_id is null),
                cancellationToken);
            return new IdResult(location_id);
        }

        [Description(CreateLocationRelationDescription)]
        private async ValueTask<BatchIdsResult> CreateLocationRelationAsync(
            [Description("要创建的地点关系列表（1-10个）")]
            CreateLocationRelationItem[] relations,
            CancellationToken cancellationToken = default)
        {
            EnsureBatch(relations, 1, MaxLocationRelationBatch, nameof(relations));
            EnsureUniqueLocationPairs(relations);
            var world = RequireWorld();
            List<long> ids = [];
            foreach (var relation in relations)
            {
                var created = await world.CreateLocationRelationAsync(
                    _context.NovelId,
                    new CreateLocationRelationPayload(
                        relation.LocationAId,
                        relation.LocationBId,
                        relation.RelationType,
                        relation.Description),
                    cancellationToken);
                ids.Add(created.Id);
            }

            return new BatchIdsResult(ids.ToArray(), ids.Count);
        }

        [Description(UpdateLocationRelationDescription)]
        private async ValueTask<IdResult> UpdateLocationRelationAsync(
            [Description("关系边 ID")]
            long relation_id,
            [Description("新的空间关系描述")]
            string? relation_type = null,
            [Description("新的补充细节")]
            string? description = null,
            CancellationToken cancellationToken = default)
        {
            if (relation_type is null && description is null)
            {
                throw new ArgumentException("At least one location relation field must be provided.", nameof(relation_id));
            }

            var updated = await RequireWorld().UpdateLocationRelationAsync(
                _context.NovelId,
                relation_id,
                new UpdateLocationRelationPayload(relation_type, description),
                cancellationToken);
            return new IdResult(updated.Id);
        }

        [Description(GetTimelineDescription)]
        private async ValueTask<object> GetTimelineAsync(
            [Description("当前章节号。传入时自动收集附近条目并检测异常")]
            int current_chapter = 0,
            [Description("按分类筛选：foreshadowing / user_directive")]
            string? category = null,
            [Description("按状态筛选：pending / resolved / abandoned")]
            string? status = null,
            [Description("页码，默认 1；仅不传 current_chapter 时生效")]
            int page = 0,
            [Description("每页数量，默认 50，范围 1-100；仅不传 current_chapter 时生效")]
            int size = 0,
            CancellationToken cancellationToken = default)
        {
            var planning = RequirePlanning();
            var entries = await planning.GetTimelineEntriesAsync(_context.NovelId, 0, 0, cancellationToken);
            var plans = await planning.GetChapterPlansAsync(_context.NovelId, cancellationToken);
            if (current_chapter > 0)
            {
                return new ContentResult(FormatTimelineContext(plans, entries, current_chapter));
            }

            var filtered = entries
                .Where(entry => string.IsNullOrWhiteSpace(category) || string.Equals(entry.Category, category, StringComparison.Ordinal))
                .Where(entry => string.IsNullOrWhiteSpace(status) || string.Equals(entry.Status, status, StringComparison.Ordinal))
                .OrderBy(entry => entry.TargetChapter)
                .ThenByDescending(entry => entry.Importance)
                .ThenBy(entry => entry.Id)
                .ToArray();
            return PageResult("entries", filtered, page, size, extra: new Dictionary<string, object?>
            {
                ["content"] = FormatTimelineFull(filtered)
            });
        }

        [Description(CreateTimelineEntryDescription)]
        private async ValueTask<BatchIdsResult> CreateTimelineEntryAsync(
            [Description("要创建的时间线条目（1-6条）")]
            CreateTimelineEntryItem[] entries,
            CancellationToken cancellationToken = default)
        {
            EnsureBatch(entries, 1, MaxTimelineEntryBatch, nameof(entries));
            var planning = RequirePlanning();
            List<long> ids = [];
            foreach (var item in entries)
            {
                var created = await planning.CreateTimelineEntryAsync(
                    _context.NovelId,
                    new CreateTimelineEntryPayload(
                        item.Category,
                        item.Title,
                        item.Content,
                        item.DetailJson,
                        item.TargetChapter,
                        item.Importance is 0 ? null : item.Importance,
                        item.SourceChapterId is 0 ? null : item.SourceChapterId,
                        string.IsNullOrWhiteSpace(item.Source) ? "ai" : item.Source),
                    cancellationToken);
                ids.Add(created.Id);
            }

            return new BatchIdsResult(ids.ToArray(), ids.Count);
        }

        [Description(UpdateTimelineEntryDescription)]
        private async ValueTask<IdResult> UpdateTimelineEntryAsync(
            [Description("条目 ID")]
            long entry_id,
            string? title = null,
            string? content = null,
            string? detail_json = null,
            int? target_chapter = null,
            int? importance = null,
            string? status = null,
            long? resolved_chapter_id = null,
            CancellationToken cancellationToken = default)
        {
            if (title is null && content is null && detail_json is null && target_chapter is null && importance is null && status is null && resolved_chapter_id is null)
            {
                throw new ArgumentException("At least one timeline field must be provided.", nameof(entry_id));
            }

            await RequirePlanning().UpdateTimelineEntryAsync(
                _context.NovelId,
                entry_id,
                new UpdateTimelineEntryPayload(title, content, detail_json, target_chapter, importance, status, resolved_chapter_id),
                cancellationToken);
            return new IdResult(entry_id);
        }

        [Description(UpdateChapterPlanDescription)]
        private async ValueTask<ScopeResult> UpdateChapterPlanAsync(
            [Description("计划范围：next / near / far")]
            string scope,
            [Description("计划内容，自然语言描述")]
            string content,
            CancellationToken cancellationToken = default)
        {
            await RequirePlanning().UpdateChapterPlanAsync(
                _context.NovelId,
                new UpdateChapterPlanPayload(scope, content),
                cancellationToken);
            return new ScopeResult(scope);
        }

        [Description(GetStoryArcsDescription)]
        private async ValueTask<object> GetStoryArcsAsync(
            int current_chapter = 0,
            string? arc_type = null,
            string? status = null,
            int page = 0,
            int size = 0,
            CancellationToken cancellationToken = default)
        {
            var planning = RequirePlanning();
            var arcs = await planning.GetStoryArcsAsync(_context.NovelId, cancellationToken);
            var nodes = await planning.GetArcNodesAsync(_context.NovelId, 0, 0, cancellationToken);
            if (current_chapter > 0)
            {
                return new ContentResult(FormatStoryArcsContext(arcs, nodes, current_chapter));
            }

            var filtered = arcs
                .Where(arc => string.IsNullOrWhiteSpace(arc_type) || string.Equals(arc.ArcType, arc_type, StringComparison.Ordinal))
                .Where(arc => string.IsNullOrWhiteSpace(status) || string.Equals(arc.Status, status, StringComparison.Ordinal))
                .OrderByDescending(arc => arc.Importance)
                .ThenBy(arc => arc.CreatedAt)
                .ThenBy(arc => arc.Id)
                .ToArray();
            return PageResult("story_arcs", filtered, page, size, extra: new Dictionary<string, object?>
            {
                ["content"] = FormatStoryArcsFull(filtered, nodes)
            });
        }

        [Description(CreateStoryArcDescription)]
        private async ValueTask<BatchIdsResult> CreateStoryArcAsync(
            CreateStoryArcItem[] story_arcs,
            CancellationToken cancellationToken = default)
        {
            EnsureBatch(story_arcs, 1, MaxStoryArcBatch, nameof(story_arcs));
            var planning = RequirePlanning();
            List<long> ids = [];
            foreach (var item in story_arcs)
            {
                var created = await planning.CreateStoryArcAsync(
                    _context.NovelId,
                    new CreateStoryArcPayload(
                        item.Name,
                        item.ArcType,
                        item.Description,
                        item.Importance is 0 ? null : item.Importance),
                    cancellationToken);
                ids.Add(created.Id);
            }

            return new BatchIdsResult(ids.ToArray(), ids.Count);
        }

        [Description(UpdateStoryArcDescription)]
        private async ValueTask<IdResult> UpdateStoryArcAsync(
            long arc_id,
            string? name = null,
            string? description = null,
            string? arc_type = null,
            int? importance = null,
            string? status = null,
            string? reactivate_at = null,
            CancellationToken cancellationToken = default)
        {
            if (name is null && description is null && arc_type is null && importance is null && status is null && reactivate_at is null)
            {
                throw new ArgumentException("At least one story arc field must be provided.", nameof(arc_id));
            }

            await RequirePlanning().UpdateStoryArcAsync(
                _context.NovelId,
                arc_id,
                new UpdateStoryArcPayload(name, description, arc_type, importance, status, reactivate_at),
                cancellationToken);
            return new IdResult(arc_id);
        }

        [Description(CreateArcNodeDescription)]
        private async ValueTask<BatchIdsResult> CreateArcNodeAsync(
            CreateArcNodeItem[] arc_nodes,
            CancellationToken cancellationToken = default)
        {
            EnsureBatch(arc_nodes, 1, MaxArcNodeBatch, nameof(arc_nodes));
            var planning = RequirePlanning();
            List<long> ids = [];
            foreach (var item in arc_nodes)
            {
                var created = await planning.CreateArcNodeAsync(
                    _context.NovelId,
                    new CreateArcNodePayload(item.ArcId, item.Title, item.Description, item.TargetChapter),
                    cancellationToken);
                ids.Add(created.Id);
            }

            return new BatchIdsResult(ids.ToArray(), ids.Count);
        }

        [Description(UpdateArcNodeDescription)]
        private async ValueTask<IdResult> UpdateArcNodeAsync(
            long node_id,
            string? title = null,
            string? description = null,
            int? target_chapter = null,
            int? actual_chapter = null,
            string? status = null,
            CancellationToken cancellationToken = default)
        {
            if (title is null && description is null && target_chapter is null && actual_chapter is null && status is null)
            {
                throw new ArgumentException("At least one arc node field must be provided.", nameof(node_id));
            }

            await RequirePlanning().UpdateArcNodeAsync(
                _context.NovelId,
                node_id,
                new UpdateArcNodePayload(title, description, target_chapter, actual_chapter, status),
                cancellationToken);
            return new IdResult(node_id);
        }

        [Description(GetReaderPerspectiveDescription)]
        private async ValueTask<ContentWithCountsResult> GetReaderPerspectiveAsync(CancellationToken cancellationToken = default)
        {
            var items = await RequirePlanning().GetReaderPerspectivesAsync(_context.NovelId, cancellationToken);
            var known = items
                .Where(item => item.Type == "known")
                .OrderByDescending(item => item.PlantedChapter)
                .ThenByDescending(item => item.Id)
                .Take(60)
                .ToArray();
            var suspense = items
                .Where(item => item.Type == "suspense" && item.RevealedChapter <= 0)
                .OrderBy(item => item.PlantedChapter)
                .ThenBy(item => item.Id)
                .ToArray();
            var misconception = items
                .Where(item => item.Type == "misconception" && item.RevealedChapter <= 0)
                .OrderBy(item => item.PlantedChapter)
                .ThenBy(item => item.Id)
                .ToArray();
            return new ContentWithCountsResult(
                FormatReaderPerspective(known, suspense, misconception),
                new Dictionary<string, int>
                {
                    ["known"] = known.Length,
                    ["suspense"] = suspense.Length,
                    ["misconception"] = misconception.Length
                });
        }

        [Description(CreateReaderPerspectiveEntryDescription)]
        private async ValueTask<BatchIdsResult> CreateReaderPerspectiveEntryAsync(
            CreateReaderPerspectiveEntryItem[] entries,
            CancellationToken cancellationToken = default)
        {
            EnsureBatch(entries, 1, MaxReaderPerspectiveBatch, nameof(entries));
            var planning = RequirePlanning();
            List<long> ids = [];
            foreach (var item in entries)
            {
                if (item.Type == "misconception" && string.IsNullOrWhiteSpace(item.RelatedTruth))
                {
                    throw new ArgumentException("misconception entries require related_truth.", nameof(entries));
                }

                var created = await planning.CreateReaderPerspectiveAsync(
                    _context.NovelId,
                    new CreateReaderPerspectivePayload(
                        item.Type,
                        item.Content,
                        item.PlantedChapter,
                        item.RelatedTruth,
                        RevealedChapter: null),
                    cancellationToken);
                ids.Add(created.Id);
            }

            return new BatchIdsResult(ids.ToArray(), ids.Count);
        }

        [Description(UpdateReaderPerspectiveEntryDescription)]
        private async ValueTask<ReaderPerspectiveUpdateResult> UpdateReaderPerspectiveEntryAsync(
            long entry_id,
            string? content = null,
            int? revealed_chapter = null,
            int? planted_chapter = null,
            string? related_truth = null,
            string? type = null,
            CancellationToken cancellationToken = default)
        {
            if (content is null && revealed_chapter is null && planted_chapter is null && related_truth is null && type is null)
            {
                throw new ArgumentException("At least one reader perspective field must be provided.", nameof(entry_id));
            }

            await RequirePlanning().UpdateReaderPerspectiveAsync(
                _context.NovelId,
                entry_id,
                new UpdateReaderPerspectivePayload(type, content, planted_chapter, related_truth, revealed_chapter),
                cancellationToken);
            var updated = (await RequirePlanning().GetReaderPerspectivesAsync(_context.NovelId, cancellationToken))
                .Single(item => item.Id == entry_id);
            return new ReaderPerspectiveUpdateResult(updated.Id, updated.RevealedChapter);
        }

        [Description(DeleteRecordDescription)]
        private async ValueTask<DeleteRecordResult> DeleteRecordAsync(
            [Description("要删除的表名：character / character_relation / location / location_relation / timeline_entry / story_arc / arc_node / reader_perspective_entry / preference")]
            string table,
            [Description("主键 ID")]
            long id,
            CancellationToken cancellationToken = default)
        {
            var normalizedTable = NormalizeMode(table, [
                "character",
                "character_relation",
                "location",
                "location_relation",
                "timeline_entry",
                "story_arc",
                "arc_node",
                "reader_perspective_entry",
                "preference"]);
            if (id <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(id), id, "Identifier must be positive.");
            }

            return normalizedTable switch
            {
                "character" => await DeleteCharacterAsync(id, cancellationToken),
                "character_relation" => await DeleteCharacterRelationAsync(id, cancellationToken),
                "location" => await DeleteLocationAsync(id, cancellationToken),
                "location_relation" => await DeleteLocationRelationAsync(id, cancellationToken),
                "timeline_entry" => await DeleteTimelineEntryAsync(id, cancellationToken),
                "story_arc" => await DeleteStoryArcAsync(id, cancellationToken),
                "arc_node" => await DeleteArcNodeAsync(id, cancellationToken),
                "reader_perspective_entry" => await DeleteReaderPerspectiveEntryAsync(id, cancellationToken),
                "preference" => await DeletePreferenceAsync(id, cancellationToken),
                _ => throw new ArgumentException($"Unsupported table: {table}", nameof(table))
            };
        }

        private async ValueTask<object> GetLocationListAsync(
            string? locationType,
            string? search,
            int page,
            int size,
            CancellationToken cancellationToken)
        {
            var query = NormalizeOptionalSearch(search);
            var type = NormalizeOptionalSearch(locationType);
            var items = (await RequireWorld().GetLocationsAsync(_context.NovelId, cancellationToken))
                .Where(location => string.IsNullOrEmpty(type) || string.Equals(location.LocationType, type, StringComparison.Ordinal))
                .Where(location => string.IsNullOrEmpty(query) || location.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(location => new LocationListItem(
                    location.Id,
                    location.Name,
                    location.LocationType,
                    TruncateRunes(location.Description, 100),
                    ParseJsonField(location.Tags)))
                .ToArray();
            return PageResult("locations", items, page, size);
        }

        private async ValueTask<ContentResult> GetLocationDetailAsync(long locationId, CancellationToken cancellationToken)
        {
            if (locationId <= 0)
            {
                throw new ArgumentException("detail mode requires location_id.", nameof(locationId));
            }

            var world = RequireWorld();
            var locations = await world.GetLocationsAsync(_context.NovelId, cancellationToken);
            var relations = await world.GetLocationRelationsAsync(_context.NovelId, cancellationToken);
            var location = locations.SingleOrDefault(item => item.Id == locationId)
                ?? throw new ArgumentException($"Location '{locationId}' does not exist.", nameof(locationId));
            var children = locations.Where(item => item.ParentLocationId == locationId).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
            var scope = children.Select(item => item.Id).Append(location.Id).ToHashSet();
            var involved = relations
                .Where(relation => scope.Contains(relation.LocationAId) || scope.Contains(relation.LocationBId))
                .OrderBy(relation => relation.Id)
                .ToArray();
            var names = locations.ToDictionary(item => item.Id, item => item.Name);
            var parentName = location.ParentLocationId is { } parentId && names.TryGetValue(parentId, out var parent)
                ? parent
                : string.Empty;
            return new ContentResult(FormatLocationDetail(location, parentName, children, involved, names));
        }

        private async ValueTask<ContentResult> GetLocationNetworkAsync(CancellationToken cancellationToken)
        {
            var world = RequireWorld();
            var locations = await world.GetLocationsAsync(_context.NovelId, cancellationToken);
            var relations = await world.GetLocationRelationsAsync(_context.NovelId, cancellationToken);
            var roots = locations.Where(item => item.ParentLocationId is null).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
            var rootSet = roots.Select(item => item.Id).ToHashSet();
            var rootRelations = relations
                .Where(relation => rootSet.Contains(relation.LocationAId) && rootSet.Contains(relation.LocationBId))
                .OrderBy(relation => relation.Id)
                .ToArray();
            var childCounts = locations
                .Where(item => item.ParentLocationId is not null)
                .GroupBy(item => item.ParentLocationId!.Value)
                .ToDictionary(group => group.Key, group => group.Count());
            return new ContentResult(FormatLocationNetwork(roots, rootRelations, childCounts));
        }

        private async ValueTask<DeleteRecordResult> DeleteCharacterAsync(long id, CancellationToken cancellationToken)
        {
            var world = RequireWorld();
            var characters = await world.GetCharactersAsync(_context.NovelId, cancellationToken);
            var character = characters.SingleOrDefault(item => item.Id == id)
                ?? throw new ArgumentException($"Character '{id}' does not exist.", nameof(id));
            var relationCount = (await world.GetAllCharacterRelationsAsync(_context.NovelId, cancellationToken))
                .Count(relation => relation.SourceCharacterId == id || relation.TargetCharacterId == id);
            if (relationCount > 0)
            {
                throw new InvalidOperationException($"角色“{character.Name}”存在 {relationCount.ToString(CultureInfo.InvariantCulture)} 条角色关系，请先删除这些关系边。");
            }

            var meta = new Dictionary<string, object?> { ["id"] = character.Id, ["name"] = character.Name, ["type"] = "character" };
            var feedback = await RequestDeleteApprovalAsync("character", id, meta, cancellationToken);
            await world.DeleteCharacterAsync(_context.NovelId, id, cancellationToken);
            return new DeleteRecordResult(meta, feedback);
        }

        private async ValueTask<DeleteRecordResult> DeleteCharacterRelationAsync(long id, CancellationToken cancellationToken)
        {
            var world = RequireWorld();
            var relations = await world.GetAllCharacterRelationsAsync(_context.NovelId, cancellationToken);
            var relation = relations.SingleOrDefault(item => item.Id == id)
                ?? throw new ArgumentException($"Character relation '{id}' does not exist.", nameof(id));
            var names = (await world.GetCharactersAsync(_context.NovelId, cancellationToken))
                .ToDictionary(item => item.Id, item => item.Name);
            var meta = new Dictionary<string, object?>
            {
                ["id"] = relation.Id,
                ["source"] = names.GetValueOrDefault(relation.SourceCharacterId, string.Empty),
                ["target"] = names.GetValueOrDefault(relation.TargetCharacterId, string.Empty),
                ["relation"] = relation.RelationDescribe,
                ["type"] = "character_relation"
            };
            var feedback = await RequestDeleteApprovalAsync("character_relation", id, meta, cancellationToken);
            await world.DeleteCharacterRelationAsync(_context.NovelId, id, cancellationToken);
            return new DeleteRecordResult(meta, feedback);
        }

        private async ValueTask<DeleteRecordResult> DeleteLocationAsync(long id, CancellationToken cancellationToken)
        {
            var world = RequireWorld();
            var locations = await world.GetLocationsAsync(_context.NovelId, cancellationToken);
            var location = locations.SingleOrDefault(item => item.Id == id)
                ?? throw new ArgumentException($"Location '{id}' does not exist.", nameof(id));
            var childCount = locations.Count(item => item.ParentLocationId == id);
            var relationCount = (await world.GetLocationRelationsAsync(_context.NovelId, cancellationToken))
                .Count(relation => relation.LocationAId == id || relation.LocationBId == id);
            if (childCount > 0 || relationCount > 0)
            {
                var impact = new Dictionary<string, int>();
                if (childCount > 0) impact["child_locations"] = childCount;
                if (relationCount > 0) impact["location_relations"] = relationCount;
                throw new InvalidOperationException($"地点“{location.Name}”存在关联数据，请先处理后再删除。impact={JsonSerializer.Serialize(impact, BridgeJson.SerializerOptions)}");
            }

            var meta = new Dictionary<string, object?> { ["id"] = location.Id, ["name"] = location.Name, ["type"] = "location" };
            var feedback = await RequestDeleteApprovalAsync("location", id, meta, cancellationToken);
            await world.DeleteLocationAsync(_context.NovelId, id, cancellationToken);
            return new DeleteRecordResult(meta, feedback);
        }

        private async ValueTask<DeleteRecordResult> DeleteLocationRelationAsync(long id, CancellationToken cancellationToken)
        {
            var world = RequireWorld();
            var relations = await world.GetLocationRelationsAsync(_context.NovelId, cancellationToken);
            var relation = relations.SingleOrDefault(item => item.Id == id)
                ?? throw new ArgumentException($"Location relation '{id}' does not exist.", nameof(id));
            var names = (await world.GetLocationsAsync(_context.NovelId, cancellationToken))
                .ToDictionary(item => item.Id, item => item.Name);
            var meta = new Dictionary<string, object?>
            {
                ["id"] = relation.Id,
                ["location_a"] = names.GetValueOrDefault(relation.LocationAId, string.Empty),
                ["location_b"] = names.GetValueOrDefault(relation.LocationBId, string.Empty),
                ["relation"] = relation.RelationType,
                ["type"] = "location_relation"
            };
            var feedback = await RequestDeleteApprovalAsync("location_relation", id, meta, cancellationToken);
            await world.DeleteLocationRelationAsync(_context.NovelId, id, cancellationToken);
            return new DeleteRecordResult(meta, feedback);
        }

        private async ValueTask<DeleteRecordResult> DeleteTimelineEntryAsync(long id, CancellationToken cancellationToken)
        {
            var planning = RequirePlanning();
            var entry = (await planning.GetTimelineEntriesAsync(_context.NovelId, 0, 0, cancellationToken))
                .SingleOrDefault(item => item.Id == id)
                ?? throw new ArgumentException($"Timeline entry '{id}' does not exist.", nameof(id));
            var meta = new Dictionary<string, object?> { ["id"] = entry.Id, ["title"] = entry.Title, ["type"] = "timeline_entry" };
            var feedback = await RequestDeleteApprovalAsync("timeline_entry", id, meta, cancellationToken);
            await planning.DeleteTimelineEntryAsync(_context.NovelId, id, cancellationToken);
            return new DeleteRecordResult(meta, feedback);
        }

        private async ValueTask<DeleteRecordResult> DeleteStoryArcAsync(long id, CancellationToken cancellationToken)
        {
            var planning = RequirePlanning();
            var arc = (await planning.GetStoryArcsAsync(_context.NovelId, cancellationToken))
                .SingleOrDefault(item => item.Id == id)
                ?? throw new ArgumentException($"Story arc '{id}' does not exist.", nameof(id));
            var nodeCount = (await planning.GetArcNodesAsync(_context.NovelId, 0, 0, cancellationToken)).Count(item => item.StoryArcId == id);
            if (nodeCount > 0)
            {
                throw new InvalidOperationException($"故事弧“{arc.Name}”存在 {nodeCount.ToString(CultureInfo.InvariantCulture)} 个弧节点，请先删除这些节点。 ");
            }

            var meta = new Dictionary<string, object?> { ["id"] = arc.Id, ["name"] = arc.Name, ["type"] = "story_arc" };
            var feedback = await RequestDeleteApprovalAsync("story_arc", id, meta, cancellationToken);
            await planning.DeleteStoryArcAsync(_context.NovelId, id, cancellationToken);
            return new DeleteRecordResult(meta, feedback);
        }

        private async ValueTask<DeleteRecordResult> DeleteArcNodeAsync(long id, CancellationToken cancellationToken)
        {
            var planning = RequirePlanning();
            var node = (await planning.GetArcNodesAsync(_context.NovelId, 0, 0, cancellationToken))
                .SingleOrDefault(item => item.Id == id)
                ?? throw new ArgumentException($"Arc node '{id}' does not exist.", nameof(id));
            var arcName = (await planning.GetStoryArcsAsync(_context.NovelId, cancellationToken))
                .SingleOrDefault(item => item.Id == node.StoryArcId)?.Name ?? string.Empty;
            var meta = new Dictionary<string, object?>
            {
                ["id"] = node.Id,
                ["title"] = node.Title,
                ["story_arc"] = arcName,
                ["type"] = "arc_node"
            };
            var feedback = await RequestDeleteApprovalAsync("arc_node", id, meta, cancellationToken);
            await planning.DeleteArcNodeAsync(_context.NovelId, id, cancellationToken);
            return new DeleteRecordResult(meta, feedback);
        }

        private async ValueTask<DeleteRecordResult> DeleteReaderPerspectiveEntryAsync(long id, CancellationToken cancellationToken)
        {
            var planning = RequirePlanning();
            var item = (await planning.GetReaderPerspectivesAsync(_context.NovelId, cancellationToken))
                .SingleOrDefault(entry => entry.Id == id)
                ?? throw new ArgumentException($"Reader perspective entry '{id}' does not exist.", nameof(id));
            var meta = new Dictionary<string, object?>
            {
                ["id"] = item.Id,
                ["entry_type"] = item.Type,
                ["planted_chapter"] = item.PlantedChapter,
                ["type"] = "reader_perspective_entry"
            };
            var feedback = await RequestDeleteApprovalAsync("reader_perspective_entry", id, meta, cancellationToken);
            await planning.DeleteReaderPerspectiveAsync(_context.NovelId, id, cancellationToken);
            return new DeleteRecordResult(meta, feedback);
        }

        private async ValueTask<DeleteRecordResult> DeletePreferenceAsync(long id, CancellationToken cancellationToken)
        {
            var preferences = await RequirePreferences().GetPreferencesAsync(_context.NovelId, cancellationToken);
            var item = preferences.Global.Concat(preferences.Novel).SingleOrDefault(preference => preference.Id == id)
                ?? throw new ArgumentException($"Preference '{id}' does not exist or is not visible in this novel.", nameof(id));
            var meta = new Dictionary<string, object?> { ["id"] = item.Id, ["category"] = item.Category, ["type"] = "preference" };
            var feedback = await RequestDeleteApprovalAsync("preference", id, meta, cancellationToken);
            await RequirePreferences().DeletePreferenceAsync(id, cancellationToken);
            return new DeleteRecordResult(meta, feedback);
        }

        private async ValueTask<string?> RequestDeleteApprovalAsync(
            string table,
            long id,
            IReadOnlyDictionary<string, object?> deleted,
            CancellationToken cancellationToken)
        {
            if (_approvals is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(_context.SessionId) ||
                _context.TurnId <= 0 ||
                string.IsNullOrWhiteSpace(_context.ToolId))
            {
                throw new InvalidOperationException("删除审批缺少会话上下文。");
            }

            var payload = JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>
                {
                    ["table"] = table,
                    ["id"] = id,
                    ["deleted"] = deleted
                },
                BridgeJson.SerializerOptions);
            var result = await _approvals.RequestApprovalAsync(
                new ToolApprovalRequestPayload(
                    _context.SessionId,
                    _context.TurnId,
                    _context.ToolId,
                    DeleteRecordToolName,
                    ApprovalType: "delete",
                    Payload: payload,
                    DisplayText: $"等待确认删除 {table}:{id.ToString(CultureInfo.InvariantCulture)}",
                    ActivityKind: "delete"),
                cancellationToken);

            if (!result.Approved)
            {
                var error = "审批未通过";
                if (!string.IsNullOrWhiteSpace(result.Feedback))
                {
                    error += $"。用户反馈：{result.Feedback.Trim()}";
                }

                throw new InvalidOperationException(error);
            }

            return string.IsNullOrWhiteSpace(result.Feedback) ? null : result.Feedback.Trim();
        }

        private IChapterContentService RequireChapters()
        {
            return _chapters ?? throw new InvalidOperationException("Chapter content service is not configured.");
        }

        private IPreferenceService RequirePreferences()
        {
            return _preferences ?? throw new InvalidOperationException("Preference service is not configured.");
        }

        private IWorldEntityService RequireWorld()
        {
            return _world ?? throw new InvalidOperationException("World entity service is not configured.");
        }

        private IPlanningService RequirePlanning()
        {
            return _planning ?? throw new InvalidOperationException("Planning service is not configured.");
        }

        private void EnsurePreferenceVisible(PreferenceItemPayload preference)
        {
            if (!preference.IsGlobal && preference.NovelId != _context.NovelId)
            {
                throw new InvalidOperationException($"Preference '{preference.Id}' is not visible in this novel.");
            }
        }

        private bool RawArgumentsHasProperty(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(_context.RawArgumentsJson))
            {
                return false;
            }

            using var document = JsonDocument.Parse(_context.RawArgumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(propertyName, out _);
        }

        private static void EnsureBatch<T>(IReadOnlyCollection<T>? items, int min, int max, string name)
        {
            if (items is null || items.Count < min || items.Count > max)
            {
                throw new ArgumentOutOfRangeException(name, items?.Count ?? 0, $"Batch size must be between {min} and {max}.");
            }
        }

        private static Dictionary<string, object?> PageResult<T>(
            string itemName,
            IReadOnlyList<T> items,
            int page,
            int size,
            IReadOnlyDictionary<string, object?>? extra = null)
        {
            var (normalizedPage, normalizedSize) = NormalizePage(page, size);
            var total = items.Count;
            var pageItems = items
                .Skip((normalizedPage - 1) * normalizedSize)
                .Take(normalizedSize)
                .ToArray();
            var result = new Dictionary<string, object?>
            {
                ["page"] = normalizedPage,
                ["size"] = normalizedSize,
                ["total"] = total,
                ["total_pages"] = total == 0 ? 0 : (int)Math.Ceiling((double)total / normalizedSize),
                ["truncated"] = total > normalizedPage * normalizedSize,
                [itemName] = pageItems
            };
            if (extra is not null)
            {
                foreach (var (key, value) in extra)
                {
                    result[key] = value;
                }
            }

            return result;
        }

        private static (int Page, int Size) NormalizePage(int page, int size)
        {
            var normalizedPage = page < 1 ? 1 : page;
            var normalizedSize = size is < 1 or > MaxPageSize ? DefaultPageSize : size;
            return (normalizedPage, normalizedSize);
        }

        private static string NormalizeOptionalSearch(string? value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Any(ch => char.IsControl(ch)))
            {
                throw new ArgumentException("Search text must not contain control characters.", nameof(value));
            }

            return normalized;
        }

        private static string NormalizeMode(string value, IReadOnlyCollection<string> allowed)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = allowed.First();
            }

            if (!allowed.Contains(normalized, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Value must be one of: {string.Join(", ", allowed)}.", nameof(value));
            }

            return normalized;
        }

        private static long[] NormalizeIds(long[]? ids, string name)
        {
            if (ids is null || ids.Length == 0)
            {
                throw new ArgumentException("At least one id is required.", name);
            }

            if (ids.Any(id => id <= 0))
            {
                throw new ArgumentOutOfRangeException(name, "All identifiers must be positive.");
            }

            return ids.Distinct().OrderBy(id => id).ToArray();
        }

        private static bool IsBlank(string? value)
        {
            return string.IsNullOrEmpty(value);
        }

        private static object? ParseJsonField(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(raw);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return raw;
            }
        }

        private static void EnsureUniqueLocationPairs(IReadOnlyList<CreateLocationRelationItem> relations)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var relation in relations)
            {
                if (relation.LocationAId <= 0 || relation.LocationBId <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(relations), "Location ids must be positive.");
                }

                if (relation.LocationAId == relation.LocationBId)
                {
                    throw new ArgumentException("A location relation cannot point to the same location.", nameof(relations));
                }

                var a = Math.Min(relation.LocationAId, relation.LocationBId);
                var b = Math.Max(relation.LocationAId, relation.LocationBId);
                if (!seen.Add(FormattableString.Invariant($"{a}:{b}")))
                {
                    throw new ArgumentException($"Duplicate location relation pair: {a} and {b}.", nameof(relations));
                }
            }
        }

        private static string TruncateRunes(string value, int maxRunes)
        {
            var runes = value.EnumerateRunes().ToArray();
            return runes.Length <= maxRunes
                ? value
                : string.Concat(runes.Take(maxRunes)) + "...";
        }

        private static string FormatPreferences(
            IReadOnlyList<PreferenceItemPayload> global,
            IReadOnlyList<PreferenceItemPayload> novel)
        {
            if (global.Count == 0 && novel.Count == 0)
            {
                return "暂无创作偏好。";
            }

            var parts = new List<string> { "### 创作偏好" };
            if (global.Count > 0)
            {
                parts.Add("\n#### 全局偏好（所有小说生效）");
                parts.AddRange(global.Select(item => $"- 【{item.Category}】{item.Content} [preference_id:{item.Id.ToString(CultureInfo.InvariantCulture)}]"));
            }

            if (novel.Count > 0)
            {
                parts.Add("\n#### 本小说专属偏好");
                parts.AddRange(novel.Select(item => $"- 【{item.Category}】{item.Content} [preference_id:{item.Id.ToString(CultureInfo.InvariantCulture)}]"));
            }

            return string.Join("\n", parts);
        }

        private static string FormatCharacterRelations(
            IReadOnlyList<CharacterRelationPayload> relations,
            IReadOnlyDictionary<long, string> names)
        {
            if (relations.Count == 0)
            {
                return "暂无关系数据。";
            }

            var lines = new List<string> { "### 角色关系" };
            foreach (var group in relations.GroupBy(relation => relation.SourceCharacterId).OrderBy(group => group.Key))
            {
                var edges = group.Select(relation =>
                {
                    var target = names.GetValueOrDefault(relation.TargetCharacterId, $"角色{relation.TargetCharacterId.ToString(CultureInfo.InvariantCulture)}");
                    var edge = $"→ {target}：{relation.RelationDescribe} [relation_id:{relation.Id.ToString(CultureInfo.InvariantCulture)}]";
                    return string.IsNullOrWhiteSpace(relation.Description) ? edge : edge + $"（{relation.Description}）";
                });
                var source = names.GetValueOrDefault(group.Key, $"角色{group.Key.ToString(CultureInfo.InvariantCulture)}");
                lines.Add($"- {source} {string.Join("、", edges)}");
            }

            return string.Join("\n", lines);
        }

        private static string FormatLocationDetail(
            LocationPayload location,
            string parentName,
            IReadOnlyList<LocationPayload> children,
            IReadOnlyList<LocationRelationPayload> relations,
            IReadOnlyDictionary<long, string> names)
        {
            var parts = new List<string> { $"### {location.Name} [location_id:{location.Id.ToString(CultureInfo.InvariantCulture)}]" };
            if (!string.IsNullOrWhiteSpace(location.LocationType)) parts.Add($"- 类型：{location.LocationType}");
            if (!string.IsNullOrWhiteSpace(parentName)) parts.Add($"- 父地点：{parentName}");
            if (!string.IsNullOrWhiteSpace(location.Description)) parts.Add($"- 描述：{location.Description}");
            if (ParseJsonField(location.DetailJson) is { } detail) parts.Add($"- 结构化信息：{JsonSerializer.Serialize(detail, BridgeJson.SerializerOptions)}");
            if (ParseJsonField(location.Tags) is { } tags) parts.Add($"- 标签：{JsonSerializer.Serialize(tags, BridgeJson.SerializerOptions)}");

            if (children.Count > 0)
            {
                parts.Add($"\n#### 子地点（{children.Count.ToString(CultureInfo.InvariantCulture)}个）");
                parts.AddRange(children.Select(child => $"- {child.Name} [location_id:{child.Id.ToString(CultureInfo.InvariantCulture)}]"));
            }

            if (relations.Count > 0)
            {
                parts.Add("\n#### 连通");
                foreach (var relation in relations)
                {
                    var a = names.GetValueOrDefault(relation.LocationAId, relation.LocationAId.ToString(CultureInfo.InvariantCulture));
                    var b = names.GetValueOrDefault(relation.LocationBId, relation.LocationBId.ToString(CultureInfo.InvariantCulture));
                    var line = $"- {a} - {b} [relation_id:{relation.Id.ToString(CultureInfo.InvariantCulture)}]：{relation.RelationType}";
                    parts.Add(string.IsNullOrWhiteSpace(relation.Description) ? line : line + $"（{relation.Description}）");
                }
            }

            return string.Join("\n", parts);
        }

        private static string FormatLocationNetwork(
            IReadOnlyList<LocationPayload> roots,
            IReadOnlyList<LocationRelationPayload> relations,
            IReadOnlyDictionary<long, int> childCounts)
        {
            var parts = new List<string> { "### 大地图" };
            var rootDescriptions = roots.Select(root =>
            {
                var childCount = childCounts.GetValueOrDefault(root.Id);
                return childCount > 0
                    ? $"{root.Name} [location_id:{root.Id.ToString(CultureInfo.InvariantCulture)}]（含{childCount.ToString(CultureInfo.InvariantCulture)}个子地点）"
                    : $"{root.Name} [location_id:{root.Id.ToString(CultureInfo.InvariantCulture)}]";
            });
            parts.Add($"\n共 {roots.Count.ToString(CultureInfo.InvariantCulture)} 个根节点：{string.Join("、", rootDescriptions)}。");

            if (relations.Count == 0)
            {
                parts.Add("\n暂无根节点间的连通关系。");
                return string.Join("\n", parts);
            }

            var names = roots.ToDictionary(root => root.Id, root => root.Name);
            parts.Add(string.Empty);
            foreach (var relation in relations)
            {
                var line = $"- {names[relation.LocationAId]} - {names[relation.LocationBId]} [relation_id:{relation.Id.ToString(CultureInfo.InvariantCulture)}]：{relation.RelationType}";
                parts.Add(string.IsNullOrWhiteSpace(relation.Description) ? line : line + $"（{relation.Description}）");
            }

            return string.Join("\n", parts);
        }

        private static string FormatTimelineContext(
            IReadOnlyList<ChapterPlanPayload> plans,
            IReadOnlyList<TimelineEntryPayload> entries,
            int currentChapter)
        {
            var parts = new List<string> { "### 章节计划" };
            var planMap = plans.ToDictionary(plan => plan.Scope, plan => string.IsNullOrWhiteSpace(plan.Content) ? "暂无" : plan.Content);
            foreach (var scope in new[] { "next", "near", "far" })
            {
                parts.Add($"- **{scope}**：{planMap.GetValueOrDefault(scope, "暂无")}");
            }

            var history = entries.Where(entry => entry.TargetChapter < currentChapter).OrderByDescending(entry => entry.TargetChapter).ThenBy(entry => entry.Id).Take(10).ToArray();
            var anomalies = entries.Where(entry =>
                    (entry.Status == "pending" && entry.TargetChapter < currentChapter) ||
                    (entry.Status == "resolved" && entry.TargetChapter >= currentChapter))
                .OrderBy(entry => entry.TargetChapter)
                .ThenBy(entry => entry.Id)
                .ToArray();
            var anomalyIds = anomalies.Select(entry => entry.Id).ToHashSet();
            var future = entries.Where(entry => entry.TargetChapter >= currentChapter).OrderBy(entry => entry.TargetChapter).ThenBy(entry => entry.Id).ToArray();

            if (history.Any(entry => !anomalyIds.Contains(entry.Id)))
            {
                parts.Add($"\n### 近期历史（最近{history.Length.ToString(CultureInfo.InvariantCulture)}条，截至第{currentChapter.ToString(CultureInfo.InvariantCulture)}章）");
                parts.AddRange(history.Where(entry => !anomalyIds.Contains(entry.Id)).Select(entry => $"- {CategoryLabel(entry.Category)} {entry.Title} [entry_id:{entry.Id.ToString(CultureInfo.InvariantCulture)}] - 目标第{entry.TargetChapter.ToString(CultureInfo.InvariantCulture)}章 - {StatusLabel(entry.Status)}"));
            }

            if (anomalies.Length > 0)
            {
                parts.Add("\n### 状态异常");
                foreach (var entry in anomalies)
                {
                    var reason = entry.Status == "pending"
                        ? $"目标第{entry.TargetChapter.ToString(CultureInfo.InvariantCulture)}章但仍为 pending"
                        : $"目标第{entry.TargetChapter.ToString(CultureInfo.InvariantCulture)}章但已标记 resolved";
                    parts.Add($"- {CategoryLabel(entry.Category)} {entry.Title} [entry_id:{entry.Id.ToString(CultureInfo.InvariantCulture)}] - {reason}");
                }
            }

            if (future.Any(entry => entry.Status != "resolved"))
            {
                parts.Add($"\n### 未来条目（{future.Count(entry => entry.Status != "resolved").ToString(CultureInfo.InvariantCulture)}条）");
                parts.AddRange(future.Where(entry => entry.Status != "resolved").Select(entry => $"- {CategoryLabel(entry.Category)} {entry.Title} [entry_id:{entry.Id.ToString(CultureInfo.InvariantCulture)}] 第{entry.TargetChapter.ToString(CultureInfo.InvariantCulture)}章 [重要度:{entry.Importance.ToString(CultureInfo.InvariantCulture)}]"));
            }

            if (history.Length == 0 && anomalies.Length == 0 && future.Length == 0)
            {
                parts.Add("\n暂无伏笔或用户指令。");
            }

            return string.Join("\n", parts);
        }

        private static string FormatTimelineFull(IReadOnlyList<TimelineEntryPayload> entries)
        {
            if (entries.Count == 0)
            {
                return "暂无伏笔或用户指令。";
            }

            var lines = new List<string> { $"### 伏笔与用户指令（{entries.Count.ToString(CultureInfo.InvariantCulture)}条）" };
            lines.AddRange(entries.Select(entry => $"- {CategoryLabel(entry.Category)} {entry.Title} [entry_id:{entry.Id.ToString(CultureInfo.InvariantCulture)}] 第{entry.TargetChapter.ToString(CultureInfo.InvariantCulture)}章 - {StatusLabel(entry.Status)} [重要度:{entry.Importance.ToString(CultureInfo.InvariantCulture)}]"));
            return string.Join("\n", lines);
        }

        private static string FormatStoryArcsContext(
            IReadOnlyList<StoryArcPayload> arcs,
            IReadOnlyList<ArcNodePayload> nodes,
            int currentChapter)
        {
            if (arcs.Count == 0)
            {
                return "暂无叙事弧线。";
            }

            var parts = new List<string> { "### 叙事弧线" };
            foreach (var arc in arcs.OrderByDescending(arc => arc.Importance).ThenBy(arc => arc.Id))
            {
                parts.Add($"\n#### {arc.Name} [arc_id:{arc.Id.ToString(CultureInfo.InvariantCulture)}] ({arc.ArcType}) - {arc.Status} {ImportanceStars(arc.Importance)}".TrimEnd());
                if (!string.IsNullOrWhiteSpace(arc.Description)) parts.Add(arc.Description);
                if (arc.Status == "paused" && !string.IsNullOrWhiteSpace(arc.ReactivateAt)) parts.Add($"恢复条件：{arc.ReactivateAt}");
                var arcNodes = nodes.Where(node => node.StoryArcId == arc.Id).OrderBy(node => node.TargetChapter).ThenBy(node => node.Id).ToArray();
                if (arcNodes.Length == 0)
                {
                    parts.Add("（暂无节点）");
                    continue;
                }

                var before = arcNodes.Where(node => node.TargetChapter < currentChapter).TakeLast(10).ToArray();
                var anomalies = arcNodes.Where(node => node.Status == "pending" && node.TargetChapter < currentChapter).ToArray();
                var after = arcNodes.Where(node => node.TargetChapter >= currentChapter).ToArray();
                if (before.Length > 0)
                {
                    parts.Add($"##### 近期（截至第{currentChapter.ToString(CultureInfo.InvariantCulture)}章）");
                    parts.AddRange(before.Select(node => FormatArcNodeLine(node, showStatus: false)));
                }

                if (anomalies.Length > 0)
                {
                    parts.Add("##### 异常");
                    parts.AddRange(anomalies.Select(node => $"- {node.Title} [node_id:{node.Id.ToString(CultureInfo.InvariantCulture)}] - 目标第{node.TargetChapter.ToString(CultureInfo.InvariantCulture)}章但仍 pending"));
                }

                if (after.Length > 0)
                {
                    parts.Add("##### 未来");
                    parts.AddRange(after.Select(node => FormatArcNodeLine(node, showStatus: false)));
                }
            }

            return string.Join("\n", parts);
        }

        private static string FormatStoryArcsFull(
            IReadOnlyList<StoryArcPayload> arcs,
            IReadOnlyList<ArcNodePayload> nodes)
        {
            if (arcs.Count == 0)
            {
                return "暂无叙事弧线。";
            }

            var nodeGroups = nodes.GroupBy(node => node.StoryArcId).ToDictionary(group => group.Key, group => group.OrderBy(node => node.TargetChapter).ThenBy(node => node.Id).ToArray());
            var parts = new List<string> { "### 叙事弧线" };
            foreach (var arc in arcs)
            {
                parts.Add($"\n#### {arc.Name} [arc_id:{arc.Id.ToString(CultureInfo.InvariantCulture)}] ({arc.ArcType}) - {arc.Status} {ImportanceStars(arc.Importance)}".TrimEnd());
                if (!string.IsNullOrWhiteSpace(arc.Description)) parts.Add(arc.Description);
                if (arc.Status == "paused" && !string.IsNullOrWhiteSpace(arc.ReactivateAt)) parts.Add($"恢复条件：{arc.ReactivateAt}");
                if (!nodeGroups.TryGetValue(arc.Id, out var arcNodes) || arcNodes.Length == 0)
                {
                    parts.Add("（暂无节点）");
                    continue;
                }

                parts.AddRange(arcNodes.Select(node => FormatArcNodeLine(node, showStatus: true)));
            }

            return string.Join("\n", parts);
        }

        private static string FormatReaderPerspective(
            IReadOnlyList<ReaderPerspectivePayload> known,
            IReadOnlyList<ReaderPerspectivePayload> suspense,
            IReadOnlyList<ReaderPerspectivePayload> misconception)
        {
            var sections = new List<string>();
            if (known.Count > 0)
            {
                sections.Add("### 已知信息\n" + string.Join("\n", known.Select(item => $"- {item.Content} [第{item.PlantedChapter.ToString(CultureInfo.InvariantCulture)}章起] [entry_id:{item.Id.ToString(CultureInfo.InvariantCulture)}]")));
            }

            if (suspense.Count > 0)
            {
                sections.Add("### 活跃悬念\n" + string.Join("\n", suspense.Select(item => $"- {item.Content}（第{item.PlantedChapter.ToString(CultureInfo.InvariantCulture)}章种下） [entry_id:{item.Id.ToString(CultureInfo.InvariantCulture)}]")));
            }

            if (misconception.Count > 0)
            {
                sections.Add("### 读者误知\n" + string.Join("\n", misconception.Select(item =>
                {
                    var truth = string.IsNullOrWhiteSpace(item.RelatedTruth) ? string.Empty : $" -> 实际：{item.RelatedTruth}";
                    return $"- {item.Content}{truth} [entry_id:{item.Id.ToString(CultureInfo.InvariantCulture)}]";
                })));
            }

            return sections.Count == 0 ? "暂无读者认知数据。" : string.Join("\n\n", sections);
        }

        private static string FormatArcNodeLine(ArcNodePayload node, bool showStatus)
        {
            var status = node.ActualChapter > 0
                ? $" - 第{node.ActualChapter.ToString(CultureInfo.InvariantCulture)}章完成"
                : node.TargetChapter > 0
                    ? $" - 目标第{node.TargetChapter.ToString(CultureInfo.InvariantCulture)}章"
                    : string.Empty;
            if (showStatus && node.Status == "abandoned")
            {
                status += " - 已废弃";
            }

            return $"- {node.Title} [node_id:{node.Id.ToString(CultureInfo.InvariantCulture)}]{status}";
        }

        private static string CategoryLabel(string category)
        {
            return category switch
            {
                "foreshadowing" => "【伏笔】",
                "user_directive" => "【用户指令】",
                _ => "【" + category + "】"
            };
        }

        private static string StatusLabel(string status)
        {
            return status switch
            {
                "resolved" => "已回收",
                "abandoned" => "已废弃",
                _ => status
            };
        }

        private static string ImportanceStars(int importance)
        {
            if (importance <= 0)
            {
                return string.Empty;
            }

            return new string('*', Math.Min(importance, 5));
        }

        private sealed record BatchIdsResult(
            [property: JsonPropertyName("ids")] IReadOnlyList<long> Ids,
            [property: JsonPropertyName("count")] int Count);

        private sealed record IdResult([property: JsonPropertyName("id")] long Id);

        private sealed record IdActionResult(
            [property: JsonPropertyName("id")] long Id,
            [property: JsonPropertyName("action")] string Action);

        private sealed record PreferenceIdResult([property: JsonPropertyName("preference_id")] long PreferenceId);

        private sealed record ScopeResult([property: JsonPropertyName("scope")] string Scope);

        private sealed record ReaderPerspectiveUpdateResult(
            [property: JsonPropertyName("id")] long Id,
            [property: JsonPropertyName("revealed_chapter")] int RevealedChapter);

        private sealed record ContentResult([property: JsonPropertyName("content")] string Content);

        private sealed record ContentWithCountsResult(
            [property: JsonPropertyName("content")] string Content,
            [property: JsonPropertyName("counts")] IReadOnlyDictionary<string, int> Counts);

        private sealed record DeleteRecordResult(
            [property: JsonPropertyName("deleted")] IReadOnlyDictionary<string, object?> Deleted,
            [property: JsonPropertyName("feedback")]
            [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            string? Feedback);

        private sealed record ChapterListItem(
            [property: JsonPropertyName("id")] long Id,
            [property: JsonPropertyName("chapter_number")] int ChapterNumber,
            [property: JsonPropertyName("title")] string Title,
            [property: JsonPropertyName("word_count")] int WordCount,
            [property: JsonPropertyName("summary")] string Summary,
            [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
            [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

        private sealed record CharacterToolItemResult(
            [property: JsonPropertyName("id")] long Id,
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("description")] string Description,
            [property: JsonPropertyName("personality")] object? Personality,
            [property: JsonPropertyName("abilities")] object? Abilities);

        private sealed record LocationListItem(
            [property: JsonPropertyName("id")] long Id,
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("location_type")] string LocationType,
            [property: JsonPropertyName("description")] string Description,
            [property: JsonPropertyName("tags")] object? Tags);

        private sealed record CreatePreferenceItem(
            [property: JsonPropertyName("is_global")] bool IsGlobal,
            [property: JsonPropertyName("category")] string Category,
            [property: JsonPropertyName("content")] string Content);

        private sealed record CreateCharacterItem(
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("description")] string? Description,
            [property: JsonPropertyName("personality")] string? Personality,
            [property: JsonPropertyName("abilities")] string? Abilities);

        private sealed record CreateLocationItem(
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("location_type")] string? LocationType,
            [property: JsonPropertyName("description")] string? Description,
            [property: JsonPropertyName("detail_json")] string? DetailJson,
            [property: JsonPropertyName("tags")] string? Tags,
            [property: JsonPropertyName("parent_location_id")] long? ParentLocationId);

        private sealed record CreateLocationRelationItem(
            [property: JsonPropertyName("location_a_id")] long LocationAId,
            [property: JsonPropertyName("location_b_id")] long LocationBId,
            [property: JsonPropertyName("relation_type")] string RelationType,
            [property: JsonPropertyName("description")] string? Description);

        private sealed record CreateTimelineEntryItem(
            [property: JsonPropertyName("category")] string Category,
            [property: JsonPropertyName("title")] string Title,
            [property: JsonPropertyName("content")] string? Content,
            [property: JsonPropertyName("detail_json")] string? DetailJson,
            [property: JsonPropertyName("target_chapter")] int TargetChapter,
            [property: JsonPropertyName("importance")] int Importance,
            [property: JsonPropertyName("source_chapter_id")] long SourceChapterId,
            [property: JsonPropertyName("source")] string? Source);

        private sealed record CreateStoryArcItem(
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("arc_type")] string ArcType,
            [property: JsonPropertyName("description")] string? Description,
            [property: JsonPropertyName("importance")] int Importance);

        private sealed record CreateArcNodeItem(
            [property: JsonPropertyName("arc_id")] long ArcId,
            [property: JsonPropertyName("title")] string Title,
            [property: JsonPropertyName("description")] string? Description,
            [property: JsonPropertyName("target_chapter")] int TargetChapter);

        private sealed record CreateReaderPerspectiveEntryItem(
            [property: JsonPropertyName("type")] string Type,
            [property: JsonPropertyName("content")] string Content,
            [property: JsonPropertyName("planted_chapter")] int PlantedChapter,
            [property: JsonPropertyName("related_truth")] string? RelatedTruth);
    }
}
