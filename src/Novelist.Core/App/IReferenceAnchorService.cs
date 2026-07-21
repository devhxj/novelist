using Novelist.Contracts.App;

namespace Novelist.Core.App;

/// <summary>
/// Owns reference-source registration and the small amount of metadata needed
/// by chapter splitting and materialization. Material extraction and search live
/// behind their dedicated services.
/// </summary>
public interface IReferenceAnchorService
{
    ValueTask<ReferenceAnchorPayload> RegisterMaterializationSourceAsync(
        RegisterReferenceMaterializationSourcePayload input,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReferenceAnchorPayload>> GetAnchorsAsync(
        long novelId,
        CancellationToken cancellationToken);

    ValueTask DeleteAnchorAsync(
        long novelId,
        long anchorId,
        CancellationToken cancellationToken);

    ValueTask DeleteAnchorsAsync(
        DeleteReferenceAnchorsPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceAnchorPayload> UpdateAnchorMetadataAsync(
        UpdateReferenceAnchorMetadataPayload input,
        CancellationToken cancellationToken);
}
