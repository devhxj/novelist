using System.Net;
using System.Text;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class WebToolServiceTests
{
    [Fact]
    public async Task WebFetchExtractsReadableMarkdownWithoutExternalNetwork()
    {
        var html = """
            <html>
              <head><title> 测试 &amp; 标题 </title></head>
              <body>
                <nav>菜单 噪声</nav>
                <article>
                  <h1>资料标题</h1>
                  <p>第一段 &amp; 中文内容。</p>
                  <p><a href="/source">来源</a> 说明。</p>
                </article>
              </body>
            </html>
            """;
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        });
        var service = CreateFetchService(handler, new FixedResolver(("example.test", "93.184.216.34")));

        var result = await service.FetchAsync("https://example.test/story", CancellationToken.None);

        Assert.Equal("https://example.test/story", result.Url);
        Assert.Equal("测试 & 标题", result.Title);
        Assert.Contains("# 资料标题", result.Text, StringComparison.Ordinal);
        Assert.Contains("第一段 & 中文内容", result.Text, StringComparison.Ordinal);
        Assert.Contains("[来源](https://example.test/source)", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("菜单 噪声", result.Text, StringComparison.Ordinal);
        Assert.Equal("Mozilla/5.0", handler.Requests[0].Headers.UserAgent.ToString()[..11]);
    }

    [Theory]
    [InlineData("file:///etc/passwd", "仅支持")]
    [InlineData("https://user@example.test/story", "用户信息")]
    [InlineData("http://metadata.google.internal/latest", "禁止访问")]
    [InlineData("http://169.254.169.254/latest", "禁止访问")]
    public async Task WebFetchRejectsUnsafeUrlShapes(string url, string message)
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body><p>never</p></body></html>", Encoding.UTF8, "text/html")
        });
        var service = CreateFetchService(handler, new FixedResolver(("example.test", "93.184.216.34")));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => service.FetchAsync(url, CancellationToken.None).AsTask());

        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WebFetchRejectsPrivateResolvedAddresses()
    {
        var service = CreateFetchService(
            new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new FixedResolver(("private.test", "10.1.2.3")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.FetchAsync("https://private.test/story", CancellationToken.None).AsTask());

        Assert.Contains("禁止访问内网地址", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebFetchRejectsRedirectsToPrivateHostsBeforeFollowing()
    {
        var handler = new RecordingHttpMessageHandler(request =>
        {
            if (request.RequestUri?.Host == "example.test")
            {
                return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri("http://private.test/secret") }
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body><p>secret</p></body></html>", Encoding.UTF8, "text/html")
            };
        });
        var service = CreateFetchService(
            handler,
            new FixedResolver(("example.test", "93.184.216.34"), ("private.test", "10.1.2.3")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.FetchAsync("https://example.test/start", CancellationToken.None).AsTask());

        Assert.Contains("禁止访问内网地址", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task WebFetchRejectsUnsupportedContentTypesAndOversizedResponses()
    {
        var unsupported = CreateFetchService(
            new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
                {
                    Headers = { ContentType = new("image/png") }
                }
            }),
            new FixedResolver(("example.test", "93.184.216.34")));

        var contentTypeError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unsupported.FetchAsync("https://example.test/image", CancellationToken.None).AsTask());
        Assert.Contains("不支持的内容类型", contentTypeError.Message, StringComparison.Ordinal);

        var oversized = CreateFetchService(
            new RecordingHttpMessageHandler(_ =>
            {
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes("<html><body><p>too large</p></body></html>"));
                content.Headers.ContentType = new("text/html");
                content.Headers.ContentLength = 21;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }),
            new FixedResolver(("example.test", "93.184.216.34")),
            maxBytes: 20);

        var sizeError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oversized.FetchAsync("https://example.test/large", CancellationToken.None).AsTask());
        Assert.Contains("网页过大", sizeError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepSeekWebSearchSendsAnthropicToolRequestAndParsesBlocks()
    {
        string requestBody = string.Empty;
        HttpRequestMessage? capturedRequest = null;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            capturedRequest = request;
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "content": [
                        {"type":"server_tool_use","input":{"query":"DeepSeek web search"}},
                        {"type":"web_search_tool_result","content":[{"title":"DeepSeek Docs","url":"https://docs.deepseek.com/web-search"}]},
                        {"type":"text","text":"可以使用 DeepSeek 的 Anthropic web_search 服务端工具。"}
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var service = new DeepSeekWebSearchService(
            new FixedLlmConfigurationService(DeepSeekProvider(apiKey: "secret-key")),
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

        var result = await service.SearchAsync("检索 DeepSeek web search 文档", CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("https://api.deepseek.com/anthropic/v1/messages", capturedRequest.RequestUri!.ToString());
        Assert.Equal("secret-key", capturedRequest.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", capturedRequest.Headers.GetValues("anthropic-version").Single());

        using var document = JsonDocument.Parse(requestBody);
        Assert.Equal("deepseek-v4-flash", document.RootElement.GetProperty("model").GetString());
        Assert.False(document.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("web_search_20260209", document.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal("web_search", document.RootElement.GetProperty("tools")[0].GetProperty("name").GetString());

        Assert.Equal(["DeepSeek web search"], result.Queries);
        Assert.Equal("可以使用 DeepSeek 的 Anthropic web_search 服务端工具。", result.Summary);
        var source = Assert.Single(result.Sources);
        Assert.Equal("DeepSeek Docs", source.Title);
        Assert.Equal("https://docs.deepseek.com/web-search", source.Url);
    }

    [Fact]
    public async Task DeepSeekWebSearchRequiresDeepSeekConfigurationAndRedactsProviderErrors()
    {
        var missing = new DeepSeekWebSearchService(
            new FixedLlmConfigurationService(),
            new HttpClient(new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        var missingError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            missing.SearchAsync("查资料", CancellationToken.None).AsTask());
        Assert.Contains("配置 DeepSeek", missingError.Message, StringComparison.Ordinal);

        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(
                """{"error":{"message":"upstream leaked secret-key"}}""",
                Encoding.UTF8,
                "application/json")
        });
        var service = new DeepSeekWebSearchService(
            new FixedLlmConfigurationService(DeepSeekProvider(apiKey: "secret-key")),
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

        var providerError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync("查资料", CancellationToken.None).AsTask());

        Assert.Contains("[redacted]", providerError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", providerError.Message, StringComparison.Ordinal);
    }

    private static HttpWebFetchService CreateFetchService(
        RecordingHttpMessageHandler handler,
        IWebHostAddressResolver resolver,
        int maxBytes = 10 * 1024 * 1024)
    {
        return new HttpWebFetchService(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            resolver,
            new HttpWebFetchOptions
            {
                MinDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                MaxBytes = maxBytes
            });
    }

    private static ProviderViewPayload DeepSeekProvider(string apiKey)
    {
        return new ProviderViewPayload(
            "deepseek",
            "DeepSeek",
            "https://api.deepseek.com/v1/chat/completions",
            apiKey,
            "https://platform.deepseek.com",
            string.Empty,
            0.7,
            "builtin",
            [new ModelInfoPayload("deepseek-v4-flash", "DeepSeek V4 Flash", 1_000_000, 384_000, true, ["high", "max"], false)],
            []);
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = _handler(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class FixedResolver : IWebHostAddressResolver
    {
        private readonly Dictionary<string, IReadOnlyList<IPAddress>> _addresses = new(StringComparer.OrdinalIgnoreCase);

        public FixedResolver(params (string Host, string Address)[] entries)
        {
            foreach (var (host, address) in entries)
            {
                _addresses[host] = [IPAddress.Parse(address)];
            }
        }

        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            if (_addresses.TryGetValue(host, out var addresses))
            {
                return ValueTask.FromResult(addresses);
            }

            throw new InvalidOperationException($"Unexpected DNS lookup for {host}");
        }
    }

    private sealed class FixedLlmConfigurationService : ILlmConfigurationService
    {
        private readonly IReadOnlyList<ProviderViewPayload> _providers;

        public FixedLlmConfigurationService(params ProviderViewPayload[] providers)
        {
            _providers = providers;
        }

        public ValueTask<LlmConfigViewPayload> GetConfigAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new LlmConfigViewPayload(_providers));
        }

        public ValueTask SaveConfigAsync(LlmConfigViewPayload input, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask<IReadOnlyList<AvailableModelPayload>> GetModelsAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask<IReadOnlyList<ModelInfoPayload>> DiscoverModelsAsync(string chatUrl, string apiKey, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask TestConnectionAsync(TestConnectionPayload input, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
