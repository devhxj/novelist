namespace Novelist.App.Desktop;

public sealed class PhotinoDesktopHost
{
    private readonly IPhotinoWindowFactory _windowFactory;

    public PhotinoDesktopHost(IPhotinoWindowFactory windowFactory)
    {
        _windowFactory = windowFactory;
    }

    public void Run(PhotinoWindowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var window = _windowFactory.Create(settings);
        window.WaitForClose();
    }
}
