using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Agent;

public sealed partial class NovelistMafToolRegistry
{
    private partial void AddWebTools(List<AIFunction> tools)
    {
        if (_webSearch is not null)
        {
            tools.Add(new WebSearchMafTool(_webSearch, _serializerOptions).CreateFunction());
        }

        if (_webFetch is not null)
        {
            tools.Add(new WebFetchMafTool(_webFetch, _serializerOptions).CreateFunction());
        }
    }

    private sealed class WebSearchMafTool
    {
        private const string ToolName = "web_search";
        private const string ToolDescription = "联网搜索真实信息，返回综合答案和参考来源。适用于需要实时数据、新闻、技术文档或超出模型知识范围的内容。搜索结果已由 AI 综合分析，可直接引用返回的 summary；sources 为来源 URL 列表。如需查看某个来源的原文细节，可用 web_fetch 抓取。";

        private static readonly MethodInfo SearchMethod =
            typeof(WebSearchMafTool).GetMethod(
                nameof(SearchAsync),
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(WebSearchMafTool).FullName, nameof(SearchAsync));

        private readonly IWebSearchService _search;
        private readonly JsonSerializerOptions _serializerOptions;

        public WebSearchMafTool(IWebSearchService search, JsonSerializerOptions serializerOptions)
        {
            _search = search;
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
        private ValueTask<WebSearchResultPayload> SearchAsync(
            [Description("发给搜索 AI 的指令，用自然语言描述你的问题和背景，这不是传统的搜索关键词，是具体的搜索指令")]
            string prompt,
            CancellationToken cancellationToken = default)
        {
            return _search.SearchAsync(prompt, cancellationToken);
        }
    }

    private sealed class WebFetchMafTool
    {
        private const string ToolName = "web_fetch";
        private const string ToolDescription = "受 SSRF 防护地抓取指定网页正文内容，返回清洗后的 markdown 文本。只读取网页内容，不执行页面脚本，不打开外部 URL，不访问本地文件或文件选择器。适用于需要查看某个来源原文、深入了解细节或验证 web_search 结果时使用。一次只能抓取一个 URL。";

        private static readonly MethodInfo FetchMethod =
            typeof(WebFetchMafTool).GetMethod(
                nameof(FetchAsync),
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(WebFetchMafTool).FullName, nameof(FetchAsync));

        private readonly IWebFetchService _fetch;
        private readonly JsonSerializerOptions _serializerOptions;

        public WebFetchMafTool(IWebFetchService fetch, JsonSerializerOptions serializerOptions)
        {
            _fetch = fetch;
            _serializerOptions = serializerOptions;
        }

        public AIFunction CreateFunction()
        {
            return AIFunctionFactory.Create(
                FetchMethod,
                this,
                new AIFunctionFactoryOptions
                {
                    Name = ToolName,
                    Description = ToolDescription,
                    SerializerOptions = _serializerOptions
                });
        }

        [Description(ToolDescription)]
        private ValueTask<WebFetchResultPayload> FetchAsync(
            [Description("要抓取的网页 URL")]
            string url,
            CancellationToken cancellationToken = default)
        {
            return _fetch.FetchAsync(url, cancellationToken);
        }
    }
}
