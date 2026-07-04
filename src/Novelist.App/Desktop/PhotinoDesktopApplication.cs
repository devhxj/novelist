using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Novelist.App.Hosting;

namespace Novelist.App.Desktop;

public sealed class PhotinoDesktopApplication
{
    private const string LoopbackUrl = "http://127.0.0.1:0";
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
        var app = NovelistAppBuilder.Build(args);
        app.Urls.Add(LoopbackUrl);
        try
        {
            DesktopLaunchLog.Write("Starting loopback host");
            app.StartAsync(cancellationToken).GetAwaiter().GetResult();
            try
            {
                var localUrl = ResolveLocalUrl(app);
                DesktopLaunchLog.Write("Loopback host started at " + localUrl);
                var settings = PhotinoLaunchMode.CreateSettings(args, localUrl);
                DesktopLaunchLog.Write("Creating Photino window");
                new PhotinoDesktopHost(_windowFactory).Run(settings);
                DesktopLaunchLog.Write("Photino window closed");
            }
            finally
            {
                DesktopLaunchLog.Write("Stopping loopback host");
                app.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        finally
        {
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string ResolveLocalUrl(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new InvalidOperationException("The local web host did not publish a server address.");
        }

        return address.EndsWith("/", StringComparison.Ordinal) ? address : address + "/";
    }
}
