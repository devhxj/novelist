using System.Net;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class UpdateCheckServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CheckForUpdatesReportsAvailableReleaseAndPersistsLastCheckedAt()
    {
        var checkedAt = DateTimeOffset.Parse("2026-07-08T10:00:00Z");
        var options = await CreateInitializedOptionsAsync();
        var settings = new FileSystemAppSettingsService(options);
        await settings.SaveUpdateCheckSettingsAsync(
            new SaveUpdateCheckSettingsPayload(
                Enabled: true,
                EndpointUrl: "https://updates.example.test/novelist/releases/latest",
                DismissedVersion: string.Empty),
            CancellationToken.None);
        var handler = new RecordingHttpHandler(_ => JsonResponse(
            """
            {
              "tag_name": "v1.2",
              "name": "Novelist 1.2",
              "html_url": "https://updates.example.test/releases/v1.2",
              "body": "## Fixes\n\n- Safer imports",
              "assets": [
                { "browser_download_url": "https://updates.example.test/downloads/novelist-1.2.zip" }
              ]
            }
            """));
        using var http = new HttpClient(handler);
        var service = new GitHubUpdateCheckService(
            options,
            settings,
            http,
            currentVersion: "1.1.9",
            clock: () => checkedAt);

        var result = await service.CheckForUpdatesAsync(
            new CheckForUpdatesPayload("update-task-1", Manual: false),
            CancellationToken.None);

        Assert.Equal("update_available", result.Status);
        Assert.Equal("1.1.9", result.CurrentVersion);
        Assert.Equal("v1.2", result.LatestVersion);
        Assert.Equal("https://updates.example.test/releases/v1.2", result.ReleaseUrl);
        Assert.Equal("https://updates.example.test/downloads/novelist-1.2.zip", result.DownloadUrl);
        Assert.Equal("## Fixes\n\n- Safer imports", result.ReleaseNotes);
        Assert.False(result.Dismissed);
        Assert.Equal(checkedAt, result.CheckedAt);
        Assert.Contains("Novelist/", Assert.Single(handler.UserAgents));

        var persisted = await settings.GetUpdateCheckSettingsAsync(CancellationToken.None);
        Assert.Equal(checkedAt, persisted.LastCheckedAt);
    }

    [Theory]
    [InlineData("1.2.0", "v1.2", "no_update")]
    [InlineData("1.2.0-beta.1", "v1.2.0", "update_available")]
    [InlineData("1.2.0", "v1.3.0-beta.1", "no_update")]
    [InlineData("1.2.0-beta.1", "v1.2.0-beta.2", "update_available")]
    public async Task SemanticVersionComparisonHandlesPrefixesMissingPatchAndPrereleaseConservatively(
        string currentVersion,
        string latestVersion,
        string expectedStatus)
    {
        var options = await CreateInitializedOptionsAsync();
        var settings = new FileSystemAppSettingsService(options);
        await settings.SaveUpdateCheckSettingsAsync(
            new SaveUpdateCheckSettingsPayload(true, "https://updates.example.test/latest", string.Empty),
            CancellationToken.None);
        using var http = new HttpClient(new RecordingHttpHandler(_ => JsonResponse(
            $$"""
            { "tag_name": "{{latestVersion}}", "html_url": "https://updates.example.test/releases/{{latestVersion}}" }
            """)));
        var service = new GitHubUpdateCheckService(options, settings, http, currentVersion);

        var result = await service.CheckForUpdatesAsync(
            new CheckForUpdatesPayload("update-task-semver", Manual: true),
            CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
    }

    [Fact]
    public async Task AutomaticChecksRespectDismissedVersionButManualChecksStillReportUpdate()
    {
        var options = await CreateInitializedOptionsAsync();
        var settings = new FileSystemAppSettingsService(options);
        await settings.SaveUpdateCheckSettingsAsync(
            new SaveUpdateCheckSettingsPayload(true, "https://updates.example.test/latest", "v1.3.0"),
            CancellationToken.None);
        using var http = new HttpClient(new RecordingHttpHandler(_ => JsonResponse(
            """
            { "tag_name": "v1.3.0", "html_url": "https://updates.example.test/releases/v1.3.0" }
            """)));
        var service = new GitHubUpdateCheckService(options, settings, http, "1.2.0");

        var automatic = await service.CheckForUpdatesAsync(
            new CheckForUpdatesPayload("update-auto", Manual: false),
            CancellationToken.None);
        var manual = await service.CheckForUpdatesAsync(
            new CheckForUpdatesPayload("update-manual", Manual: true),
            CancellationToken.None);

        Assert.Equal("dismissed", automatic.Status);
        Assert.True(automatic.Dismissed);
        Assert.Equal("update_available", manual.Status);
        Assert.False(manual.Dismissed);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{ }", "update.http_status")]
    [InlineData(HttpStatusCode.OK, "{ \"html_url\": \"https://updates.example.test/release\" }", "update.release_version_missing")]
    [InlineData(HttpStatusCode.OK, "not json", "update.invalid_json")]
    public async Task CheckForUpdatesReturnsStructuredFailuresForHttpAndParseErrors(
        HttpStatusCode statusCode,
        string body,
        string expectedCode)
    {
        var options = await CreateInitializedOptionsAsync();
        var settings = new FileSystemAppSettingsService(options);
        await settings.SaveUpdateCheckSettingsAsync(
            new SaveUpdateCheckSettingsPayload(true, "https://updates.example.test/latest", string.Empty),
            CancellationToken.None);
        using var http = new HttpClient(new RecordingHttpHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body)
        }));
        var service = new GitHubUpdateCheckService(options, settings, http, "1.0.0");

        var result = await service.CheckForUpdatesAsync(
            new CheckForUpdatesPayload("update-failure", Manual: true),
            CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Fact]
    public async Task CheckForUpdatesUsesConfiguredTimeout()
    {
        var options = await CreateInitializedOptionsAsync(updateTimeoutMs: 25);
        var settings = new FileSystemAppSettingsService(options);
        await settings.SaveUpdateCheckSettingsAsync(
            new SaveUpdateCheckSettingsPayload(true, "https://updates.example.test/latest", string.Empty),
            CancellationToken.None);
        using var http = new HttpClient(new RecordingHttpHandler(async cancellationToken =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return JsonResponse("""{ "tag_name": "v9.0.0" }""");
        }));
        var service = new GitHubUpdateCheckService(options, settings, http, "1.0.0");

        var result = await service.CheckForUpdatesAsync(
            new CheckForUpdatesPayload("update-timeout", Manual: true),
            CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("update.timeout", result.ErrorCode);
    }

    [Fact]
    public async Task BridgeUpdateCheckHandlerRoutesServiceAndValidatesPayload()
    {
        var options = await CreateInitializedOptionsAsync();
        var settings = new FileSystemAppSettingsService(options);
        await settings.SaveUpdateCheckSettingsAsync(
            new SaveUpdateCheckSettingsPayload(true, "https://updates.example.test/latest", string.Empty),
            CancellationToken.None);
        using var http = new HttpClient(new RecordingHttpHandler(_ => JsonResponse(
            """
            { "tag_name": "v1.1.0", "html_url": "https://updates.example.test/releases/v1.1.0" }
            """)));
        var service = new GitHubUpdateCheckService(options, settings, http, "1.0.0");
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterUpdateCheckHandlers(service);

        using var ok = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_update",
              "method": "CheckForUpdates",
              "payload": { "args": [{ "task_id": "update-bridge", "manual": true }] }
            }
            """));
        Assert.True(ok.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("update_available", ok.RootElement.GetProperty("result").GetProperty("status").GetString());

        using var invalid = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_bad_update",
              "method": "CheckForUpdates",
              "payload": { "args": [] }
            }
            """));
        Assert.Equal(BridgeErrorCodes.ValidationError, invalid.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async ValueTask<AppInitializationOptions> CreateInitializedOptionsAsync(int updateTimeoutMs = 5000)
    {
        var options = new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config", Guid.NewGuid().ToString("N")),
            DefaultDataDirectory = Path.Combine(_root, "data", Guid.NewGuid().ToString("N")),
            UpdateCheckTimeoutMs = updateTimeoutMs
        };
        await new FileSystemAppInitializationService(options).InitializeAsync(
            options.DefaultDataDirectory,
            CancellationToken.None);
        return options;
    }

    private static HttpResponseMessage JsonResponse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public List<string> UserAgents { get; } = [];

        public RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = (request, _) => Task.FromResult(respond(request));
        }

        public RecordingHttpHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = (_, cancellationToken) => respond(cancellationToken);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(Uri.UriSchemeHttps, request.RequestUri?.Scheme);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            return await _respond(request, cancellationToken);
        }
    }
}
