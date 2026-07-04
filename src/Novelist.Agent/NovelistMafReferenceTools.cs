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
    }

    private sealed class ReferenceMafTools
    {
        private const string GetAnchorsDescription = "列出当前小说已导入的参考锚定书籍。novel_id 由运行时注入，不需要也不能传入。";
        private const string SearchMaterialsDescription = "搜索参考锚定材料库。返回材料 id、标签、来源、文本和 score_components；用于给蓝图 beat 绑定材料，不直接写章节。";
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
            [Description("材料类型过滤：chapter / paragraph / sentence / passage")]
            string[]? material_types = null,
            [Description("情绪标签过滤")]
            string[]? emotion_tags = null,
            [Description("功能标签过滤，例如 interiority / environment / narration")]
            string[]? function_tags = null,
            [Description("视角标签过滤")]
            string[]? pov_tags = null,
            [Description("写作手法标签过滤")]
            string[]? technique_tags = null,
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
                    Math.Clamp(size <= 0 ? 10 : size, 1, 20)),
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

    private sealed class ReferenceDraftMafTools
    {
        private const string GenerateBlueprintDescription = "生成当前小说某章节的 reference-anchored Chapter Narrative Blueprint。只生成结构化蓝图，不生成正文。";
        private const string ReviewBlueprintDescription = "评审 reference-anchored 蓝图。纯检查工具，不静默修订蓝图。";
        private const string ReviseBlueprintDescription = "按字段路径修订蓝图，并使已批准 review/material links 失效。";
        private const string ApproveBlueprintDescription = "批准已通过评审的蓝图。只有批准后的蓝图才能绑定材料和生成候选。";
        private const string BindMaterialsDescription = "把参考材料绑定到已通过评审并已批准的蓝图 beat。";
        private const string GenerateDraftDescription = "从 approved/material_bound 蓝图和已选择材料链接生成候选段落；只返回 candidates，不调用 SaveContent，不直接写章节。";
        private const string AuditDraftDescription = "按 candidate_id 审计已生成的 reference-anchored 草稿候选。纯检查工具，不写章节。";

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
                new ApproveReferenceChapterBlueprintPayload(_context.NovelId, blueprint_id, review_id),
                cancellationToken);
        }

        [Description(BindMaterialsDescription)]
        private ValueTask<ReferenceBlueprintMaterialBindingResultPayload> BindReferenceBlueprintMaterialsAsync(
            [Description("蓝图 id")]
            long blueprint_id,
            [Description("每个 beat 最多候选数，默认 3，范围 1-10")]
            int max_results_per_beat = 0,
            CancellationToken cancellationToken = default)
        {
            return _referenceDrafts.BindBlueprintMaterialsAsync(
                new BindReferenceBlueprintMaterialsPayload(
                    _context.NovelId,
                    blueprint_id,
                    Math.Clamp(max_results_per_beat <= 0 ? 3 : max_results_per_beat, 1, 10)),
                cancellationToken);
        }

        [Description(GenerateDraftDescription)]
        private ValueTask<ReferenceAnchoredDraftPayload> GenerateReferenceAnchoredDraftAsync(
            [Description("已批准蓝图 id")]
            long blueprint_id,
            [Description("指定 beat id，空数组表示全部目标 beat")]
            string[]? beat_ids = null,
            CancellationToken cancellationToken = default)
        {
            return _referenceDrafts.GenerateDraftFromBlueprintAsync(
                new GenerateReferenceAnchoredDraftPayload(_context.NovelId, blueprint_id, beat_ids ?? []),
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
    }
}
