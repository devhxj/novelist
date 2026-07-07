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

    ValueTask<ReferenceStyleProfilePayload> ArchiveStyleProfileAsync(
        ArchiveReferenceStyleProfilePayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceStyleProfilePayload> RestoreStyleProfileAsync(
        RestoreReferenceStyleProfilePayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceStyleProfileComparisonPayload> CompareStyleProfilesAsync(
        CompareReferenceStyleProfilesPayload input,
        CancellationToken cancellationToken);
}
