using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceStyleLlmAnalyzer
{
    ValueTask<string?> AnalyzeAsync(
        ReferenceStyleLlmAnalysisRequestPayload request,
        CancellationToken cancellationToken);
}
