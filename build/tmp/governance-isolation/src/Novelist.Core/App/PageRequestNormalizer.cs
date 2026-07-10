using Novelist.Contracts.App;

namespace Novelist.Core.App;

public static class PageRequestNormalizer
{
    private const int MaxCursorLength = 512;

    public static NormalizedPageRequest Normalize(
        PageRequestPayload request,
        PageRequestPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        ValidatePolicy(policy);

        if (request.PageSize <= 0 || request.PageSize > policy.MaxPageSize)
        {
            throw new PageRequestValidationException(
                PageRequestErrorCodes.PageSizeOutOfRange,
                $"page_size must be between 1 and {policy.MaxPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        }

        var sortBy = string.IsNullOrWhiteSpace(request.SortBy)
            ? policy.DefaultSortBy
            : request.SortBy.Trim();
        if (!policy.AllowedSortFields.Contains(sortBy, StringComparer.Ordinal))
        {
            throw new PageRequestValidationException(
                PageRequestErrorCodes.InvalidSortField,
                $"sort_by '{sortBy}' is not supported.");
        }

        var sortDir = NormalizeSortDirection(request.SortDir);
        var filters = NormalizeFilters(request.Filters);
        var stableSortFields = BuildStableSortFields(sortBy, policy);

        return new NormalizedPageRequest(
            NormalizeCursor(request.Cursor),
            request.PageSize,
            sortBy,
            sortDir,
            filters,
            stableSortFields);
    }

    private static string? NormalizeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        var normalized = cursor.Trim();
        if (normalized.Length > MaxCursorLength ||
            normalized.Any(ch => !char.IsAscii(ch) || char.IsControl(ch) || char.IsWhiteSpace(ch)))
        {
            throw new PageRequestValidationException(
                PageRequestErrorCodes.InvalidCursor,
                "cursor is invalid.");
        }

        return normalized;
    }

    private static string NormalizeSortDirection(string sortDir)
    {
        var normalized = string.IsNullOrWhiteSpace(sortDir)
            ? "desc"
            : sortDir.Trim().ToLowerInvariant();
        if (normalized is "asc" or "desc")
        {
            return normalized;
        }

        throw new PageRequestValidationException(
            PageRequestErrorCodes.InvalidSortDirection,
            "sort_dir must be 'asc' or 'desc'.");
    }

    private static IReadOnlyDictionary<string, string> NormalizeFilters(IReadOnlyDictionary<string, string>? filters)
    {
        if (filters is null || filters.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in filters.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var key = item.Key.Trim();
            if (!IsValidFilterKey(key))
            {
                throw new PageRequestValidationException(
                    PageRequestErrorCodes.InvalidFilterKey,
                    $"filter key '{item.Key}' is not supported.");
            }

            normalized[key] = item.Value;
        }

        return normalized;
    }

    private static bool IsValidFilterKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        foreach (var ch in key)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not '_' and not '-' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<string> BuildStableSortFields(string sortBy, PageRequestPolicy policy)
    {
        var fields = new List<string> { sortBy };
        foreach (var tieBreaker in policy.StableTieBreakers)
        {
            if (string.IsNullOrWhiteSpace(tieBreaker))
            {
                throw new PageRequestValidationException(
                    PageRequestErrorCodes.InvalidSortField,
                    "stable tie-breaker fields cannot be blank.");
            }

            var normalizedTieBreaker = tieBreaker.Trim();
            if (!policy.AllowedSortFields.Contains(normalizedTieBreaker, StringComparer.Ordinal))
            {
                throw new PageRequestValidationException(
                    PageRequestErrorCodes.InvalidSortField,
                    $"stable tie-breaker '{normalizedTieBreaker}' is not supported.");
            }

            if (!fields.Contains(normalizedTieBreaker, StringComparer.Ordinal))
            {
                fields.Add(normalizedTieBreaker);
            }
        }

        return fields;
    }

    private static void ValidatePolicy(PageRequestPolicy policy)
    {
        if (policy.MaxPageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy.MaxPageSize, "Max page size must be positive.");
        }

        if (string.IsNullOrWhiteSpace(policy.DefaultSortBy))
        {
            throw new ArgumentException("Default sort field cannot be blank.", nameof(policy));
        }

        if (!policy.AllowedSortFields.Contains(policy.DefaultSortBy, StringComparer.Ordinal))
        {
            throw new ArgumentException("Default sort field must be included in allowed sort fields.", nameof(policy));
        }
    }
}

public sealed record PageRequestPolicy(
    IReadOnlyList<string> AllowedSortFields,
    string DefaultSortBy,
    IReadOnlyList<string> StableTieBreakers,
    int MaxPageSize = 200);

public sealed record NormalizedPageRequest(
    string? Cursor,
    int PageSize,
    string SortBy,
    string SortDir,
    IReadOnlyDictionary<string, string> Filters,
    IReadOnlyList<string> StableSortFields);

public sealed class PageRequestValidationException : Exception
{
    public PageRequestValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public static class PageRequestErrorCodes
{
    public const string PageSizeOutOfRange = "page_size_out_of_range";
    public const string InvalidCursor = "invalid_cursor";
    public const string InvalidSortField = "invalid_sort_field";
    public const string InvalidSortDirection = "invalid_sort_direction";
    public const string InvalidFilterKey = "invalid_filter_key";
}
