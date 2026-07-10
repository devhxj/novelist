using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceCorpusAnalysisScheduler
{
    ValueTask<ReferenceCorpusAnalysisJobPayload> EnqueueAsync(EnqueueReferenceCorpusAnalysisJobPayload input, CancellationToken cancellationToken);
    ValueTask<ReferenceCorpusAnalysisJobPayload?> GetAsync(GetReferenceCorpusAnalysisJobPayload input, CancellationToken cancellationToken);
    ValueTask<PageResultPayload<ReferenceCorpusAnalysisJobPayload>> ListAsync(ListReferenceCorpusAnalysisJobsPayload input, CancellationToken cancellationToken);
    ValueTask<ReferenceCorpusAnalysisJobPayload> PauseAsync(PauseReferenceCorpusAnalysisJobPayload input, CancellationToken cancellationToken);
    ValueTask<ReferenceCorpusAnalysisJobPayload> ResumeAsync(ResumeReferenceCorpusAnalysisJobPayload input, CancellationToken cancellationToken);
    ValueTask<ReferenceCorpusAnalysisJobPayload> CancelAsync(CancelReferenceCorpusAnalysisJobPayload input, CancellationToken cancellationToken);
    ValueTask<ReferenceCorpusAnalysisJobPayload> ReprioritizeAsync(ReprioritizeReferenceCorpusAnalysisJobPayload input, CancellationToken cancellationToken);
}
