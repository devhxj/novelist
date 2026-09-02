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

    /// <summary>全书聚合的观察/标本总数：供语料区总览一次调用取全量，消除逐锚点 N+1。</summary>
    ValueTask<ReferenceCorpusAssetTotalsPayload> GetAssetTotalsAsync(
        GetReferenceCorpusAssetTotalsPayload input,
        CancellationToken cancellationToken);
}
