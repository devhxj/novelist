using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IAppInitializationService
{
    ValueTask<bool> IsInitializedAsync(CancellationToken cancellationToken);

    ValueTask InitializeAsync(string dataDirectory, CancellationToken cancellationToken);

    ValueTask<AppConfigPayload> GetAppConfigAsync(CancellationToken cancellationToken);

    ValueTask UpdateDataDirectoryAsync(string dataDirectory, CancellationToken cancellationToken);

    ValueTask<PlatformPayload> GetPlatformAsync(CancellationToken cancellationToken);
}
