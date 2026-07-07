using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceStyleProfileService
{
    ValueTask<ReferenceStyleProfilePayload> BuildStyleProfileAsync(
        BuildReferenceStyleProfilePayload input,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReferenceStyleProfileSummaryPayload>> GetStyleProfilesAsync(
        GetReferenceStyleProfilesPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceStyleProfilePayload?> GetStyleProfileAsync(
        long novelId,
        long profileId,
        CancellationToken cancellationToken);
}
