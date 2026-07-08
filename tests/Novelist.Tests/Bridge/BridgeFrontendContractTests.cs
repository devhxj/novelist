using System.Text.Json;
using System.Text.RegularExpressions;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;

namespace Novelist.Tests.Bridge;

public sealed class BridgeFrontendContractTests
{
    [Fact]
    public void FrontendAppApiMethodsMatchBackendCompatibilityRegistry()
    {
        var frontendMethods = ExtractFrontendAppMethods();

        Assert.Equal(BridgeCompatibilityAppMethods.MethodNames.Order(StringComparer.Ordinal), frontendMethods.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FrontendRuntimeMethodsMatchBackendRuntimeRegistry()
    {
        var frontendMethods = ExtractFrontendRuntimeMethods();

        Assert.Equal(BridgeRuntimeMethodNames.All.Order(StringComparer.Ordinal), frontendMethods.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FrontendUpdateContractsUseBackendAppConfigWithoutHardCodedReleaseEndpoint()
    {
        var root = FindRepositoryRoot();
        var apiSource = File.ReadAllText(Path.Combine(root, "frontend", "src", "lib", "novelist", "api.ts"));
        var typeSource = File.ReadAllText(Path.Combine(root, "frontend", "src", "lib", "novelist", "types.ts"));
        var bridgeAdapterSource = apiSource + Environment.NewLine + typeSource;

        Assert.Contains("GetAppConfig: AppMethod<[], config.AppConfig>", apiSource, StringComparison.Ordinal);
        Assert.Contains("update_check", typeSource, StringComparison.Ordinal);
        Assert.Contains("import_recovery?: novelImport.ImportReconciliationResult | null", typeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("api.github.com/repos", bridgeAdapterSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sigpanic/goink", bridgeAdapterSource, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("GetNovels")]
    [InlineData("SearchAll")]
    [InlineData("DiscoverModels")]
    public async Task RepresentativeAppRequestsFailFastAsNotImplemented(string method)
    {
        var dispatcher = new BridgeDispatcher()
            .RegisterDefaultNovelistHandlers(new RecordingRuntimeHost());

        var result = await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_{{method}}",
              "method": "{{method}}",
              "payload": { "args": [] }
            }
            """);

        using var json = ParseOutbound(result);
        var error = AssertBridgeError(json.RootElement, $"req_{method}", BridgeErrorCodes.MethodNotImplemented);
        Assert.Equal(method, error.GetProperty("details").GetProperty("method").GetString());
    }

    [Theory]
    [InlineData(BridgeRuntimeMethodNames.WindowMinimize)]
    [InlineData(BridgeRuntimeMethodNames.WindowToggleMaximize)]
    [InlineData(BridgeRuntimeMethodNames.WindowIsMaximized)]
    [InlineData(BridgeRuntimeMethodNames.AppQuit)]
    public async Task RepresentativeRuntimeRequestsAreRegistered(string method)
    {
        var dispatcher = new BridgeDispatcher()
            .RegisterDefaultNovelistHandlers(new RecordingRuntimeHost());

        var result = await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_runtime",
              "method": "{{method}}",
              "payload": {}
            }
            """);

        using var json = ParseOutbound(result);
        Assert.Equal("response", json.RootElement.GetProperty("kind").GetString());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task RepresentativeOpenExternalRequestUsesBackendHttpsValidation()
    {
        var runtime = new RecordingRuntimeHost();
        var dispatcher = new BridgeDispatcher()
            .RegisterDefaultNovelistHandlers(runtime);

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_open_external",
              "method": "runtime.shell.openExternal",
              "payload": { "url": "https://example.com/path" }
            }
            """);

        using var json = ParseOutbound(result);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(new Uri("https://example.com/path"), runtime.LastOpenedUrl);
    }

    private static SortedSet<string> ExtractFrontendAppMethods()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "frontend", "src", "lib", "novelist", "api.ts"));
        var matches = Regex.Matches(source, @"^\s{2}([A-Za-z][A-Za-z0-9]*): AppMethod<", RegexOptions.Multiline);
        return new SortedSet<string>(
            matches.Select(match => match.Groups[1].Value),
            StringComparer.Ordinal);
    }

    private static SortedSet<string> ExtractFrontendRuntimeMethods()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "frontend", "src", "lib", "novelist", "runtime.ts"));
        var matches = Regex.Matches(source, @"'(runtime\.[^']+)'");
        return new SortedSet<string>(
            matches.Select(match => match.Groups[1].Value),
            StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Novelist.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private static JsonElement AssertBridgeError(JsonElement root, string expectedId, string expectedCode)
    {
        Assert.Equal("response", root.GetProperty("kind").GetString());
        Assert.Equal(expectedId, root.GetProperty("id").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());
        var error = root.GetProperty("error");
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        return error;
    }

    private sealed class RecordingRuntimeHost : IBridgeRuntimeHost
    {
        public Uri? LastOpenedUrl { get; private set; }

        public ValueTask MinimizeWindowAsync(CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ToggleMaximizeWindowAsync(CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> IsWindowMaximizedAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(false);
        }

        public ValueTask QuitApplicationAsync(CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask OpenExternalAsync(Uri url, CancellationToken cancellationToken)
        {
            LastOpenedUrl = url;
            return ValueTask.CompletedTask;
        }
    }
}
