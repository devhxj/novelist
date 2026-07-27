using System.Runtime.CompilerServices;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceChapterMaterialChatCompletionExtractorTests
{
    [Fact]
    public void SchemaVersionIdentifiesTheFinalArchiveLineRangeContract()
    {
        Assert.Equal("reference-chapter-materials-v4", ReferenceChapterMaterialChatCompletionExtractor.SchemaVersion);
    }

    [Fact]
    public async Task ExtractAsyncSendsTheWholeChapterAndReturnsCrossParagraphSourceMaterial()
    {
        const string chapter = "雨声压住窗沿。\n\n“你还是来了？”\n\n“我答应过你。”\n\n她把门彻底推开。";
        var chat = new RecordingChatCompletionClient(
        [
            ToolCall(
                """
                {"materials":[{"source_kind":"对话","start_line":3,"end_line":5,"entities":[{"name":"她","kind":"人物"}],"setting":{"location":"门前","time":null,"environment":"雨声压住窗沿"},"perspective":{"mode":"限知","focus_entity":"她"},"event":"她以承诺回应来访并推开门。","facts":[{"content":"她曾作出承诺。","subject":"她"}],"causality":{"cause":"先前承诺","consequence":"她开门接纳来访者"},"state_changes":[{"subject":"二人关系","before":"试探","after":"接纳"}],"character_dynamics":"二人从试探转向接纳。","conflict":{"pressure":"来访的信任尚待确认。","cost":null},"information":{"role":"已确立","content":"她履行承诺。"},"emotion":{"tone":"克制","subtext":"接纳仍带保留。"},"narrative_functions":["关系转变","压力积累"],"foreshadowing":[{"phase":"埋设","target":"来访信任的后续考验"}],"motifs":["雨声","门"],"expression_techniques":["对白留白","环境烘托"],"reuse_hint":"用简短应答兑现先前承诺。"}]}
                """)
        ]);
        var extractor = new ReferenceChapterMaterialChatCompletionExtractor(chat);

        var result = await extractor.ExtractAsync(
            new ReferenceChapterMaterialExtractionRequest(
                new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                AnchorId: 17,
                ChapterIndex: 3,
                ChapterTitle: "第三章 赴约",
                ChapterText: chapter),
            CancellationToken.None);

        var material = Assert.Single(result.Materials);
        Assert.Equal("对话", material.Metadata.SourceKind);
        Assert.Equal("“你还是来了？”\n\n“我答应过你。”", material.Text);
        Assert.Equal("她", Assert.Single(material.Metadata.Entities).Name);
        Assert.Equal("人物", material.Metadata.Entities[0].Kind);
        Assert.Equal("门前", material.Metadata.Setting?.Location);
        Assert.Equal(3, material.Metadata.SourceSpan.StartLine);
        Assert.Equal("限知", material.Metadata.Perspective?.Mode);
        Assert.Equal("克制", material.Metadata.Emotion?.Tone);
        Assert.Equal("她曾作出承诺。", Assert.Single(material.Metadata.Facts).Content);
        Assert.Equal(["关系转变", "压力积累"], material.Metadata.NarrativeFunctions);
        Assert.Equal("用简短应答兑现先前承诺。", material.Metadata.ReuseHint);

        Assert.Equal("qwen", chat.LastRequest?.ProviderName);
        Assert.Equal("qwen-plus", chat.LastRequest?.ModelId);
        Assert.Equal("high", chat.LastRequest?.ReasoningEffort);
        Assert.Equal(0, chat.LastRequest?.TemperatureOverride);
        Assert.True(chat.LastRequest?.RequireToolCall);
        using var requestJson = JsonDocument.Parse(chat.LastRequest!.Messages[1].Content);
        Assert.Equal("第三章 赴约", requestJson.RootElement.GetProperty("chapter_title").GetString());
        var chapterLines = requestJson.RootElement.GetProperty("chapter_lines");
        Assert.Equal(7, chapterLines.GetArrayLength());
        Assert.Equal(3, chapterLines[2].GetProperty("line_number").GetInt32());
        Assert.Equal("“你还是来了？”", chapterLines[2].GetProperty("text").GetString());
        var tool = Assert.Single(chat.LastRequest!.Tools!);
        Assert.Equal("submit_reference_chapter_materials", tool.Name);
        Assert.True(tool.Strict);
        var materialSchema = tool.ParametersSchema
            .GetProperty("properties")
            .GetProperty("materials")
            .GetProperty("items")
            .GetProperty("properties");
        Assert.True(materialSchema.TryGetProperty("start_line", out _));
        Assert.True(materialSchema.TryGetProperty("end_line", out _));
        Assert.True(materialSchema.TryGetProperty("source_kind", out _));
        Assert.True(materialSchema.TryGetProperty("perspective", out _));
        Assert.True(materialSchema.TryGetProperty("facts", out _));
        Assert.True(materialSchema.TryGetProperty("foreshadowing", out _));
        Assert.True(materialSchema.TryGetProperty("narrative_functions", out _));
        Assert.Equal(
            3,
            materialSchema
                .GetProperty("setting")
                .GetProperty("anyOf")[1]
                .GetProperty("anyOf")
                .GetArrayLength());
        Assert.False(materialSchema.TryGetProperty("text", out _));
        Assert.False(materialSchema.TryGetProperty("material_type", out _));
    }

    [Fact]
    public async Task ExtractAsyncDoesNotTruncateOrWindowLongChapters()
    {
        var chapter = "第一段。\n\n" + new string('甲', 200_000) + "\n\n结尾证据。";
        var chat = new RecordingChatCompletionClient(
        [
            ToolCall(
                """
                {"materials":[{"source_kind":"叙述","start_line":5,"end_line":5,"entities":[],"setting":null,"perspective":null,"event":"章节以证据收束。","facts":[],"causality":null,"state_changes":[],"character_dynamics":null,"conflict":null,"information":{"role":"回收","content":null},"emotion":{"tone":"庄重","subtext":null},"narrative_functions":["收束"],"foreshadowing":[],"motifs":[],"expression_techniques":[],"reuse_hint":"用于章节收束。"}]}
                """)
        ]);
        var extractor = new ReferenceChapterMaterialChatCompletionExtractor(chat);

        var result = await extractor.ExtractAsync(
            new ReferenceChapterMaterialExtractionRequest(
                new ReferenceMaterializationLlmSelection("openai", "gpt-test", "medium"),
                AnchorId: 4,
                ChapterIndex: 1,
                ChapterTitle: "第一章",
                ChapterText: chapter),
            CancellationToken.None);

        Assert.Equal("结尾证据。", Assert.Single(result.Materials).Text);
        using var requestJson = JsonDocument.Parse(chat.LastRequest!.Messages[1].Content);
        var chapterLines = requestJson.RootElement.GetProperty("chapter_lines");
        Assert.Equal(5, chapterLines.GetArrayLength());
        Assert.Equal(new string('甲', 200_000), chapterLines[2].GetProperty("text").GetString());
    }

    [Fact]
    public void MetadataValidatorNamesAnEmptySettingObject()
    {
        var metadata = new ReferenceMaterialMetadata(
            new ReferenceMaterialSourceSpan(1, 1), "叙述", [], new ReferenceMaterialSetting(null, null, null),
            null, null, [], null, [], null, null, null, null, [], [], [], [], "用于验证章节事实。");

        Assert.False(ReferenceMaterialMetadataValidator.TryValidate(metadata, out var error));
        Assert.Equal("setting must be null or contain at least one non-empty location, time, or environment.", error);
    }

    [Fact]
    public void MetadataValidatorLocatesAnUnsupportedNarrativeFunctionAndShowsItsValue()
    {
        var metadata = new ReferenceMaterialMetadata(
            new ReferenceMaterialSourceSpan(1, 1), "叙述", [], null,
            null, null, [], null, [], null, null, null, null, ["人物塑造", "未知功能"], [], [], [], "用于验证章节事实。");

        Assert.False(ReferenceMaterialMetadataValidator.TryValidate(metadata, out var error));
        Assert.Equal(
            "narrative_functions[1] has unsupported value \"未知功能\"; allowed values are: 人物塑造、关系转变、冲突升级、压力积累、信息揭示、伏笔、误导、转折、悬念、世界观构建、场景铺陈、情绪释放、钩子、收束、主题呼应、节奏调整、因果铺垫、状态确认、视角校准、线索回收.",
            error);
    }

    [Fact]
    public void MetadataValidatorLocatesADuplicateNarrativeFunction()
    {
        var metadata = new ReferenceMaterialMetadata(
            new ReferenceMaterialSourceSpan(1, 1), "叙述", [], null,
            null, null, [], null, [], null, null, null, null, ["人物塑造", "人物塑造"], [], [], [], "用于验证章节事实。");

        Assert.False(ReferenceMaterialMetadataValidator.TryValidate(metadata, out var error));
        Assert.Equal("narrative_functions[1] duplicates narrative_functions[0].", error);
    }

    [Fact]
    public async Task ExtractAsyncNamesTheMaterialAndLineRangeForARequiredCollectionFailure()
    {
        var extractor = new ReferenceChapterMaterialChatCompletionExtractor(
            new RecordingChatCompletionClient(
            [
                ToolCall("""
                    {"materials":[{"source_kind":"叙述","start_line":1,"end_line":1,"entities":[],"setting":null,"perspective":null,"event":null,"facts":null,"causality":null,"state_changes":[],"character_dynamics":null,"conflict":null,"information":null,"emotion":null,"narrative_functions":[],"foreshadowing":[],"motifs":[],"expression_techniques":[],"reuse_hint":"用于建立章节事实。"}]}
                    """)
            ]));

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await extractor.ExtractAsync(
                new ReferenceChapterMaterialExtractionRequest(
                    new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                    AnchorId: 9,
                    ChapterIndex: 1,
                    ChapterTitle: "第一章",
                    ChapterText: "原文证据。"),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.LlmOutputInvalid, exception.ErrorCode);
        Assert.Equal(
            "Chapter material #1 (lines 1-1) is invalid: facts must be an array.",
            exception.Message);
    }

    [Theory]
    [InlineData("{\"materials\":[]}", ReferenceMaterializationErrorCodes.NoMaterials)]
    [InlineData("{\"materials\":[{\"material_type\":\"dialogue\",\"start_line\":1,\"end_line\":1,\"description\":\"旧契约。\",\"tags\":[\"dialogue\"]}]}", ReferenceMaterializationErrorCodes.LlmOutputInvalid)]
    [InlineData("{\"materials\":[{\"source_kind\":\"无效\",\"start_line\":1,\"end_line\":1,\"entities\":[],\"setting\":null,\"event\":null,\"character_dynamics\":null,\"conflict\":null,\"emotional_tone\":null,\"narrative_functions\":[],\"information_role\":null,\"reuse_hint\":null}]}", ReferenceMaterializationErrorCodes.LlmOutputInvalid)]
    [InlineData("{\"materials\":[{\"source_kind\":\"对话\",\"start_line\":0,\"end_line\":1,\"entities\":[],\"setting\":null,\"event\":null,\"character_dynamics\":null,\"conflict\":null,\"emotional_tone\":null,\"narrative_functions\":[],\"information_role\":null,\"reuse_hint\":null}]}", ReferenceMaterializationErrorCodes.LlmOutputInvalid)]
    [InlineData("{\"materials\":[{\"source_kind\":\"对话\",\"start_line\":1,\"end_line\":2,\"entities\":[],\"setting\":null,\"event\":null,\"character_dynamics\":null,\"conflict\":null,\"emotional_tone\":null,\"narrative_functions\":[],\"information_role\":null,\"reuse_hint\":null}]}", ReferenceMaterializationErrorCodes.LlmOutputInvalid)]
    [InlineData("{\"materials\":[{\"source_kind\":\"对话\",\"start_line\":2,\"end_line\":3,\"entities\":[],\"setting\":null,\"event\":null,\"character_dynamics\":null,\"conflict\":null,\"emotional_tone\":null,\"narrative_functions\":[],\"information_role\":null,\"reuse_hint\":null}]}", ReferenceMaterializationErrorCodes.LlmOutputInvalid)]
    public async Task ExtractAsyncRejectsTheWholeResultWhenAnyMaterialIsInvalid(
        string response,
        string expectedErrorCode)
    {
        var extractor = new ReferenceChapterMaterialChatCompletionExtractor(
            new RecordingChatCompletionClient([ToolCall(response)]));

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await extractor.ExtractAsync(
                new ReferenceChapterMaterialExtractionRequest(
                    new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                    AnchorId: 9,
                    ChapterIndex: 1,
                    ChapterTitle: "第一章",
                    ChapterText: "原文证据。\n\n结尾证据。"),
                CancellationToken.None));

        Assert.Equal(expectedErrorCode, exception.ErrorCode);
    }

    [Fact]
    public async Task ExtractAsyncRequiresTheStrictToolCall()
    {
        var extractor = new ReferenceChapterMaterialChatCompletionExtractor(
            new RecordingChatCompletionClient(
            [
                new ChatCompletionStreamEvent(
                    ChatCompletionStreamEventKind.Content,
                    "{\"materials\":[]}")
            ]));

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await extractor.ExtractAsync(
                new ReferenceChapterMaterialExtractionRequest(
                    new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                    AnchorId: 9,
                    ChapterIndex: 1,
                    ChapterTitle: "第一章",
                    ChapterText: "原文证据。"),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.LlmOutputInvalid, exception.ErrorCode);
    }

    [Fact]
    public async Task ExtractAsyncExplainsWhyTheRequiredToolCallIsMissingWithoutEchoingOutput()
    {
        const string sensitiveOutput = "private model output";
        var extractor = new ReferenceChapterMaterialChatCompletionExtractor(
            new RecordingChatCompletionClient(
            [
                new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Thinking, "reasoning"),
                new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, sensitiveOutput),
                new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Finish, "length")
            ]));

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await extractor.ExtractAsync(
                new ReferenceChapterMaterialExtractionRequest(
                    new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high"),
                    AnchorId: 9,
                    ChapterIndex: 4,
                    ChapterTitle: "第四章",
                    ChapterText: "原文证据。"),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.LlmOutputInvalid, exception.ErrorCode);
        Assert.Contains("finish_reason=length", exception.Message, StringComparison.Ordinal);
        Assert.Contains("thinking_chars=9", exception.Message, StringComparison.Ordinal);
        Assert.Contains("content_chars=20", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveOutput, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsyncReportsSafeJsonPathWithoutEchoingToolArguments()
    {
        const string sensitiveValue = "private chapter fragment";
        var extractor = new ReferenceChapterMaterialChatCompletionExtractor(
            new RecordingChatCompletionClient(
            [
                ToolCall($$"""
                    {"materials":[{"source_kind":"叙述","start_line":1,"end_line":1,"entities":[],"setting":null,"perspective":null,"event":"章节事实。","facts":[],"causality":null,"state_changes":[],"character_dynamics":null,"conflict":null,"information":{"role":"已确立","content":null},"emotion":null,"narrative_functions":[],"foreshadowing":[],"motifs":[],"expression_techniques":[],"reuse_hint":"用于建立章节事实。","raw_text":"{{sensitiveValue}}"}]}
                    """)
            ]));

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await extractor.ExtractAsync(
                new ReferenceChapterMaterialExtractionRequest(
                    new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high"),
                    AnchorId: 9,
                    ChapterIndex: 1,
                    ChapterTitle: "第一章",
                    ChapterText: "原文证据。"),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.LlmOutputInvalid, exception.ErrorCode);
        Assert.Contains("$.materials[0].raw_text", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveValue, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsyncIgnoresExplanatoryTextWhenTheRequiredToolCallIsPresent()
    {
        const string chapter = "原文证据。";
        var extractor = new ReferenceChapterMaterialChatCompletionExtractor(
            new RecordingChatCompletionClient(
            [
                new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "I found one reusable material."),
                ToolCall("""
                    {"materials":[{"source_kind":"叙述","start_line":1,"end_line":1,"entities":[],"setting":null,"perspective":null,"event":"章节事实。","facts":[],"causality":null,"state_changes":[],"character_dynamics":null,"conflict":null,"information":{"role":"已确立","content":null},"emotion":null,"narrative_functions":[],"foreshadowing":[],"motifs":[],"expression_techniques":[],"reuse_hint":"用于建立章节事实。"}]}
                    """)
            ]));

        var result = await extractor.ExtractAsync(
            new ReferenceChapterMaterialExtractionRequest(
                new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                AnchorId: 9,
                ChapterIndex: 1,
                ChapterTitle: "第一章",
                ChapterText: chapter),
            CancellationToken.None);

        Assert.Equal("原文证据。", Assert.Single(result.Materials).Text);
    }

    private static ChatCompletionStreamEvent ToolCall(string argumentsJson) =>
        new(
            ChatCompletionStreamEventKind.ToolCall,
            ToolCall: new ChatToolCall(
                "call-materials",
                "submit_reference_chapter_materials",
                argumentsJson));

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
