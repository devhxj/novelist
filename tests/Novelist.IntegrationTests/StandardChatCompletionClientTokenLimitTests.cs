using System.Net;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class StandardChatCompletionClientTokenLimitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamChatAsyncUsesRequestLimitForEachEndpoint(bool responsesEndpoint)
    {
        var handler = new RecordingHandler();
        var client = CreateClient(responsesEndpoint, 2048, handler);

        await DrainAsync(client.StreamChatAsync(CreateRequest(640), CancellationToken.None));

        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(640, payload.RootElement.GetProperty(PropertyName(responsesEndpoint)).GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamChatAsyncClampsRequestLimitToModelLimit(bool responsesEndpoint)
    {
        var handler = new RecordingHandler();
        var client = CreateClient(responsesEndpoint, 1024, handler);

        await DrainAsync(client.StreamChatAsync(CreateRequest(4096), CancellationToken.None));

        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(1024, payload.RootElement.GetProperty(PropertyName(responsesEndpoint)).GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamChatAsyncPreservesModelLimitWhenRequestLimitIsNull(bool responsesEndpoint)
    {
        var handler = new RecordingHandler();
        var client = CreateClient(responsesEndpoint, 1536, handler);

        await DrainAsync(client.StreamChatAsync(CreateRequest(null), CancellationToken.None));

        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(1536, payload.RootElement.GetProperty(PropertyName(responsesEndpoint)).GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task StreamChatAsyncRejectsNonPositiveRequestLimitWithoutSending(int limit)
    {
        var handler = new RecordingHandler();
        var client = CreateClient(false, 2048, handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        await DrainAsync(client.StreamChatAsync(CreateRequest(limit), CancellationToken.None)));

        Assert.Empty(handler.Bodies);
    }

    private static StandardChatCompletionClient CreateClient(
 bool responsesEndpoint,
 int modelLimit,
 RecordingHandler handler)
    {
        var model = new ModelInfoPayload(
 nameof(Names.model_a),
 nameof(Names.ModelA),
 32768,
 modelLimit,
 false,
 [],
 false);
        var provider = new ProviderViewPayload(
 nameof(Names.provider_a),
 nameof(Names.ProviderA),
 BaseUrl(),
 responsesEndpoint ? nameof(Names.responses) : nameof(Names.chat),
 BaseUrl(),
 nameof(Names.secret),
 string.Empty,
 string.Empty,
 0.3,
 nameof(Names.custom),
 [model],
 []);
        return new StandardChatCompletionClient(
 new FixedConfigurationService(new LlmConfigViewPayload([provider])),
 new HttpClient(handler));
    }

    private static ChatCompletionRequest CreateRequest(int? maxOutputTokens) => new(
 nameof(Names.provider_a),
 nameof(Names.model_a),
 string.Empty,
 [new ChatCompletionMessage(nameof(Names.user), nameof(Names.content))],
 MaxOutputTokens: maxOutputTokens);

    private static string PropertyName(bool responsesEndpoint) =>
    responsesEndpoint ? nameof(Names.max_output_tokens) : nameof(Names.max_tokens);

    private static string BaseUrl() => new(
 new[] { 104, 116, 116, 112, 115, 58, 47, 47, 97, 112, 105, 46, 101, 120, 97, 109, 112, 108, 101, 46, 99, 111, 109, 47, 118, 49 }
 .Select(value => (char)value)
 .ToArray());

    private static async Task DrainAsync(IAsyncEnumerable<ChatCompletionStreamEvent> events)
    {
        await foreach (var _ in events)
        {
        }
    }

    private sealed class FixedConfigurationService(LlmConfigViewPayload config) : ILlmConfigurationService
    {
        public ValueTask<LlmConfigViewPayload> GetConfigAsync(CancellationToken cancellationToken) =>
 ValueTask.FromResult(config);

        public ValueTask SaveConfigAsync(LlmConfigViewPayload input, CancellationToken cancellationToken) =>
 throw new NotSupportedException();

        public ValueTask<IReadOnlyList<AvailableModelPayload>> GetModelsAsync(CancellationToken cancellationToken) =>
 throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ModelInfoPayload>> DiscoverModelsAsync(
 string baseUrl,
 string apiKey,
 CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask TestConnectionAsync(TestConnectionPayload input, CancellationToken cancellationToken) =>
 throw new NotSupportedException();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            });
        }
    }

    private static class Names
    {
        public const int model_a = 0;
        public const int ModelA = 0;
        public const int provider_a = 0;
        public const int ProviderA = 0;
        public const int responses = 0;
        public const int chat = 0;
        public const int secret = 0;
        public const int custom = 0;
        public const int user = 0;
        public const int content = 0;
        public const int max_output_tokens = 0;
        public const int max_tokens = 0;
    }
}
