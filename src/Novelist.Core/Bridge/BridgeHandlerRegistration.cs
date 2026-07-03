using System.Text.Json;
using Novelist.Contracts.Bridge;

namespace Novelist.Core.Bridge;

public static class BridgeHandlerRegistration
{
    public static BridgeDispatcher RegisterRuntimeHandlers(
        this BridgeDispatcher dispatcher,
        IBridgeRuntimeHost runtimeHost)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(runtimeHost);

        dispatcher.Register(BridgeRuntimeMethodNames.WindowMinimize, async (_, cancellationToken) =>
        {
            await runtimeHost.MinimizeWindowAsync(cancellationToken);
            return null;
        });

        dispatcher.Register(BridgeRuntimeMethodNames.WindowToggleMaximize, async (_, cancellationToken) =>
        {
            await runtimeHost.ToggleMaximizeWindowAsync(cancellationToken);
            return null;
        });

        dispatcher.Register(BridgeRuntimeMethodNames.WindowIsMaximized, async (_, cancellationToken) =>
        {
            return await runtimeHost.IsWindowMaximizedAsync(cancellationToken);
        });

        dispatcher.Register(BridgeRuntimeMethodNames.AppQuit, async (_, cancellationToken) =>
        {
            await runtimeHost.QuitApplicationAsync(cancellationToken);
            return null;
        });

        dispatcher.Register(BridgeRuntimeMethodNames.ShellOpenExternal, async (context, cancellationToken) =>
        {
            var url = ReadHttpsUrl(context.Payload);
            await runtimeHost.OpenExternalAsync(url, cancellationToken);
            return null;
        });

        return dispatcher;
    }

    public static BridgeDispatcher RegisterCompatibilityAppMethodHandlers(this BridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        foreach (var method in BridgeCompatibilityAppMethods.MethodNames)
        {
            dispatcher.Register(method, NotImplementedAppMethod);
        }

        return dispatcher;
    }

    public static BridgeDispatcher RegisterDefaultNovelistHandlers(
        this BridgeDispatcher dispatcher,
        IBridgeRuntimeHost runtimeHost)
    {
        return dispatcher
            .RegisterRuntimeHandlers(runtimeHost)
            .RegisterCompatibilityAppMethodHandlers();
    }

    private static ValueTask<object?> NotImplementedAppMethod(
        BridgeInvocationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new BridgeRequestException(
            BridgeErrorCodes.MethodNotImplemented,
            $"Bridge method '{context.Method}' is registered but not implemented yet.",
            new { method = context.Method });
    }

    private static Uri ReadHttpsUrl(JsonElement? payload)
    {
        if (payload is null || payload.Value.ValueKind != JsonValueKind.Object)
        {
            throw new BridgeValidationException(
                "External URL payload is required.",
                new Dictionary<string, string> { ["url"] = "Payload must be an object with a url field." });
        }

        if (!payload.Value.TryGetProperty("url", out var urlElement) ||
            urlElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(urlElement.GetString()))
        {
            throw new BridgeValidationException(
                "External URL is required.",
                new Dictionary<string, string> { ["url"] = "URL must be a non-empty string." });
        }

        var rawUrl = urlElement.GetString()!;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new BridgeValidationException(
                "External URL must use https://.",
                new Dictionary<string, string> { ["url"] = "Only absolute https:// URLs are allowed." });
        }

        return uri;
    }
}
