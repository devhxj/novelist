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
}
