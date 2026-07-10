using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Tests.Bridge;

public sealed class ReferenceCorpusCascadeImpactBridgeTests
{
 [Fact]
 public async Task RoutesObservationIdsAndReturnsCascadeImpact()
 {
 var service = new RecordingReferenceCorpusService();
 var dispatcher = new BridgeDispatcher().RegisterReferenceCorpusHandlers(service);
 var payload = JsonSerializer.Serialize(
 new
 {
 kind = "request",
 id = "cascade-request",
 method = "GetReferenceCorpusCascadeImpact",
 payload = new
 {
 args = new object?[]
 {
 new { observation_ids = new[] { "obs-2", "obs-1" } }
 }
 }
 },
 BridgeJson.SerializerOptions);

 var response = await dispatcher.DispatchAsync(payload);

 Assert.Null(response.CancelRequestId);
 Assert.False(string.IsNullOrWhiteSpace(response.OutboundJson));
 using var json = JsonDocument.Parse(response.OutboundJson);
 Assert.True(json.RootElement.GetProperty("ok").GetBoolean(), json.RootElement.GetRawText());
 Assert.Equal(["obs-2", "obs-1"], service.ObservationIds);
 var result = json.RootElement.GetProperty("result");
 Assert.Equal(["obs-1", "obs-2"], ReadStrings(result, "observation_ids"));
 Assert.Equal(["specimen-a"], ReadStrings(result, "specimen_ids"));
 Assert.Equal(["beat-a"], ReadStrings(result, "beat_ids"));
 Assert.Equal(["blueprint-a"], ReadStrings(result, "blueprint_ids"));
 }

 private static string[] ReadStrings(JsonElement parent, string propertyName) =>
 parent.GetProperty(propertyName).EnumerateArray().Select(item => item.GetString()!).ToArray();

private sealed class RecordingReferenceCorpusService : IReferenceCorpusService
{
public IReadOnlyList<string> ObservationIds { get; private set; } = [];

 public ValueTask<PageResultPayload<ReferenceCorpusCandidatePayload>> SearchCandidatesAsync(
 SearchReferenceCorpusCandidatesPayload input,
 CancellationToken cancellationToken) => throw new NotSupportedException();

 public ValueTask<ReferenceCorpusTechniqueVectorIndexBackfillPayload> BackfillTechniqueVectorIndexAsync(
 BackfillReferenceCorpusTechniqueVectorIndexPayload input,
 CancellationToken cancellationToken) => throw new NotSupportedException();

public ValueTask<ReferenceCorpusCascadeImpactPayload> GetCascadeImpactAsync(
 GetReferenceCorpusCascadeImpactPayload input,
 CancellationToken cancellationToken)
 {
 cancellationToken.ThrowIfCancellationRequested();
 ObservationIds = input.ObservationIds.ToArray();
 return ValueTask.FromResult(new ReferenceCorpusCascadeImpactPayload(
 ["obs-1", "obs-2"],
 ["specimen-a"],
 ["beat-a"],
 ["blueprint-a"]));
 }
 }
}
