using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Agent;

public sealed class NovelistMafToolRegistry
{
    private static readonly JsonSerializerOptions DefaultSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly IStoryMemorySearchService _storyMemory;
    private readonly IChapterContentService? _chapterContent;
    private readonly IApprovalCoordinator? _approvals;
    private readonly IBridgeEventSink _events;
    private readonly JsonSerializerOptions _serializerOptions;

    public NovelistMafToolRegistry(
        IStoryMemorySearchService storyMemory,
        JsonSerializerOptions? serializerOptions = null)
        : this(
            storyMemory,
            chapterContent: null,
            approvals: null,
            events: null,
            serializerOptions)
    {
    }

    public NovelistMafToolRegistry(
        IStoryMemorySearchService storyMemory,
        IChapterContentService? chapterContent,
        IApprovalCoordinator? approvals,
        IBridgeEventSink? events,
        JsonSerializerOptions? serializerOptions = null)
    {
        _storyMemory = storyMemory ?? throw new ArgumentNullException(nameof(storyMemory));
        _chapterContent = chapterContent;
        _approvals = approvals;
        _events = events ?? new NullBridgeEventSink();
        _serializerOptions = EnsureTypeInfoResolver(serializerOptions ?? DefaultSerializerOptions);
    }

    public IReadOnlyList<AIFunction> CreateTools(NovelistMafToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.NovelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(context), context.NovelId, "Novel id must be positive.");
        }

        var tools = new List<AIFunction>
        {
            new StoryMemoryMafTool(_storyMemory, context.NovelId, _serializerOptions).CreateFunction()
        };

        if (_chapterContent is not null)
        {
            tools.Add(new ReadMafTool(_chapterContent, context.NovelId, _serializerOptions).CreateFunction());
            if (_approvals is not null)
            {
                tools.Add(new EditMafTool(
                    _chapterContent,
                    _approvals,
                    _events,
                    context,
                    _serializerOptions).CreateFunction());
            }
        }

        return tools;
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

    private sealed class ReadMafTool
    {
        private const string ToolName = "read";
        private const string ToolDescription = """
            读取小说文件或技能文件。

            路径格式：
            - chapters/001.md ~ chapters/999999.md（章节文件）
            - outlines/001.md ~ outlines/999999.md（章节大纲）
            - goink.md（故事状态文档）
            - skills/<name>.md（小说级技能）
            - ~/.goink/skills/<name>.md（用户级技能）
            - /builtin/skills/<name>.md（内置技能，只读）

            默认添加行号前缀（123|），方便后续 edit 工具进行 line_range_replace 和 search_replace。
            start_line 和 end_line 支持行范围读取：默认读前 2000 行，可通过调整参数翻页或精确引用。
            include_lines=false 返回纯文本。
            """;

        private static readonly MethodInfo ReadMethod =
            typeof(ReadMafTool).GetMethod(
                nameof(ReadFileAsync),
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(ReadMafTool).FullName,
                nameof(ReadFileAsync));

        private readonly IChapterContentService _chapterContent;
        private readonly long _novelId;
        private readonly JsonSerializerOptions _serializerOptions;

        public ReadMafTool(
            IChapterContentService chapterContent,
            long novelId,
            JsonSerializerOptions serializerOptions)
        {
            _chapterContent = chapterContent;
            _novelId = novelId;
            _serializerOptions = serializerOptions;
        }

        public AIFunction CreateFunction()
        {
            return AIFunctionFactory.Create(
                ReadMethod,
                this,
                new AIFunctionFactoryOptions
                {
                    Name = ToolName,
                    Description = ToolDescription,
                    SerializerOptions = _serializerOptions
                });
        }

        [Description(ToolDescription)]
        private async ValueTask<ReadFileToolResult> ReadFileAsync(
            [Description("要读取的文件路径。章节文件格式为 chapters/001.md，大纲为 outlines/001.md，故事状态为 goink.md")]
            string path,
            [Description("是否包含行号前缀（如 123|）。默认 true")]
            bool include_lines = true,
            [Description("起始行号，1-based，默认 1")]
            int start_line = 0,
            [Description("结束行号，1-based，默认 2000；设为 0 读取前 2000 行")]
            int end_line = 0,
            CancellationToken cancellationToken = default)
        {
            var normalizedPath = NormalizeToolPath(path);
            var content = await _chapterContent.GetContentAsync(_novelId, normalizedPath, cancellationToken);
            var start = start_line <= 0 ? 1 : start_line;
            var end = end_line <= 0 ? 2000 : end_line;

            var lines = SplitLines(content);
            var totalLines = lines.Length;
            if (start > totalLines)
            {
                throw new InvalidOperationException($"起始行 {start} 超出文件总行数 {totalLines}");
            }

            if (end > totalLines)
            {
                end = totalLines;
            }

            if (end < start)
            {
                throw new InvalidOperationException($"结束行 {end} 不能小于起始行 {start}");
            }

            var selected = lines.Skip(start - 1).Take(end - start + 1).ToArray();
            var output = include_lines
                ? FormatLineNumberedContent(selected, start)
                : string.Join("\n", selected);

            return new ReadFileToolResult(
                normalizedPath,
                DisplayNameForPath(normalizedPath),
                output,
                totalLines,
                start,
                end,
                end < totalLines);
        }
    }

    private sealed class EditMafTool
    {
        private const string ToolName = "edit";
        private const string ToolDescription = """
            编辑小说文件（章节正文、章节大纲、故事状态 goink.md 或技能文件）。支持三种编辑模式：

            1. full_replace：全文替换整个文件，new_content 为完整替换后内容。
            2. search_replace：查找并替换指定文本。search_text 必须从文件中精确复制，replace_all=false 默认仅替换第一个匹配项。
            3. line_range_replace：替换指定行范围。start_line 和 end_line 为 1-based 行号（含两端），new_content 为插入的新内容。

            路径格式：
            - chapters/001.md ~ chapters/999999.md
            - outlines/001.md ~ outlines/999999.md
            - goink.md
            - skills/<name>.md
            - ~/.goink/skills/<name>.md

            所有修改都会先提交用户审批，审批通过后才写入文件；被拒绝时会返回用户反馈，可根据反馈修正后重试。
            """;

        private static readonly MethodInfo EditMethod =
            typeof(EditMafTool).GetMethod(
                nameof(EditFileAsync),
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(EditMafTool).FullName,
                nameof(EditFileAsync));

        private readonly IChapterContentService _chapterContent;
        private readonly IApprovalCoordinator _approvals;
        private readonly IBridgeEventSink _events;
        private readonly NovelistMafToolContext _context;
        private readonly JsonSerializerOptions _serializerOptions;

        public EditMafTool(
            IChapterContentService chapterContent,
            IApprovalCoordinator approvals,
            IBridgeEventSink events,
            NovelistMafToolContext context,
            JsonSerializerOptions serializerOptions)
        {
            _chapterContent = chapterContent;
            _approvals = approvals;
            _events = events;
            _context = context;
            _serializerOptions = serializerOptions;
        }

        public AIFunction CreateFunction()
        {
            return AIFunctionFactory.Create(
                EditMethod,
                this,
                new AIFunctionFactoryOptions
                {
                    Name = ToolName,
                    Description = ToolDescription,
                    SerializerOptions = _serializerOptions
                });
        }

        [Description(ToolDescription)]
        private async ValueTask<EditFileToolResult> EditFileAsync(
            [Description("要编辑的文件路径。章节文件格式为 chapters/001.md，大纲为 outlines/001.md，故事状态为 goink.md")]
            string path,
            [Description("编辑方式：full_replace / search_replace / line_range_replace")]
            string change_type,
            [Description("search_replace 时要查找的原文片段。请从文件中精确复制")]
            string search_text = "",
            [Description("新内容。full_replace 时为完整全文；search_replace 时为替换文本；line_range_replace 时为插入的新行")]
            string new_content = "",
            [Description("search_replace 是否替换所有匹配项。默认 false")]
            bool replace_all = false,
            [Description("line_range_replace 起始行号，1-based，含此行")]
            int start_line = 0,
            [Description("line_range_replace 结束行号，1-based，含此行")]
            int end_line = 0,
            [Description("修改原因，供人类审阅")]
            string reason = "",
            [Description("章节标题。对已有章节传入非空 title 将覆盖原标题")]
            string title = "",
            CancellationToken cancellationToken = default)
        {
            var normalizedPath = NormalizeToolPath(path);
            if (IsBuiltinSkillPath(normalizedPath))
            {
                throw new InvalidOperationException("内置 skill 为只读，不可编辑");
            }

            var changeType = NormalizeChangeType(change_type);
            var current = await _chapterContent.GetContentAsync(_context.NovelId, normalizedPath, cancellationToken);
            var proposed = ApplyChange(
                changeType,
                current,
                search_text ?? string.Empty,
                new_content ?? string.Empty,
                replace_all,
                start_line,
                end_line);

            if (string.Equals(proposed, current, StringComparison.Ordinal))
            {
                return new EditFileToolResult(
                    normalizedPath,
                    changeType,
                    Approved: true,
                    Message: "内容未变化，跳过",
                    Before: null,
                    After: null,
                    Feedback: null);
            }

            EnsureApprovalContext();
            var approvalPayload = JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>
                {
                    ["original"] = current,
                    ["modified"] = proposed,
                    ["path"] = normalizedPath,
                    ["change_type"] = changeType,
                    ["reason"] = reason ?? string.Empty
                },
                BridgeJson.SerializerOptions);

            var approval = await _approvals.RequestApprovalAsync(
                new ToolApprovalRequestPayload(
                    _context.SessionId,
                    _context.TurnId,
                    _context.ToolId,
                    ToolName,
                    ApprovalType: "file_edit",
                    Payload: approvalPayload,
                    DisplayText: $"等待确认写入{DisplayNameForPath(normalizedPath)}",
                    ActivityKind: "file_edit"),
                cancellationToken);

            if (!approval.Approved)
            {
                var error = "审批未通过";
                if (!string.IsNullOrWhiteSpace(approval.Feedback))
                {
                    error += $"。用户反馈：{approval.Feedback.Trim()}";
                }

                throw new InvalidOperationException(error);
            }

            var fresh = await _chapterContent.GetContentAsync(_context.NovelId, normalizedPath, cancellationToken);
            if (!string.Equals(fresh, current, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("文件已被修改，请重新读取最新内容后重试");
            }

            await _chapterContent.SaveContentAsync(
                new SaveContentPayload(_context.NovelId, normalizedPath, proposed),
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(title) && IsChapterPath(normalizedPath))
            {
                await TryUpdateChapterTitleAsync(normalizedPath, title, cancellationToken);
            }

            await _events.EmitAsync(
                "file:changed",
                new { novel_id = _context.NovelId, path = normalizedPath },
                cancellationToken);

            return new EditFileToolResult(
                normalizedPath,
                changeType,
                Approved: true,
                Message: "修改已写入",
                Before: changeType == "line_range_replace"
                    ? LinePreview(current, start_line, end_line)
                    : null,
                After: changeType == "line_range_replace"
                    ? LinePreview(proposed, start_line, start_line + CountNewLines(new_content ?? string.Empty))
                    : null,
                Feedback: string.IsNullOrWhiteSpace(approval.Feedback) ? null : approval.Feedback.Trim());
        }

        private void EnsureApprovalContext()
        {
            if (string.IsNullOrWhiteSpace(_context.SessionId) ||
                _context.TurnId <= 0 ||
                string.IsNullOrWhiteSpace(_context.ToolId))
            {
                throw new InvalidOperationException("文件编辑审批缺少会话上下文。");
            }
        }

        private async ValueTask TryUpdateChapterTitleAsync(
            string path,
            string title,
            CancellationToken cancellationToken)
        {
            var chapterNumber = ParseNumberedPath(path, "chapters/");
            if (chapterNumber is null)
            {
                return;
            }

            try
            {
                await _chapterContent.UpdateChapterTitleAsync(
                    _context.NovelId,
                    chapterNumber.Value,
                    title,
                    cancellationToken);
            }
            catch (ArgumentException)
            {
                // The edit itself is durable through SaveContentAsync; absent chapter metadata should not undo it.
            }
        }
    }

    private sealed record ReadFileToolResult(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("display")] string Display,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("total_lines")] int TotalLines,
        [property: JsonPropertyName("start_line")] int StartLine,
        [property: JsonPropertyName("end_line")] int EndLine,
        [property: JsonPropertyName("truncated")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool Truncated);

    private sealed record EditFileToolResult(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("change_type")] string ChangeType,
        [property: JsonPropertyName("approved")] bool Approved,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("before")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Before,
        [property: JsonPropertyName("after")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? After,
        [property: JsonPropertyName("feedback")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Feedback);

    private static string NormalizeToolPath(string? path)
    {
        var normalized = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("文件路径不能为空。", nameof(path));
        }

        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException("文件路径不能包含控制字符。", nameof(path));
        }

        return normalized;
    }

    private static string NormalizeChangeType(string? changeType)
    {
        var normalized = (changeType ?? string.Empty).Trim();
        return normalized switch
        {
            "full_replace" or "search_replace" or "line_range_replace" => normalized,
            _ => throw new ArgumentException($"未知的 change_type: {changeType}", nameof(changeType))
        };
    }

    private static string ApplyChange(
        string changeType,
        string current,
        string searchText,
        string newContent,
        bool replaceAll,
        int startLine,
        int endLine)
    {
        return changeType switch
        {
            "full_replace" => newContent,
            "search_replace" => ApplySearchReplace(current, searchText, newContent, replaceAll),
            "line_range_replace" => LineRangeReplace(current, startLine, endLine, newContent),
            _ => throw new ArgumentException($"未知的 change_type: {changeType}", nameof(changeType))
        };
    }

    private static string ApplySearchReplace(
        string content,
        string searchText,
        string newContent,
        bool replaceAll)
    {
        if (string.IsNullOrEmpty(searchText))
        {
            throw new InvalidOperationException("search_replace 模式需要提供 search_text");
        }

        var normalizedSearch = searchText.TrimEnd('\n');
        var (result, found, hint) = SearchReplace(content, normalizedSearch, newContent, replaceAll);
        if (found)
        {
            return result;
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(hint) ? "未找到匹配文本，请用精确文本重试" : hint);
    }

    private static string LineRangeReplace(
        string content,
        int startLine,
        int endLine,
        string newContent)
    {
        if (startLine <= 0 || endLine <= 0)
        {
            throw new InvalidOperationException("line_range_replace 模式需要提供 start_line 和 end_line");
        }

        if (startLine > endLine)
        {
            throw new InvalidOperationException("start_line 不能大于 end_line");
        }

        var lines = SplitLines(content);
        if (startLine < 1 || endLine > lines.Length)
        {
            throw new InvalidOperationException(
                $"行号超出范围: start={startLine} end={endLine} 总行数={lines.Length}");
        }

        var result = new List<string>();
        result.AddRange(lines.Take(startLine - 1));
        if (!string.IsNullOrEmpty(newContent))
        {
            result.AddRange(SplitLines(newContent));
        }

        result.AddRange(lines.Skip(endLine));
        return string.Join("\n", result);
    }

    private static (string Result, bool Found, string Hint) SearchReplace(
        string content,
        string searchText,
        string newContent,
        bool replaceAll)
    {
        if (content.Contains(searchText, StringComparison.Ordinal))
        {
            return (Replace(content, searchText, newContent, replaceAll), true, string.Empty);
        }

        var trimmedSearch = searchText.Trim();
        if (!string.Equals(trimmedSearch, searchText, StringComparison.Ordinal) &&
            content.Contains(trimmedSearch, StringComparison.Ordinal))
        {
            return (Replace(content, trimmedSearch, newContent, replaceAll), true, string.Empty);
        }

        var normalizedSearch = NormalizePunctuation(searchText);
        var normalizedContent = NormalizePunctuation(content);
        if (!string.Equals(normalizedSearch, searchText, StringComparison.Ordinal) ||
            !string.Equals(normalizedContent, content, StringComparison.Ordinal))
        {
            var contentRunes = normalizedContent.EnumerateRunes().ToArray();
            var searchRunes = normalizedSearch.EnumerateRunes().ToArray();
            var position = RuneIndex(contentRunes, searchRunes);
            if (position >= 0)
            {
                var originalRunes = content.EnumerateRunes().ToArray();
                var original = string.Concat(originalRunes.Skip(position).Take(searchRunes.Length));
                return (Replace(content, original, newContent, replaceAll), true, string.Empty);
            }
        }

        return (string.Empty, false, FuzzyHint(searchText, content));
    }

    private static string Replace(string content, string searchText, string newContent, bool replaceAll)
    {
        return replaceAll
            ? content.Replace(searchText, newContent, StringComparison.Ordinal)
            : ReplaceFirst(content, searchText, newContent);
    }

    private static string ReplaceFirst(string content, string searchText, string newContent)
    {
        var index = content.IndexOf(searchText, StringComparison.Ordinal);
        return index < 0
            ? content
            : string.Concat(content.AsSpan(0, index), newContent, content.AsSpan(index + searchText.Length));
    }

    private static string NormalizePunctuation(string value)
    {
        return value
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('「', '"')
            .Replace('」', '"')
            .Replace('『', '"')
            .Replace('』', '"')
            .Replace('＂', '"')
            .Replace('‘', '\'')
            .Replace('’', '\'')
            .Replace('＇', '\'');
    }

    private static int RuneIndex(ReadOnlySpan<System.Text.Rune> source, ReadOnlySpan<System.Text.Rune> target)
    {
        if (target.Length == 0 || source.Length < target.Length)
        {
            return -1;
        }

        for (var i = 0; i <= source.Length - target.Length; i++)
        {
            var match = true;
            for (var j = 0; j < target.Length; j++)
            {
                if (source[i + j] != target[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static string FuzzyHint(string searchText, string content)
    {
        var searchLines = SplitLines(searchText.Trim());
        var contentLines = SplitLines(content);
        if (searchLines.Length == 0 || contentLines.Length == 0)
        {
            return string.Empty;
        }

        var width = searchLines.Length;
        var bestScore = 0.0;
        var bestStart = 0;
        var bestWidth = width;
        ScoreWindows(width);
        foreach (var delta in new[] { 2, -2, 1, -1 })
        {
            var candidateWidth = width + delta;
            if (candidateWidth > 0 && candidateWidth <= contentLines.Length)
            {
                ScoreWindows(candidateWidth);
            }
        }

        if (bestScore < 0.4)
        {
            return "未找到任何相似内容，请用精确文本或 line_range_replace 重试。";
        }

        var contextStart = Math.Max(0, bestStart - 2);
        var contextEnd = Math.Min(contentLines.Length, bestStart + bestWidth + 2);
        var contextLines = contentLines.Skip(contextStart).Take(contextEnd - contextStart).Take(8);
        var nearby = string.Join("\n", contextLines);
        return string.Format(
            CultureInfo.InvariantCulture,
            "未找到精确匹配。以下为模糊匹配到的最相似片段（相似度 {0:0}%，第 {1}-{2} 行附近），仅供参考——请自行判断是否就是你想要修改的位置：\n{3}\n如果确认就是此处，可直接用 line_range_replace(start_line={1}, end_line={2}) 修改，或根据实际内容修正 search_text 后重新调用 search_replace。",
            bestScore * 100,
            bestStart + 1,
            bestStart + bestWidth,
            nearby);

        void ScoreWindows(int candidateWidth)
        {
            for (var i = 0; i <= contentLines.Length - candidateWidth; i++)
            {
                var candidate = string.Join("\n", contentLines.Skip(i).Take(candidateWidth));
                var score = PartialRatio(searchText, candidate);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestStart = i;
                    bestWidth = candidateWidth;
                }
            }
        }
    }

    private static double PartialRatio(string first, string second)
    {
        var shortText = first.Length <= second.Length ? first : second;
        var longText = first.Length <= second.Length ? second : first;
        if (shortText.Length == 0)
        {
            return longText.Length == 0 ? 1 : 0;
        }

        var shortRunes = shortText.EnumerateRunes().ToArray();
        var longRunes = longText.EnumerateRunes().ToArray();
        if (longRunes.Length < shortRunes.Length)
        {
            (shortRunes, longRunes) = (longRunes, shortRunes);
        }

        var best = 0.0;
        for (var i = 0; i <= longRunes.Length - shortRunes.Length; i++)
        {
            var matches = 0;
            for (var j = 0; j < shortRunes.Length; j++)
            {
                if (shortRunes[j] == longRunes[i + j])
                {
                    matches++;
                }
            }

            best = Math.Max(best, (double)matches / shortRunes.Length);
        }

        return best;
    }

    private static string LinePreview(string content, int start, int end)
    {
        var lines = SplitLines(content);
        var contextStart = Math.Max(0, start - 1);
        var contextEnd = Math.Min(lines.Length, end);
        var preStart = Math.Max(0, contextStart - 1);
        var postEnd = Math.Min(lines.Length, contextEnd + 1);
        var builder = new StringBuilder();

        for (var i = preStart; i < postEnd; i++)
        {
            if (i == contextStart)
            {
                builder.AppendLine("─── 改动区间 ───");
            }

            builder.Append(i + 1).Append('|').AppendLine(lines[i]);
            if (i == contextEnd - 1)
            {
                builder.AppendLine("─── 改动结束 ───");
            }
        }

        return builder.ToString();
    }

    private static string[] SplitLines(string content)
    {
        return content.Split('\n', StringSplitOptions.None);
    }

    private static string FormatLineNumberedContent(IReadOnlyList<string> selected, int start)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < selected.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder
                .Append((start + i).ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(selected[i]);
        }

        return builder.ToString();
    }

    private static int CountNewLines(string value)
    {
        return value.Count(ch => ch == '\n');
    }

    private static bool IsChapterPath(string path)
    {
        return path.StartsWith("chapters/", StringComparison.Ordinal);
    }

    private static bool IsBuiltinSkillPath(string path)
    {
        return path.StartsWith("/builtin/skills/", StringComparison.Ordinal);
    }

    private static string DisplayNameForPath(string path)
    {
        if (path.StartsWith("chapters/", StringComparison.Ordinal) &&
            ParseNumberedPath(path, "chapters/") is { } chapterNumber)
        {
            return $"第{chapterNumber.ToString(CultureInfo.InvariantCulture)}章";
        }

        if (path.StartsWith("outlines/", StringComparison.Ordinal) &&
            ParseNumberedPath(path, "outlines/") is { } outlineNumber)
        {
            return $"第{outlineNumber.ToString(CultureInfo.InvariantCulture)}章大纲";
        }

        if (path == "goink.md")
        {
            return "故事状态";
        }

        if (path.StartsWith("skills/", StringComparison.Ordinal) ||
            path.StartsWith("~/.goink/skills/", StringComparison.Ordinal) ||
            IsBuiltinSkillPath(path))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return $"技能: {name}";
        }

        return path;
    }

    private static int? ParseNumberedPath(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
            !path.EndsWith(".md", StringComparison.Ordinal))
        {
            return null;
        }

        var text = path[prefix.Length..^3];
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }
}
