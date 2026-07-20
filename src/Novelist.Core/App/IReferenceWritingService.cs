using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceWritingService
{
    ValueTask<ReferenceWritingSessionPayload> GenerateBlueprintsAsync(
        GenerateReferenceBlueprintsPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceWritingSessionPayload?> GetSessionAsync(
        GetReferenceWritingSessionPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceWritingSessionPayload> SelectBlueprintAsync(
        SelectReferenceBlueprintPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceWritingDraftCandidatesPayload> GenerateDraftCandidatesAsync(
        GenerateReferenceDraftCandidatesPayload input,
        CancellationToken cancellationToken);
}

public sealed class ReferenceWritingException : Exception
{
    public ReferenceWritingException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
