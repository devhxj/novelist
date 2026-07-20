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
    public async Task SearchReferenceMaterialsRoutesScopeAndPreservesCompleteMultilineText()
    {
        const string materialText = "\u201c\u4f60\u8fd8\u8981\u8d70\uff1f\u201d\n\u5979\u6ca1\u6709\u56de\u7b54\u3002\n\n\u201c\u90a3\u6211\u7b49\u4f60\u3002\u201d";
        var service = new RecordingReferenceMaterialSearch(materialText);
        var dispatcher = new BridgeDispatcher().RegisterReferenceMaterialSearchHandlers(service);

        var result = await dispatcher.DispatchAsync(Request(
            "SearchReferenceMaterials",
            new
            {
                query = "\u627f\u63a5\u7b49\u5f85\u7684\u5bf9\u8bdd",
                max_results = 6,
                novel_id = 42L,
                session_id = "project:42:default",
                library_ids = new[] { "library-1" },
                anchor_ids = new[] { 99L }
            }));
        using var json = JsonDocument.Parse(
            result.OutboundJson ?? throw new InvalidOperationException("Bridge returned no response."));

        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("search:\u627f\u63a5\u7b49\u5f85\u7684\u5bf9\u8bdd:6:42:project:42:default:library-1:99", service.Call);
        var hit = json.RootElement.GetProperty("result")[0];
        Assert.Equal("material-1", hit.GetProperty("material_id").GetString());
        Assert.Equal("generation-1", hit.GetProperty("generation_id").GetString());
        Assert.Equal(materialText, hit.GetProperty("text").GetString());
        Assert.Equal(0.125, hit.GetProperty("vector_distance").GetDouble());
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
            CancellationToken cancellationToken)
        {
            Call = $"search:{input.Query}:{input.MaxResults}:{input.NovelId}:{input.SessionId}:{string.Join(',', input.LibraryIds ?? [])}:{string.Join(',', input.AnchorIds ?? [])}";
            if (Exception is not null)
            {
                throw Exception;
            }

            return ValueTask.FromResult<IReadOnlyList<ReferenceMaterialSearchHit>>(
            [
                new ReferenceMaterialSearchHit(
                    "material-1",
                    "generation-1",
                    99,
                    3,
                    2,
                    "dialogue_exchange",
                    materialText,
                    "\u7528\u4e8e\u627f\u63a5\u8de8\u6bb5\u5bf9\u8bdd\u3002",
                    ["dialogue", "subtext"],
                    "text-hash",
                    0.125)
            ]);
        }
    }
}
