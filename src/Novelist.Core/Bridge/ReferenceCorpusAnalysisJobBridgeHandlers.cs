using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ReferenceCorpusAnalysisJobBridgeHandlers
{
 public static BridgeDispatcher RegisterReferenceCorpusAnalysisJobHandlers(
 this BridgeDispatcher dispatcher,
 IReferenceCorpusAnalysisScheduler scheduler)
 {
 ArgumentNullException.ThrowIfNull(dispatcher);
 ArgumentNullException.ThrowIfNull(scheduler);

 dispatcher.Register("EnqueueReferenceCorpusAnalysisJob", async (context, cancellationToken) =>
 await scheduler.EnqueueAsync(Read<EnqueueReferenceCorpusAnalysisJobPayload>(context.Payload), cancellationToken));
 dispatcher.Register("GetReferenceCorpusAnalysisJob", async (context, cancellationToken) =>
 await scheduler.GetAsync(Read<GetReferenceCorpusAnalysisJobPayload>(context.Payload), cancellationToken));
 dispatcher.Register("ListReferenceCorpusAnalysisJobs", async (context, cancellationToken) =>
 await scheduler.ListAsync(Read<ListReferenceCorpusAnalysisJobsPayload>(context.Payload), cancellationToken));
 dispatcher.Register("PauseReferenceCorpusAnalysisJob", async (context, cancellationToken) =>
 await scheduler.PauseAsync(Read<PauseReferenceCorpusAnalysisJobPayload>(context.Payload), cancellationToken));
 dispatcher.Register("ResumeReferenceCorpusAnalysisJob", async (context, cancellationToken) =>
 await scheduler.ResumeAsync(Read<ResumeReferenceCorpusAnalysisJobPayload>(context.Payload), cancellationToken));
 dispatcher.Register("CancelReferenceCorpusAnalysisJob", async (context, cancellationToken) =>
 await scheduler.CancelAsync(Read<CancelReferenceCorpusAnalysisJobPayload>(context.Payload), cancellationToken));
 dispatcher.Register("ReprioritizeReferenceCorpusAnalysisJob", async (context, cancellationToken) =>
 await scheduler.ReprioritizeAsync(Read<ReprioritizeReferenceCorpusAnalysisJobPayload>(context.Payload), cancellationToken));
 return dispatcher;
 }

private static T Read<T>(JsonElement? payload)
{
 if (payload is null || payload.Value.ValueKind != JsonValueKind.Object ||
 !payload.Value.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array ||
 args.GetArrayLength() < 1)
 throw Invalid("input", "Expected one object argument.");
 var value = args[0];
if (value.ValueKind != JsonValueKind.Object)
 throw Invalid("input", "Value must be an object.");
try
{
 return JsonSerializer.Deserialize<T>(value.GetRawText(), BridgeJson.SerializerOptions)
 ?? throw Invalid("input", "Value is required.");
}
 catch (JsonException)
{
 throw Invalid("input", "Value must match the expected object shape.");
}
}

 private static BridgeValidationException Invalid(string argumentName, string message) => new(
 $"Invalid argument '{argumentName}'.",
 new Dictionary<string, string> { [argumentName] = message });
}
