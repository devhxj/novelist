using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        if (_referenceStyleProfiles is not null)
        {
            var styleProfileTools = new ReferenceStyleProfileMafTools(_referenceStyleProfiles, context, _serializerOptions);
            styleProfileTools.AddAvailableTools(tools);
        }
    }

    private sealed class ReferenceMafTools
    {
        private const int MaterialListPreviewMaxChars = 160;
        private const string GetAnchorsDescription = "列出当前小说可访问的已导入参考锚定书籍。返回 path-free 来源摘要，不返回 source_path；novel_id 由运行时注入，不需要也不能传入；不能导入新来源，不能读取任意文件。";
        private const string SearchMaterialsDescription = "按 story context 和可选 style filters 搜索已导入且受 license/visibility 过滤的参考语料库。返回材料 id、标签、来源、bounded text_preview 和 score_components，不返回完整材料文本；style_profile_ids 只影响受授权材料排序和 style-risk 解释，不能绕过来源/许可边界。用于给蓝图 beat 绑定材料，不直接写章节，不能导入新来源，不能读取任意文件。";
        private const string GetMaterialDetailDescription = "只读查看已导入参考材料的结构化明细。返回 provenance、tags/confidence、bounded previews、slots、score_components 和 processing_notes；不返回 source_path，不返回 source_text，不返回 candidate_text，不返回 prompt，不返回完整来源或完整章节，不能导入新来源，不能读取任意文件，不能写章节。";
        private const string GetSourceSegmentDetailDescription = "只读查看已导入参考来源片段的结构化明细。返回 source summary、segment metadata、bounded text_preview 和 processing_notes；不返回 source_path，不返回 source_text，不返回 candidate_text，不返回 prompt，不返回完整来源或完整章节，不能导入新来源，不能读取任意文件，不能写章节。";
        private const string GetSourceProcessingDetailDescription = "只读查看已导入参考来源的处理记录。返回 parse/segment/extract/index 状态、counts、affected ids 和已脱敏 diagnostics；不返回 source_path，不返回 source_text，不返回 candidate_text，不返回 prompt，不返回完整来源或完整章节，不能导入新来源，不能读取任意文件，不能写章节。";

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
            tools.Add(CreateFunction(nameof(GetReferenceMaterialDetailAsync), "get_reference_material_detail", GetMaterialDetailDescription));
            tools.Add(CreateFunction(nameof(GetReferenceSourceSegmentDetailAsync), "get_reference_source_segment_detail", GetSourceSegmentDetailDescription));
            tools.Add(CreateFunction(nameof(GetReferenceSourceProcessingDetailAsync), "get_reference_source_processing_detail", GetSourceProcessingDetailDescription));
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
        private async ValueTask<IReadOnlyList<ReferenceMaterialSourceSummaryPayload>> GetReferenceAnchorsAsync(CancellationToken cancellationToken = default)
        {
            var anchors = await _referenceAnchors.GetAnchorsAsync(_context.NovelId, cancellationToken);
            return anchors
                .Select(anchor => new ReferenceMaterialSourceSummaryPayload(
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
                    anchor.OwnerNovelId))
                .Select(ReferencePayloadSanitizer.SanitizeSourceSummary)
                .ToArray();
        }

        [Description(SearchMaterialsDescription)]
        private async ValueTask<PageResultPayload<ReferenceMaterialSummaryPayload>> SearchReferenceMaterialsAsync(
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
            var result = await _referenceAnchors.SearchMaterialsAsync(
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
            return ToMaterialSummaryPage(result);
        }

        private static PageResultPayload<ReferenceMaterialSummaryPayload> ToMaterialSummaryPage(
            PageResultPayload<ReferenceMaterialPayload> result)
        {
            return new PageResultPayload<ReferenceMaterialSummaryPayload>(
                result.Items.Select(ToMaterialSummary).ToArray(),
                result.Total,
                result.Page,
                result.Size,
                result.TotalPages);
        }

        private static ReferenceMaterialSummaryPayload ToMaterialSummary(ReferenceMaterialPayload material)
        {
            var preview = BuildPreview(material.Text, MaterialListPreviewMaxChars);
            return ReferencePayloadSanitizer.SanitizeMaterialSummary(new ReferenceMaterialSummaryPayload(
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
                ScoreComponents: material.ScoreComponents));
        }

        private static TextPreview BuildPreview(string? text, int maxLength)
        {
            var normalized = ReferencePayloadSanitizer.RedactSensitiveText(
                Regex.Replace((text ?? string.Empty).Trim(), @"\s+", " "));
            if (normalized.Length <= maxLength)
            {
                return new TextPreview(normalized, false);
            }

            return new TextPreview(normalized[..maxLength].TrimEnd() + "...", true);
        }

        [Description(GetMaterialDetailDescription)]
        private async ValueTask<ReferenceMaterialDetailPayload?> GetReferenceMaterialDetailAsync(
            [Description("参考材料 id")]
            string material_id,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(material_id))
            {
                throw new ArgumentException("material_id is required.", nameof(material_id));
            }

            return ReferencePayloadSanitizer.SanitizeMaterialDetail(await _referenceAnchors.GetMaterialDetailAsync(
                new GetReferenceMaterialDetailPayload(_context.NovelId, material_id.Trim()),
                cancellationToken));
        }

        [Description(GetSourceSegmentDetailDescription)]
        private async ValueTask<ReferenceSourceSegmentDetailPayload?> GetReferenceSourceSegmentDetailAsync(
            [Description("参考来源 anchor id")]
            long anchor_id,
            [Description("参考来源片段 id")]
            string segment_id,
            CancellationToken cancellationToken = default)
        {
            if (anchor_id <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(anchor_id), anchor_id, "anchor_id must be positive.");
            }

            if (string.IsNullOrWhiteSpace(segment_id))
            {
                throw new ArgumentException("segment_id is required.", nameof(segment_id));
            }

            return ReferencePayloadSanitizer.SanitizeSourceSegmentDetail(await _referenceAnchors.GetSourceSegmentDetailAsync(
                new GetReferenceSourceSegmentDetailPayload(_context.NovelId, anchor_id, segment_id.Trim()),
                cancellationToken));
        }

        [Description(GetSourceProcessingDetailDescription)]
        private async ValueTask<ReferenceSourceProcessingDetailPayload?> GetReferenceSourceProcessingDetailAsync(
            [Description("参考来源 anchor id")]
            long anchor_id,
            CancellationToken cancellationToken = default)
        {
            if (anchor_id <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(anchor_id), anchor_id, "anchor_id must be positive.");
            }

            return ReferencePayloadSanitizer.SanitizeSourceProcessingDetail(await _referenceAnchors.GetSourceProcessingDetailAsync(
                new GetReferenceSourceProcessingDetailPayload(_context.NovelId, anchor_id),
                cancellationToken));
        }

        private readonly record struct TextPreview(string Text, bool Truncated);
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


}
