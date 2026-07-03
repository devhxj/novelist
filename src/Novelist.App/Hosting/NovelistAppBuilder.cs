using Novelist.App.Realtime;

namespace Novelist.App.Hosting;

public static class NovelistAppBuilder
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddProblemDetails();
        builder.Services.AddSignalR();

        var app = builder.Build();
        var frontendAssets = FrontendAssets.TryResolve(app.Configuration, app.Environment);

        if (frontendAssets is not null)
        {
            app.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = frontendAssets.FileProvider
            });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = frontendAssets.FileProvider
            });
        }

        if (frontendAssets is null)
        {
            app.MapGet("/", () => Results.Text("novelist"));
        }

        app.MapGet("/health", () => Results.Ok(new HealthResponse("ok", "novelist")));
        app.MapHub<EventsHub>("/hubs/events");

        if (frontendAssets is not null)
        {
            app.MapFallback(async context =>
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(frontendAssets.FileProvider.GetFileInfo("index.html"));
            });
        }

        return app;
    }
}

public sealed record HealthResponse(string Status, string Service);
