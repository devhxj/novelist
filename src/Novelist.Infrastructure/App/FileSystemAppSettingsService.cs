using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemAppSettingsService : IAppSettingsService
{
    private const int MinChatPanelWidth = 240;
    private const int MaxChatPanelWidth = 1200;
    private const int MaxTextLength = 512;
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
        ValidateShortText(reasoningEffort, nameof(reasoningEffort), allowEmpty: true);
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
        ValidateShortText(reasoningEffort, nameof(reasoningEffort), allowEmpty: true);
        return MutateAsync(settings => settings with { ReasoningEffort = reasoningEffort }, cancellationToken);
    }

    public ValueTask SetChatPanelWidthAsync(int width, CancellationToken cancellationToken)
    {
        if (width is < MinChatPanelWidth or > MaxChatPanelWidth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                $"Chat panel width must be between {MinChatPanelWidth} and {MaxChatPanelWidth}.");
        }

        return MutateAsync(settings => settings with { ChatPanelWidth = width }, cancellationToken);
    }

    public ValueTask SetLastSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        ValidateShortText(sessionId, nameof(sessionId), allowEmpty: true);
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
        var trimmed = name.Trim();
        ValidateShortText(trimmed, nameof(name), allowEmpty: true);
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

    private static AppSettingsPayload DefaultSettings()
    {
        return new AppSettingsPayload(
            Id: 1,
            LastNovelId: 0,
            SelectedModelKey: string.Empty,
            ReasoningEffort: string.Empty,
            ApprovalMode: "manual",
            ChatPanelWidth: 360,
            LastSessionId: string.Empty,
            UserName: string.Empty);
    }

    private static AppSettingsPayload Validate(AppSettingsPayload settings)
    {
        if (settings.Id != 1)
        {
            settings = settings with { Id = 1 };
        }

        ValidateModelKey(settings.SelectedModelKey, allowEmpty: true);
        ValidateShortText(settings.ReasoningEffort, nameof(settings.ReasoningEffort), allowEmpty: true);
        ValidateShortText(settings.LastSessionId, nameof(settings.LastSessionId), allowEmpty: true);
        ValidateShortText(settings.UserName, nameof(settings.UserName), allowEmpty: true);

        if (!string.Equals(settings.ApprovalMode, "manual", StringComparison.Ordinal) &&
            !string.Equals(settings.ApprovalMode, "auto", StringComparison.Ordinal))
        {
            settings = settings with { ApprovalMode = "manual" };
        }

        if (settings.ChatPanelWidth is < MinChatPanelWidth or > MaxChatPanelWidth)
        {
            settings = settings with { ChatPanelWidth = 360 };
        }

        return settings;
    }

    private static void ValidateModelKey(string value, bool allowEmpty = false)
    {
        ValidateShortText(value, nameof(value), allowEmpty);
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var parts = value.Split('/', StringSplitOptions.None);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new ArgumentException("Selected model key must use 'provider/model' format.", nameof(value));
        }
    }

    private static void ValidateShortText(string value, string name, bool allowEmpty)
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
    }
}
