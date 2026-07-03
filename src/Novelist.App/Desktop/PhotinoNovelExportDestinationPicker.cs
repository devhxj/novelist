using Novelist.Core.App;

namespace Novelist.App.Desktop;

public sealed class PhotinoNovelExportDestinationPicker : INovelExportDestinationPicker
{
    private readonly IPhotinoWindow _window;

    public PhotinoNovelExportDestinationPicker(IPhotinoWindow window)
    {
        _window = window;
    }

    public async ValueTask<string?> PickSaveFileAsync(
        NovelExportDestinationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await _window.ShowSaveFileAsync(
            "导出小说",
            request.DefaultFileName,
            request.Filters,
            cancellationToken);
    }
}
