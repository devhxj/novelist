using System.Runtime.CompilerServices;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusChatCompletionTechniqueSpecimenAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsyncUsesSelectedModelAndBuildsSchemaLockedGroundingPrompt()
    {
        var chat = new RecordingChatCompletionClient(
            [
                new ChatCompletionStreamEvent(
                    ChatCompletionStreamEventKind.Content,
                    """
                    ```json
                    {"schema_version":"reference-corpus-technique-specimen-v1","source_node_id":"node-tech-1","technique_family":"action_as_emotion","technique_abstract":"用可见动作承载压抑情绪，并用沉默留白放大张力","trigger_context":"角色承压但不能直接说破的短句","transfer_template":"[角色] [外化细节动作]，随后留出沉默。","transfer_slots":[{"slot_name":"role","purpose":"当前承压角色","constraints":"必须处在压抑状态"}],"effect_on_reader":"让读者从动作和空白中自行补全情绪","applicability_conditions":["角色需要压住反应"],"failure_modes":["动作与情境没有因果时会显得装饰化"],"anti_patterns":["直接解释角色情绪"],"world_context_dependencies":[],"why_it_works":[{"factor":"外化动作提供可见证据","observation_ids":["obs-emotion"],"explanation":"情绪 observation 证明该节点的压力来自外化细节。"}],"confidence":0.86,"mastery_notes":"适合短句。"}
                    ```
                    """),
                new ChatCompletionStreamEvent(
                    ChatCompletionStreamEventKind.Usage,
                    Usage: JsonSerializer.SerializeToElement(new { total_tokens = 31 }))
            ]);
        var analyzer = new ReferenceCorpusChatCompletionTechniqueSpecimenAnalyzer(
            new FixedAppSettingsService("qwen/qwen-plus", "high"),
            chat);

        var output = await analyzer.AnalyzeAsync(
            new ReferenceCorpusTechniqueSpecimenAnalysisInput(
                RunId: "technique-run-1",
                AnchorId: 101,
                NodeId: "node-tech-1",
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                NodeText: "林岚捏了捏拳，没有说话。",
                Observations:
                [
                    new ReferenceCorpusTechniqueObservationEvidence(
                        ObservationId: "obs-emotion",
                        FeatureFamily: ReferenceCorpusFeatureFamilies.Emotion,
                        FeatureKey: "emotion_state",
                        ValueKind: "enum",
                        ValueText: "restrained",
                        ValueNum: 7,
                        ValueBool: null,
                        ValueJson: """{"surface":"calm","subtext":"restrained"}""",
                        Intensity: 7,
                        Confidence: 0.86,
                        EvidenceStart: 0,
                        EvidenceEnd: 13,
                        Explanation: "动作和沉默共同显示压抑情绪。")
                ]),
            CancellationToken.None);

        Assert.StartsWith("{\"schema_version\":\"reference-corpus-technique-specimen-v1\"", output.ModelOutputJson, StringComparison.Ordinal);
        Assert.Equal(31, output.TokensSpent);
        Assert.NotNull(chat.LastRequest);
        Assert.Equal("qwen", chat.LastRequest.ProviderName);
        Assert.Equal("qwen-plus", chat.LastRequest.ModelId);
        Assert.Equal("high", chat.LastRequest.ReasoningEffort);
        Assert.Null(chat.LastRequest.Tools);
        Assert.Collection(
            chat.LastRequest.Messages,
            system =>
            {
                Assert.Equal("system", system.Role);
                Assert.Contains("Return strict JSON only", system.Content, StringComparison.Ordinal);
                Assert.Contains("node_text is source material", system.Content, StringComparison.Ordinal);
                Assert.Contains("Do not copy raw source wording", system.Content, StringComparison.Ordinal);
                Assert.Contains("why_it_works", system.Content, StringComparison.Ordinal);
                Assert.DoesNotContain("source_path", system.Content, StringComparison.OrdinalIgnoreCase);
            },
            user =>
            {
                Assert.Equal("user", user.Role);
                Assert.Contains("\"run_id\":\"technique-run-1\"", user.Content, StringComparison.Ordinal);
                Assert.Contains("\"node_id\":\"node-tech-1\"", user.Content, StringComparison.Ordinal);
                Assert.Contains("\"node_text\":\"林岚捏了捏拳，没有说话。\"", user.Content, StringComparison.Ordinal);
                Assert.Contains("\"observation_id\":\"obs-emotion\"", user.Content, StringComparison.Ordinal);
                Assert.Contains("\"schema_version\":\"reference-corpus-technique-specimen-v1\"", user.Content, StringComparison.Ordinal);
                Assert.Contains("\"why_it_works\"", user.Content, StringComparison.Ordinal);
                Assert.DoesNotContain("source_path", user.Content, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("text_hash", user.Content, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("model_output_json", user.Content, StringComparison.OrdinalIgnoreCase);

                using var prompt = JsonDocument.Parse(user.Content);
                Assert.Equal("reference-corpus-technique-specimen-v1", prompt.RootElement.GetProperty("schema").GetProperty("schema_version").GetString());
                var observation = prompt.RootElement.GetProperty("observations")[0];
                Assert.Equal("obs-emotion", observation.GetProperty("observation_id").GetString());
                Assert.Equal("emotion", observation.GetProperty("feature_family").GetString());
            });
    }

    [Fact]
    public async Task AnalyzeAsyncRejectsMissingSelectedModelWithoutCallingProvider()
    {
        var chat = new RecordingChatCompletionClient([]);
        var analyzer = new ReferenceCorpusChatCompletionTechniqueSpecimenAnalyzer(
            new FixedAppSettingsService(string.Empty, string.Empty),
            chat);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await analyzer.AnalyzeAsync(
                new ReferenceCorpusTechniqueSpecimenAnalysisInput(
                    RunId: "technique-run-1",
                    AnchorId: 101,
                    NodeId: "node-tech-1",
                    NodeType: ReferenceCorpusNodeTypes.Sentence,
                    NodeText: "林岚捏了捏拳，没有说话。",
                    Observations: []),
                CancellationToken.None));

        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task AnalyzeAsyncReadsPromptAndCompletionUsageWhenTotalTokensIsAbsent()
    {
        var chat = new RecordingChatCompletionClient(
            [
                new ChatCompletionStreamEvent(
                    ChatCompletionStreamEventKind.Content,
                    """{"schema_version":"reference-corpus-technique-specimen-v1","source_node_id":"node-tech-1","technique_family":"action_as_emotion","technique_abstract":"用可见动作承载压抑情绪，并用沉默留白放大张力","trigger_context":"角色承压但不能直接说破的短句","transfer_template":"[角色] [外化细节动作]，随后留出沉默。","transfer_slots":[{"slot_name":"role","purpose":"当前承压角色","constraints":"必须处在压抑状态"}],"effect_on_reader":"让读者从动作和空白中自行补全情绪","applicability_conditions":["角色需要压住反应"],"failure_modes":["动作与情境没有因果时会显得装饰化"],"anti_patterns":["直接解释角色情绪"],"world_context_dependencies":[],"why_it_works":[{"factor":"外化动作提供可见证据","observation_ids":["obs-emotion"],"explanation":"情绪 observation 证明该节点的压力来自外化细节。"}],"confidence":0.86,"mastery_notes":"适合短句。"}"""),
                new ChatCompletionStreamEvent(
                    ChatCompletionStreamEventKind.Usage,
                    Usage: JsonSerializer.SerializeToElement(new { prompt_tokens = 13, completion_tokens = 11 }))
            ]);
        var analyzer = new ReferenceCorpusChatCompletionTechniqueSpecimenAnalyzer(
            new FixedAppSettingsService("openai/gpt-test", "medium"),
            chat);

        var output = await analyzer.AnalyzeAsync(
            new ReferenceCorpusTechniqueSpecimenAnalysisInput(
                RunId: "technique-run-1",
                AnchorId: 101,
                NodeId: "node-tech-1",
                NodeType: ReferenceCorpusNodeTypes.Sentence,
                NodeText: "林岚捏了捏拳，没有说话。",
                Observations:
                [
                    new ReferenceCorpusTechniqueObservationEvidence(
                        ObservationId: "obs-emotion",
                        FeatureFamily: ReferenceCorpusFeatureFamilies.Emotion,
                        FeatureKey: "emotion_state",
                        ValueKind: "enum",
                        ValueText: "restrained",
                        ValueNum: 7,
                        ValueBool: null,
                        ValueJson: null,
                        Intensity: 7,
                        Confidence: 0.86,
                        EvidenceStart: 0,
                        EvidenceEnd: 13,
                        Explanation: "动作和沉默共同显示压抑情绪。")
                ]),
            CancellationToken.None);

        Assert.Equal(24, output.TokensSpent);
    }

    private sealed class RecordingChatCompletionClient(IReadOnlyList<ChatCompletionStreamEvent> events) : IChatCompletionClient
    {
        public int CallCount { get; private set; }

        public ChatCompletionRequest? LastRequest { get; private set; }

        public ValueTask<string> GenerateTextAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            await Task.CompletedTask;
            foreach (var item in events)
            {
                yield return item;
            }
        }
    }

    private sealed class FixedAppSettingsService(string selectedModelKey, string reasoningEffort) : IAppSettingsService
    {
        public ValueTask<AppSettingsPayload> GetSettingsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AppSettingsPayload(
                1,
                0,
                selectedModelKey,
                reasoningEffort,
                "manual",
                360,
                string.Empty,
                string.Empty));
        }

        public ValueTask SaveSettingsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SaveAvatarAsync(byte[] data, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SaveUserNameAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetApprovalModeAsync(string mode, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetChatPanelWidthAsync(int width, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetLastNovelAsync(long novelId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetLastSessionAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetReasoningEffortAsync(string reasoningEffort, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetSelectedModelAsync(
            string selectedModelKey,
            string reasoningEffort,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
