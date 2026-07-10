using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record WebFetchResultPayload(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("text")] string Text);

public sealed record WebSearchResultPayload(
    [property: JsonPropertyName("queries")] IReadOnlyList<string> Queries,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("sources")] IReadOnlyList<WebSearchSourcePayload> Sources);

public sealed record WebSearchSourcePayload(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url);
