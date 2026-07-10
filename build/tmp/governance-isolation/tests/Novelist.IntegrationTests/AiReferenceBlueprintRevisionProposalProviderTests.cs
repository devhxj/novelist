using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class AiReferenceBlueprintRevisionProposalProviderTests
{
    [Fact]
    public async Task ProposeRevisionAsyncUsesSelectedModelAndFiltersUntrustedChanges()
    {
        var chat = new RecordingChatCompletionClient(
            """
            {
              "blueprint_id": 999,
              "review_id": "review-stale",
              "origin": "malicious",
              "revision_reason": "model suggested repair",
              "changes": [
                { "field_path": "final_hook", "new_value": "AI hook stays inside approved facts" },
                { "field_path": "known_facts", "new_value": "[\"model-invented fact\"]" },
                { "field_path": "beat:42:beat:1:external_evidence", "new_value": "A visible hesitation shows the pressure." },
                { "field_path": "beat:missing:external_evidence", "new_value": "missing beat" },
                { "field_path": "chapter_function", "new_value": "unsupported field" },
                { "field_path": "final_hook", "new_value": "duplicate should be ignored" }
              ]
            }
            """);
        var provider = new AiReferenceBlueprintRevisionProposalProvider(
            new FixedAppSettingsService("deepseek/deepseek-chat", "high"),
            chat);

        var blueprint = Blueprint();
        var review = FailedReview(
            new ReferenceChapterBlueprintReviewDefectPayload(
                "forbidden_fact",
                "final_hook",
                string.Empty,
                "error",
                "Final hook contains forbidden wording.",
                "Rewrite the final hook inside known facts."),
            new ReferenceChapterBlueprintReviewDefectPayload(
                "emotion",
                "beat:42:beat:1:external_evidence",
                "42:beat:1",
                "error",
                "Emotion lacks external evidence.",
                "Add visible external evidence."));

        var proposal = await provider.ProposeRevisionAsync(blueprint, review, CancellationToken.None);

        Assert.NotNull(chat.LastRequest);
        Assert.Equal("deepseek", chat.LastRequest.ProviderName);
        Assert.Equal("deepseek-chat", chat.LastRequest.ModelId);
        Assert.Equal("high", chat.LastRequest.ReasoningEffort);
        Assert.Equal(blueprint.BlueprintId, proposal.BlueprintId);
        Assert.Equal(review.ReviewId, proposal.ReviewId);
        Assert.Equal("ai_assistant", proposal.Origin);
        Assert.Contains("model suggested repair", proposal.RevisionReason, StringComparison.OrdinalIgnoreCase);

        Assert.Collection(
            proposal.Changes,
            change =>
            {
                Assert.Equal("final_hook", change.FieldPath);
                Assert.Equal("AI hook stays inside approved facts", change.NewValue);
            },
            change =>
            {
                Assert.Equal("beat:42:beat:1:external_evidence", change.FieldPath);
                Assert.Equal("A visible hesitation shows the pressure.", change.NewValue);
            });
    }

    [Fact]
    public async Task ProposeRevisionAsyncFallsBackWhenNoModelIsSelected()
    {
        var chat = new RecordingChatCompletionClient("{}");
        var provider = new AiReferenceBlueprintRevisionProposalProvider(
            new FixedAppSettingsService(string.Empty, string.Empty),
            chat);
        var blueprint = Blueprint();
        var review = FailedReview(new ReferenceChapterBlueprintReviewDefectPayload(
            "forbidden_fact",
            "final_hook",
            string.Empty,
            "error",
            "Final hook contains forbidden wording.",
            "Rewrite the final hook inside known facts."));

        var proposal = await provider.ProposeRevisionAsync(blueprint, review, CancellationToken.None);

        Assert.Equal(0, chat.CallCount);
        Assert.Equal("orchestrator", proposal.Origin);
        var change = Assert.Single(proposal.Changes);
        Assert.Equal("final_hook", change.FieldPath);
    }

    [Fact]
    public async Task ProposeRevisionAsyncFallsBackWhenModelReturnsInvalidJson()
    {
        var chat = new RecordingChatCompletionClient("not json");
        var provider = new AiReferenceBlueprintRevisionProposalProvider(
            new FixedAppSettingsService("qwen/qwen-plus", string.Empty),
            chat);
        var blueprint = Blueprint();
        var review = FailedReview(new ReferenceChapterBlueprintReviewDefectPayload(
            "forbidden_fact",
            "final_hook",
            string.Empty,
            "error",
            "Final hook contains forbidden wording.",
            "Rewrite the final hook inside known facts."));

        var proposal = await provider.ProposeRevisionAsync(blueprint, review, CancellationToken.None);

        Assert.Equal(1, chat.CallCount);
        Assert.Equal("orchestrator", proposal.Origin);
        var change = Assert.Single(proposal.Changes);
        Assert.Equal("final_hook", change.FieldPath);
    }

    private static ReferenceChapterBlueprintPayload Blueprint()
    {
        return new ReferenceChapterBlueprintPayload(
            42,
            7,
            3,
            "雨夜回门",
            ReferenceBlueprintStates.ReviewFailed,
            "next",
            "source-hash",
            "context-hash",
            "analysis-hash",
            1,
            0,
            0,
            "主角在雨夜回到旧门前，压力必须通过动作和感官细节推进。",
            new ReferenceChapterBlueprintAnalysisTrackPayload("logic", "logic summary", ["logic point"]),
            new ReferenceChapterBlueprintAnalysisTrackPayload("emotion", "emotion summary", ["emotion point"]),
            new ReferenceChapterBlueprintAnalysisTrackPayload("narration", "narration summary", ["narration point"]),
            new ReferenceChapterBlueprintAnalysisTrackPayload("character", "character summary", ["character point"]),
            new ReferenceChapterBlueprintAnalysisTrackPayload("reference", "reference summary", ["reference point"]),
            new ReferenceChapterBlueprintAnalysisTrackPayload("transition", "transition summary", ["transition point"]),
            new ReferenceChapterBlueprintExecutionTrackPayload(
                "execution",
                "execution summary",
                ["hold the threshold"],
                ["interiority"],
                ["show evidence before explanation"],
                ["rain on the door"],
                ["reject forbidden facts"]),
            "主角已经站在旧门外。",
            "主角决定敲门。",
            "final hook leaks forbidden wording",
            "林岚",
            "close",
            ["雨声压低了整条街的呼吸", "主角在门口"],
            ["forbidden wording"],
            [],
            [Beat()],
            LatestReview: null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
    }

    private static ReferenceChapterBlueprintBeatPayload Beat()
    {
        return new ReferenceChapterBlueprintBeatPayload(
            "42:beat:1",
            1,
            1,
            ReferenceBlueprintBeatTypes.Interiority,
            "让主角在门前停顿并承受压力。",
            "雨夜和旧门共同触发迟疑。",
            "他必须决定是否敲门。",
            "主角走到旧门前。",
            "主角准备敲门。",
            "雨声先压住动作。",
            "手指停在门板前。",
            "林岚",
            "close",
            ["林岚知道自己来到旧门前"],
            ["forbidden wording"],
            ["迟疑"],
            ["决定敲门"],
            ["确认门内是否有人"],
            ["门后的人不会回应"],
            ["门内关系紧张"],
            "雨声触发迟疑。",
            "克制",
            "紧绷",
            "没有立刻敲门。",
            "手指停在湿冷门板前。",
            "贴近感官，不提前解释。",
            "慢",
            "让动作承载心理压力。",
            "interiority",
            "避免对白替代心理变化。",
            "雨水贴着门缝。",
            "不直接解释恐惧来源。",
            "雨水和门板细节必须来自参考材料。",
            "拒绝没有外部证据的心理说明。",
            ["主角在门口"],
            [],
            new ReferenceMaterialQueryPayload(
                "雨夜 门口 迟疑",
                [ReferenceMaterialTypes.Sentence],
                ["pressure"],
                ["interiority"],
                ["close"],
                ["sensory"],
                3),
            [ReferenceMaterialTypes.Sentence],
            ReferenceRewriteLevels.L2,
            [],
            "slot_only",
            string.Empty,
            ["避免剧本式动作堆叠"],
            []);
    }

    private static ReferenceChapterBlueprintReviewPayload FailedReview(
        params ReferenceChapterBlueprintReviewDefectPayload[] defects)
    {
        return new ReferenceChapterBlueprintReviewPayload(
            "review-ai-1",
            42,
            "context-hash",
            "source-hash",
            "analysis-hash",
            1,
            ReferenceBlueprintReviewStatuses.Failed,
            0.6,
            defects.Select(defect => defect.Reason).ToArray(),
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            defects.Where(defect => defect.Category == "forbidden_fact").Select(defect => defect.Reason).ToArray(),
            [],
            [],
            [],
            [],
            [],
            defects.Select(defect => defect.RequiredFix).ToArray(),
            defects,
            DateTimeOffset.UnixEpoch);
    }

    private sealed class RecordingChatCompletionClient(string response) : IChatCompletionClient
    {
        public int CallCount { get; private set; }

        public ChatCompletionRequest? LastRequest { get; private set; }

        public ValueTask<string> GenerateTextAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            return ValueTask.FromResult(response);
        }

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            await Task.CompletedTask;
            yield return new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, response);
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
