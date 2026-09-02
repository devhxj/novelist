using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Tests.Bridge;

public sealed class ChapterCorpusBridgeHandlerTests
{
    [Fact]
    public async Task CoverageHandlerReturnsTheDomainCoveragePayload()
    {
        var dispatcher = new BridgeDispatcher().RegisterChapterCorpusHandlers(new RecordingCoverageService());
        var result = await dispatcher.DispatchAsync(Request(
            "GetChapterCorpusCoverage",
            new GetChapterCorpusCoveragePayload(42, 3)));
        using var json = JsonDocument.Parse(result.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));

        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(42, json.RootElement.GetProperty("result").GetProperty("novel_id").GetInt64());
    }

    [Fact]
    public async Task CoverageHandlerRejectsMissingObjectArguments()
    {
        var dispatcher = new BridgeDispatcher().RegisterChapterCorpusHandlers(new RecordingCoverageService());
        var result = await dispatcher.DispatchAsync(Request("GetChapterCorpusCoverage", 42L));
        using var json = JsonDocument.Parse(result.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));

        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(BridgeErrorCodes.ValidationError, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CoverageHandlerMapsMalformedFieldTypesToValidationErrors()
    {
        var dispatcher = new BridgeDispatcher().RegisterChapterCorpusHandlers(new RecordingCoverageService());
        var result = await dispatcher.DispatchAsync(Request(
            "GetChapterCorpusCoverage",
            JsonSerializer.Deserialize<JsonElement>("{\"novel_id\": \"abc\"}")));
        using var json = JsonDocument.Parse(result.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));

        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(BridgeErrorCodes.ValidationError, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static string Request(string method, params object?[] args)
    {
        return JsonSerializer.Serialize(new
        {
            kind = "request",
            id = $"req_{method}",
            method,
            payload = new { args }
        }, BridgeJson.SerializerOptions);
    }

    private sealed class RecordingCoverageService : IChapterCorpusCoverageService
    {
        public ValueTask<ChapterCorpusCoveragePayload> ComputeCoverageAsync(
            GetChapterCorpusCoveragePayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ChapterCorpusCoveragePayload(
                input.NovelId,
                input.ChapterNumber,
                "next",
                [],
                0,
                0,
                0,
                false));
        }
    }
}
