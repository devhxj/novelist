using System.Runtime.CompilerServices;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceChapterMaterialExtractorTests
{
    [Fact]
    public async Task ExtractChapterMaterialsParsesVerbatimMaterialsAndDefaultsUnknownTypes()
    {
        var chat = new RecordingChatCompletionClient(
        [
            ReferenceChapterMaterialExtractorTests.ToolCall("""
                {"materials":[
                  {"excerpt":"他推门而入，屋里安静得能听见雨声。","material_type":"dialogue_exchange","tags":{"narrative_functions":["reveal","情绪张力"],"emotion_mechanics":[],"pov":[],"techniques":["subtext"],"scene_beat_roles":[],"character_relations":[],"causal_information_roles":[]},"scores":{"semantic_completeness":0.9,"information_density":0.7,"narrative_value":0.8,"transferability":0.6,"context_independence":0.7,"technique_distinctiveness":0.6},"confidence":0.9,"reason_codes":["complete_exchange","自造原因"]},
                  {"excerpt":"第二段摘录材料。","material_type":"未知类型","tags":{"narrative_functions":[],"emotion_mechanics":[],"pov":[],"techniques":[],"scene_beat_roles":[],"character_relations":[],"causal_information_roles":[]},"scores":{"semantic_completeness":0.5,"information_density":0.5,"narrative_value":0.5,"transferability":0.5,"context_independence":0.5,"technique_distinctiveness":0.5},"confidence":0.4,"reason_codes":[]}
                ]}
                """)
        ]);
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);

        var result = await extractor.ExtractChapterMaterialsAsync(
            new ReferenceChapterExtractionRequest(
                1,
                1,
                "第一章",
                "他推门而入，屋里安静得能听见雨声。",
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
            CancellationToken.None);

        Assert.Equal(2, result.Materials.Count);
        Assert.Equal("dialogue_exchange", result.Materials[0].MaterialType);
        Assert.Equal("他推门而入，屋里安静得能听见雨声。", result.Materials[0].Excerpt);
        Assert.Equal(["reveal"], result.Materials[0].Tags.NarrativeFunctions);
        Assert.Equal(["subtext"], result.Materials[0].Tags.Techniques);
        Assert.Equal(["complete_exchange"], result.Materials[0].ReasonCodes);
        // 未知材料类型回退为 passage；未知标签/原因值被丢弃。
        Assert.Equal("passage", result.Materials[1].MaterialType);
        Assert.Empty(result.Materials[1].Tags.NarrativeFunctions);
        Assert.Empty(result.Materials[1].ReasonCodes);
    }

    private static ChatCompletionStreamEvent ToolCall(string argumentsJson) => new(
        ChatCompletionStreamEventKind.ToolCall,
        ToolCall: new ChatToolCall(
            "call-extraction",
            "submit_chapter_materials",
            argumentsJson));

    private sealed class RecordingChatCompletionClient(IReadOnlyList<ChatCompletionStreamEvent> events) : IChatCompletionClient
    {
        public ChatCompletionRequest? LastRequest { get; private set; }

        public ValueTask<string> GenerateTextAsync(ChatCompletionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastRequest = request;
            await Task.CompletedTask;
            foreach (var item in events)
            {
                yield return item;
            }
        }
    }
}
