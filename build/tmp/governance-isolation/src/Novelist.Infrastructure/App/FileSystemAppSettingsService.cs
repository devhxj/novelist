using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemAppSettingsService : IPhase15AppSettingsService
{
    private const int DefaultSidebarWidth = 280;
    private const int MinSidebarWidth = 220;
    private const int MaxSidebarWidth = 640;
    private const int DefaultChatPanelWidth = 360;
    private const int MinChatPanelWidth = 240;
    private const int MaxChatPanelWidth = 1200;
    private const int DefaultMetadataPanelWidth = 320;
    private const int MinMetadataPanelWidth = 240;
    private const int MaxMetadataPanelWidth = 900;
    private const int DefaultWindowWidth = 1280;
    private const int DefaultWindowHeight = 840;
    private const int MinWindowWidth = 800;
    private const int MaxWindowWidth = 3840;
    private const int MinWindowHeight = 600;
    private const int MaxWindowHeight = 2160;
    private const int MaxWindowCoordinateAbs = 100000;
    private const int MaxTextLength = 512;
    private const int MaxEndpointUrlLength = 2048;
    private const int MaxAvatarBytes = 5 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemAppSettingsService(AppInitializationOptions? options = null)
    {
        _options = options ?? new AppInitializationOptions();
    }

    public async ValueTask<AppSettingsPayload> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            return await LoadOrCreateAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask SaveSettingsAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var settings = await LoadOrCreateAsync(cancellationToken);
            await SaveAsync(settings, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public ValueTask SetSelectedModelAsync(
        string selectedModelKey,
        string reasoningEffort,
        CancellationToken cancellationToken)
    {
        ValidateModelKey(selectedModelKey);
        ValidateShortTextForSave(reasoningEffort, nameof(reasoningEffort), allowEmpty: true);
        return MutateAsync(
            settings => settings with
            {
                SelectedModelKey = selectedModelKey,
                ReasoningEffort = reasoningEffort
            },
            cancellationToken);
    }

    public ValueTask SetReasoningEffortAsync(string reasoningEffort, CancellationToken cancellationToken)
    {
        ValidateShortTextForSave(reasoningEffort, nameof(reasoningEffort), allowEmpty: true);
        return MutateAsync(settings => settings with { ReasoningEffort = reasoningEffort }, cancellationToken);
    }

    public ValueTask SetChatPanelWidthAsync(int width, CancellationToken cancellationToken)
    {
        ValidateRange(width, nameof(width), MinChatPanelWidth, MaxChatPanelWidth);
        return MutateAsync(settings => settings with { ChatPanelWidth = width }, cancellationToken);
    }

    public ValueTask SetLastSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        ValidateSessionIdForSave(sessionId, nameof(sessionId), allowEmpty: true);
        return MutateAsync(settings => settings with { LastSessionId = sessionId }, cancellationToken);
    }

    public ValueTask SetLastNovelAsync(long novelId, CancellationToken cancellationToken)
    {
        if (novelId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must not be negative.");
        }

        return MutateAsync(settings => settings with { LastNovelId = novelId }, cancellationToken);
    }

    public ValueTask SetApprovalModeAsync(string mode, CancellationToken cancellationToken)
    {
        if (!string.Equals(mode, "manual", StringComparison.Ordinal) &&
            !string.Equals(mode, "auto", StringComparison.Ordinal))
        {
            throw new ArgumentException("Approval mode must be 'manual' or 'auto'.", nameof(mode));
        }

        return MutateAsync(settings => settings with { ApprovalMode = mode }, cancellationToken);
    }

    public ValueTask SaveUserNameAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = name.Trim();
        ValidateShortTextForSave(trimmed, nameof(name), allowEmpty: true);
        return MutateAsync(settings => settings with { UserName = trimmed }, cancellationToken);
    }

    public async ValueTask SaveAvatarAsync(byte[] data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
        {
            throw new ArgumentException("Avatar data is required.", nameof(data));
        }

        if (data.Length > MaxAvatarBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(data), data.Length, "Avatar data exceeds 5 MiB.");
        }

        var userDirectory = Path.Combine(await ResolveDataDirectoryAsync(cancellationToken), "user");
        Directory.CreateDirectory(userDirectory);
        await File.WriteAllBytesAsync(Path.Combine(userDirectory, "avatar.jpg"), data, cancellationToken);
    }

    public async ValueTask<GitAuthorSettingsPayload> GetGitAuthorSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        return ToGitAuthorSettings(settings);
    }

    public ValueTask<GitAuthorSettingsPayload> SaveGitAuthorSettingsAsync(
        SaveGitAuthorSettingsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (name, email) = ValidateGitAuthor(input.Name, input.Email);
        return MutateAndReturnAsync(
            settings =>
            {
                var updated = settings with
                {
                    GitAuthorName = name,
                    GitAuthorEmail = email
                };
                return (updated, ToGitAuthorSettings(updated));
            },
            cancellationToken);
    }

    public async ValueTask<UpdateCheckSettingsPayload> GetUpdateCheckSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        return ToUpdateCheckSettings(settings);
    }

    public ValueTask<UpdateCheckSettingsPayload> SaveUpdateCheckSettingsAsync(
        SaveUpdateCheckSettingsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var endpointUrl = NormalizeUpdateEndpointForSave(input.EndpointUrl, input.Enabled);
        var dismissedVersion = NormalizeShortTextForSave(
            input.DismissedVersion?.Trim(),
            nameof(input.DismissedVersion),
            allowEmpty: true);
        return MutateAndReturnAsync(
            settings =>
            {
                var updated = settings with
                {
                    UpdateCheckEnabled = input.Enabled && endpointUrl.Length > 0,
                    UpdateCheckEndpointUrl = endpointUrl,
                    UpdateCheckDismissedVersion = dismissedVersion
                };
                return (updated, ToUpdateCheckSettings(updated));
            },
            cancellationToken);
    }

    public ValueTask SetUpdateCheckLastCheckedAtAsync(DateTimeOffset? checkedAt, CancellationToken cancellationToken)
    {
        return MutateAsync(settings => settings with { UpdateCheckLastCheckedAt = checkedAt }, cancellationToken);
    }

    public async ValueTask<LayoutSettingsPayload> GetLayoutSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        return ToLayoutSettings(settings);
    }

    public ValueTask<LayoutSettingsPayload> SaveLayoutSettingsAsync(
        SaveLayoutSettingsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateRange(input.SidebarWidth, nameof(input.SidebarWidth), MinSidebarWidth, MaxSidebarWidth);
        ValidateRange(input.ChatPanelWidth, nameof(input.ChatPanelWidth), MinChatPanelWidth, MaxChatPanelWidth);
        ValidateRange(input.MetadataPanelWidth, nameof(input.MetadataPanelWidth), MinMetadataPanelWidth, MaxMetadataPanelWidth);
        return MutateAndReturnAsync(
            settings =>
            {
                var updated = settings with
                {
                    SidebarWidth = input.SidebarWidth,
                    ChatPanelWidth = input.ChatPanelWidth,
                    MetadataPanelWidth = input.MetadataPanelWidth
                };
                return (updated, ToLayoutSettings(updated));
            },
            cancellationToken);
    }

    public async ValueTask<WindowSettingsPayload> GetWindowSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        return ToWindowSettings(settings);
    }

    public ValueTask<WindowSettingsPayload> SaveWindowSettingsAsync(
        SaveWindowSettingsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateOptionalWindowCoordinate(input.X, nameof(input.X));
        ValidateOptionalWindowCoordinate(input.Y, nameof(input.Y));
        ValidateRange(input.Width, nameof(input.Width), MinWindowWidth, MaxWindowWidth);
        ValidateRange(input.Height, nameof(input.Height), MinWindowHeight, MaxWindowHeight);
        return MutateAndReturnAsync(
            settings =>
            {
                var updated = settings with
                {
                    WindowX = input.X,
                    WindowY = input.Y,
                    WindowWidth = input.Width,
                    WindowHeight = input.Height,
                    WindowMaximized = input.Maximized
                };
                return (updated, ToWindowSettings(updated));
            },
            cancellationToken);
    }

    private async ValueTask MutateAsync(
        Func<AppSettingsPayload, AppSettingsPayload> update,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadOrCreateAsync(cancellationToken);
            await SaveAsync(Validate(update(current)), cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<TResult> MutateAndReturnAsync<TResult>(
        Func<AppSettingsPayload, (AppSettingsPayload Settings, TResult Result)> update,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadOrCreateAsync(cancellationToken);
            var (settings, result) = update(current);
            await SaveAsync(Validate(settings), cancellationToken);
            return result;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<AppSettingsPayload> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var path = await SettingsPathAsync(cancellationToken);
        if (!File.Exists(path))
        {
            var defaults = DefaultSettings();
            await SaveAsync(defaults, cancellationToken);
            return defaults;
        }

        await using var stream = File.OpenRead(path);
        var settings = await JsonSerializer.DeserializeAsync<AppSettingsPayload>(stream, JsonOptions, cancellationToken);
        return Validate(settings ?? DefaultSettings());
    }

    private async ValueTask SaveAsync(AppSettingsPayload settings, CancellationToken cancellationToken)
    {
        var path = await SettingsPathAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async ValueTask<string> SettingsPathAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(await ResolveDataDirectoryAsync(cancellationToken), "app_settings.json");
    }

    private async ValueTask<string> ResolveDataDirectoryAsync(CancellationToken cancellationToken)
    {
        return await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken);
    }

    private AppSettingsPayload DefaultSettings()
    {
        var updateEndpoint = NormalizeUpdateEndpointForStorage(_options.UpdateCheckEndpointUrl, fallback: string.Empty);
        return new AppSettingsPayload(
            Id: 1,
            LastNovelId: 0,
            SelectedModelKey: string.Empty,
            ReasoningEffort: string.Empty,
            ApprovalMode: "manual",
            ChatPanelWidth: DefaultChatPanelWidth,
            LastSessionId: string.Empty,
            UserName: string.Empty,
            UpdateCheckEnabled: _options.UpdateChecksEnabledByDefault && updateEndpoint.Length > 0,
            UpdateCheckEndpointUrl: updateEndpoint,
            SidebarWidth: DefaultSidebarWidth,
            MetadataPanelWidth: DefaultMetadataPanelWidth,
            WindowWidth: DefaultWindowWidth,
            WindowHeight: DefaultWindowHeight);
    }

    private AppSettingsPayload Validate(AppSettingsPayload settings)
    {
        if (settings.Id != 1)
        {
            settings = settings with { Id = 1 };
        }

        settings = settings with
        {
            SelectedModelKey = IsValidModelKey(settings.SelectedModelKey) ? settings.SelectedModelKey : string.Empty,
            ReasoningEffort = NormalizeStoredShortText(settings.ReasoningEffort),
            LastSessionId = NormalizeStoredSessionId(settings.LastSessionId),
            UserName = NormalizeStoredShortText(settings.UserName).Trim()
        };

        if (!string.Equals(settings.ApprovalMode, "manual", StringComparison.Ordinal) &&
            !string.Equals(settings.ApprovalMode, "auto", StringComparison.Ordinal))
        {
            settings = settings with { ApprovalMode = "manual" };
        }

        if (settings.ChatPanelWidth is < MinChatPanelWidth or > MaxChatPanelWidth)
        {
            settings = settings with { ChatPanelWidth = DefaultChatPanelWidth };
        }

        var (authorName, authorEmail) = NormalizeStoredGitAuthor(settings.GitAuthorName, settings.GitAuthorEmail);
        var updateEndpoint = NormalizeUpdateEndpointForStorage(
            settings.UpdateCheckEndpointUrl,
            NormalizeUpdateEndpointForStorage(_options.UpdateCheckEndpointUrl, fallback: string.Empty));
        var dismissedVersion = NormalizeStoredShortText(settings.UpdateCheckDismissedVersion).Trim();

        return settings with
        {
            GitAuthorName = authorName,
            GitAuthorEmail = authorEmail,
            UpdateCheckEnabled = settings.UpdateCheckEnabled && updateEndpoint.Length > 0,
            UpdateCheckEndpointUrl = updateEndpoint,
            UpdateCheckDismissedVersion = dismissedVersion,
            SidebarWidth = NormalizeRange(settings.SidebarWidth, MinSidebarWidth, MaxSidebarWidth, DefaultSidebarWidth),
            MetadataPanelWidth = NormalizeRange(settings.MetadataPanelWidth, MinMetadataPanelWidth, MaxMetadataPanelWidth, DefaultMetadataPanelWidth),
            WindowX = NormalizeWindowCoordinate(settings.WindowX),
            WindowY = NormalizeWindowCoordinate(settings.WindowY),
            WindowWidth = NormalizeRange(settings.WindowWidth, MinWindowWidth, MaxWindowWidth, DefaultWindowWidth),
            WindowHeight = NormalizeRange(settings.WindowHeight, MinWindowHeight, MaxWindowHeight, DefaultWindowHeight)
        };
    }

    private static void ValidateModelKey(string value, bool allowEmpty = false)
    {
        ValidateShortTextForSave(value, nameof(value), allowEmpty);
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (!IsValidModelKey(value))
        {
            throw new ArgumentException("Selected model key must use 'provider/model' format.", nameof(value));
        }
    }

    private static string NormalizeShortTextForSave(string? value, string name, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must be non-empty.", name);
        }

        if (value.Length > MaxTextLength)
        {
            throw new ArgumentOutOfRangeException(name, value.Length, $"Value must be at most {MaxTextLength} characters.");
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Value must not contain control characters.", name);
        }

        return value;
    }

    private static void ValidateShortTextForSave(string? value, string name, bool allowEmpty)
    {
        _ = NormalizeShortTextForSave(value, name, allowEmpty);
    }

    private static void ValidateSessionIdForSave(string? value, string name, bool allowEmpty)
    {
        ValidateShortTextForSave(value, name, allowEmpty);
        if (value!.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Session id must not contain whitespace.", name);
        }
    }

    private static bool IsValidModelKey(string? value)
    {
        if (!IsValidStoredShortText(value))
        {
            return false;
        }

        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        var parts = value.Split('/', StringSplitOptions.None);
        return parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]);
    }

    private static string NormalizeStoredShortText(string? value)
    {
        return IsValidStoredShortText(value) ? value! : string.Empty;
    }

    private static string NormalizeStoredSessionId(string? value)
    {
        if (!IsValidStoredShortText(value))
        {
            return string.Empty;
        }

        var sessionId = value!;
        return sessionId.Any(char.IsWhiteSpace) ? string.Empty : sessionId;
    }

    private static bool IsValidStoredShortText(string? value)
    {
        return value is not null && value.Length <= MaxTextLength && !value.Any(char.IsControl);
    }

    private static (string Name, string Email) ValidateGitAuthor(string? name, string? email)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(email);
        var normalizedName = NormalizeShortTextForSave(name.Trim(), nameof(name), allowEmpty: true);
        var normalizedEmail = NormalizeShortTextForSave(email.Trim(), nameof(email), allowEmpty: true);
        if (normalizedName.Length == 0 && normalizedEmail.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        if (normalizedName.Length == 0 || normalizedEmail.Length == 0 || !IsValidGitEmail(normalizedEmail))
        {
            throw new ArgumentException("Git author name and a valid email must be provided together.");
        }

        return (normalizedName, normalizedEmail);
    }

    private static (string Name, string Email) NormalizeStoredGitAuthor(string? name, string? email)
    {
        var normalizedName = NormalizeStoredShortText(name).Trim();
        var normalizedEmail = NormalizeStoredShortText(email).Trim();
        return normalizedName.Length > 0 && IsValidGitEmail(normalizedEmail)
            ? (normalizedName, normalizedEmail)
            : (string.Empty, string.Empty);
    }

    private static bool IsValidGitEmail(string email)
    {
        return email.Length is > 2 and <= 320 &&
            !email.Any(char.IsWhiteSpace) &&
            email.Count(ch => ch == '@') == 1 &&
            email.IndexOf('@', StringComparison.Ordinal) > 0 &&
            email.IndexOf('@', StringComparison.Ordinal) < email.Length - 1;
    }

    private static string NormalizeUpdateEndpointForSave(string? value, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = NormalizeShortTextForSave(value.Trim(), nameof(value), allowEmpty: !enabled);
        if (trimmed.Length == 0)
        {
            if (enabled)
            {
                throw new ArgumentException("Update check endpoint URL is required when update checks are enabled.");
            }

            return string.Empty;
        }

        if (!IsValidHttpsUrl(trimmed))
        {
            throw new ArgumentException("Update check endpoint URL must be an absolute https:// URL.");
        }

        return trimmed;
    }

    private static string NormalizeUpdateEndpointForStorage(string? value, string fallback)
    {
        var trimmed = NormalizeStoredShortText(value).Trim();
        return IsValidHttpsUrl(trimmed) ? trimmed : fallback;
    }

    private static bool IsValidHttpsUrl(string value)
    {
        return value.Length is > 0 and <= MaxEndpointUrlLength &&
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static int NormalizeRange(int value, int min, int max, int fallback)
    {
        return value >= min && value <= max ? value : fallback;
    }

    private static void ValidateRange(int value, string name, int min, int max)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {min} and {max}.");
        }
    }

    private static int? NormalizeWindowCoordinate(int? value)
    {
        return value is >= -MaxWindowCoordinateAbs and <= MaxWindowCoordinateAbs ? value : null;
    }

    private static void ValidateOptionalWindowCoordinate(int? value, string name)
    {
        if (value is not null && value is (< -MaxWindowCoordinateAbs or > MaxWindowCoordinateAbs))
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} is outside the supported desktop coordinate range.");
        }
    }

    private static GitAuthorSettingsPayload ToGitAuthorSettings(AppSettingsPayload settings)
    {
        return new GitAuthorSettingsPayload(settings.GitAuthorName, settings.GitAuthorEmail, "app");
    }

    private static UpdateCheckSettingsPayload ToUpdateCheckSettings(AppSettingsPayload settings)
    {
        return new UpdateCheckSettingsPayload(
            settings.UpdateCheckEnabled,
            settings.UpdateCheckEndpointUrl,
            settings.UpdateCheckDismissedVersion,
            settings.UpdateCheckLastCheckedAt);
    }

    private static LayoutSettingsPayload ToLayoutSettings(AppSettingsPayload settings)
    {
        return new LayoutSettingsPayload(
            settings.SidebarWidth,
            settings.ChatPanelWidth,
            settings.MetadataPanelWidth);
    }

    private static WindowSettingsPayload ToWindowSettings(AppSettingsPayload settings)
    {
        return new WindowSettingsPayload(
            settings.WindowX,
            settings.WindowY,
            settings.WindowWidth,
            settings.WindowHeight,
            settings.WindowMaximized);
    }
}
