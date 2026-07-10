using Novelist.Core.App;
using Novelist.Infrastructure.App;
using Novelist.Contracts.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusFeatureWorkItemProcessorTests
{
 [Fact]
 public async Task ProcessAsyncUsesFrozenInputAndRemainingTokenBudgetAcrossRetries()
 {
 var analyzer = new SequenceAnalyzer(("not-json", 7), (InvalidJson(), 11), (ValidJson(), 13));
 var delay = new RecordingDelay();
 var input = new ReferenceCorpusFeatureWorkItemInput(
 "run-1",
 101,
 "node-1",
 "Frozen sentence.",
 ReferenceCorpusNodeTypes.Sentence,
 ReferenceCorpusFeatureFamilies.Syntax,
 ReferenceCorpusFeatureAnalysisContext.Empty,
 4,
 new ReferenceCorpusFeatureTokenEnvelope(100, 100, 16));

 var result = await new ReferenceCorpusFeatureWorkItemProcessor(analyzer, delay)
 .ProcessAsync(input, CancellationToken.None);

 Assert.Equal(ReferenceCorpusFeatureWorkItemStatuses.Succeeded, result.Status);
 Assert.Equal(31, result.TokensSpent);
 Assert.Equal([100, 93, 82], analyzer.Calls.Select(call => call.MaxOutputTokens));
 Assert.All(analyzer.Calls, call => Assert.Equal("Frozen sentence.", call.NodeText));
 Assert.Equal(2, delay.Delays.Count);
 }

 [Fact]
 public async Task ProcessAsyncAcceptsPartialAndConservativelyChargesUnknownUsage()
 {
 var partial = await new ReferenceCorpusFeatureWorkItemProcessor(
 new SequenceAnalyzer((PartialJson(), 9)), NoOpDelay.Instance)
 .ProcessAsync(Input(100), CancellationToken.None);
 Assert.Equal(ReferenceCorpusFeatureFamilyValidationStatuses.Partial, partial.ValidationStatus);
 Assert.Single(partial.AcceptedObservations);
 Assert.Single(partial.RejectedObservations);

 var unknownAnalyzer = new SequenceAnalyzer(("not-json", 0), (ValidJson(), 0));
 var unknown = await new ReferenceCorpusFeatureWorkItemProcessor(unknownAnalyzer, NoOpDelay.Instance)
 .ProcessAsync(Input(16), CancellationToken.None);
 Assert.Equal(ReferenceCorpusFeatureWorkItemStatuses.BudgetExhausted, unknown.Status);
 Assert.Equal(16, unknown.TokensSpent);
 Assert.Single(unknownAnalyzer.Calls);
 Assert.Contains(unknown.Diagnostics, item => item.Contains("unknown", StringComparison.OrdinalIgnoreCase));
 }

 private static ReferenceCorpusFeatureWorkItemInput Input(int budget) => new(
 "run-1",
 101,
 "node-1",
 "Frozen sentence.",
 ReferenceCorpusNodeTypes.Sentence,
 ReferenceCorpusFeatureFamilies.Syntax,
 ReferenceCorpusFeatureAnalysisContext.Empty,
 4,
 new ReferenceCorpusFeatureTokenEnvelope(budget, budget, 16));

 private static string ValidJson() =>
 "{\"schema_version\":\"reference-corpus-feature-family-v1\",\"family\":\"syntax\",\"node_type\":\"sentence\",\"observations\":[{\"feature_key\":\"sentence_pattern\",\"label\":\"subject_predicate\",\"complexity\":\"simple\",\"confidence\":0.8,\"evidence_start\":0,\"evidence_end\":4,\"explanation\":\"valid\"}]}";

 private static string InvalidJson() => ValidJson().Replace("subject_predicate", "unsupported", StringComparison.Ordinal);

 private static string PartialJson() => ValidJson().Replace(
 "]}",
 ",{\"feature_key\":\"sentence_pattern\",\"label\":\"unsupported\",\"complexity\":\"simple\",\"confidence\":0.8,\"evidence_start\":0,\"evidence_end\":4,\"explanation\":\"unsupported\"}]}",
 StringComparison.Ordinal);

 private sealed class SequenceAnalyzer(params (string Json, int Tokens)[] outputs) : IReferenceCorpusFeatureFamilyAnalyzer
 {
 private readonly Queue<(string Json, int Tokens)> _outputs = new(outputs);
 public List<ReferenceCorpusFeatureFamilyAnalysisInput> Calls { get; } = [];

 public ValueTask<ReferenceCorpusFeatureFamilyAnalysisOutput> AnalyzeAsync(
 ReferenceCorpusFeatureFamilyAnalysisInput input,
 CancellationToken cancellationToken)
 {
 Calls.Add(input);
 var output = _outputs.Dequeue();
 return ValueTask.FromResult(new ReferenceCorpusFeatureFamilyAnalysisOutput(output.Json, output.Tokens));
 }
 }

 private sealed class RecordingDelay : IReferenceCorpusFeatureValidationDelay
 {
 public List<TimeSpan> Delays { get; } = [];
 public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
 {
 Delays.Add(delay);
 return ValueTask.CompletedTask;
 }
 }

 private sealed class NoOpDelay : IReferenceCorpusFeatureValidationDelay
 {
 public static NoOpDelay Instance { get; } = new();
 public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => ValueTask.CompletedTask;
 }
}
