using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceCorpusService
{
    ValueTask<PageResultPayload<ReferenceCorpusCandidatePayload>> SearchCandidatesAsync(
        SearchReferenceCorpusCandidatesPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceCorpusTechniqueVectorIndexBackfillPayload> BackfillTechniqueVectorIndexAsync(
        BackfillReferenceCorpusTechniqueVectorIndexPayload input,
        CancellationToken cancellationToken);
}
