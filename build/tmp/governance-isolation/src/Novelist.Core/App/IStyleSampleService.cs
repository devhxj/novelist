using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IStyleSampleService
{
    ValueTask<StyleSamplePayload> CreateSampleAsync(
        CreateStyleSamplePayload input,
        CancellationToken cancellationToken);

    ValueTask<StyleSamplePayload> UpdateSampleAsync(
        UpdateStyleSamplePayload input,
        CancellationToken cancellationToken);

    ValueTask DeleteSampleAsync(DeleteStyleSamplePayload input, CancellationToken cancellationToken);

    ValueTask<StyleSampleDetailPayload?> GetSampleAsync(
        GetStyleSamplePayload input,
        CancellationToken cancellationToken);

    ValueTask<PageResultPayload<StyleSamplePayload>> SearchSamplesAsync(
        SearchStyleSamplesPayload input,
        CancellationToken cancellationToken);
}
