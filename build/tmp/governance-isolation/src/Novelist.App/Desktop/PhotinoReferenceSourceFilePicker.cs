using Novelist.Core.App;

namespace Novelist.App.Desktop;

public sealed class PhotinoReferenceSourceFilePicker : IReferenceSourceFilePicker
{
    private static readonly WorkspaceFileFilter[] Filters =
    [
        new WorkspaceFileFilter("Markdown 文件 (*.md;*.markdown)", ["*.md", "*.markdown"]),
        new WorkspaceFileFilter("文本文件 (*.txt)", ["*.txt"]),
        new WorkspaceFileFilter("所有文件 (*.*)", ["*.*"])
    ];

    private readonly IPhotinoWindow _window;

    public PhotinoReferenceSourceFilePicker(IPhotinoWindow window)
    {
        _window = window;
    }

    public async ValueTask<string?> PickSourceFileAsync(CancellationToken cancellationToken)
    {
        return await _window.ShowOpenFileAsync(
            "选择参考源文件",
            string.Empty,
            Filters,
            cancellationToken);
    }
}
