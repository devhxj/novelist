using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Agent;

public sealed partial class NovelistMafToolRegistry
{
    private partial void AddReferenceTools(List<AIFunction> tools, NovelistMafToolContext context)
    {
        if (_referenceAnchors is not null)
        {
            var referenceTools = new ReferenceMafTools(_referenceAnchors, context, _serializerOptions);
            referenceTools.AddAvailableTools(tools);
        }

        if (_referenceDrafts is not null)
        {
            var draftTools = new ReferenceDraftMafTools(_referenceDrafts, context, _serializerOptions);
            draftTools.AddAvailableTools(tools);
        }

        if (_referenceStyleProfiles is not null)
        {
            var styleProfileTools = new ReferenceStyleProfileMafTools(_referenceStyleProfiles, context, _serializerOptions);
            styleProfileTools.AddAvailableTools(tools);
        }
    }

    private sealed class ReferenceMafTools
    {
        private const string GetAnchorsDescription = "列出当前小说可访问的已导入参考锚定书籍。novel_id 由运行时注入，不需要也不能传入；不能导入新来源，不能读取任意文件。";
        private const string SearchMaterialsDescription = "按 story context 和可选 style filters 搜索已导入且受 license/visibility 过滤的参考语料库。返回材料 id、标签、来源、文本和 score_components；style_profile_ids 只影响受授权材料排序和 style-risk 解释，不能绕过来源/许可边界。用于给蓝图 beat 绑定材料，不直接写章节，不能导入新来源，不能读取任意文件。";
        private const string AdaptMaterialDescription = "预览参考材料改写。只允许基于 material_id、slot_values、scene_facts 和 max_rewrite_level 生成候选，不直接写章节。";
        private const string AuditReuseDescription = "审计参考材料复用候选。纯检查工具，不写章节，不保存正文。";

        private readonly IReferenceAnchorService _referenceAnchors;
        private readonly NovelistMafToolContext _context;
        private readonly JsonSerializerOptions _serializerOptions;

        public ReferenceMafTools(
            IReferenceAnchorService referenceAnchors,
            NovelistMafToolContext context,
            JsonSerializerOptions serializerOptions)
        {
            _referenceAnchors = referenceAnchors;
            _context = context;
            _serializerOptions = serializerOptions;
        }

        public void AddAvailableTools(List<AIFunction> tools)
        {
            tools.Add(CreateFunction(nameof(GetReferenceAnchorsAsync), "get_reference_anchors", GetAnchorsDescription));
            tools.Add(CreateFunction(nameof(SearchReferenceMaterialsAsync), "search_reference_materials", SearchMaterialsDescription));
            tools.Add(CreateFunction(nameof(AdaptReferenceMaterialAsync), "adapt_reference_material", AdaptMaterialDescription));
            tools.Add(CreateFunction(nameof(AuditReferenceReuseAsync), "audit_reference_reuse", AuditReuseDescription));
        }

        private AIFunction CreateFunction(string methodName, string toolName, string description)
        {
            var method = typeof(ReferenceMafTools).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(ReferenceMafTools).FullName, methodName);
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

        [Description(GetAnchorsDescription)]
        private ValueTask<IReadOnlyList<ReferenceAnchorPayload>> GetReferenceAnchorsAsync(CancellationToken cancellationToken = default)
        {
            return _referenceAnchors.GetAnchorsAsync(_context.NovelId, cancellationToken);
        }

