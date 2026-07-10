using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IStyleSkillExtractionService
{
    ValueTask<StyleSkillExtractionRunPayload> StartExtractionAsync(
        StartStyleSkillExtractionPayload input,
        CancellationToken cancellationToken);

    ValueTask<StyleSkillExtractionRunPayload> CancelExtractionAsync(
        CancelStyleSkillExtractionPayload input,
        CancellationToken cancellationToken);

    ValueTask<StyleSkillExtractionRunPayload?> GetRunAsync(
        GetNovelImportRunPayload input,
        CancellationToken cancellationToken);
}
