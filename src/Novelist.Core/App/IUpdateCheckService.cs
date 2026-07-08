using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IUpdateCheckService
{
    ValueTask<UpdateCheckResultPayload> CheckForUpdatesAsync(
        CheckForUpdatesPayload input,
        CancellationToken cancellationToken);
}
