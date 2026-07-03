using System.Text.Json;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;

namespace Novelist.Tests.Bridge;

public sealed class BridgeDispatcherTests
{
    [Fact]
    public async Task DispatchAsyncRoutesRegisteredRequestAndPreservesId()
    {
        var dispatcher = new BridgeDispatcher();
        dispatcher.Register("Echo", (context, _) =>
        {
            Assert.Equal("req_success", context.Id);
            Assert.Equal("Echo", context.Method);
            Assert.Equal(TimeSpan.FromMilliseconds(5000), context.Deadline);
            Assert.Equal("hello", context.Payload!.Value.GetProperty("text").GetString());
            return ValueTask.FromResult<object?>(new { text = "hello" });
        });

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_success",
              "method": "Echo",
              "payload": { "text": "hello" },
              "deadline_ms": 5000
            }
            """);

        using var json = ParseOutbound(result);
        var root = json.RootElement;
        Assert.Equal("response", root.GetProperty("kind").GetString());
        Assert.Equal("req_success", root.GetProperty("id").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("hello", root.GetProperty("result").GetProperty("text").GetString());
    }

    [Fact]
    public async Task DispatchAsyncRejectsUnknownMethodWithStableCode()
    {
        var dispatcher = new BridgeDispatcher();

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_unknown",
              "method": "MissingMethod",
              "payload": {}
            }
            """);

        using var json = ParseOutbound(result);
        AssertBridgeError(json.RootElement, "req_unknown", BridgeErrorCodes.MethodNotFound);
    }

    [Fact]
    public async Task DispatchAsyncRejectsMalformedJson()
    {
        var dispatcher = new BridgeDispatcher();

        var result = await dispatcher.DispatchAsync("{ not json");

        using var json = ParseOutbound(result);
        AssertBridgeError(json.RootElement, expectedId: null, BridgeErrorCodes.InvalidMessage);
    }

    [Fact]
    public async Task DispatchAsyncConvertsValidationException()
    {
        var dispatcher = new BridgeDispatcher();
        dispatcher.Register("Validate", (_, _) =>
        {
            throw new BridgeValidationException(
                "Payload is invalid.",
                new Dictionary<string, string> { ["title"] = "Title is required." });
        });

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_validation",
              "method": "Validate",
              "payload": {}
            }
            """);

        using var json = ParseOutbound(result);
        var error = AssertBridgeError(json.RootElement, "req_validation", BridgeErrorCodes.ValidationError);
        Assert.Equal("Title is required.", error.GetProperty("details").GetProperty("title").GetString());
    }

    [Fact]
    public async Task DispatchAsyncParsesCancelEnvelopeWithoutResponse()
    {
        var dispatcher = new BridgeDispatcher();

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "cancel",
              "id": "req_to_cancel"
            }
            """);

        Assert.Null(result.OutboundJson);
        Assert.Equal("req_to_cancel", result.CancelRequestId);
    }

    [Fact]
    public async Task DispatchAsyncConvertsInternalErrorWithoutLeakingStackTrace()
    {
        var dispatcher = new BridgeDispatcher();
        dispatcher.Register("Boom", (_, _) => throw new InvalidOperationException("database password abc123"));

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_boom",
              "method": "Boom",
              "payload": {}
            }
            """);

        using var json = ParseOutbound(result);
        var error = AssertBridgeError(json.RootElement, "req_boom", BridgeErrorCodes.InternalError);
        var serialized = json.RootElement.GetRawText();
        Assert.Equal("Internal bridge error.", error.GetProperty("message").GetString());
        Assert.DoesNotContain("database password", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BridgeOutboundEventUsesStableEnvelope()
    {
        var message = BridgeOutboundEvent.Create("file:changed", new { novel_id = 42, path = "chapters/003.md" });
        var json = JsonSerializer.Serialize(message, BridgeJson.SerializerOptions);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("event", root.GetProperty("kind").GetString());
        Assert.Equal("file:changed", root.GetProperty("name").GetString());
        Assert.Equal(42, root.GetProperty("payload").GetProperty("novel_id").GetInt32());
        Assert.Equal("chapters/003.md", root.GetProperty("payload").GetProperty("path").GetString());
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private static JsonElement AssertBridgeError(JsonElement root, string? expectedId, string expectedCode)
    {
        Assert.Equal("response", root.GetProperty("kind").GetString());
        if (expectedId is null)
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
        }
        else
        {
            Assert.Equal(expectedId, root.GetProperty("id").GetString());
        }

        Assert.False(root.GetProperty("ok").GetBoolean());
        var error = root.GetProperty("error");
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        return error;
    }
}
