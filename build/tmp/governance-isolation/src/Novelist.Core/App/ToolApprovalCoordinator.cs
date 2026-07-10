using System.Globalization;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;

namespace Novelist.Core.App;

public sealed class ToolApprovalCoordinator : IApprovalCoordinator
{
    private const int MaxIdentifierLength = 512;
    private const int MaxNameLength = 128;
    private const int MaxFeedbackLength = 4_000;
    private readonly object _sync = new();
    private readonly Dictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);
    private readonly IBridgeEventSink _events;
    private readonly TimeProvider _timeProvider;

    public ToolApprovalCoordinator(
        IBridgeEventSink events,
        TimeProvider? timeProvider = null)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    public async ValueTask<ToolApprovalResultPayload> RequestApprovalAsync(
        ToolApprovalRequestPayload request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeRequest(request);
        var pending = new PendingApproval(normalized);

        lock (_sync)
        {
            if (_pending.ContainsKey(normalized.ToolId))
            {
                throw new InvalidOperationException($"Approval request '{normalized.ToolId}' is already pending.");
            }

            _pending.Add(normalized.ToolId, pending);
        }

        try
        {
            await EmitAwaitingApprovalAsync(normalized, cancellationToken);
            return await pending.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            pending.TryCancel();
            throw;
        }
        finally
        {
            RemovePendingIfSame(normalized.ToolId, pending);
        }
    }

    public ValueTask<bool> CompleteAsync(
        ToolApprovalDecisionPayload decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeDecision(decision);
        var pending = RemovePending(normalized.ToolId);
        if (pending is null)
        {
            return ValueTask.FromResult(false);
        }

        pending.TryComplete(new ToolApprovalResultPayload(
            normalized.ToolId,
            normalized.Approved,
            normalized.Feedback));
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> CancelToolAsync(
        string toolId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedToolId = NormalizeRequiredText(toolId, nameof(toolId), MaxIdentifierLength);
        var pending = RemovePending(normalizedToolId);
        if (pending is null)
        {
            return ValueTask.FromResult(false);
        }

        pending.TryCancel();
        return ValueTask.FromResult(true);
    }

    public ValueTask<int> CancelSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSessionId = NormalizeRequiredText(sessionId, nameof(sessionId), MaxIdentifierLength);
        List<PendingApproval> cancelled = [];

        lock (_sync)
        {
            var toolIds = _pending
                .Where(item => string.Equals(item.Value.Request.SessionId, normalizedSessionId, StringComparison.Ordinal))
                .Select(item => item.Key)
                .ToArray();

            foreach (var toolId in toolIds)
            {
                cancelled.Add(_pending[toolId]);
                _pending.Remove(toolId);
            }
        }

        foreach (var pending in cancelled)
        {
            pending.TryCancel();
        }

        return ValueTask.FromResult(cancelled.Count);
    }

    private async ValueTask EmitAwaitingApprovalAsync(
        ToolApprovalRequestPayload request,
        CancellationToken cancellationToken)
    {
        await _events.EmitAsync(
            $"agent:{request.TurnId.ToString(CultureInfo.InvariantCulture)}",
            new AgentEventPayload
            {
                TurnId = request.TurnId,
                Type = 3,
                ToolName = request.ToolName,
                ToolId = request.ToolId,
                Phase = "awaiting_approval",
                DisplayText = request.DisplayText,
                ActivityKind = request.ActivityKind,
                Metadata = new Dictionary<string, object?>
                {
                    ["approval_type"] = request.ApprovalType,
                    ["payload"] = request.Payload
                },
                Timestamp = _timeProvider.GetUtcNow()
            },
            cancellationToken);
    }

    private PendingApproval? RemovePending(string toolId)
    {
        lock (_sync)
        {
            if (!_pending.Remove(toolId, out var pending))
            {
                return null;
            }

            return pending;
        }
    }

    private void RemovePendingIfSame(string toolId, PendingApproval pending)
    {
        lock (_sync)
        {
            if (_pending.TryGetValue(toolId, out var current) && ReferenceEquals(current, pending))
            {
                _pending.Remove(toolId);
            }
        }
    }

    private static ToolApprovalRequestPayload NormalizeRequest(ToolApprovalRequestPayload request)
    {
        if (request.TurnId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.TurnId, "Turn id must be positive.");
        }

        var payload = request.Payload.ValueKind == JsonValueKind.Undefined
            ? JsonSerializer.SerializeToElement(new { }, BridgeJson.SerializerOptions)
            : request.Payload.Clone();

        return request with
        {
            SessionId = NormalizeRequiredText(request.SessionId, nameof(request.SessionId), MaxIdentifierLength),
            ToolId = NormalizeRequiredText(request.ToolId, nameof(request.ToolId), MaxIdentifierLength),
            ToolName = NormalizeRequiredText(request.ToolName, nameof(request.ToolName), MaxNameLength),
            ApprovalType = NormalizeRequiredText(request.ApprovalType, nameof(request.ApprovalType), MaxNameLength),
            Payload = payload,
            DisplayText = NormalizeRequiredText(request.DisplayText, nameof(request.DisplayText), MaxIdentifierLength),
            ActivityKind = NormalizeOptionalText(request.ActivityKind, nameof(request.ActivityKind), MaxNameLength)
        };
    }

    private static ToolApprovalDecisionPayload NormalizeDecision(ToolApprovalDecisionPayload decision)
    {
        return decision with
        {
            ToolId = NormalizeRequiredText(decision.ToolId, nameof(decision.ToolId), MaxIdentifierLength),
            Feedback = NormalizeOptionalText(decision.Feedback, nameof(decision.Feedback), MaxFeedbackLength)
        };
    }

    private static string NormalizeRequiredText(string? value, string name, int maxLength)
    {
        var normalized = NormalizeOptionalText(value, name, maxLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value must be a non-empty string.", name);
        }

        return normalized;
    }

    private static string NormalizeOptionalText(string? value, string name, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(name, normalized.Length, $"Value must be at most {maxLength} characters.");
        }

        if (normalized.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')))
        {
            throw new ArgumentException("Value must not contain unsupported control characters.", name);
        }

        return normalized;
    }

    private sealed class PendingApproval
    {
        private readonly TaskCompletionSource<ToolApprovalResultPayload> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingApproval(ToolApprovalRequestPayload request)
        {
            Request = request;
        }

        public ToolApprovalRequestPayload Request { get; }

        public Task<ToolApprovalResultPayload> Task => _completion.Task;

        public bool TryComplete(ToolApprovalResultPayload result)
        {
            return _completion.TrySetResult(result);
        }

        public bool TryCancel()
        {
            return _completion.TrySetCanceled();
        }
    }
}
