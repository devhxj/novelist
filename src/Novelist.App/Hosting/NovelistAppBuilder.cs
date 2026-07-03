using Novelist.App.Realtime;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

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
        app.MapGet("/covers/{novelId:long}", async (long novelId, HttpContext context) =>
        {
            var options = CreateAppInitializationOptions(app.Configuration);
            var service = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
            try
            {
                var cover = await service.GetCoverAsync(novelId, context.RequestAborted);
                if (cover is null)
                {
                    return Results.NotFound();
                }

                context.Response.Headers.CacheControl = "no-cache";
                return Results.File(
                    cover.LocalPath,
                    cover.ContentType,
                    lastModified: cover.LastModified,
                    enableRangeProcessing: false);
            }
            catch (AppNotInitializedException)
            {
                return Results.NotFound();
            }
            catch (ArgumentException)
            {
                return Results.NotFound();
            }
        });

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

    private static AppInitializationOptions CreateAppInitializationOptions(IConfiguration configuration)
    {
        var defaults = new AppInitializationOptions();
        return defaults with
        {
            ConfigDirectory = ReadPath(configuration, "Novelist:ConfigDirectory") ?? defaults.ConfigDirectory,
            DefaultDataDirectory = ReadPath(configuration, "Novelist:DefaultDataDirectory") ?? defaults.DefaultDataDirectory,
            EnableLegacyGoinkMigration = bool.TryParse(configuration["Novelist:EnableLegacyGoinkMigration"], out var enabled) && enabled,
            LegacyGoinkConfigDirectory = ReadPath(configuration, "Novelist:LegacyGoinkConfigDirectory"),
            LegacyGoinkDataDirectory = ReadPath(configuration, "Novelist:LegacyGoinkDataDirectory")
        };
    }

    private static string? ReadPath(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
    }
}

public sealed record HealthResponse(string Status, string Service);