        [Description(SearchMaterialsDescription)]
        private ValueTask<PageResultPayload<ReferenceMaterialPayload>> SearchReferenceMaterialsAsync(
            [Description("参考锚定 id 列表。空数组表示搜索当前小说下全部参考锚定")]
            long[]? anchor_ids = null,
            [Description("搜索查询，描述需要的情绪、叙事功能、场景压力或句料特征")]
            string? query = null,
            [Description("材料类型过滤：chapter / paragraph / sentence / passage / scene / beat / dialogue_exchange / action_afterbeat / image_motif / hook / payoff / transition")]
            string[]? material_types = null,
            [Description("情绪标签过滤")]
            string[]? emotion_tags = null,
            [Description("功能标签过滤，例如 interiority / environment / narration")]
            string[]? function_tags = null,
            [Description("视角标签过滤")]
            string[]? pov_tags = null,
            [Description("写作手法标签过滤")]
            string[]? technique_tags = null,
            [Description("叙事职责过滤，例如 interiority / external_evidence / transition / sensory")]
            string[]? narrative_duties = null,
            [Description("情绪转变过滤，例如 controlled->heightened；匹配材料 emotion_tag")]
            string[]? emotion_transitions = null,
            [Description("文体/执行职责过滤，例如 source_backed_detail / external_evidence / subtext / delayed_reaction")]
            string[]? prose_duties = null,
            [Description("可选 style profile id 列表；只用于受授权参考材料的 style-aware 排序，不绕过 license/visibility 过滤")]
            long[]? style_profile_ids = null,
            [Description("可选 style 维度过滤，例如 dialogue_ratio / sensory_ratio / transition_ratio / hook_marker_ratio")]
            string[]? style_dimensions = null,
            [Description("可选 imitation intensity：diagnostic_only / loose / moderate / strong")]
            string? imitation_intensity = null,
            [Description("页码，默认 1")]
            int page = 0,
            [Description("每页数量，默认 10，最大 20")]
            int size = 0,
            CancellationToken cancellationToken = default)
        {
            return _referenceAnchors.SearchMaterialsAsync(
                new SearchReferenceMaterialsPayload(
                    _context.NovelId,
                    anchor_ids ?? [],
                    query ?? string.Empty,
                    material_types ?? [],
                    emotion_tags ?? [],
                    function_tags ?? [],
                    pov_tags ?? [],
                    technique_tags ?? [],
                    page <= 0 ? 1 : page,
                    Math.Clamp(size <= 0 ? 10 : size, 1, 20),
                    narrative_duties,
                    emotion_transitions,
                    prose_duties,
                    ArchiveFilter: null,
                    StyleProfileIds: style_profile_ids,
                    StyleDimensions: style_dimensions,
                    ImitationIntensity: imitation_intensity),
                cancellationToken);
        }

        [Description(AdaptMaterialDescription)]
        private ValueTask<AdaptReferenceMaterialResultPayload> AdaptReferenceMaterialAsync(
            [Description("参考材料 id")]
            string material_id,
            [Description("声明式槽位替换，只允许替换材料中已识别的 slot")]
            ReferenceSlotValuePayload[]? slot_values = null,
            [Description("最大允许改写等级，默认 L1")]
            string? max_rewrite_level = null,
            [Description("当前场景已批准事实，用于拒绝新增不支持事实")]
            string[]? scene_facts = null,
            CancellationToken cancellationToken = default)
        {
            return _referenceAnchors.AdaptMaterialAsync(
                new AdaptReferenceMaterialPayload(
                    _context.NovelId,
                    material_id,
                    slot_values ?? [],
                    string.IsNullOrWhiteSpace(max_rewrite_level) ? ReferenceRewriteLevels.L1 : max_rewrite_level,
                    scene_facts ?? []),
                cancellationToken);
        }

        [Description(AuditReuseDescription)]
        private ValueTask<ReferenceReuseAuditPayload> AuditReferenceReuseAsync(
            [Description("参考材料 id")]
            string material_id,
            [Description("待审计候选文本")]
            string candidate_text,
            [Description("最大允许改写等级，默认 L1")]
            string? max_rewrite_level = null,
            [Description("当前场景已批准事实")]
            string[]? scene_facts = null,
            CancellationToken cancellationToken = default)
        {
            return _referenceAnchors.AuditCandidateAsync(
                new AuditReferenceReusePayload(
                    _context.NovelId,
                    material_id,
                    candidate_text,
                    string.IsNullOrWhiteSpace(max_rewrite_level) ? ReferenceRewriteLevels.L1 : max_rewrite_level,
                    scene_facts ?? []),
                cancellationToken);
        }
    }

