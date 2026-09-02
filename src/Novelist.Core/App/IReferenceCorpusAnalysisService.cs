using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceCorpusAnalysisService
{
    ValueTask<ReferenceCorpusFeatureAnalysisRunPayload> StartFeatureAnalysisAsync(
        StartReferenceCorpusFeatureAnalysisPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceCorpusFeatureAnalysisRunPayload?> GetFeatureAnalysisRunAsync(
        GetReferenceCorpusFeatureAnalysisRunPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisRunPayload> StartTechniqueSpecimenAnalysisAsync(
        StartReferenceCorpusTechniqueSpecimenAnalysisPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisRunPayload?> GetTechniqueSpecimenAnalysisRunAsync(
        GetReferenceCorpusTechniqueSpecimenAnalysisRunPayload input,
        CancellationToken cancellationToken);

    ValueTask<PageResultPayload<ReferenceCorpusFeatureObservationPayload>> ListFeatureObservationsAsync(
        ListReferenceCorpusFeatureObservationsPayload input,
        CancellationToken cancellationToken);

    ValueTask<PageResultPayload<ReferenceCorpusTechniqueSpecimenPayload>> ListTechniqueSpecimensAsync(
        ListReferenceCorpusTechniqueSpecimensPayload input,
        CancellationToken cancellationToken);

    /// <summary>导出语料包：观察 + 标本 + 证据文本写为 JSONL（默认保存对话框选择目标）。</summary>
    ValueTask<ReferenceCorpusPackageExportResult> ExportPackageAsync(
        ExportReferenceCorpusPackagePayload input,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>导入语料包：同书备份恢复语义；observation_id/specimen_id 冲突的行跳过。</summary>
    ValueTask<ReferenceCorpusPackageImportResult> ImportPackageAsync(
        ImportReferenceCorpusPackagePayload input,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>全书聚合的观察/标本总数：供语料区总览一次调用取全量，消除逐锚点 N+1。</summary>
    ValueTask<ReferenceCorpusAssetTotalsPayload> GetAssetTotalsAsync(
        GetReferenceCorpusAssetTotalsPayload input,
        CancellationToken cancellationToken);
}
