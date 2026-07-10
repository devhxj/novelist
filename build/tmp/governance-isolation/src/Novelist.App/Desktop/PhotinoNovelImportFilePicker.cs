using Novelist.Core.App;

namespace Novelist.App.Desktop;

public sealed class PhotinoNovelImportFilePicker : INovelImportFilePicker
{
    private static readonly WorkspaceFileFilter[] Filters =
    [
        new WorkspaceFileFilter("EPUB 文件 (*.epub)", ["*.epub"]),
        new WorkspaceFileFilter("文本和 Markdown 文件 (*.txt;*.md;*.markdown)", ["*.txt", "*.md", "*.markdown"])
    ];

    private readonly IPhotinoWindow _window;

    public PhotinoNovelImportFilePicker(IPhotinoWindow window)
    {
        _window = window;
    }

    public async ValueTask<string?> PickImportFileAsync(CancellationToken cancellationToken)
    {
        return await _window.ShowOpenFileAsync(
            "选择导入小说文件",
            string.Empty,
            Filters,
            cancellationToken);
    }
}
