using Novelist.Core.App;

namespace Novelist.App.Desktop;

public interface IPhotinoWindowFactory
{
    IPhotinoWindow Create(PhotinoWindowSettings settings);
}

public interface IPhotinoWindow
{
    void WaitForClose();

    void SendWebMessage(string message);

    ValueTask<string?> ShowSaveFileAsync(
        string title,
        string defaultPath,
        IReadOnlyList<NovelExportFileFilter> filters,
        CancellationToken cancellationToken);

    ValueTask<string?> ShowOpenFileAsync(
        string title,
        string defaultPath,
        IReadOnlyList<WorkspaceFileFilter> filters,
        CancellationToken cancellationToken);

    void Minimize();

    void ToggleMaximize();

    bool IsMaximized();

    void Close();
}
