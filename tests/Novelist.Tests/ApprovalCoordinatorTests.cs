using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Tests;

public sealed class ApprovalCoordinatorTests
{
    [Fact]
    public async Task RequestApprovalEmitsLegacyAwaitingApprovalAgentEventAndResumesOnDecision()
    {
        var events = new RecordingBridgeEventSink();
        var coordinator = new ToolApprovalCoordinator(events);
        var payload = JsonSerializer.SerializeToElement(new
        {
            path = "chapters/003.md",
            original = "旧文本",
            modified = "新文本",
            change_type = "search_replace",
            reason = "补强冲突"
        }, BridgeJson.SerializerOptions);

        var pending = coordinator.RequestApprovalAsync(
            new ToolApprovalRequestPayload(
                SessionId: "sess_1",
                TurnId: 7,
                ToolId: "tool_edit_001",
                ToolName: "edit",
                ApprovalType: "file_edit",
                Payload: payload,
                DisplayText: "等待确认写入第 3 章",
                ActivityKind: "file_edit"),
            CancellationToken.None).AsTask();

        var approvalEvent = Assert.Single(events.Events);
        Assert.Equal("agent:7", approvalEvent.Name);
        Assert.Equal(7, approvalEvent.Payload.GetProperty("turn_id").GetInt32());
        Assert.Equal(3, approvalEvent.Payload.GetProperty("type").GetInt32());
        Assert.Equal("edit", approvalEvent.Payload.GetProperty("tool_name").GetString());
        Assert.Equal("tool_edit_001", approvalEvent.Payload.GetProperty("tool_id").GetString());
        Assert.Equal("awaiting_approval", approvalEvent.Payload.GetProperty("phase").GetString());
        Assert.Equal("等待确认写入第 3 章", approvalEvent.Payload.GetProperty("display_text").GetString());
        Assert.Equal("file_edit", approvalEvent.Payload.GetProperty("activity_kind").GetString());
        var metadata = approvalEvent.Payload.GetProperty("metadata");
        Assert.Equal("file_edit", metadata.GetProperty("approval_type").GetString());
        Assert.Equal("chapters/003.md", metadata.GetProperty("payload").GetProperty("path").GetString());

        var completed = await coordinator.CompleteAsync(
            new ToolApprovalDecisionPayload("tool_edit_001", Approved: true, Feedback: "可以"),
            CancellationToken.None);
        var duplicate = await coordinator.CompleteAsync(
            new ToolApprovalDecisionPayload("tool_edit_001", Approved: false, Feedback: "太晚"),
            CancellationToken.None);
        var result = await pending;

        Assert.True(completed);
        Assert.False(duplicate);
        Assert.True(result.Approved);
        Assert.Equal("可以", result.Feedback);
        Assert.Equal("tool_edit_001", result.ToolId);
        Assert.Equal(0, coordinator.PendingCount);
    }

    [Fact]
    public async Task CancelSessionClearsPendingApprovals()
    {
        var coordinator = new ToolApprovalCoordinator(new RecordingBridgeEventSink());
        var first = coordinator.RequestApprovalAsync(CreateRequest("sess_a", "tool_a"), CancellationToken.None).AsTask();
        var second = coordinator.RequestApprovalAsync(CreateRequest("sess_b", "tool_b"), CancellationToken.None).AsTask();

        var cancelled = await coordinator.CancelSessionAsync("sess_a", CancellationToken.None);

        Assert.Equal(1, cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, coordinator.PendingCount);

        await coordinator.CompleteAsync(new ToolApprovalDecisionPayload("tool_b", true, ""), CancellationToken.None);
        Assert.True((await second).Approved);
        Assert.Equal(0, coordinator.PendingCount);
    }

    [Fact]
    public async Task ApproveToolBridgeHandlerKeepsLegacyWailsSignature()
    {
        var coordinator = new ToolApprovalCoordinator(new RecordingBridgeEventSink());
        var pending = coordinator.RequestApprovalAsync(CreateRequest("sess_1", "tool_1"), CancellationToken.None).AsTask();
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterApprovalHandlers(coordinator);

        var dispatch = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_approve",
              "method": "ApproveTool",
              "payload": { "args": ["tool_1", false, "请重写动机"] }
            }
            """);

        using var json = JsonDocument.Parse(dispatch.OutboundJson!);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(json.RootElement.GetProperty("result").GetBoolean());
        var decision = await pending;
        Assert.False(decision.Approved);
        Assert.Equal("请重写动机", decision.Feedback);
    }

    private static ToolApprovalRequestPayload CreateRequest(string sessionId, string toolId)
    {
        return new ToolApprovalRequestPayload(
            sessionId,
            TurnId: 5,
            toolId,
            ToolName: "delete_character",
            ApprovalType: "delete",
            Payload: JsonSerializer.SerializeToElement(new { deleted = new { type = "character", id = 3, name = "林岚" } }),
            DisplayText: "确认删除角色",
            ActivityKind: "delete");
    }

    private sealed record RecordedBridgeEvent(string Name, JsonElement Payload);

    private sealed class RecordingBridgeEventSink : IBridgeEventSink
    {
        public List<RecordedBridgeEvent> Events { get; } = [];

        public ValueTask EmitAsync(string name, object? payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(new RecordedBridgeEvent(
                name,
                JsonSerializer.SerializeToElement(payload ?? new { }, BridgeJson.SerializerOptions)));
            return ValueTask.CompletedTask;
        }
    }
}
