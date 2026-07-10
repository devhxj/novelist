using System.Text.Json;
using Novelist.Core.App;

namespace Novelist.IntegrationTests.TestDoubles;

public sealed class FakeLlmHarnessTests
{
    [Fact]
    public async Task GenerateTextAsyncReturnsScenarioResponsesAndRecordsCalls()
    {
        var harness = new FakeLlmHarness()
            .RespondWithJson(
                "parse-query-context",
                """{"schema_version":"fake-query-context-v1","scene_type":"doorway_pressure"}""",
                scenario: "happy-path")
            .RespondWithText("draft-paragraph", "她把回答压回喉间。", scenario: "minimal");
        var request = RequestWithPromptMetadata("parse-query-context", "happy-path");

        var response = await harness.GenerateTextAsync(request, CancellationToken.None);
        var text = await harness.GenerateTextAsync(RequestWithPromptMetadata("draft-paragraph", "minimal"), CancellationToken.None);

        using var json = JsonDocument.Parse(response);
        Assert.Equal("fake-query-context-v1", json.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("她把回答压回喉间。", text);
        Assert.Equal(2, harness.CallCount);
        Assert.Equal(1, harness.GetCallCount("parse-query-context", "happy-path"));
        Assert.Equal(1, harness.GetCallCount("draft-paragraph", "minimal"));
        Assert.Equal("parse-query-context", harness.Calls[0].PromptKey);
        Assert.Equal("happy-path", harness.Calls[0].Scenario);
        Assert.Same(request, harness.Calls[0].Request);
    }

    [Fact]
    public async Task StreamChatAsyncYieldsConfiguredResponseAsContentEvent()
    {
        var harness = new FakeLlmHarness()
            .RespondWithText("draft-paragraph", "固定正文");

        var events = new List<ChatCompletionStreamEvent>();
        await foreach (var streamEvent in harness.StreamChatAsync(
            RequestWithPromptMetadata("draft-paragraph", FakeLlmHarness.DefaultScenario),
            CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        var singleEvent = Assert.Single(events);
        Assert.Equal(ChatCompletionStreamEventKind.Content, singleEvent.Kind);
        Assert.Equal("固定正文", singleEvent.Data);
        Assert.Equal(1, harness.GetCallCount("draft-paragraph"));
    }

    private static ChatCompletionRequest RequestWithPromptMetadata(string promptKey, string scenario)
    {
        return new ChatCompletionRequest(
            ProviderName: "fake",
            ModelId: "fake-model",
            ReasoningEffort: "",
            Messages:
            [
                new ChatCompletionMessage("system", "Return strict JSON when requested."),
                new ChatCompletionMessage(
                    "user",
                    $$"""{"prompt_key":"{{promptKey}}","scenario":"{{scenario}}","goal":"测试"}""")
            ]);
    }
}
