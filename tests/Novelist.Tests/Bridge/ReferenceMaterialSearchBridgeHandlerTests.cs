using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Tests.Bridge;

public sealed class ReferenceMaterialSearchBridgeHandlerTests
{
    [Fact]
    public async Task ListReferenceMaterialsRoutesAndPreservesCompleteMultilineText()
    {
        const string materialText = "他停在门边。\n\n她没有回答，目光越过他落在雨幕里。";
        var service = new RecordingReferenceMaterialSearch(materialText);
        var dispatcher = new BridgeDispatcher().RegisterReferenceMaterialSearchHandlers(service);

        var result = await dispatcher.DispatchAsync(Request(
            "ListReferenceMaterials",
            new { novel_id = 42L, anchor_id = 99L, page = 2, size = 10 }));
        using var json = JsonDocument.Parse(
            result.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));

        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("list:42:99:2:10", service.Call);
        var page = json.RootElement.GetProperty("result");
        Assert.Equal(11, page.GetProperty("total").GetInt64());
        Assert.Equal(materialText, page.GetProperty("items")[0].GetProperty("text").GetString());
        Assert.Equal("用于承接跨段对话。", page.GetProperty("items")[0].GetProperty("description").GetString());
        Assert.Equal("dialogue", page.GetProperty("items")[0].GetProperty("tags")[0].GetString());
    }

    [Fact]
    public async Task ListReferenceMaterialsReturnsStableMaterializationErrors()
    {
        var service = new RecordingReferenceMaterialSearch("unused")
        {
            Exception = new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "The active generation changed while materials were being listed.")
        };
        var dispatcher = new BridgeDispatcher().RegisterReferenceMaterialSearchHandlers(service);

        var result = await dispatcher.DispatchAsync(Request(
            "ListReferenceMaterials",
            new { novel_id = 42L, anchor_id = 99L, page = 1, size = 20 }));
        using var json = JsonDocument.Parse(
            result.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));

        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        var error = json.RootElement.GetProperty("error");
        Assert.Equal(ReferenceMaterializationErrorCodes.GenerationIncomplete, error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
    }

    private static string Request(string method, params object?[] args) =>
        JsonSerializer.Serialize(new
        {
            kind = "request",
            id = $"req_{method}",
            method,
            payload = new { args }
        }, BridgeJson.SerializerOptions);

    private sealed class RecordingReferenceMaterialSearch(string materialText) : IReferenceMaterialSearch
    {
        public string? Call { get; private set; }

        public Exception? Exception { get; init; }

        public ValueTask<ReferenceMaterialListPage> ListAsync(
            ReferenceMaterialListRequest input,
            CancellationToken cancellationToken)
        {
            Call = $"list:{input.NovelId}:{input.AnchorId}:{input.Page}:{input.Size}";
            if (Exception is not null)
            {
                throw Exception;
            }

            return ValueTask.FromResult(new ReferenceMaterialListPage(
                [new ReferenceMaterialListItem(
                    "material-1",
                    "generation-1",
                    input.AnchorId,
                    3,
                    2,
                    "dialogue_exchange",
                    materialText,
                    "用于承接跨段对话。",
                    ["dialogue", "subtext"],
                    "text-hash")],
                11,
                input.Page,
                input.Size,
                2));
        }

        public ValueTask<IReadOnlyList<ReferenceMaterialSearchHit>> SearchAsync(
            ReferenceMaterialSearchRequest input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