    private sealed class ReferenceStyleProfileMafTools
    {
        private const string GetProfilesDescription = "列出当前小说已存在的 reference style profiles。novel_id 由运行时注入；只读工具，不能构建 style profile，不能导入 style profile，不能审批 style contract，不能写章节。";
        private const string GetProfileDescription = "读取单个 reference style profile 的结构化 features 和 evidence spans。novel_id 由运行时注入；只读工具，不返回源文本，不能构建 style profile，不能导入 style profile，不能审批 style contract，不能写章节。";

        private readonly IReferenceStyleProfileService _styleProfiles;
        private readonly NovelistMafToolContext _context;
        private readonly JsonSerializerOptions _serializerOptions;

        public ReferenceStyleProfileMafTools(
            IReferenceStyleProfileService styleProfiles,
            NovelistMafToolContext context,
            JsonSerializerOptions serializerOptions)
        {
            _styleProfiles = styleProfiles;
            _context = context;
            _serializerOptions = serializerOptions;
        }

        public void AddAvailableTools(List<AIFunction> tools)
        {
            tools.Add(CreateFunction(nameof(GetReferenceStyleProfilesAsync), "get_reference_style_profiles", GetProfilesDescription));
            tools.Add(CreateFunction(nameof(GetReferenceStyleProfileAsync), "get_reference_style_profile", GetProfileDescription));
        }

        private AIFunction CreateFunction(string methodName, string toolName, string description)
        {
            var method = typeof(ReferenceStyleProfileMafTools).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(ReferenceStyleProfileMafTools).FullName, methodName);
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

        [Description(GetProfilesDescription)]
        private ValueTask<IReadOnlyList<ReferenceStyleProfileSummaryPayload>> GetReferenceStyleProfilesAsync(
            [Description("是否包含 archived style profiles，默认 false")]
            bool include_archived = false,
            CancellationToken cancellationToken = default)
        {
            return _styleProfiles.GetStyleProfilesAsync(
                new GetReferenceStyleProfilesPayload(_context.NovelId, include_archived),
                cancellationToken);
        }

        [Description(GetProfileDescription)]
        private ValueTask<ReferenceStyleProfilePayload?> GetReferenceStyleProfileAsync(
            [Description("style profile id")]
            long profile_id,
            CancellationToken cancellationToken = default)
        {
            if (profile_id <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(profile_id), profile_id, "profile_id must be positive.");
            }

