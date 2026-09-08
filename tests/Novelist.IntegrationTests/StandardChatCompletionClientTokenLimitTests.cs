using System.Net;
using System.Text;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Core.Bridge;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamChatAsyncUsesExplicitTemperatureOverrideForEachEndpoint(bool responsesEndpoint)
    {
        var handler = new RecordingHandler();
        var client = CreateClient(responsesEndpoint, 2048, handler);

        await DrainAsync(client.StreamChatAsync(CreateRequest(640, temperatureOverride: 0), CancellationToken.None));

        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(0, payload.RootElement.GetProperty("temperature").GetDouble());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamChatAsyncCarriesStrictToolDefinitionsForEachEndpoint(bool responsesEndpoint)
    {
        var handler = new RecordingHandler();
        var client = CreateClient(responsesEndpoint, 2048, handler);
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        var request = CreateRequest(640) with
        {
            Tools = [new ChatToolDefinition("submit_result", "Submit a result.", schema.RootElement.Clone(), Strict: true)]
        };

        await DrainAsync(client.StreamChatAsync(request, CancellationToken.None));

        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var tool = payload.RootElement.GetProperty("tools")[0];
        var strict = responsesEndpoint
            ? tool.GetProperty("strict").GetBoolean()
            : tool.GetProperty("function").GetProperty("strict").GetBoolean();
        Assert.True(strict);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamChatAsyncDefaultsToolChoiceToAuto(bool responsesEndpoint)
    {
        var handler = new RecordingHandler();
        var client = CreateClient(responsesEndpoint, 2048, handler);
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        var request = CreateRequest(640) with
        {
            Tools = [new ChatToolDefinition("submit_result", "Submit a result.", schema.RootElement.Clone())]
        };

        await DrainAsync(client.StreamChatAsync(request, CancellationToken.None));

        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("auto", payload.RootElement.GetProperty("tool_choice").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamChatAsyncPropagatesRequiredToolChoice(bool responsesEndpoint)
    {
        var handler = new RecordingHandler();
        var client = CreateClient(responsesEndpoint, 2048, handler);
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        var request = CreateRequest(640) with
        {
            Tools = [new ChatToolDefinition("submit_result", "Submit a result.", schema.RootElement.Clone())],
            RequireToolCall = true
        };

        await DrainAsync(client.StreamChatAsync(request, CancellationToken.None));

        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("required", payload.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task StreamChatAsyncSurfacesIncompleteReasonForResponsesEndpoint()
    {
        var handler = new RecordingHandler(responsesStream: """
            {"type":"response.created","response":{}}
            {"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"}}}
            """);
        var client = CreateClient(responsesEndpoint: true, 2048, handler);

        var exception = await Assert.ThrowsAsync<BridgeRequestException>(async () =>
            await DrainAsync(client.StreamChatAsync(CreateRequest(640), CancellationToken.None)));

        Assert.Contains("输出预算", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamChatAsyncTreatsLengthIncompleteReasonAsBudgetExhaustion()
    {
        var handler = new RecordingHandler(responsesStream: """
            {"type":"response.created","response":{}}
            {"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"length"}}}
            """);
        var client = CreateClient(responsesEndpoint: true, 2048, handler);

        var exception = await Assert.ThrowsAsync<BridgeRequestException>(async () =>
            await DrainAsync(client.StreamChatAsync(CreateRequest(640), CancellationToken.None)));

        // DeepSeek 风格的 Responses 端点用 chat-completions 的 "length" 表示预算耗尽。
        Assert.Contains("输出预算", exception.Message, StringComparison.Ordinal);
        Assert.Contains("length", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamChatAsyncSurfacesFailureEventForResponsesEndpoint()
    {
        var handler = new RecordingHandler(responsesStream: """
            {"type":"response.created","response":{}}
            {"type":"response.failed","response":{"error":{"code":"server_error","message":"upstream overloaded"}}}
            """);
        var client = CreateClient(responsesEndpoint: true, 2048, handler);

        var exception = await Assert.ThrowsAsync<BridgeRequestException>(async () =>
            await DrainAsync(client.StreamChatAsync(CreateRequest(640), CancellationToken.None)));

        Assert.Contains("upstream overloaded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamChatAsyncSurfacesStreamErrorEventForResponsesEndpoint()
    {
        var handler = new RecordingHandler(responsesStream: """
            {"type":"error","code":"overloaded","message":"service overloaded, try again"}
            """);
        var client = CreateClient(responsesEndpoint: true, 2048, handler);

        var exception = await Assert.ThrowsAsync<BridgeRequestException>(async () =>
            await DrainAsync(client.StreamChatAsync(CreateRequest(640), CancellationToken.None)));

        Assert.Contains("service overloaded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamChatAsyncGuidsBurstProtectionCooldownInsteadOfRetrying()
    {
        var handler = new ScriptedResponseHandler(
            (HttpStatusCode.OK, """
                data: {"type":"error","code":"_arc_rate_limit_","message":"System protection triggered by request burst. Please slow down traffic growth and increase requests gradually before retrying."}

                data: [DONE]

                """, "text/event-stream"));
        var client = CreateClient(responsesEndpoint: true, 2048, handler);

        var exception = await Assert.ThrowsAsync<BridgeRequestException>(async () =>
            await DrainAsync(client.StreamChatAsync(CreateRequest(640), CancellationToken.None)));

        // 突发保护是冷却窗口：机器重试会重置窗口，必须立即失败并给出等待指引。
        Assert.Equal(1, handler.SendCount);
        Assert.False(exception.Retryable);
        Assert.Contains("请求频率保护", exception.Message, StringComparison.Ordinal);
        Assert.Contains("System protection triggered by request burst", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamChatAsyncFailsImmediatelyOnRateLimitWithoutMachineRetry()
    {
        var handler = new ScriptedResponseHandler(
            (HttpStatusCode.TooManyRequests, """{"error":"burst"}""", "application/json"));
        var client = CreateClient(responsesEndpoint: true, 2048, handler);

        var exception = await Assert.ThrowsAsync<BridgeRequestException>(async () =>
            await DrainAsync(client.StreamChatAsync(CreateRequest(640), CancellationToken.None)));

        // 失败立即上抛，不做机器重试；是否重试由用户在界面上决定。
        Assert.Equal(1, handler.SendCount);
        Assert.True(exception.Retryable);
        Assert.Contains("429", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamChatAsyncFailsImmediatelyOnNonRetryableStatuses()
    {
        var handler = new ScriptedResponseHandler(
            (HttpStatusCode.BadRequest, """{"error":"bad request"}""", "application/json"));
        var client = CreateClient(responsesEndpoint: true, 2048, handler);

        var exception = await Assert.ThrowsAsync<BridgeRequestException>(async () =>
            await DrainAsync(client.StreamChatAsync(CreateRequest(640), CancellationToken.None)));

        Assert.Equal(1, handler.SendCount);
        Assert.False(exception.Retryable);
        Assert.Contains("400", exception.Message, StringComparison.Ordinal);
    }

    private static StandardChatCompletionClient CreateClient(
 bool responsesEndpoint,
 int modelLimit,
 HttpMessageHandler handler)
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

    private static ChatCompletionRequest CreateRequest(int? maxOutputTokens, double? temperatureOverride = null) => new(
 nameof(Names.provider_a),
 nameof(Names.model_a),
 string.Empty,
 [new ChatCompletionMessage(nameof(Names.user), nameof(Names.content))],
 MaxOutputTokens: maxOutputTokens,
 TemperatureOverride: temperatureOverride);

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

    private sealed class ScriptedResponseHandler(params (HttpStatusCode status, string body, string contentType)[] responses)
 : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        {
            var (status, body, contentType) = responses[Math.Min(SendCount, responses.Length - 1)];
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            });
        }
    }

    private sealed class RecordingHandler(string? responsesStream = null) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            if (responsesStream is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty)
                });
            }

            var sse = new StringBuilder();
            foreach (var line in responsesStream.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    sse.Append("data: ").Append(line.Trim()).Append("\n\n");
                }
            }

            sse.Append("data: [DONE]\n\n");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream")
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
