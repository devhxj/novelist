using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ReferenceCorpusGovernanceBridgeHandlers
{
 public static BridgeDispatcher RegisterReferenceCorpusGovernanceHandlers(this BridgeDispatcher dispatcher, IReferenceCorpusGovernanceService service)
 {
 ArgumentNullException.ThrowIfNull(dispatcher); ArgumentNullException.ThrowIfNull(service);
 Register<GetReferenceCorpusGovernancePayload, ReferenceCorpusGovernancePayload>(dispatcher, "GetReferenceCorpusGovernance", service.GetGovernanceAsync);
 Register<SetReferenceCorpusSessionLibraryBindingPayload, ReferenceCorpusGovernancePayload>(dispatcher, "SetReferenceCorpusSessionLibraryBinding", service.SetSessionLibraryBindingAsync);
 Register<UpdateReferenceCorpusLibraryMemberPayload, ReferenceCorpusGovernancePayload>(dispatcher, "UpdateReferenceCorpusLibraryMember", service.UpdateLibraryMemberAsync);
 Register<UpdateReferenceCorpusLicensePayload, ReferenceCorpusGovernancePayload>(dispatcher, "UpdateReferenceCorpusLicense", service.UpdateLicenseAsync);
Register<RebuildReferenceCorpusDedupGroupsPayload, ReferenceCorpusDedupResultPayload>(dispatcher, "RebuildReferenceCorpusDedupGroups", service.RebuildDedupGroupsAsync);
 Register<RecordReferenceCorpusInsertionAuditPayload, bool>(dispatcher, "RecordReferenceCorpusInsertionAudit", service.RecordInsertionAuditAsync);
 Register<BuildReferenceCorpusAggregatesPayload, IReadOnlyList<ReferenceCorpusAggregatePayload>>(dispatcher, "BuildReferenceCorpusAggregates", service.BuildAggregatesAsync);
 Register<ListReferenceCorpusAggregatesPayload, IReadOnlyList<ReferenceCorpusAggregatePayload>>(dispatcher, "ListReferenceCorpusAggregates", service.ListAggregatesAsync);
 Register<RefreshReferenceCorpusReviewQueuePayload, int>(dispatcher, "RefreshReferenceCorpusReviewQueue", service.RefreshReviewQueueAsync);
 Register<ListReferenceCorpusReviewQueuePayload, PageResultPayload<ReferenceCorpusReviewQueueItemPayload>>(dispatcher, "ListReferenceCorpusReviewQueue", service.ListReviewQueueAsync);
 Register<ReviewReferenceCorpusItemsPayload, int>(dispatcher, "ReviewReferenceCorpusItems", service.ReviewItemsAsync);
 Register<ReconcileReferenceCorpusRunPayload, ReferenceCorpusReconcileResultPayload>(dispatcher, "ReconcileReferenceCorpusRun", service.ReconcileRunAsync);
 return dispatcher;
 }

private static void Register<T, TResult>(BridgeDispatcher dispatcher, string method, Func<T, CancellationToken, ValueTask<TResult>> handler)
 {
 dispatcher.Register(method, async (context, cancellationToken) => await handler(Read<T>(context.Payload), cancellationToken));
 }

 private static T Read<T>(JsonElement? payload)
 {
 if (payload is null || payload.Value.ValueKind != JsonValueKind.Object || !payload.Value.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array || args.GetArrayLength() == 0)
 throw Invalid();
 try { return JsonSerializer.Deserialize<T>(args[0].GetRawText(), BridgeJson.SerializerOptions) ?? throw Invalid(); }
 catch (JsonException) { throw Invalid(); }
 }

 private static BridgeValidationException Invalid() => new("Invalid argument 'input'.", new Dictionary<string, string> { ["input"] = "Value must match the expected object shape." });
}
