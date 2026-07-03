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

    public async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        await using var app = NovelistAppBuilder.Build(args);
        app.Urls.Add(LoopbackUrl);

        await app.StartAsync(cancellationToken);
        try
        {
            var localUrl = ResolveLocalUrl(app);
            var settings = PhotinoLaunchMode.CreateSettings(args, localUrl);
            new PhotinoDesktopHost(_windowFactory).Run(settings);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
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
