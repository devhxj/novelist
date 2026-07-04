using System.Globalization;

namespace Novelist.Infrastructure.App;

internal static class EmbeddingEndpoint
{
    private const string ChatPath = "/chat/completions";
    private const string ResponsesPath = "/responses";
    private const string ModelsPath = "/models";
    private const string EmbeddingsPath = "/embeddings";

    public static string NormalizeEndpointUrl(string? raw, int maxLength = 2_048)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new ArgumentException("Embedding endpoint is required.", nameof(raw));
        }

        if (value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(raw), value.Length, $"Embedding endpoint must be at most {maxLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Embedding endpoint must be an absolute http:// or https:// URL.", nameof(raw));
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Embedding endpoint must not include query strings or fragments.", nameof(raw));
        }

        var builder = new UriBuilder(uri)
        {
            Path = StripKnownEndpointPath(uri.AbsolutePath),
            Query = string.Empty,
            Fragment = string.Empty
        };

        return new Uri(builder.Uri.ToString().TrimEnd('/') + EmbeddingsPath, UriKind.Absolute).ToString();
    }

    private static string StripKnownEndpointPath(string path)
    {
        var normalized = NormalizePath(path);
        foreach (var suffix in new[] { ChatPath, ResponsesPath, ModelsPath, EmbeddingsPath })
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
