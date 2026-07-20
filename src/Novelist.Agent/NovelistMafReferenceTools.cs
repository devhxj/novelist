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
        if (_referenceAnchors is null || _referenceMaterials is null)
        {
            return;
        }

        new ReferenceMafTools(
            _referenceAnchors,
            _referenceMaterials,
            context,
            _serializerOptions).AddAvailableTools(tools);
    }

    private sealed class ReferenceMafTools
    {
        private const string GetAnchorsDescription = "列出当前小说可访问的参考书来源；不返回源文件路径或原书全文。";
        private const string SearchMaterialsDescription = "使用当前激活 generation 的向量索引搜索参考材料；只返回经过授权过滤的材料，不做词法或旧语料兜底。";

        private readonly IReferenceAnchorService _anchors;
        private readonly IReferenceMaterialSearch _materials;
        private readonly NovelistMafToolContext _context;
        private readonly JsonSerializerOptions _serializerOptions;

        public ReferenceMafTools(
            IReferenceAnchorService anchors,
            IReferenceMaterialSearch materials,
            NovelistMafToolContext context,
            JsonSerializerOptions serializerOptions)
        {
            _anchors = anchors;
            _materials = materials;
            _context = context;
            _serializerOptions = serializerOptions;
        }

        public void AddAvailableTools(List<AIFunction> tools)
        {
            tools.Add(CreateFunction(nameof(GetReferenceAnchorsAsync), "get_reference_anchors", GetAnchorsDescription));
            tools.Add(CreateFunction(nameof(SearchReferenceMaterialsAsync), "search_reference_materials", SearchMaterialsDescription));
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
        private async ValueTask<IReadOnlyList<ReferenceMaterialSourceSummaryPayload>> GetReferenceAnchorsAsync(
            CancellationToken cancellationToken = default)
        {
            var anchors = await _anchors.GetAnchorsAsync(_context.NovelId, cancellationToken);
            return anchors.Select(anchor => ReferencePayloadSanitizer.SanitizeSourceSummary(
                new ReferenceMaterialSourceSummaryPayload(
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
                    anchor.OwnerNovelId))).ToArray();
        }

        [Description(SearchMaterialsDescription)]
        private ValueTask<IReadOnlyList<ReferenceMaterialSearchHit>> SearchReferenceMaterialsAsync(
            [Description("要检索的章节目标或写作需求")] string query,
            [Description("最多返回材料数，默认 10，范围 1-20")] int max_results = 10,
            CancellationToken cancellationToken = default)
        {
            return _materials.SearchAsync(
                new ReferenceMaterialSearchRequest(
                    query,
                    Math.Clamp(max_results, 1, 20),
                    NovelId: _context.NovelId),
                cancellationToken);
        }
    }
}
