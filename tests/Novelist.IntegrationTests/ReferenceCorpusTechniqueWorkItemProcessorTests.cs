using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusTechniqueWorkItemProcessorTests
{
 [Fact]
 public async Task ProcessAsyncRetriesAgainstFrozenEvidenceAndRemainingBudget()
 {
 var analyzer = new SequenceAnalyzer(("not-json", 7), (ValidJson(), 13));
 var delay = new RecordingDelay();
 var input = Input(100);

 var result = await new ReferenceCorpusTechniqueWorkItemProcessor(analyzer, delay)
 .ProcessAsync(input, CancellationToken.None);

 Assert.Equal(ReferenceCorpusTechniqueWorkItemStatuses.Succeeded, result.Status);
 Assert.Equal(20, result.TokensSpent);
 Assert.NotNull(result.Candidate);
 Assert.Equal([100, 93], analyzer.Calls.Select(call => call.MaxOutputTokens));
 Assert.All(analyzer.Calls, call =>
 {
 Assert.Equal("他攥紧手指，没有开口。", call.NodeText);
 Assert.Equal(["obs-emotion", "obs-rhetoric"], call.Observations.Select(item => item.ObservationId));
 });
 Assert.Single(delay.Delays);
 }

 [Fact]
 public async Task ProcessAsyncStopsAtBudgetWhenUsageIsUnknown()
 {
 var analyzer = new SequenceAnalyzer(("not-json", 0), (ValidJson(), 0));

 var result = await new ReferenceCorpusTechniqueWorkItemProcessor(analyzer, NoOpDelay.Instance)
 .ProcessAsync(Input(16), CancellationToken.None);

 Assert.Equal(ReferenceCorpusTechniqueWorkItemStatuses.BudgetExhausted, result.Status);
 Assert.Equal(16, result.TokensSpent);
 Assert.Single(analyzer.Calls);
 Assert.Contains(result.Diagnostics, item => item.Contains("unknown", StringComparison.OrdinalIgnoreCase));
 }

 private static ReferenceCorpusTechniqueWorkItemInput Input(int budget) => new(
 "run-tech-1",
 101,
 "node-tech-1",
 ReferenceCorpusNodeTypes.Sentence,
 "他攥紧手指，没有开口。",
 [
 new("obs-emotion", "emotion", "emotion_mode", "text", "suppressed", null, null, null, 8, 0.88, 0, 6, "动作承载压抑情绪"),
 new("obs-rhetoric", "rhetoric", "ellipsis", "text", "silence", null, null, null, null, 0.84, 7, 12, "沉默形成留白")
 ],
 4,
 new ReferenceCorpusFeatureTokenEnvelope(budget, budget, 16));

 private static string ValidJson() =>
 """
 {
 "schema_version": "reference-corpus-technique-specimen-v1",
 "source_node_id": "node-tech-1",
 "technique_family": "action_as_emotion",
 "technique_abstract": "用可见动作承载压抑情绪，并以沉默留白放大张力",
 "trigger_context": "角色有强烈情绪但不能直接说破的短句节点",
 "transfer_template": "[角色] [外化细节动作]，随后留出沉默。",
 "transfer_slots": [
 { "slot_name": "role", "purpose": "当前承压角色", "constraints": "必须处在情绪压抑状态" },
 { "slot_name": "external_action", "purpose": "可见的细节动作", "constraints": "动作必须承载情绪压力" }
 ],
 "effect_on_reader": "让读者从动作和空白中自行补全情绪",
 "applicability_conditions": ["角色需要压住反应"],
 "failure_modes": ["动作与情境没有因果时会显得装饰化"],
 "anti_patterns": ["直接解释角色情绪"],
 "world_context_dependencies": [],
 "why_it_works": [
 { "factor": "动作提供可见证据", "observation_ids": ["obs-emotion"], "explanation": "情绪证据来自外化动作。" },
 { "factor": "沉默形成留白", "observation_ids": ["obs-rhetoric"], "explanation": "修辞证据让读者补全反应。" }
 ],
 "confidence": 0.86,
 "mastery_notes": "适合短句。"
 }
 """;

 private sealed class SequenceAnalyzer(params (string Json, int Tokens)[] outputs) : IReferenceCorpusTechniqueSpecimenAnalyzer
 {
 private readonly Queue<(string Json, int Tokens)> _outputs = new(outputs);
 public List<ReferenceCorpusTechniqueSpecimenAnalysisInput> Calls { get; } = [];

 public ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisOutput> AnalyzeAsync(
 ReferenceCorpusTechniqueSpecimenAnalysisInput input,
 CancellationToken cancellationToken)
 {
 Calls.Add(input);
 var output = _outputs.Dequeue();
 return ValueTask.FromResult(new ReferenceCorpusTechniqueSpecimenAnalysisOutput(output.Json, output.Tokens));
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
