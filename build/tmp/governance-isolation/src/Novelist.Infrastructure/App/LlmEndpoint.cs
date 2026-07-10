using System.Globalization;

namespace Novelist.Infrastructure.App;

internal static class LlmEndpoint
{
    public const string Chat = "chat";
    public const string Responses = "responses";

    private const string ChatPath = "/chat/completions";
    private const string ResponsesPath = "/responses";
    private const string ModelsPath = "/models";

    public static string NormalizeEndpointType(string? value, string fallback = Chat)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            normalized = fallback;
        }

        return normalized switch
        {
            "chat" or "chat_completions" or "chat-completions" => Chat,
            "res" or "response" or "responses" => Responses,
            _ => throw new ArgumentException("Endpoint type must be chat or responses.", nameof(value))
        };
    }

    public static string NormalizeBaseUrl(string? raw, bool requireValue, int maxLength = 2_048)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            if (requireValue)
            {
                throw new ArgumentException("Base URL is required.", nameof(raw));
            }

            return string.Empty;
        }

        if (value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(raw), value.Length, $"Base URL must be at most {maxLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Base URL must be an absolute http:// or https:// URL.", nameof(raw));
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Base URL must not include query strings or fragments.", nameof(raw));
        }

        var builder = new UriBuilder(uri)
        {
            Path = StripKnownEndpointPath(uri.AbsolutePath),
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.ToString().TrimEnd('/');
    }

    public static Uri BuildEndpointUrl(string baseUrl, string endpointType)
    {
        var normalizedBase = NormalizeBaseUrl(baseUrl, requireValue: true);
        var path = NormalizeEndpointType(endpointType) == Responses ? ResponsesPath : ChatPath;
        return new Uri(normalizedBase.TrimEnd('/') + path, UriKind.Absolute);
    }

    public static Uri BuildModelsUrl(string baseUrl)
    {
        var normalizedBase = NormalizeBaseUrl(baseUrl, requireValue: true);
        return new Uri(normalizedBase.TrimEnd('/') + ModelsPath, UriKind.Absolute);
    }

    private static string StripKnownEndpointPath(string path)
    {
        var normalized = NormalizePath(path);
        foreach (var suffix in new[] { ChatPath, ResponsesPath, ModelsPath })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return NormalizePath(normalized[..^suffix.Length]);
            }
        }

        return normalized;
    }

    private static string NormalizePath(string path)
    {
        var normalized = (path ?? string.Empty).Trim().TrimEnd('/');
        return normalized is "" or "/" ? string.Empty : normalized;
    }
}
