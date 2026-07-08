using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class GitHubUpdateCheckService : IUpdateCheckService
{
    private const int MaxReleaseNotesLength = 20_000;
    private const int MinTimeoutMs = 500;
    private const int MaxTimeoutMs = 30_000;

    private readonly AppInitializationOptions _options;
    private readonly IPhase15AppSettingsService _settings;
    private readonly HttpClient _httpClient;
    private readonly string _currentVersion;
    private readonly Func<DateTimeOffset> _clock;

    public GitHubUpdateCheckService(
        AppInitializationOptions options,
        IPhase15AppSettingsService settings,
        HttpClient? httpClient = null,
        string? currentVersion = null,
        Func<DateTimeOffset>? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClient = httpClient ?? new HttpClient();
        _currentVersion = NormalizeVersionForDisplay(currentVersion ?? ResolveCurrentVersion());
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask<UpdateCheckResultPayload> CheckForUpdatesAsync(
        CheckForUpdatesPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.TaskId))
        {
            throw new ArgumentException("Update check task id is required.", nameof(input));
        }

        var checkedAt = _clock();
        var settings = await _settings.GetUpdateCheckSettingsAsync(cancellationToken);
        if (!input.Manual && !settings.Enabled)
        {
            return await CompleteAsync(
                FailureLike(input.TaskId, "disabled", checkedAt, null, null),
                checkedAt,
                cancellationToken);
        }

        var endpoint = ResolveEndpoint(settings.EndpointUrl);
        if (endpoint is null)
        {
            return await CompleteAsync(
                Failed(input.TaskId, checkedAt, "update.endpoint_missing", "Update check endpoint is not configured."),
                checkedAt,
                cancellationToken);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(NormalizeTimeout(_options.UpdateCheckTimeoutMs));
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Novelist", SanitizeUserAgentVersion(_currentVersion)));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return await CompleteAsync(
                    Failed(
                        input.TaskId,
                        checkedAt,
                        "update.http_status",
                        $"Update endpoint returned HTTP {(int)response.StatusCode} {response.StatusCode}."),
                    checkedAt,
                    cancellationToken);
            }

            var release = await ReadReleaseAsync(response, timeout.Token);
            var result = BuildResult(input, settings, release, checkedAt);
            return await CompleteAsync(result, checkedAt, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await CompleteAsync(
                Failed(input.TaskId, checkedAt, "update.timeout", "Update check timed out."),
                checkedAt,
                cancellationToken);
        }
        catch (JsonException)
        {
            return await CompleteAsync(
                Failed(input.TaskId, checkedAt, "update.invalid_json", "Update endpoint returned invalid JSON."),
                checkedAt,
                cancellationToken);
        }
        catch (UpdateReleaseParseException ex)
        {
            return await CompleteAsync(
                Failed(input.TaskId, checkedAt, ex.Code, ex.Message),
                checkedAt,
                cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return await CompleteAsync(
                Failed(input.TaskId, checkedAt, "update.network_error", ex.Message),
                checkedAt,
                cancellationToken);
        }
    }

    private async ValueTask<UpdateCheckResultPayload> CompleteAsync(
        UpdateCheckResultPayload result,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        await _settings.SetUpdateCheckLastCheckedAtAsync(checkedAt, cancellationToken);
        return result;
    }

    private UpdateCheckResultPayload BuildResult(
        CheckForUpdatesPayload input,
        UpdateCheckSettingsPayload settings,
        ReleaseMetadata release,
        DateTimeOffset checkedAt)
    {
        if (string.IsNullOrWhiteSpace(release.Version))
        {
            throw new UpdateReleaseParseException(
                "update.release_version_missing",
                "Update release metadata does not contain a version.");
        }

        if (!SemanticVersion.TryParse(_currentVersion, out var current))
        {
            return Failed(
                input.TaskId,
                checkedAt,
                "update.current_version_invalid",
                $"Current application version '{_currentVersion}' is not a supported semantic version.");
        }

        if (!SemanticVersion.TryParse(release.Version, out var latest))
        {
            return Failed(
                input.TaskId,
                checkedAt,
                "update.release_version_invalid",
                $"Latest release version '{release.Version}' is not a supported semantic version.");
        }

        var available = latest.IsUpdateFor(current);
        var dismissed = available &&
            !input.Manual &&
            VersionsEquivalent(settings.DismissedVersion, release.Version);
        var status = available
            ? dismissed ? "dismissed" : "update_available"
            : "no_update";

        return new UpdateCheckResultPayload(
            TaskId: input.TaskId,
            Status: status,
            CurrentVersion: _currentVersion,
            LatestVersion: release.Version,
            ReleaseUrl: release.ReleaseUrl,
            CheckedAt: checkedAt,
            ErrorCode: null,
            ErrorMessage: null,
            ReleaseName: release.Name,
            ReleaseNotes: release.ReleaseNotes,
            DownloadUrl: release.DownloadUrl,
            Dismissed: dismissed);
    }

    private async ValueTask<ReleaseMetadata> ReadReleaseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ReadRelease(document.RootElement);
    }

    private static ReleaseMetadata ReadRelease(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && !ReadBool(item, "draft"))
                {
                    return ReadReleaseObject(item);
                }
            }

            throw new UpdateReleaseParseException(
                "update.release_missing",
                "Update endpoint did not return a readable release object.");
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array)
            {
                return ReadRelease(releases);
            }

            return ReadReleaseObject(root);
        }

        throw new UpdateReleaseParseException(
            "update.release_shape_invalid",
            "Update endpoint must return a release object or release array.");
    }

    private static ReleaseMetadata ReadReleaseObject(JsonElement release)
    {
        if (ReadBool(release, "draft"))
        {
            throw new UpdateReleaseParseException(
                "update.release_draft",
                "Latest release metadata points to a draft release.");
        }

        var version = ReadString(release, "tag_name") ??
            ReadString(release, "latest_version") ??
            ReadString(release, "version");
        var releaseUrl = ReadHttpsString(release, "html_url") ??
            ReadHttpsString(release, "release_url");
        var downloadUrl = ReadHttpsString(release, "download_url") ??
            ReadFirstAssetDownloadUrl(release);
        var notes = ReadString(release, "body") ??
            ReadString(release, "release_notes") ??
            ReadString(release, "notes");

        return new ReleaseMetadata(
            Version: version ?? string.Empty,
            Name: Truncate(ReadString(release, "name"), 512),
            ReleaseUrl: releaseUrl,
            ReleaseNotes: Truncate(notes, MaxReleaseNotesLength),
            DownloadUrl: downloadUrl);
    }

    private Uri? ResolveEndpoint(string settingsEndpoint)
    {
        var endpoint = string.IsNullOrWhiteSpace(settingsEndpoint)
            ? _options.UpdateCheckEndpointUrl
            : settingsEndpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return uri;
    }

    private UpdateCheckResultPayload Failed(
        string taskId,
        DateTimeOffset checkedAt,
        string code,
        string message)
    {
        return FailureLike(taskId, "failed", checkedAt, code, message);
    }

    private UpdateCheckResultPayload FailureLike(
        string taskId,
        string status,
        DateTimeOffset checkedAt,
        string? code,
        string? message)
    {
        return new UpdateCheckResultPayload(
            TaskId: taskId,
            Status: status,
            CurrentVersion: _currentVersion,
            LatestVersion: null,
            ReleaseUrl: null,
            CheckedAt: checkedAt,
            ErrorCode: code,
            ErrorMessage: message);
    }

    private static string? ReadFirstAssetDownloadUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var url = ReadHttpsString(asset, "browser_download_url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
    }

    private static bool ReadBool(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.True;
    }

    private static string? ReadHttpsString(JsonElement root, string name)
    {
        var value = ReadString(root, name);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? uri.ToString()
                : null;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static int NormalizeTimeout(int value)
    {
        return Math.Clamp(value, MinTimeoutMs, MaxTimeoutMs);
    }

    private static string ResolveCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(GitHubUpdateCheckService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return NormalizeVersionForDisplay(informational ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0");
    }

    private static string NormalizeVersionForDisplay(string value)
    {
        var trimmed = value.Trim();
        var plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? trimmed[..plus] : trimmed;
    }

    private static string SanitizeUserAgentVersion(string value)
    {
        var version = value.Trim().TrimStart('v', 'V');
        return version.Length == 0 || version.Any(char.IsWhiteSpace) ? "0.0.0" : version;
    }

    private static bool VersionsEquivalent(string dismissedVersion, string latestVersion)
    {
        if (string.Equals(dismissedVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return SemanticVersion.TryParse(dismissedVersion, out var dismissed) &&
            SemanticVersion.TryParse(latestVersion, out var latest) &&
            dismissed.CompareTo(latest) == 0;
    }

    private sealed record ReleaseMetadata(
        string Version,
        string? Name,
        string? ReleaseUrl,
        string? ReleaseNotes,
        string? DownloadUrl);

    private sealed class UpdateReleaseParseException : Exception
    {
        public UpdateReleaseParseException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    private readonly record struct SemanticVersion(
        int Major,
        int Minor,
        int Patch,
        IReadOnlyList<string> PrereleaseIdentifiers) : IComparable<SemanticVersion>
    {
        public bool IsPrerelease => PrereleaseIdentifiers.Count > 0;

        public static bool TryParse(string? value, out SemanticVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var text = value.Trim().TrimStart('v', 'V');
            var buildIndex = text.IndexOf('+', StringComparison.Ordinal);
            if (buildIndex >= 0)
            {
                text = text[..buildIndex];
            }

            string? prerelease = null;
            var preIndex = text.IndexOf('-', StringComparison.Ordinal);
            if (preIndex >= 0)
            {
                prerelease = text[(preIndex + 1)..];
                text = text[..preIndex];
            }

            var parts = text.Split('.', StringSplitOptions.None);
            if (parts.Length is < 1 or > 3)
            {
                return false;
            }

            Span<int> numbers = stackalloc int[3];
            for (var i = 0; i < numbers.Length; i++)
            {
                numbers[i] = 0;
            }

            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                    number < 0)
                {
                    return false;
                }

                numbers[i] = number;
            }

            var identifiers = string.IsNullOrWhiteSpace(prerelease)
                ? []
                : prerelease.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            version = new SemanticVersion(numbers[0], numbers[1], numbers[2], identifiers);
            return true;
        }

        public bool IsUpdateFor(SemanticVersion current)
        {
            if (IsPrerelease && !current.IsPrerelease)
            {
                return false;
            }

            return CompareTo(current) > 0;
        }

        public int CompareTo(SemanticVersion other)
        {
            var core = Major.CompareTo(other.Major);
            if (core != 0)
            {
                return core;
            }

            core = Minor.CompareTo(other.Minor);
            if (core != 0)
            {
                return core;
            }

            core = Patch.CompareTo(other.Patch);
            if (core != 0)
            {
                return core;
            }

            if (!IsPrerelease && !other.IsPrerelease)
            {
                return 0;
            }

            if (!IsPrerelease)
            {
                return 1;
            }

            if (!other.IsPrerelease)
            {
                return -1;
            }

            var count = Math.Max(PrereleaseIdentifiers.Count, other.PrereleaseIdentifiers.Count);
            for (var i = 0; i < count; i++)
            {
                if (i >= PrereleaseIdentifiers.Count)
                {
                    return -1;
                }

                if (i >= other.PrereleaseIdentifiers.Count)
                {
                    return 1;
                }

                var left = PrereleaseIdentifiers[i];
                var right = other.PrereleaseIdentifiers[i];
                var leftNumeric = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
                var rightNumeric = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
                if (leftNumeric && rightNumeric)
                {
                    var numeric = leftNumber.CompareTo(rightNumber);
                    if (numeric != 0)
                    {
                        return numeric;
                    }
                }
                else if (leftNumeric)
                {
                    return -1;
                }
                else if (rightNumeric)
                {
                    return 1;
                }
                else
                {
                    var lexical = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
                    if (lexical != 0)
                    {
                        return lexical;
                    }
                }
            }

            return 0;
        }
    }
}
