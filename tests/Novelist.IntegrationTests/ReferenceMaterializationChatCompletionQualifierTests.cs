using System.Runtime.CompilerServices;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterializationChatCompletionQualifierTests
{
    [Fact]
    public async Task QualifyAsyncUsesFrozenModelAndReturnsOneValidatedDecisionForEveryCandidate()
    {
        var chat = new RecordingChatCompletionClient(
        [
            new ChatCompletionStreamEvent(
                ChatCompletionStreamEventKind.Content,
                """
                {"schema_version":"reference-materialization-qualifier-v2","decisions":[{"candidate_id":"candidate-a","decision":"accept","source_spans":[{"node_id":"node-a","start":0,"end":7}],"scores":{"semantic_completeness":0.91,"information_density":0.72,"narrative_value":0.83,"transferability":0.61,"context_independence":0.75,"technique_distinctiveness":0.69},"tags":{"narrative_functions":["reveal"],"emotion_mechanics":["escalation"],"pov":["close_third"],"techniques":["subtext"],"scene_beat_roles":["turn_beat"],"character_relations":["mistrust"],"causal_information_roles":["reveal"]},"confidence":0.84,"reason_codes":["complete_exchange"]},{"candidate_id":"candidate-b","decision":"reject","source_spans":[{"node_id":"node-b","start":0,"end":5}],"scores":{"semantic_completeness":0.15,"information_density":0.12,"narrative_value":0.09,"transferability":0.03,"context_independence":0.18,"technique_distinctiveness":0.04},"tags":{"narrative_functions":[],"emotion_mechanics":[],"pov":[],"techniques":[],"scene_beat_roles":[],"character_relations":[],"causal_information_roles":[]},"confidence":0.93,"reason_codes":["context_dependent"]}]}
                """)
        ]);
        var qualifier = new ReferenceMaterializationChatCompletionQualifier(chat);

        var result = await qualifier.QualifyAsync(
            new ReferenceMaterializationQualificationRequest(
                new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                [
                    Candidate("candidate-a", "node-a", "他说出了真相。\n他没有回头。"),
                    Candidate("candidate-b", "node-b", "他点了头。")
                ]),
            CancellationToken.None);

        Assert.Equal(2, result.Decisions.Count);
        Assert.Equal(ReferenceMaterializationCandidateDecisions.Accepted, result.Decisions[0].Decision);
        Assert.Equal("node-a", Assert.Single(result.Decisions[0].SourceSpans).NodeId);
        Assert.Equal(["turn_beat"], result.Decisions[0].Tags.SceneBeatRoles);
        Assert.Equal(["mistrust"], result.Decisions[0].Tags.CharacterRelations);
        Assert.Equal(["reveal"], result.Decisions[0].Tags.CausalInformationRoles);
        Assert.Equal(ReferenceMaterializationCandidateDecisions.Rejected, result.Decisions[1].Decision);
        Assert.Equal("qwen", chat.LastRequest?.ProviderName);
        Assert.Equal("qwen-plus", chat.LastRequest?.ModelId);
        Assert.Equal("high", chat.LastRequest?.ReasoningEffort);
        Assert.Contains("Return strict JSON only", chat.LastRequest?.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("candidate-a", chat.LastRequest?.Messages[1].Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"schema_version\":\"reference-materialization-qualifier-v1\",\"decisions\":[{\"candidate_id\":\"candidate-a\",\"decision\":\"approve\",\"source_spans\":[{\"node_id\":\"node-a\",\"start\":0,\"end\":7}],\"scores\":{\"semantic_completeness\":0.5,\"information_density\":0.5,\"narrative_value\":0.5,\"transferability\":0.5,\"context_independence\":0.5,\"technique_distinctiveness\":0.5},\"tags\":{\"narrative_functions\":[],\"emotion_mechanics\":[],\"pov\":[],\"techniques\":[]},\"confidence\":0.5,\"reason_codes\":[\"complete_exchange\"]}]}")]
    [InlineData("{\"schema_version\":\"reference-materialization-qualifier-v1\",\"decisions\":[{\"candidate_id\":\"candidate-a\",\"decision\":\"accept\",\"source_spans\":[{\"node_id\":\"node-b\",\"start\":0,\"end\":7}],\"scores\":{\"semantic_completeness\":0.5,\"information_density\":0.5,\"narrative_value\":0.5,\"transferability\":0.5,\"context_independence\":0.5,\"technique_distinctiveness\":0.5},\"tags\":{\"narrative_functions\":[],\"emotion_mechanics\":[],\"pov\":[],\"techniques\":[]},\"confidence\":0.5,\"reason_codes\":[\"complete_exchange\"]}]}")]
    [InlineData("{\"schema_version\":\"reference-materialization-qualifier-v1\",\"decisions\":[{\"candidate_id\":\"candidate-a\",\"decision\":\"accept\",\"source_spans\":[{\"node_id\":\"node-a\",\"start\":0,\"end\":99}],\"scores\":{\"semantic_completeness\":0.5,\"information_density\":0.5,\"narrative_value\":0.5,\"transferability\":0.5,\"context_independence\":0.5,\"technique_distinctiveness\":0.5},\"tags\":{\"narrative_functions\":[],\"emotion_mechanics\":[],\"pov\":[],\"techniques\":[]},\"confidence\":0.5,\"reason_codes\":[\"complete_exchange\"]}]}")]
    [InlineData("{\"schema_version\":\"reference-materialization-qualifier-v1\",\"decisions\":[{\"candidate_id\":\"candidate-a\",\"decision\":\"accept\",\"source_spans\":[{\"node_id\":\"node-a\",\"start\":0,\"end\":7}],\"scores\":{\"semantic_completeness\":0.5,\"information_density\":0.5,\"narrative_value\":0.5,\"transferability\":0.5,\"context_independence\":0.5,\"technique_distinctiveness\":0.5},\"tags\":{\"narrative_functions\":[],\"emotion_mechanics\":[],\"pov\":[],\"techniques\":[]},\"confidence\":0.5,\"reason_codes\":[\"complete_exchange\"],\"new_text\":\"invented\"}]}")]
    public async Task QualifyAsyncRejectsInvalidOrUngroundedOutput(string response)
    {
        var qualifier = new ReferenceMaterializationChatCompletionQualifier(
            new RecordingChatCompletionClient([new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, response)]));

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await qualifier.QualifyAsync(
                new ReferenceMaterializationQualificationRequest(
                    new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                    [Candidate("candidate-a", "node-a", "他说出了真相。")]),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.LlmOutputInvalid, exception.ErrorCode);
    }

    [Fact]
    public async Task QualifyAsyncRejectsPartialDecisionSets()
    {
        const string response = """
            {"schema_version":"reference-materialization-qualifier-v1","decisions":[{"candidate_id":"candidate-a","decision":"accept","source_spans":[{"node_id":"node-a","start":0,"end":7}],"scores":{"semantic_completeness":0.5,"information_density":0.5,"narrative_value":0.5,"transferability":0.5,"context_independence":0.5,"technique_distinctiveness":0.5},"tags":{"narrative_functions":[],"emotion_mechanics":[],"pov":[],"techniques":[]},"confidence":0.5,"reason_codes":["complete_exchange"]}]}
            """;
        var qualifier = new ReferenceMaterializationChatCompletionQualifier(
            new RecordingChatCompletionClient([new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, response)]));

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await qualifier.QualifyAsync(
                new ReferenceMaterializationQualificationRequest(
                    new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                    [Candidate("candidate-a", "node-a", "他说出了真相。"), Candidate("candidate-b", "node-b", "他点了头。")]),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.LlmOutputInvalid, exception.ErrorCode);
    }

    [Fact]
    public async Task QualifyAsyncRejectsUnknownExtendedTagValues()
    {
        const string response = """
            {"schema_version":"reference-materialization-qualifier-v2","decisions":[{"candidate_id":"candidate-a","decision":"accept","source_spans":[{"node_id":"node-a","start":0,"end":7}],"scores":{"semantic_completeness":0.5,"information_density":0.5,"narrative_value":0.5,"transferability":0.5,"context_independence":0.5,"technique_distinctiveness":0.5},"tags":{"narrative_functions":[],"emotion_mechanics":[],"pov":[],"techniques":[],"scene_beat_roles":["invented_beat"],"character_relations":[],"causal_information_roles":[]},"confidence":0.5,"reason_codes":["complete_exchange"]}]}
            """;
        var qualifier = new ReferenceMaterializationChatCompletionQualifier(
            new RecordingChatCompletionClient([new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, response)]));

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await qualifier.QualifyAsync(
                new ReferenceMaterializationQualificationRequest(
                    new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                    [Candidate("candidate-a", "node-a", "他说出了真相。")]),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.LlmOutputInvalid, exception.ErrorCode);
    }

    private static ReferenceMaterializationQualificationCandidate Candidate(string candidateId, string nodeId, string text)
    {
        return new ReferenceMaterializationQualificationCandidate(
            candidateId,
            "dialogue_exchange",
            text,
            [new ReferenceMaterializationQualificationSourceNode(nodeId, text)]);
    }

    private sealed class RecordingChatCompletionClient(IReadOnlyList<ChatCompletionStreamEvent> events) : IChatCompletionClient
    {
        public ChatCompletionRequest? LastRequest { get; private set; }

        public ValueTask<string> GenerateTextAsync(ChatCompletionRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            await Task.CompletedTask;
            foreach (var item in events)
            {
                yield return item;
            }
        }
    }
}
