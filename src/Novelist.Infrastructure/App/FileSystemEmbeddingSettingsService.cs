using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemEmbeddingSettingsService : IEmbeddingSettingsService
{
    private const int MaxProviderKeyLength = 128;
    private const int MaxUrlLength = 2_048;
    private const int MaxApiKeyLength = 4_096;
    private const int MaxModelIdLength = 256;
    private const int MaxUserLength = 256;
    private const int MaxDimensions = 1_000_000;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly byte[] AppKey =
    [
        0x7a, 0x3f, 0x71, 0xe2, 0x5c, 0x9d, 0x0b, 0x46,
        0x1a, 0x5f, 0x33, 0xc8, 0x6e, 0x22, 0x4d, 0x0f,
        0x85, 0xce, 0x1c, 0x29, 0x3f, 0xa7, 0x80, 0xf4,
        0x2e, 0x9c, 0x17, 0xd5, 0x4a, 0x8e, 0xd2, 0x06
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly EmbeddingConfigPayload EmptyConfig = new(
        ProviderKey: string.Empty,
        EndpointUrl: string.Empty,
        ApiKey: string.Empty,
        ModelId: string.Empty,
        Dimensions: null,
        User: string.Empty);

    private readonly AppInitializationOptions _options;
    private readonly IEmbeddingClient _embeddings;
    private readonly ISqliteVecExtensionResolver _sqliteVecResolver;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemEmbeddingSettingsService(
        AppInitializationOptions? options = null,
        IEmbeddingClient? embeddings = null,
        ISqliteVecExtensionResolver? sqliteVecResolver = null)
    {
        _options = options ?? new AppInitializationOptions();
        _embeddings = embeddings ?? new StandardEmbeddingClient();
        _sqliteVecResolver = sqliteVecResolver ?? new PackagedSqliteVecExtensionResolver();
    }

    public async ValueTask<EmbeddingConfigPayload> GetConfigAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            return await LoadAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask SaveConfigAsync(
        EmbeddingConfigPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = NormalizeConfig(input, allowDisabled: true);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            await SaveAsync(normalized, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask TestConnectionAsync(
        EmbeddingConfigPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var options = ToOptions(NormalizeConfig(input, allowDisabled: false));
        await _embeddings.EmbedAsync(["novelist embedding test"], options, cancellationToken);
    }

    public async ValueTask<EmbeddingRequestOptions?> GetActiveEmbeddingOptionsAsync(
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var config = await LoadAsync(cancellationToken);
            return IsDisabled(config) ? null : ToOptions(config);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public ValueTask<SqliteVecStatusPayload> GetSqliteVecStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _sqliteVecResolver.Resolve();
        return ValueTask.FromResult(new SqliteVecStatusPayload(
            resolved.Available,
            resolved.Status,
            resolved.RuntimeIdentifier,
            resolved.Available ? Path.GetFileName(resolved.ExtensionPath) : string.Empty,
            resolved.Error));
    }

    private async ValueTask<EmbeddingConfigPayload> LoadAsync(CancellationToken cancellationToken)
    {
        var path = await StorePathAsync(cancellationToken);
        if (!File.Exists(path))
        {
            return EmptyConfig;
        }

        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
        var plain = Decrypt(encrypted);
        var config = JsonSerializer.Deserialize<EmbeddingConfigPayload>(plain, JsonOptions)
            ?? throw new InvalidOperationException("Embedding config is empty or malformed.");
        return NormalizeConfig(config, allowDisabled: true);
    }

    private async ValueTask SaveAsync(
        EmbeddingConfigPayload config,
        CancellationToken cancellationToken)
    {
        var path = await StorePathAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions);
        var encrypted = Encrypt(plain);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllBytesAsync(tempPath, encrypted, cancellationToken);
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

    private async ValueTask<string> StorePathAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken), "embedding", "config.enc");
    }

    private static EmbeddingConfigPayload NormalizeConfig(
        EmbeddingConfigPayload input,
        bool allowDisabled)
    {
        if (allowDisabled && IsDisabled(input))
        {
            return EmptyConfig;
        }

        var providerKey = NormalizeProviderKey(input.ProviderKey);
        var endpointUrl = NormalizeEndpointUrl(input.EndpointUrl);
        var apiKey = NormalizeRequiredText(input.ApiKey, nameof(input.ApiKey), MaxApiKeyLength);
        var modelId = NormalizeRequiredText(input.ModelId, nameof(input.ModelId), MaxModelIdLength);
        if (input.Dimensions is <= 0 or > MaxDimensions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input.Dimensions),
                input.Dimensions,
                $"Dimensions must be between 1 and {MaxDimensions}.");
        }

        var user = string.IsNullOrWhiteSpace(input.User)
            ? string.Empty
            : NormalizeRequiredText(input.User, nameof(input.User), MaxUserLength);

        return new EmbeddingConfigPayload(providerKey, endpointUrl, apiKey, modelId, input.Dimensions, user);
    }

    private static EmbeddingRequestOptions ToOptions(EmbeddingConfigPayload config)
    {
        return new EmbeddingRequestOptions(
            config.ProviderKey,
            config.EndpointUrl,
            config.ApiKey,
            config.ModelId,
            config.Dimensions,
            string.IsNullOrWhiteSpace(config.User) ? null : config.User);
    }

    private static bool IsDisabled(EmbeddingConfigPayload input)
    {
        return string.IsNullOrWhiteSpace(input.ProviderKey) &&
            string.IsNullOrWhiteSpace(input.EndpointUrl) &&
            string.IsNullOrWhiteSpace(input.ApiKey) &&
            string.IsNullOrWhiteSpace(input.ModelId) &&
            input.Dimensions is null &&
            string.IsNullOrWhiteSpace(input.User);
    }

    private static string NormalizeProviderKey(string? value)
    {
        var normalized = NormalizeRequiredText(value, nameof(value), MaxProviderKeyLength).ToLowerInvariant();
        if (normalized.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            throw new ArgumentException("Provider key may only contain letters, digits, hyphen, underscore, and dot.", nameof(value));
        }

        return normalized;
    }

    private static string NormalizeEndpointUrl(string? raw)
    {
        var value = NormalizeRequiredText(raw, nameof(raw), MaxUrlLength);
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^"/chat/completions".Length];
        }

        if (!value.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
        {
            value = value.TrimEnd('/') + "/embeddings";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Embedding endpoint must be an absolute http:// or https:// URL.", nameof(raw));
        }

        return uri.ToString();
    }

    private static string NormalizeRequiredText(string? value, string name, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value must be a non-empty string.", name);
        }

        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(name, normalized.Length, $"Value must be at most {maxLength} characters.");
        }

        if (normalized.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')))
        {
            throw new ArgumentException("Value must not contain unsupported control characters.", name);
        }

        return normalized;
    }

    private static byte[] Encrypt(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(AppKey, TagSize);
        aes.Encrypt(nonce, plain, ciphertext, tag);

        var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length + ciphertext.Length, tag.Length);
        return result;
    }

    private static byte[] Decrypt(byte[] data)
    {
        if (data.Length < NonceSize + TagSize)
        {
            throw new InvalidOperationException("Embedding config ciphertext is too short.");
        }

        var nonce = data[..NonceSize];
        var ciphertext = data[NonceSize..^TagSize];
        var tag = data[^TagSize..];
        var plain = new byte[ciphertext.Length];
        using var aes = new AesGcm(AppKey, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plain);
        return plain;
    }
}
