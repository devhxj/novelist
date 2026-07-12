using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceMaterializationBlueprintPreviewService
{
    ValueTask<ReferenceMaterializationBlueprintPreviewPayload> GenerateAsync(
        GenerateReferenceMaterializationBlueprintPreviewPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceMaterializationBlueprintPreviewPayload?> GetAsync(
        GetReferenceMaterializationBlueprintPreviewPayload input,
        CancellationToken cancellationToken);
}
