using Novelist.Core.App;

namespace Novelist.App.Desktop;

public sealed class PhotinoReferenceCorpusPackageFilePicker : IReferenceCorpusPackageFilePicker
{
    private readonly IPhotinoWindow _window;

    public PhotinoReferenceCorpusPackageFilePicker(IPhotinoWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public async ValueTask<string?> PickPackageSaveFileAsync(string defaultFileName, CancellationToken cancellationToken)
    {
        return await _window.ShowSaveFileAsync(
            "导出语料包",
            defaultFileName,
            [new NovelExportFileFilter("语料包 JSONL (*.jsonl)", "*.jsonl")],
            cancellationToken);
    }

    public async ValueTask<string?> PickPackageOpenFileAsync(CancellationToken cancellationToken)
    {
        var path = await _window.ShowOpenFileAsync(
            "导入语料包",
            string.Empty,
            [new WorkspaceFileFilter("语料包 JSONL (*.jsonl)", ["*.jsonl"])],
            cancellationToken);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
}
