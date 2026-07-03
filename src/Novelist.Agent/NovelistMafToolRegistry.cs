using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Agent;

public sealed class NovelistMafToolRegistry
{
    private static readonly JsonSerializerOptions DefaultSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly IStoryMemorySearchService _storyMemory;
    private readonly JsonSerializerOptions _serializerOptions;

    public NovelistMafToolRegistry(
        IStoryMemorySearchService storyMemory,
        JsonSerializerOptions? serializerOptions = null)
    {
        _storyMemory = storyMemory ?? throw new ArgumentNullException(nameof(storyMemory));
        _serializerOptions = EnsureTypeInfoResolver(serializerOptions ?? DefaultSerializerOptions);
    }

    public IReadOnlyList<AIFunction> CreateTools(NovelistMafToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.NovelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(context), context.NovelId, "Novel id must be positive.");
        }

        return
        [
            new StoryMemoryMafTool(_storyMemory, context.NovelId, _serializerOptions).CreateFunction()
        ];
    }

    private static JsonSerializerOptions EnsureTypeInfoResolver(JsonSerializerOptions options)
    {
        if (options.TypeInfoResolver is not null)
        {
            return options;
        }

        return new JsonSerializerOptions(options)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
    }

    private sealed class StoryMemoryMafTool
    {
        private const string ToolName = "search_story_memory";
        private const string ToolDescription = """
            语义检索小说记忆，在已索引的章节内容中查找与查询最相关的文本片段。

            支持的块类型（chunk_types 过滤）：
            - summary：章节摘要（AI 生成的高密度剧情总结）
            - chapter_brief：章节概要（标题 + 摘要 + 正文开头）
            - content：正文内容块（文本窗口）

            返回每个结果的来源章节、相关度分数和内容文本。相关度分数 0-1，越高越匹配。
            当需要查找特定情节、对话、场景或细节时使用此工具，而非逐个读取章节文件。
            """;

        private static readonly MethodInfo SearchMethod =
            typeof(StoryMemoryMafTool).GetMethod(
                nameof(SearchStoryMemoryAsync),
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(StoryMemoryMafTool).FullName,
                nameof(SearchStoryMemoryAsync));

        private readonly IStoryMemorySearchService _storyMemory;
        private readonly long _novelId;
        private readonly JsonSerializerOptions _serializerOptions;

        public StoryMemoryMafTool(
            IStoryMemorySearchService storyMemory,
            long novelId,
            JsonSerializerOptions serializerOptions)
        {
            _storyMemory = storyMemory;
            _novelId = novelId;
            _serializerOptions = serializerOptions;
        }

        public AIFunction CreateFunction()
        {
            return AIFunctionFactory.Create(
                SearchMethod,
                this,
                new AIFunctionFactoryOptions
                {
                    Name = ToolName,
                    Description = ToolDescription,
                    SerializerOptions = _serializerOptions
                });
        }

        [Description(ToolDescription)]
        private ValueTask<SearchStoryMemoryResultPayload> SearchStoryMemoryAsync(
            [Description("语义搜索查询。用自然语言描述你想找的内容")]
            string query,
            [Description("返回结果数量。默认 5，范围 1-20")]
            int top_k = 0,
            [Description("相关度阈值 0-1。默认 0.5")]
            double min_relevance = 0,
            [Description("限定章节号范围，空表示不限制")]
            int[]? chapter_numbers = null,
            [Description("限定块类型：summary / chapter_brief / content，空表示全部")]
            string[]? chunk_types = null,
            CancellationToken cancellationToken = default)
        {
            return _storyMemory.SearchAsync(
                new SearchStoryMemoryPayload(
                    _novelId,
                    query,
                    top_k,
                    min_relevance,
                    chapter_numbers ?? [],
                    chunk_types ?? []),
                cancellationToken);
        }
    }
}
