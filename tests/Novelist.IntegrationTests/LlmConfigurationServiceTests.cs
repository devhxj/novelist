using System.Net;
using System.Text;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class LlmConfigurationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LlmConfigBuildsBuiltinsPersistsUserConfigAndExtractsModels()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemLlmConfigurationService(options);

        var initial = await service.GetConfigAsync(CancellationToken.None);
        Assert.Contains(initial.Providers, provider => provider.Key == "deepseek" && provider.Source == "builtin");
        Assert.All(initial.Providers, provider => Assert.DoesNotContain("sk-secret", provider.HelpText));

        var deepseek = initial.Providers.Single(provider => provider.Key == "deepseek");
        var customModel = new ModelInfoPayload(
            "deepseek-custom",
            "DeepSeek Custom",
            128_000,
            64_000,
            true,
            ["high"],
            false);
        var customProviderModel = new ModelInfoPayload(
            "custom-chat",
            "Custom Chat",
            64_000,
            16_000,
            false,
            [],
            false);

        var providers = initial.Providers
            .Select(provider => provider.Key == "deepseek"
                ? provider with
                {
                    ApiKey = "sk-secret",
                    Temperature = 1.2,
                    CustomModels = [customModel]
                }
                : provider)
            .Append(new ProviderViewPayload(
                "my-api",
                "My API",
                "example.com/v1",
                "sk-custom",
                "",
                "",
                0.4,
                "custom",
                [],
                [customProviderModel]))
            .ToArray();

        await service.SaveConfigAsync(new LlmConfigViewPayload(providers), CancellationToken.None);

        var reloaded = new FileSystemLlmConfigurationService(options);
        var saved = await reloaded.GetConfigAsync(CancellationToken.None);
        var savedDeepseek = saved.Providers.Single(provider => provider.Key == "deepseek");
        Assert.Equal("sk-secret", savedDeepseek.ApiKey);
        Assert.Equal(1.2, savedDeepseek.Temperature);
        Assert.Contains(savedDeepseek.CustomModels, model => model.Id == customModel.Id);

        var custom = saved.Providers.Single(provider => provider.Key == "my-api");
        Assert.Equal("custom", custom.Source);
        Assert.Equal("https://example.com/v1/chat/completions", custom.ChatUrl);

        var models = await reloaded.GetModelsAsync(CancellationToken.None);
        Assert.Contains(models, model => model.Key == "deepseek/deepseek-v4-pro");
        Assert.Contains(models, model => model.Key == "deepseek/deepseek-custom");
        Assert.Contains(models, model => model.Key == "my-api/custom-chat");
        Assert.DoesNotContain(models, model => model.Key.StartsWith("qwen/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverModelsUsesModelsEndpointAndParsesOpenAICompatibleResponse()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "data": [
                    {
                      "id": "kimi-k2",
                      "context_length": 262144,
                      "supports_image_in": true,
                      "supports_reasoning": true
                    },
                    { "id": "" }
                  ]
                }
                """)
        });
        var service = new FileSystemLlmConfigurationService(options, httpClient: new HttpClient(handler));

        var models = await service.DiscoverModelsAsync(
            "https://api.example.com/v1/chat/completions",
            "sk-secret",
            CancellationToken.None);

        Assert.Single(models);
        Assert.Equal("kimi-k2", models[0].Id);
        Assert.Equal("Kimi k2", models[0].Name);
        Assert.Equal(262_144, models[0].ContextWindow);
        Assert.True(models[0].SupportsThinking);
        Assert.True(models[0].SupportsVision);
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
        Assert.Equal("https://api.example.com/v1/models", handler.Requests.Single().RequestUri!.ToString());
        Assert.Equal("Bearer sk-secret", handler.Requests.Single().Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task TestConnectionPostsMinimalPayloadAndProviderSpecificHeaders()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""{"id":"ok"}""")
        });
        var service = new FileSystemLlmConfigurationService(options, httpClient: new HttpClient(handler));

        await service.TestConnectionAsync(
            new TestConnectionPayload("mimo", "", "sk-mimo", "mimo-v2.5"),
            CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.xiaomimimo.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.Equal("sk-mimo", request.Headers.GetValues("api-key").Single());

        var body = handler.RequestBodies.Single();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("mimo-v2.5", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("hi", json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task BridgeLlmHandlersPersistConfigDiscoverModelsAndTestConnection()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var handler = new RecordingHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""{"data":[{"id":"custom-model","context_length":32768}]}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""{"id":"ok"}""")
            };
        });
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterLlmConfigurationHandlers(new FileSystemLlmConfigurationService(
                options,
                httpClient: new HttpClient(handler)));

        using var config = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_save_llm",
              "method": "SaveLLMConfig",
              "payload": {
                "args": [
                  {
                    "providers": [
                      {
                        "key": "deepseek",
                        "name": "DeepSeek",
                        "chat_url": "https://api.deepseek.com/v1/chat/completions",
                        "api_key": "sk-secret",
                        "temperature": 0.9,
                        "source": "builtin",
                        "builtin_models": [],
                        "custom_models": []
                      }
                    ]
                  }
                ]
              }
            }
            """));
        Assert.True(config.RootElement.GetProperty("ok").GetBoolean());

        using var models = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_models",
              "method": "GetModels"
            }
            """));
        Assert.Contains(models.RootElement.GetProperty("result").EnumerateArray(), model =>
            model.GetProperty("Key").GetString() == "deepseek/deepseek-v4-pro");

        using var discovered = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_discover",
              "method": "DiscoverModels",
              "payload": { "args": ["https://api.example.com/v1/chat/completions", "sk-secret"] }
            }
            """));
        Assert.Equal("custom-model", discovered.RootElement.GetProperty("result")[0].GetProperty("id").GetString());

        using var tested = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_test",
              "method": "TestConnection",
              "payload": { "args": [{ "provider_name": "deepseek", "api_key": "sk-secret", "model_id": "deepseek-v4-pro" }] }
            }
            """));
        Assert.True(tested.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task BridgeLlmHandlersReturnStableValidationAndProviderErrorsWithoutSecrets()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = JsonContent("""{"error":{"message":"bad key sk-secret"}}""")
        });
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterLlmConfigurationHandlers(new FileSystemLlmConfigurationService(
                options,
                httpClient: new HttpClient(handler)));

        using var invalid = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_bad_discover",
              "method": "DiscoverModels",
              "payload": { "args": ["", ""] }
            }
            """));
        AssertBridgeError(invalid.RootElement, "req_bad_discover", BridgeErrorCodes.ValidationError);

        using var upstream = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_upstream",
              "method": "DiscoverModels",
              "payload": { "args": ["https://api.example.com/v1/chat/completions", "sk-secret"] }
            }
            """));
        AssertBridgeError(upstream.RootElement, "req_upstream", BridgeErrorCodes.LlmProviderError);
        Assert.DoesNotContain("sk-secret", upstream.RootElement.GetRawText());
    }

    [Fact]
    public async Task BridgeLlmHandlersReturnStableErrorWhenAppIsNotInitialized()
    {
        var options = CreateOptions();
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterLlmConfigurationHandlers(new FileSystemLlmConfigurationService(options));

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_llm",
              "method": "GetLLMConfig"
            }
            """);

        using var json = ParseOutbound(result);
        AssertBridgeError(json.RootElement, "req_llm", BridgeErrorCodes.AppNotInitialized);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data")
        };
    }

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static StringContent JsonContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private static void AssertBridgeError(JsonElement root, string expectedId, string expectedCode)
    {
        Assert.Equal("response", root.GetProperty("kind").GetString());
        Assert.Equal(expectedId, root.GetProperty("id").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedCode, root.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            Requests.Add(request);
            return Task.FromResult(_handler(request));
        }
    }
}
