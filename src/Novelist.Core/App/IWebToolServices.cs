using System.Net;
using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IWebFetchService
{
    ValueTask<WebFetchResultPayload> FetchAsync(string url, CancellationToken cancellationToken);
}

public interface IWebSearchService
{
    ValueTask<WebSearchResultPayload> SearchAsync(string prompt, CancellationToken cancellationToken);
}

public interface IWebHostAddressResolver
{
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}