            return _styleProfiles.GetStyleProfileAsync(_context.NovelId, profile_id, cancellationToken);
        }
    }

    private sealed class ReferenceDraftMafTools
    {
        private const string GenerateBlueprintDescription = "生成当前小说某章节的 reference-anchored Chapter Narrative Blueprint。只生成结构化蓝图，不生成正文；下一步必须调用 review_reference_chapter_blueprint。";
        private const string ReviewBlueprintDescription = "评审由 generate_reference_chapter_blueprint 生成或修订后的 reference-anchored 蓝图。纯检查工具，不静默修订蓝图；失败时调用 revise_reference_chapter_blueprint 后重新评审，通过后才能调用 approve_reference_chapter_blueprint。";
        private const string ReviseBlueprintDescription = "按字段路径修订蓝图，并使已批准 review/material links 失效；修订后必须再调用 review_reference_chapter_blueprint。";
        private const string ApproveBlueprintDescription = "批准已通过 review_reference_chapter_blueprint 的蓝图。只有批准后的蓝图才能调用 bind_reference_blueprint_materials 并生成候选。";
        private const string BindMaterialsDescription = "为已通过 approve_reference_chapter_blueprint 批准的蓝图 beat 返回参考材料候选；默认不自动选中，进入 generate_reference_anchored_draft 前需显式 select_top_candidate=true。";
        private const string GenerateDraftDescription = "从已按 generate_reference_chapter_blueprint -> review_reference_chapter_blueprint -> approve_reference_chapter_blueprint -> bind_reference_blueprint_materials 且 select_top_candidate=true 准备好的 approved/material_bound 蓝图生成候选段落；只返回 candidates，随后调用 audit_reference_anchored_draft，不调用 SaveContent，不直接写章节。";
        private const string AuditDraftDescription = "按 candidate_id 审计 generate_reference_anchored_draft 生成的 reference-anchored 草稿候选。纯检查工具，不写章节。";
        private const string GetDraftAuditsDescription = "只读读取已持久化的 reference-anchored 草稿审计报告；只返回审计元数据、candidate_id、结构化 findings 和 required_action，不返回候选正文，不返回源文本，不返回 prompt，不能批准候选，不能恢复流程，不能写章节。";
        private const string GetStyleAuditFindingsDescription = "只读读取已持久化草稿审计中的 style/source-leak findings；只返回审计 id、candidate_id、risk_type、message 和 required_action，不返回候选正文，不返回源文本，不返回 prompt，不能批准候选，不能恢复流程，不能写章节。";
        private const string StartOrchestrationDescription = "启动默认 reference orchestration 候选流程，只检索已导入且受 license/visibility 过滤的语料。novel_id 由运行时注入；agent 只能提供章节目标、已知/禁止事实和 corpus policy，不能导入新来源，不能读取任意文件，不能确认 source/fact，不能批准 blueprint revision，不能批准 final insertion，这些决策必须由作者完成。";
        private const string GetOrchestrationRunsDescription = "列出当前小说的 reference orchestration 运行历史，可按章节过滤；只读工具，不批准、不恢复、不写章节。";
        private const string GetOrchestrationRunDescription = "读取单个 reference orchestration run 的状态、当前停点和 required decision；只读工具，不批准、不恢复、不写章节。";
        private const string GetOrchestrationRunEventsDescription = "读取单个 reference orchestration run 的本地事件历史；只读工具，只用于解释流程为何停止或继续，不批准、不恢复、不写章节。";
        private const string CancelOrchestrationDescription = "取消当前小说的 reference orchestration run；不能批准 source/fact、blueprint revision 或 final insertion，也不能写入章节正文。";

        private readonly IReferenceAnchoredDraftService _referenceDrafts;
        private readonly NovelistMafToolContext _context;
        private readonly JsonSerializerOptions _serializerOptions;

        public ReferenceDraftMafTools(
            IReferenceAnchoredDraftService referenceDrafts,
            NovelistMafToolContext context,
            JsonSerializerOptions serializerOptions)
        {
            _referenceDrafts = referenceDrafts;
            _context = context;
            _serializerOptions = serializerOptions;
        }

        public void AddAvailableTools(List<AIFunction> tools)
        {
            tools.Add(CreateFunction(nameof(GenerateReferenceChapterBlueprintAsync), "generate_reference_chapter_blueprint", GenerateBlueprintDescription));
            tools.Add(CreateFunction(nameof(ReviewReferenceChapterBlueprintAsync), "review_reference_chapter_blueprint", ReviewBlueprintDescription));
            tools.Add(CreateFunction(nameof(ReviseReferenceChapterBlueprintAsync), "revise_reference_chapter_blueprint", ReviseBlueprintDescription));
            tools.Add(CreateFunction(nameof(ApproveReferenceChapterBlueprintAsync), "approve_reference_chapter_blueprint", ApproveBlueprintDescription));
            tools.Add(CreateFunction(nameof(BindReferenceBlueprintMaterialsAsync), "bind_reference_blueprint_materials", BindMaterialsDescription));
            tools.Add(CreateFunction(nameof(GenerateReferenceAnchoredDraftAsync), "generate_reference_anchored_draft", GenerateDraftDescription));
            tools.Add(CreateFunction(nameof(AuditReferenceAnchoredDraftAsync), "audit_reference_anchored_draft", AuditDraftDescription));
            tools.Add(CreateFunction(nameof(GetReferenceDraftAuditsAsync), "get_reference_draft_audits", GetDraftAuditsDescription));
            tools.Add(CreateFunction(nameof(GetReferenceStyleAuditFindingsAsync), "get_reference_style_audit_findings", GetStyleAuditFindingsDescription));
            tools.Add(CreateFunction(nameof(StartReferenceOrchestrationRunAsync), "start_reference_orchestration_run", StartOrchestrationDescription));
            tools.Add(CreateFunction(nameof(GetReferenceOrchestrationRunsAsync), "get_reference_orchestration_runs", GetOrchestrationRunsDescription));
            tools.Add(CreateFunction(nameof(GetReferenceOrchestrationRunAsync), "get_reference_orchestration_run", GetOrchestrationRunDescription));
            tools.Add(CreateFunction(nameof(GetReferenceOrchestrationRunEventsAsync), "get_reference_orchestration_run_events", GetOrchestrationRunEventsDescription));
            tools.Add(CreateFunction(nameof(CancelReferenceOrchestrationRunAsync), "cancel_reference_orchestration_run", CancelOrchestrationDescription));
        }

        private AIFunction CreateFunction(string methodName, string toolName, string description)
        {
            var method = typeof(ReferenceDraftMafTools).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(ReferenceDraftMafTools).FullName, methodName);
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

        [Description(GenerateBlueprintDescription)]
        private ValueTask<ReferenceChapterBlueprintPayload> GenerateReferenceChapterBlueprintAsync(
            [Description("目标章节号")]
            int chapter_number,
            [Description("蓝图标题")]
            string? title = null,
            [Description("用户给定的本章目标")]
            string? chapter_goal = null,
            [Description("参与本章锚定的参考书 id")]
            long[]? anchor_ids = null,
            [Description("本章允许使用的已知事实")]
            string[]? known_facts = null,
            [Description("本章禁止引入的事实")]
            string[]? forbidden_facts = null,
            CancellationToken cancellationToken = default)
        {
            return _referenceDrafts.GenerateChapterBlueprintAsync(
                new GenerateReferenceChapterBlueprintPayload(
                    _context.NovelId,
                    chapter_number,
                    title,
                    chapter_goal,
                    anchor_ids ?? [],
                    known_facts ?? [],
                    forbidden_facts ?? []),
                cancellationToken);
        }

        [Description(ReviewBlueprintDescription)]
        private ValueTask<ReferenceChapterBlueprintReviewPayload> ReviewReferenceChapterBlueprintAsync(
            [Description("蓝图 id")]
            long blueprint_id,
            CancellationToken cancellationToken = default)
        {
            return _referenceDrafts.ReviewChapterBlueprintAsync(
                new ReviewReferenceChapterBlueprintPayload(_context.NovelId, blueprint_id),
                cancellationToken);
        }

        [Description(ReviseBlueprintDescription)]
        private ValueTask<ReferenceChapterBlueprintPayload> ReviseReferenceChapterBlueprintAsync(
            [Description("蓝图 id")]
            long blueprint_id,
            [Description("字段级修订列表")]
            ReferenceBlueprintRevisionChangePayload[] changes,
            [Description("修订来源，默认 agent")]
            string? origin = null,
            [Description("修订原因")]
            string? revision_reason = null,
            CancellationToken cancellationToken = default)
        {
            return _referenceDrafts.ReviseChapterBlueprintAsync(
                new ReviseReferenceChapterBlueprintPayload(
                    _context.NovelId,
                    blueprint_id,
                    changes,
                    string.IsNullOrWhiteSpace(origin) ? "agent" : origin,
                    revision_reason ?? string.Empty),
                cancellationToken);
        }

        [Description(ApproveBlueprintDescription)]
        private ValueTask<ReferenceChapterBlueprintPayload> ApproveReferenceChapterBlueprintAsync(
            [Description("蓝图 id")]
            long blueprint_id,
            [Description("通过评审的 review id")]
            string review_id,
            CancellationToken cancellationToken = default)
        {
            return _referenceDrafts.ApproveChapterBlueprintAsync(
                new ApproveReferenceChapterBlueprintPayload(_context.NovelId, blueprint_id, review_id, "agent"),
                cancellationToken);
        }

        [Description(BindMaterialsDescription)]
        private ValueTask<ReferenceBlueprintMaterialBindingResultPayload> BindReferenceBlueprintMaterialsAsync(
            [Description("蓝图 id")]
            long blueprint_id,
            [Description("每个 beat 最多候选数，默认 3，范围 1-10")]
            int max_results_per_beat = 0,
            [Description("是否自动选中每个 beat 的最高分候选。默认 false；需要进入草稿生成前必须显式传 true")]
            bool select_top_candidate = false,
            CancellationToken cancellationToken = default)
        {
            return _referenceDrafts.BindBlueprintMaterialsAsync(
                new BindReferenceBlueprintMaterialsPayload(
                    _context.NovelId,
                    blueprint_id,
                    Math.Clamp(max_results_per_beat <= 0 ? 3 : max_results_per_beat, 1, 10),
                    select_top_candidate),
                cancellationToken);
        }

        [Description(GenerateDraftDescription)]
        private ValueTask<ReferenceAnchoredDraftPayload> GenerateReferenceAnchoredDraftAsync(
            [Description("已批准蓝图 id")]
            long blueprint_id,
            [Description("指定 beat id，空数组表示全部目标 beat")]
            string[]? beat_ids = null,
            [Description("可选候选风格强度矩阵，仅允许 diagnostic_only/loose/moderate/strong；不提供时使用蓝图 beat 的 style_contract 强度")]
            string[]? style_intensities = null,
            [Description("每个 beat 最多生成的候选数，默认 1，范围 1-6")]
            int candidates_per_beat = 0,
            CancellationToken cancellationToken = default)
        {
            return _referenceDrafts.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(
                    _context.NovelId,
                    blueprint_id,
                    beat_ids ?? [],
                    style_intensities,
                    Math.Clamp(candidates_per_beat <= 0 ? 1 : candidates_per_beat, 1, 6)),
                cancellationToken);
        }

        [Description(AuditDraftDescription)]
        private ValueTask<ReferenceAnchoredDraftAuditPayload> AuditReferenceAnchoredDraftAsync(
            [Description("蓝图 id")]
            long blueprint_id,
            [Description("候选段落 id 列表")]
            string[] candidate_ids,
            CancellationToken cancellationToken = default)
        {
            return _referenceDrafts.AuditDraftAgainstBlueprintAsync(
                new AuditReferenceAnchoredDraftPayload(_context.NovelId, blueprint_id, candidate_ids),
                cancellationToken);
        }

        [Description(GetDraftAuditsDescription)]
        private ValueTask<IReadOnlyList<ReferenceAnchoredDraftAuditPayload>> GetReferenceDraftAuditsAsync(
            [Description("蓝图 id")]
            long blueprint_id,
            [Description("可选候选段落 id 过滤；为空时返回最近审计报告")]
            string[]? candidate_ids = null,
            [Description("最多返回条数，默认 20，范围 1-100")]
            int limit = 0,
            CancellationToken cancellationToken = default)
        {
            return _referenceDrafts.GetDraftAuditsAsync(
                new GetReferenceAnchoredDraftAuditsPayload(
                    _context.NovelId,
                    blueprint_id,
                    candidate_ids,
                    Math.Clamp(limit <= 0 ? 20 : limit, 1, 100)),
                cancellationToken);
        }

        [Description(GetStyleAuditFindingsDescription)]
        private ValueTask<IReadOnlyList<ReferenceStyleAuditFindingPayload>> GetReferenceStyleAuditFindingsAsync(
            [Description("蓝图 id")]
            long blueprint_id,
            [Description("可选候选段落 id 过滤；为空时读取最近审计中的 style/source-leak findings")]
            string[]? candidate_ids = null,
            [Description("可选风险类型过滤：source_leak / style_distance / style_fit；为空时返回全部 style/source-leak 风险")]
            string[]? risk_types = null,
            [Description("最多返回条数，默认 20，范围 1-100")]
            int limit = 0,
            CancellationToken cancellationToken = default)
        {
            return _referenceDrafts.GetStyleAuditFindingsAsync(
                new GetReferenceStyleAuditFindingsPayload(
                    _context.NovelId,
                    blueprint_id,
                    candidate_ids,
                    risk_types,
                    Math.Clamp(limit <= 0 ? 20 : limit, 1, 100)),
                cancellationToken);
        }

        [Description(StartOrchestrationDescription)]
        private ValueTask<ReferenceOrchestrationRunPayload> StartReferenceOrchestrationRunAsync(
            [Description("目标章节号")]
            int chapter_number,
            [Description("用户给定的本章目标或章节计划摘要")]
            string? chapter_goal = null,
            [Description("本章允许使用的已知事实；新增或扩大事实边界仍需要作者确认")]
            string[]? known_facts = null,
            [Description("本章禁止引入的事实")]
            string[]? forbidden_facts = null,
            [Description("高级包含过滤：允许 corpus 检索优先考虑的参考锚定 id；不等于已审批锚点选择")]
            long[]? include_anchor_ids = null,
            [Description("高级排除过滤：本次 corpus 检索排除的参考锚定 id")]
            long[]? exclude_anchor_ids = null,
            [Description("允许的来源许可状态，默认 user_provided")]
            string[]? license_statuses = null,
            [Description("每个 beat 最多检索候选数，默认 3，范围 1-10")]
            int max_results_per_beat = 0,
            [Description("语料检索模式，默认 story_context")]
            string? corpus_search_mode = null,
            CancellationToken cancellationToken = default)
        {
            var policy = new ReferenceCorpusSearchPolicyPayload(
                string.IsNullOrWhiteSpace(corpus_search_mode) ? "story_context" : corpus_search_mode.Trim(),
                Math.Clamp(max_results_per_beat <= 0 ? 3 : max_results_per_beat, 1, 10),
                license_statuses is { Length: > 0 } ? license_statuses : ["user_provided"],
                include_anchor_ids ?? [],
                exclude_anchor_ids ?? []);

            return _referenceDrafts.StartOrchestrationRunAsync(
                new StartReferenceOrchestrationRunPayload(
                    _context.NovelId,
                    chapter_number,
                    chapter_goal,
                    known_facts ?? [],
                    forbidden_facts ?? [],
                    AnchorIds: null,
                    policy,
                    SourceConfirmed: false),
                cancellationToken);
        }

        [Description(GetOrchestrationRunsDescription)]
        private ValueTask<IReadOnlyList<ReferenceOrchestrationRunPayload>> GetReferenceOrchestrationRunsAsync(
            [Description("章节号过滤；小于等于 0 表示全部章节")]
            int chapter_number = 0,
            CancellationToken cancellationToken = default)
        {
            return _referenceDrafts.GetOrchestrationRunsAsync(
                _context.NovelId,
                chapter_number <= 0 ? null : chapter_number,
                cancellationToken);
        }

        [Description(GetOrchestrationRunDescription)]
        private ValueTask<ReferenceOrchestrationRunPayload?> GetReferenceOrchestrationRunAsync(
            [Description("orchestration run id")]
            string run_id,
            CancellationToken cancellationToken = default)
        {
            return _referenceDrafts.GetOrchestrationRunAsync(_context.NovelId, run_id, cancellationToken);
        }

        [Description(GetOrchestrationRunEventsDescription)]
        private ValueTask<IReadOnlyList<ReferenceOrchestrationRunEventPayload>> GetReferenceOrchestrationRunEventsAsync(
            [Description("orchestration run id")]
            string run_id,
            CancellationToken cancellationToken = default)
        {
            return _referenceDrafts.GetOrchestrationRunEventsAsync(_context.NovelId, run_id, cancellationToken);
        }

        [Description(CancelOrchestrationDescription)]
        private ValueTask<ReferenceOrchestrationRunPayload> CancelReferenceOrchestrationRunAsync(
            [Description("orchestration run id")]
            string run_id,
            [Description("取消原因")]
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            return _referenceDrafts.CancelOrchestrationRunAsync(
                new CancelReferenceOrchestrationRunPayload(
                    _context.NovelId,
                    run_id,
                    string.IsNullOrWhiteSpace(reason) ? "agent_cancelled" : reason),
                cancellationToken);
        }
    }
}
