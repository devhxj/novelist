namespace Novelist.App.Desktop;

public sealed class PhotinoDesktopApplication
{
    private readonly IPhotinoWindowFactory _windowFactory;

    public PhotinoDesktopApplication(IPhotinoWindowFactory windowFactory)
    {
        _windowFactory = windowFactory;
    }

    public Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        Run(args, cancellationToken);
        return Task.CompletedTask;
    }

    public void Run(string[] args, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var frontendAssets = DesktopFrontendAssets.TryResolve(args);
        if (frontendAssets is not null)
        {
            DesktopLaunchLog.Write("Resolved frontend assets at " + frontendAssets.DistPath);
        }
        else
        {
            DesktopLaunchLog.Write("Frontend assets not found.");
            if (!PhotinoLaunchMode.HasStartUrlOverride(args))
            {
                throw new InvalidOperationException(
                    "Frontend assets were not found. Run `npm run build` from the frontend directory, " +
                    "or debug with the `Novelist.App (Vite)` launch profile after starting the Vite dev server.");
            }
        }

        var settings = PhotinoLaunchMode.CreateSettings(args, frontendAssets?.StartUrl);
        DesktopLaunchLog.Write("Creating Photino window");
        new PhotinoDesktopHost(_windowFactory).Run(settings);
        DesktopLaunchLog.Write("Photino window closed");
    }
}
