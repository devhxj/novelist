using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemChatSessionService : IChatSessionService, ISubagentRunner
{
    private const int MaxSessionIdLength = 512;
    private const int MaxMessageLength = 200_000;
    private const int MaxProviderNameLength = 128;
    private const int MaxModelIdLength = 256;
    private const int MaxReasoningEffortLength = 128;
    private const int MaxSearchLength = 512;
    private const int MaxTitleLength = 30;
    private const int MaxEventChunkChars = 8 * 1024;
    private const int MaxToolLoopCount = 8;
    private const int MaxSubagentLoopCount = 50;
    private const double AutoCompressionUsageRatio = 80.0;
    private const int MaxRetainedUserMessagesAfterCompression = 15;
    private const int MinRetainedConversationTurnsAfterCompression = 4;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly Lazy<IReadOnlyList<ParsedSkillDocument>> BuiltinSkills = new(SkillDocuments.LoadBuiltin);

    private static readonly IReadOnlySet<string> MemorySubagentTools =
        new HashSet<string>([
            "search_story_memory",
            "read",
            "get_chapter_list",
            "get_preferences",
            "get_characters",
            "get_character_relations",
            "get_locations",
            "get_timeline",
            "get_story_arcs",
            "get_reader_perspective"], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> ReviewSubagentTools =
        new HashSet<string>([
            "search_story_memory",
            "read",
            "get_chapter_list",
            "get_preferences",
            "get_characters",
            "get_character_relations",
            "get_locations",
            "get_timeline",
            "get_story_arcs",
            "get_reader_perspective"], StringComparer.Ordinal);

    private const string MainAgentPrompt = """
        你是 Novelist 小说创作系统的主创作助手，协助用户持续管理一部长篇小说的章节、故事状态、技能方法论和创作上下文。

        【核心理念】

        本系统不是一次性问答工具，而是持续积累的创作环境。每一轮对话都可能影响后续章节，所以你必须优先保持一致性、可追溯性和数据真实。不要凭记忆猜测；需要原文、章节大纲、故事状态或技能内容时，使用当前可用工具读取。

        【当前已迁移工具面】

        当前对话可见的 tools 才是你实际能够调用的工具。不要调用未出现在 tools 列表中的旧工具名。

        已迁移的核心工具语义：
        - read：读取 chapters/NNN.md、outlines/NNN.md、goink.md、小说级/user/builtin skills。
        - edit：经用户审批后写入章节、大纲、goink.md 或可编辑 skill。
        - search_story_memory：在已构建的 RAG 记忆中检索章节片段。
        - run_subagent：启动 memory/review 子 Agent，获取专项检索报告或审稿报告。

        【创作流程】

        1. 判断意图：用户是在讨论、检索、审稿、规划，还是要求直接创作。
        2. 搜集上下文：优先读取 goink.md、相关章节/大纲，必要时用 search_story_memory 检索相关片段。
        3. 大纲先行：用户要求创作新章节时，先写大纲到 outlines/NNN.md 并等待审批。大纲应覆盖章节标题、基调与字数、场景设计、关键事件、重点角色、伏笔操作、章末钩子。
        4. 执行创作：审批通过后再写 chapters/NNN.md。正文 edit 的 new_content 只包含正文，不写章节号、章节标题或“本章完”。
        5. 状态维护：重要创作后必须同步维护 goink.md，记录当前进展、角色动态、未回收悬念和后续需要注意的信息。
        6. 审稿校验：较大改动或新章节完成后，可启动 review 子 Agent 审读一致性、逻辑、伏笔和节奏风险。
        7. 整合汇报：向用户简洁汇报已完成事项、重要决策和下一步。

        【文件路径约定】

        - chapters/001.md：章节正文。
        - outlines/001.md：章节大纲。
        - goink.md：故事状态文档，用于快速恢复创作状态。
        - skills/<name>.md：小说级技能。
        - ~/.goink/skills/<name>.md：用户级技能。
        - /builtin/skills/<name>.md：内置技能，只读。

        【技能系统】

        技能是 markdown 格式的创作方法论模块，包含 YAML frontmatter 和正文。
        - auto：出现在技能目录中。你可按需 read 完整技能内容后执行。
        - manual：只由用户通过 /skill 手动触发，不出现在 auto 技能目录中。
        - always：会话开头自动注入为系统消息，始终生效。

        如果用户通过 /skill 触发技能，你会收到额外的 system-reminder 注入。你必须结合该技能和用户原始消息执行，不要只处理斜杠命令本身。

        【输出规范】

        - 使用与用户一致的语言。
        - thinking 用于内部推理，content 用于给用户看的正式回复。
        - 工具调用遵循聚合原则，不要逐个报幕。
        - 工具结果异常或信息不足时明确说明缺口，并给出下一步可执行方案。
        - 遇到有风险的写入、设定冲突或模糊需求时，先确认或通过审批流程保护用户数据。
        """;

    private const string CompressionPrompt = """
        <system-reminder>
        你是上下文压缩助手。请基于完整对话历史生成结构化摘要，用于后续对话的上下文恢复。

        ## 已完成的事项
        （每个一句话，最多 15 条，从最近的开始保留。不再重复执行的事项）

        ## 进行中（断点）
        （最详细的部分：当前正在做什么、做到哪一步、下一步计划做什么。这是最重要的部分，请务必详尽）

        ## 用户偏好和要求
        （从用户消息中提炼的核心写作风格、约束条件、反复强调的事项）

        ## 关键决策和设定变更
        （已确认的情节走向、角色设定、世界观规则、命名等决定）

        ## 待办事项
        （已计划但尚未开始的任务清单）
        </system-reminder>
        """;

    private const string CompressionReminder = """
        <system-reminder>
        上下文已压缩，请根据下面的摘要继续工作。
        </system-reminder>
        """;

    private const string MemorySubagentPrompt = """
        你是小说创作系统的记忆检索分析员，负责按需查询和整理小说数据。

        ## 系统架构

        你与主 Agent 共享同一小说数据。你只有只读工具，不能修改任何数据。你的职责是按用户需求检索信息并整理成结构化报告。

        ## 工作流程

        1. 理解需求：明确用户想了解什么，例如角色背景、伏笔关系、弧线进展或章节细节。
        2. 多维度检索：优先使用 search_story_memory 检索语义记忆；需要精确原文、章节大纲、故事状态或技能时使用 read。
        3. 整理输出：将分散的信息整合为连贯报告，标注信息来源。

        ## 输出规范

        - 用中文回复。
        - 报告结构清晰，按主题分段。
        - 引用具体数据时注明来源，例如章节号、文件路径或检索片段。
        - 不输出无依据的推测；信息不足时明确说明还缺什么。
        """;

    private const string ReviewSubagentPrompt = """
        你是小说创作系统的审稿 Agent，负责对已完成章节进行专业审读。

        ## 系统架构

        你与主 Agent 共享同一小说数据。你可以调用只读工具获取章节正文、章节大纲、故事状态和语义记忆来辅助审读。你不能修改任何文件或结构化数据。

        ## 审读流程

        1. 阅读章节：使用 read 读取目标章节或大纲；若指令未给出章节号，先根据章节目录和故事状态判断需要审读的范围。
        2. 收集上下文：使用 search_story_memory 查询相关角色、伏笔、场景和前后文。
        3. 逐项检查：
           - 角色一致性：性格、能力、关系是否前后一致。
           - 情节逻辑：事件因果是否合理，有无逻辑漏洞。
           - 伏笔管理：已埋伏笔是否推进或回收，新伏笔是否需要记录。
           - 读者认知：悬念是否恰当维护，误知是否按时回收。
           - 弧线推进：故事线进度是否合理，节点是否需要校准。
        4. 输出审稿意见：格式自由，但应包含发现的问题、严重程度和可执行建议。

        ## 输出规范

        - 用中文回复。
        - 审稿意见按维度分段，每段标注问题严重程度。
        - thinking 用于分析推理，content 用于最终审稿意见。
        - 不能直接改稿；如需修改，给主 Agent 提供明确建议。
        """;

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly IAppSettingsService _settings;
    private readonly ILlmConfigurationService _llm;
    private readonly IChatCompletionClient _completion;
    private readonly IBridgeEventSink _events;
    private readonly IApprovalCoordinator? _approvals;
    private readonly IChatToolExecutor? _toolExecutor;
    private readonly IChapterContentService? _chapterContent;
    private readonly IVersionControlService _versionControl;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly ConcurrentDictionary<string, ActiveChatOperation> _activeChats = new(StringComparer.Ordinal);

    public FileSystemChatSessionService(
        AppInitializationOptions? options,
        INovelService novels,
        IAppSettingsService settings,
        ILlmConfigurationService llm,
        IChatCompletionClient completion,
        IBridgeEventSink? events = null,
        IApprovalCoordinator? approvals = null,
        IChatToolExecutor? toolExecutor = null,
        IChapterContentService? chapterContent = null,
        IVersionControlService? versionControl = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels;
        _settings = settings;
        _llm = llm;
        _completion = completion;
        _events = events ?? new NullBridgeEventSink();
        _approvals = approvals;
        _toolExecutor = toolExecutor;
        _chapterContent = chapterContent;
        _versionControl = versionControl ?? new GitVersionControlService(_options);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<PageResultPayload<SessionMetaPayload>> GetSessionsAsync(
        GetSessionsPayload input,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(input.NovelId);
        var page = input.Page < 1 ? 1 : input.Page;
        var size = input.Size is < 1 or > 100 ? 20 : input.Size;
        var search = NormalizeOptionalText(input.Search, nameof(input.Search), MaxSearchLength);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            IEnumerable<ChatSessionDocument> query = store.Sessions
                .Where(session => session.NovelId == input.NovelId);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var matchingSessionIds = store.Messages
                    .Where(message =>
                        message.SessionId.Length > 0 &&
                        message.Content.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .Select(message => message.SessionId)
                    .ToHashSet(StringComparer.Ordinal);
                query = query.Where(session => matchingSessionIds.Contains(session.SessionId));
            }

            var ordered = query
                .OrderByDescending(session => session.UpdatedAt)
                .ThenByDescending(session => session.CreatedAt)
                .ThenBy(session => session.SessionId, StringComparer.Ordinal)
                .ToArray();
            var total = ordered.LongLength;
            var items = ordered
                .Skip((page - 1) * size)
                .Take(size)
                .Select(ToSessionMetaPayload)
                .ToArray();
            var totalPages = size > 0
                ? (int)(total / size + (total % size == 0 ? 0 : 1))
                : 0;

            return new PageResultPayload<SessionMetaPayload>(items, total, page, size, totalPages);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<SessionDetailPayload> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var normalizedSessionId = NormalizeSessionId(sessionId, allowEmpty: false);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var session = FindSession(store, normalizedSessionId)
                ?? throw new ArgumentException($"Session '{normalizedSessionId}' does not exist.", nameof(sessionId));
            return ToSessionDetailPayload(session);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<SessionMessagePayload>> GetSessionMessagesAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var normalizedSessionId = NormalizeSessionId(sessionId, allowEmpty: false);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            return store.Messages
                .Where(message => string.Equals(message.SessionId, normalizedSessionId, StringComparison.Ordinal) &&
                    message.ToFrontend)
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id)
                .Select(ToMessagePayload)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ChatResultPayload> ChatAsync(
        ChatInputPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = NormalizeChatInput(input);

        await EnsureNovelExistsAsync(normalized.NovelId, cancellationToken);
        var model = await GetConfiguredModelAsync(normalized.ProviderName, normalized.ModelId, cancellationToken);
        var initialSystemMessages = await BuildMainInitialSystemMessagesAsync(normalized.NovelId, cancellationToken);
        var slashInjection = await ResolveSlashInjectionAsync(normalized.NovelId, normalized.Message, cancellationToken);

        ChatSessionDocument session;
        bool isNew;
        int turnId;
        IReadOnlyList<ChatCompletionMessage> apiMessages;
        JsonElement? previousUsage;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var existingSession = FindSession(store, normalized.SessionId);
            isNew = existingSession is null;
            session = existingSession ?? CreateSession(normalized);
            if (isNew)
            {
                store.Sessions.Add(session);
            }

            turnId = checked(session.LastTurnId + 1);
            session.LastTurnId = turnId;
            session.UpdatedAt = UtcNow();

            if (isNew)
            {
                foreach (var systemMessage in initialSystemMessages)
                {
                    store.Messages.Add(CreateMessage(
                        store,
                        session.SessionId,
                        turnId,
                        role: "system",
                        content: systemMessage,
                        thinkingContent: null,
                        version: session.ActiveVersion,
                        toApi: true,
                        toFrontend: false,
                        eventType: null,
                        agentType: "main"));
                }
            }

            if (!string.IsNullOrWhiteSpace(slashInjection.InjectContent))
            {
                store.Messages.Add(CreateMessage(
                    store,
                    session.SessionId,
                    turnId,
                    role: "user",
                    content: slashInjection.InjectContent,
                    thinkingContent: null,
                    version: session.ActiveVersion,
                    toApi: true,
                    toFrontend: false,
                    eventType: null,
                    agentType: "main"));
            }

            store.Messages.Add(CreateMessage(
                store,
                session.SessionId,
                turnId,
                role: "user",
                content: normalized.Message,
                thinkingContent: null,
                version: session.ActiveVersion,
                toApi: true,
                toFrontend: true,
                eventType: null,
                agentType: "main"));

            apiMessages = store.Messages
                .Where(message => string.Equals(message.SessionId, session.SessionId, StringComparison.Ordinal) &&
                    message.ToApi &&
                    message.Version == session.ActiveVersion)
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id)
                .Select(ToCompletionMessage)
                .ToArray();

            previousUsage = ParseUsage(session.UsageJson);
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }

        if (isNew)
        {
            await _events.EmitAsync("chat:session_created", ToSessionDetailPayload(session), cancellationToken);
        }

        await _settings.SetLastSessionAsync(session.SessionId, cancellationToken);
        await CommitUserChangesAtTurnStartAsync(
            normalized.NovelId,
            turnId,
            session.SessionId,
            cancellationToken);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new ActiveChatOperation(linkedCancellation);
        RegisterActiveChat(session.SessionId, active);

        var finalText = string.Empty;
        JsonElement? usage = null;
        var seq = 0;
        var loopMessages = apiMessages.ToList();
        var toolDefinitions = _toolExecutor?.GetToolDefinitions(normalized.NovelId) ?? [];
        var autoCompressedThisTurn = false;

        try
        {
            await _events.EmitAsync(
                "chat:started",
                new { session_id = session.SessionId, turn_id = turnId },
                linkedCancellation.Token);

            if (ShouldAutoCompress(previousUsage))
            {
                var compression = await CompressSessionContextAsync(
                    session.SessionId,
                    normalized.NovelId,
                    turnId,
                    normalized.ProviderName,
                    normalized.ModelId,
                    normalized.ReasoningEffort,
                    seq,
                    subTaskId: null,
                    linkedCancellation.Token);
                seq = compression.Seq;
                loopMessages = compression.Messages.ToList();
                autoCompressedThisTurn = true;
            }

            for (var loop = 0; loop < MaxToolLoopCount; loop++)
            {
                var roundText = new StringBuilder();
                var roundThinkingText = new StringBuilder();
                var toolOutputs = new List<ExecutedChatToolCall>();

                await foreach (var streamEvent in _completion.StreamChatAsync(
                    new ChatCompletionRequest(
                        normalized.ProviderName,
                        normalized.ModelId,
                        normalized.ReasoningEffort,
                        loopMessages,
                        toolDefinitions),
                    linkedCancellation.Token).WithCancellation(linkedCancellation.Token))
                {
                    switch (streamEvent.Kind)
                    {
                        case ChatCompletionStreamEventKind.Thinking:
                            roundThinkingText.Append(streamEvent.Data);
                            seq = await EmitAgentEventAsync(
                                turnId,
                                seq,
                                type: 0,
                                data: streamEvent.Data,
                                usage: null,
                                error: null,
                                subTaskId: null,
                                linkedCancellation.Token);
                            break;
                        case ChatCompletionStreamEventKind.Content:
                            roundText.Append(streamEvent.Data);
                            seq = await EmitAgentEventAsync(
                                turnId,
                                seq,
                                type: 2,
                                data: streamEvent.Data,
                                usage: null,
                                error: null,
                                subTaskId: null,
                                linkedCancellation.Token);
                            break;
                        case ChatCompletionStreamEventKind.Usage:
                            if (streamEvent.Usage is not null)
                            {
                                usage = BuildUsagePayload(streamEvent.Usage.Value, model);
                                seq = await EmitAgentEventAsync(
                                    turnId,
                                    seq,
                                    type: 4,
                                    data: null,
                                    usage: usage,
                                    error: null,
                                    subTaskId: null,
                                    linkedCancellation.Token);
                            }

                            break;
                        case ChatCompletionStreamEventKind.ToolCall:
                            if (streamEvent.ToolCall is null)
                            {
                                break;
                            }

                            var output = await ExecuteToolCallAsync(
                                normalized.NovelId,
                                session.SessionId,
                                turnId,
                                normalized.ProviderName,
                                normalized.ModelId,
                                normalized.ReasoningEffort,
                                seq,
                                streamEvent.ToolCall,
                                allowedToolNames: null,
                                subTaskId: null,
                                agentType: "main",
                                linkedCancellation.Token);
                            seq = output.Seq;
                            toolOutputs.Add(output);
                            break;
                        default:
                            throw new InvalidOperationException($"Unsupported chat stream event kind '{streamEvent.Kind}'.");
                    }
                }

                if (toolOutputs.Count == 0)
                {
                    finalText = roundText.ToString();
                    await PersistAssistantMessageAsync(
                        session.SessionId,
                        turnId,
                        finalText,
                        roundThinkingText.ToString(),
                        usage,
                        cancellationToken);
                    break;
                }

                await PersistToolRoundAsync(
                    session.SessionId,
                    turnId,
                    roundText.ToString(),
                    roundThinkingText.ToString(),
                    usage,
                    toolOutputs,
                    cancellationToken);

                loopMessages.Add(new ChatCompletionMessage(
                    "assistant",
                    roundText.ToString(),
                    NullIfEmpty(roundThinkingText.ToString()),
                    toolOutputs.Select(output => output.Call).ToArray()));
                foreach (var output in toolOutputs)
                {
                    loopMessages.Add(new ChatCompletionMessage(
                        "tool",
                        FormatToolResultJson(output.Result),
                        ToolCallId: output.Call.Id,
                        ToolName: output.Call.Name));
                }

                if (!autoCompressedThisTurn && ShouldAutoCompress(usage))
                {
                    var compression = await CompressSessionContextAsync(
                        session.SessionId,
                        normalized.NovelId,
                        turnId,
                        normalized.ProviderName,
                        normalized.ModelId,
                        normalized.ReasoningEffort,
                        seq,
                        subTaskId: null,
                        linkedCancellation.Token);
                    seq = compression.Seq;
                    loopMessages = compression.Messages.ToList();
                    autoCompressedThisTurn = true;
                }

                if (loop == MaxToolLoopCount - 1)
                {
                    throw new BridgeRequestException(
                        BridgeErrorCodes.LlmProviderError,
                        "工具调用轮次过多，已中止。",
                        retryable: false);
                }
            }

            if (isNew)
            {
                await GenerateAndPersistTitleAsync(
                    session.SessionId,
                    normalized,
                    cancellationToken);
            }

            await CommitAiChangesAtTurnEndAsync(
                normalized.NovelId,
                turnId,
                session.SessionId,
                model.ModelName,
                CancellationToken.None);

            return new ChatResultPayload(session.SessionId, turnId, finalText);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            await PersistSystemEventAsync(
                session.SessionId,
                turnId,
                active.UserCancelled ? "user_stopped" : "system_interrupted",
                active.UserCancelled ? "对话已停止" : "对话被中断",
                CancellationToken.None);
            await CommitAiChangesAtTurnEndAsync(
                normalized.NovelId,
                turnId,
                session.SessionId,
                model.ModelName,
                CancellationToken.None);
            throw;
        }
        catch (BridgeRequestException ex)
        {
            await PersistAndEmitErrorAsync(session.SessionId, turnId, seq, ex.Message, CancellationToken.None);
            await CommitAiChangesAtTurnEndAsync(
                normalized.NovelId,
                turnId,
                session.SessionId,
                model.ModelName,
                CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await PersistAndEmitErrorAsync(session.SessionId, turnId, seq, ex.Message, CancellationToken.None);
            await CommitAiChangesAtTurnEndAsync(
                normalized.NovelId,
                turnId,
                session.SessionId,
                model.ModelName,
                CancellationToken.None);
            throw new BridgeRequestException(
                BridgeErrorCodes.LlmProviderError,
                $"LLM 调用失败: {ex.Message}",
                retryable: true);
        }
        finally
        {
            if (_activeChats.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, active))
            {
                _activeChats.TryRemove(session.SessionId, out _);
            }
        }
    }

    public async ValueTask<CompressResultPayload> CompressContextAsync(
        CompressInputPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = NormalizeCompressInput(input);
        await GetConfiguredModelAsync(normalized.ProviderName, normalized.ModelId, cancellationToken);

        ChatSessionDocument session;
        int turnId;
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            session = FindSession(store, normalized.SessionId)
                ?? throw new ArgumentException($"Session '{normalized.SessionId}' does not exist.", nameof(input));

            if (_activeChats.ContainsKey(session.SessionId))
            {
                throw new BridgeRequestException(
                    BridgeErrorCodes.ValidationError,
                    "对话进行中，无法手动压缩上下文，请等待当前对话完成。",
                    retryable: false);
            }

            turnId = checked(session.LastTurnId + 1);
            session.LastTurnId = turnId;
            session.UpdatedAt = UtcNow();
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new ActiveChatOperation(linkedCancellation);
        if (!_activeChats.TryAdd(session.SessionId, active))
        {
            throw new BridgeRequestException(
                BridgeErrorCodes.ValidationError,
                "对话进行中，无法手动压缩上下文，请等待当前对话完成。",
                retryable: false);
        }

        try
        {
            await CompressSessionContextAsync(
                session.SessionId,
                session.NovelId,
                turnId,
                normalized.ProviderName,
                normalized.ModelId,
                session.ReasoningEffort,
                currentSeq: 0,
                subTaskId: null,
                linkedCancellation.Token);
            return new CompressResultPayload(turnId);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            await PersistSystemEventAsync(
                session.SessionId,
                turnId,
                active.UserCancelled ? "user_stopped" : "system_interrupted",
                active.UserCancelled ? "上下文压缩已停止" : "上下文压缩被中断",
                CancellationToken.None);
            throw;
        }
        finally
        {
            if (_activeChats.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, active))
            {
                _activeChats.TryRemove(session.SessionId, out _);
            }
        }
    }

    public async ValueTask CancelChatAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSessionId = NormalizeSessionId(sessionId, allowEmpty: true);
        if (normalizedSessionId.Length == 0)
        {
            return;
        }

        if (_activeChats.TryGetValue(normalizedSessionId, out var active))
        {
            active.UserCancelled = true;
            active.Cancellation.Cancel();
        }

        if (_approvals is not null)
        {
            await _approvals.CancelSessionAsync(normalizedSessionId, cancellationToken);
        }
    }

    public async ValueTask<SubagentRunResult> RunAsync(
        SubagentRunRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeSubagentRunRequest(request);
        var model = await GetConfiguredModelAsync(
            normalized.ProviderName,
            normalized.ModelId,
            cancellationToken);
        var seq = normalized.StartSequence;
        var messages = await BuildSubagentInitialMessagesAsync(normalized, cancellationToken);
        var loopMessages = messages.ToList();
        var toolDefinitions = GetSubagentToolDefinitions(normalized.NovelId, normalized.AgentType);
        var allowedToolNames = toolDefinitions
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        var finalText = string.Empty;
        var subagentCompressedThisRun = false;

        for (var loop = 0; loop < MaxSubagentLoopCount; loop++)
        {
            var roundText = new StringBuilder();
            var roundThinkingText = new StringBuilder();
            var toolOutputs = new List<ExecutedChatToolCall>();
            JsonElement? roundUsage = null;

            await foreach (var streamEvent in _completion.StreamChatAsync(
                new ChatCompletionRequest(
                    normalized.ProviderName,
                    normalized.ModelId,
                    normalized.ReasoningEffort,
                    loopMessages,
                    toolDefinitions),
                cancellationToken).WithCancellation(cancellationToken))
            {
                switch (streamEvent.Kind)
                {
                    case ChatCompletionStreamEventKind.Thinking:
                        roundThinkingText.Append(streamEvent.Data);
                        seq = await EmitAgentEventAsync(
                            normalized.TurnId,
                            seq,
                            type: 0,
                            data: streamEvent.Data,
                            usage: null,
                            error: null,
                            subTaskId: normalized.ToolId,
                            cancellationToken);
                        break;
                    case ChatCompletionStreamEventKind.Content:
                        roundText.Append(streamEvent.Data);
                        seq = await EmitAgentEventAsync(
                            normalized.TurnId,
                            seq,
                            type: 2,
                            data: streamEvent.Data,
                            usage: null,
                            error: null,
                            subTaskId: normalized.ToolId,
                            cancellationToken);
                        break;
                    case ChatCompletionStreamEventKind.Usage:
                        if (streamEvent.Usage is not null)
                        {
                            roundUsage = BuildUsagePayload(streamEvent.Usage.Value, model);
                            seq = await EmitAgentEventAsync(
                                normalized.TurnId,
                                seq,
                                type: 4,
                                data: null,
                                usage: roundUsage,
                                error: null,
                                subTaskId: normalized.ToolId,
                                cancellationToken);
                        }

                        break;
                    case ChatCompletionStreamEventKind.ToolCall:
                        if (streamEvent.ToolCall is null)
                        {
                            break;
                        }

                        var output = await ExecuteToolCallAsync(
                            normalized.NovelId,
                            normalized.SessionId,
                            normalized.TurnId,
                            normalized.ProviderName,
                            normalized.ModelId,
                            normalized.ReasoningEffort,
                            seq,
                            streamEvent.ToolCall,
                            allowedToolNames,
                            normalized.ToolId,
                            normalized.AgentType,
                            cancellationToken);
                        seq = output.Seq;
                        toolOutputs.Add(output);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported chat stream event kind '{streamEvent.Kind}'.");
                }
            }

            if (toolOutputs.Count == 0)
            {
                finalText = roundText.ToString();
                if (finalText.Length > 0 || roundThinkingText.Length > 0)
                {
                    await PersistSubagentAssistantMessageAsync(
                        normalized,
                        finalText,
                        roundThinkingText.ToString(),
                        cancellationToken);
                }

                return new SubagentRunResult(normalized.AgentType, finalText, seq);
            }

            await PersistSubagentToolRoundAsync(
                normalized,
                roundText.ToString(),
                roundThinkingText.ToString(),
                toolOutputs,
                cancellationToken);

            loopMessages.Add(new ChatCompletionMessage(
                "assistant",
                roundText.ToString(),
                NullIfEmpty(roundThinkingText.ToString()),
                toolOutputs.Select(output => output.Call).ToArray()));
            foreach (var output in toolOutputs)
            {
                loopMessages.Add(new ChatCompletionMessage(
                    "tool",
                    FormatToolResultJson(output.Result),
                    ToolCallId: output.Call.Id,
                    ToolName: output.Call.Name));
            }

            if (!subagentCompressedThisRun && ShouldAutoCompress(roundUsage))
            {
                var compression = await CompressSubagentContextAsync(
                    normalized,
                    loopMessages,
                    seq,
                    cancellationToken);
                loopMessages = compression.Messages.ToList();
                seq = compression.Seq;
                subagentCompressedThisRun = true;
            }
        }

        throw new BridgeRequestException(
            BridgeErrorCodes.LlmProviderError,
            "子 Agent 工具调用轮次过多，已中止。",
            retryable: false);
    }

    private async ValueTask<IReadOnlyList<ChatCompletionMessage>> BuildSubagentInitialMessagesAsync(
        SubagentRunRequest request,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatCompletionMessage>
        {
            new("system", SubagentIdentityPrompt(request.AgentType))
        };

        var state = await BuildSubagentNovelStateAsync(request.NovelId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(state))
        {
            messages.Add(new ChatCompletionMessage("system", state));
        }

        messages.Add(new ChatCompletionMessage("user", request.Instruction));
        return messages;
    }

    private async ValueTask<string> BuildSubagentNovelStateAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        try
        {
            var novels = await _novels.GetNovelsAsync(cancellationToken);
            var novel = novels.FirstOrDefault(item => item.Id == novelId);
            if (novel is not null)
            {
                builder.AppendLine("<novel-state>");
                builder.AppendLine($"小说：{novel.Title}");
                if (!string.IsNullOrWhiteSpace(novel.Genre))
                {
                    builder.AppendLine($"类型：{novel.Genre}");
                }

                if (!string.IsNullOrWhiteSpace(novel.Description))
                {
                    builder.AppendLine($"简介：{novel.Description}");
                }
            }

            if (_chapterContent is not null)
            {
                var chapters = await _chapterContent.GetChaptersAsync(novelId, cancellationToken);
                if (chapters.Count > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine("章节目录：");
                    foreach (var chapter in chapters.OrderBy(item => item.ChapterNumber).Take(200))
                    {
                        builder.AppendLine(
                            $"- 第{chapter.ChapterNumber.ToString(CultureInfo.InvariantCulture)}章 {chapter.Title}（{chapter.WordCount.ToString(CultureInfo.InvariantCulture)}字）");
                    }
                }

                var goink = await _chapterContent.GetContentAsync(novelId, "goink.md", cancellationToken);
                if (!string.IsNullOrWhiteSpace(goink))
                {
                    builder.AppendLine();
                    builder.AppendLine("故事状态 goink.md：");
                    builder.AppendLine(TrimLongState(goink));
                }
            }

            if (builder.Length > 0)
            {
                builder.AppendLine("</novel-state>");
            }
        }
        catch
        {
            // Match the old runner: a snapshot failure must not prevent the subagent from using tools.
        }

        return builder.ToString();
    }

    private async ValueTask<IReadOnlyList<string>> BuildMainInitialSystemMessagesAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        var messages = new List<string> { MainAgentPrompt };
        var skills = await LoadMergedSkillDocumentsAsync(novelId, cancellationToken);
        var alwaysSkills = BuildAlwaysSkillsContent(skills);
        if (!string.IsNullOrWhiteSpace(alwaysSkills))
        {
            messages.Add(alwaysSkills);
        }

        var skillCatalog = BuildSkillCatalog(skills);
        if (!string.IsNullOrWhiteSpace(skillCatalog))
        {
            messages.Add(skillCatalog);
        }

        var novelState = await BuildMainNovelStateAsync(novelId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(novelState))
        {
            messages.Add(novelState);
        }

        return messages;
    }

    private async ValueTask<string> BuildMainNovelStateAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        var novels = await _novels.GetNovelsAsync(cancellationToken);
        var novel = novels.FirstOrDefault(item => item.Id == novelId)
            ?? throw new ArgumentException($"Novel '{novelId}' does not exist.", nameof(novelId));

        var builder = new StringBuilder();
        builder.AppendLine("【小说基础信息】");
        builder.AppendLine($"书名：{novel.Title}");
        if (!string.IsNullOrWhiteSpace(novel.Genre))
        {
            builder.AppendLine($"类型：{novel.Genre}");
        }

        if (!string.IsNullOrWhiteSpace(novel.Description))
        {
            builder.AppendLine($"简介：{novel.Description}");
        }

        if (_chapterContent is not null)
        {
            var state = await _chapterContent.GetContentAsync(novelId, "goink.md", cancellationToken);
            if (!string.IsNullOrWhiteSpace(state))
            {
                builder.AppendLine();
                builder.AppendLine("【故事状态文档】");
                builder.AppendLine(state.Trim());
            }
        }

        return builder.ToString().Trim();
    }

    private async ValueTask<SlashInjection> ResolveSlashInjectionAsync(
        long novelId,
        string message,
        CancellationToken cancellationToken)
    {
        if (!message.StartsWith("/", StringComparison.Ordinal))
        {
            return SlashInjection.Empty;
        }

        var first = message
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first) || first.Length <= 1)
        {
            return SlashInjection.Empty;
        }

        var commandName = first[1..];
        ResolvedSkillDocument? skill;
        try
        {
            var normalizedName = SkillDocuments.NormalizeSkillName(commandName);
            var skills = await LoadMergedSkillDocumentsAsync(novelId, cancellationToken);
            skill = skills.FirstOrDefault(item => string.Equals(item.Name, normalizedName, StringComparison.Ordinal));
        }
        catch (ArgumentException)
        {
            return SlashInjection.Empty;
        }

        if (skill is null)
        {
            return SlashInjection.Empty;
        }

        var content = skill.Mode switch
        {
            "always" =>
                $"用户通过 /{skill.Name} 提醒你注意常驻技能「{skill.Name}」，其完整内容已在本次对话开头注入，请严格遵循。",
            "manual" =>
                $"用户启用了快捷指令「{skill.Name}」。请根据该指令的内容和用户需求进行工作。\n\n---\n{skill.Content}\n---",
            _ =>
                $"用户启用了技能「{skill.Name}」。请根据该技能的定义和用户需求进行工作。\n\n---\n{skill.RawContent}\n---"
        };

        return new SlashInjection(
            $"<system-reminder>\n{content}\n</system-reminder>",
            skill.Name);
    }

    private async ValueTask<IReadOnlyList<ResolvedSkillDocument>> LoadMergedSkillDocumentsAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        var dataDirectory = await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken);
        var layers = new[]
        {
            ("novel", SkillDocuments.ScanDirectory(NovelSkillsDirectory(dataDirectory, novelId), "user")),
            ("user", SkillDocuments.ScanDirectory(UserSkillsDirectory(dataDirectory), "user")),
            ("builtin", BuiltinSkills.Value)
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ResolvedSkillDocument>();
        foreach (var (source, skills) in layers)
        {
            foreach (var skill in skills.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                if (!seen.Add(skill.Name))
                {
                    continue;
                }

                result.Add(new ResolvedSkillDocument(
                    skill.Name,
                    skill.Description,
                    skill.Mode,
                    source,
                    skill.Content,
                    skill.RawContent));
            }
        }

        return result;
    }

    private static string NovelSkillsDirectory(string dataDirectory, long novelId)
    {
        return SafeChildPath(Path.Combine(dataDirectory, "novels"), $"{novelId}/skills");
    }

    private static string UserSkillsDirectory(string dataDirectory)
    {
        return SafeChildPath(dataDirectory, "skills");
    }

    private static string SafeChildPath(string parentDirectory, string relativePath)
    {
        var parent = Path.GetFullPath(parentDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(parent, relativePath));
        var parentWithSeparator = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(parentWithSeparator, comparison))
        {
            throw new InvalidContentPathException(relativePath, "Resolved path escapes the novelist data directory.");
        }

        return fullPath;
    }

    private static string BuildAlwaysSkillsContent(IReadOnlyList<ResolvedSkillDocument> skills)
    {
        var always = skills
            .Where(skill => string.Equals(skill.Mode, "always", StringComparison.Ordinal))
            .ToArray();
        if (always.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("【常驻技能】");
        builder.AppendLine("以下技能在本次对话中始终生效：");
        builder.AppendLine();
        foreach (var skill in always)
        {
            builder.Append("--- ").Append(skill.Name).AppendLine(" ---");
            builder.AppendLine(skill.Content);
            builder.AppendLine();
        }

        return builder.ToString().Trim();
    }

    private static string BuildSkillCatalog(IReadOnlyList<ResolvedSkillDocument> skills)
    {
        var auto = skills
            .Where(skill => string.Equals(skill.Mode, "auto", StringComparison.Ordinal))
            .ToArray();
        if (auto.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("<available_skills>");
        AddGroup("小说专属技能", "novel");
        AddGroup("用户技能", "user");
        AddGroup("内置技能（只读）", "builtin");
        builder.AppendLine("---");
        builder.AppendLine("使用 read 工具加载技能完整内容：");
        builder.AppendLine("- skills/<name>.md（小说技能）");
        builder.AppendLine("- ~/.goink/skills/<name>.md（用户技能）");
        builder.AppendLine("- /builtin/skills/<name>.md（内置技能，只读）");
        builder.AppendLine("使用 edit 工具创建或修改技能。内置技能不可编辑。");
        builder.Append("</available_skills>");
        return builder.ToString();

        void AddGroup(string title, string source)
        {
            var group = auto
                .Where(skill => string.Equals(skill.Source, source, StringComparison.Ordinal))
                .ToArray();
            if (group.Length == 0)
            {
                return;
            }

            builder.Append("## ").AppendLine(title);
            foreach (var skill in group)
            {
                builder.Append("- ").Append(skill.Name);
                if (!string.IsNullOrWhiteSpace(skill.Description))
                {
                    builder.Append(": ").Append(skill.Description);
                }

                builder.AppendLine();
            }

            builder.AppendLine();
        }
    }

    private async ValueTask<CompressionRunResult> CompressSessionContextAsync(
        string sessionId,
        long novelId,
        int turnId,
        string providerName,
        string modelId,
        string reasoningEffort,
        int currentSeq,
        string? subTaskId,
        CancellationToken cancellationToken)
    {
        var seq = await EmitCompressionEventAsync(
            turnId,
            currentSeq,
            phase: "compressing",
            summary: null,
            subTaskId,
            cancellationToken);
        var messages = await LoadApiMessagesForSessionAsync(sessionId, cancellationToken);
        var summary = await GenerateCompressionSummaryAsync(
            providerName,
            modelId,
            reasoningEffort,
            messages,
            cancellationToken);
        var retained = RetainMessages(messages);
        var compressedMessages = await PersistMainCompressionAsync(
            sessionId,
            novelId,
            turnId,
            summary,
            retained,
            cancellationToken);
        seq = await EmitCompressionEventAsync(
            turnId,
            seq,
            phase: "done",
            summary,
            subTaskId,
            cancellationToken);
        return new CompressionRunResult(compressedMessages, seq);
    }

    private async ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadApiMessagesForSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var session = FindSession(store, sessionId)
                ?? throw new ArgumentException($"Session '{sessionId}' does not exist.", nameof(sessionId));
            return store.Messages
                .Where(message => string.Equals(message.SessionId, sessionId, StringComparison.Ordinal) &&
                    message.ToApi &&
                    message.Version == session.ActiveVersion)
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id)
                .Select(ToCompletionMessage)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<string> GenerateCompressionSummaryAsync(
        string providerName,
        string modelId,
        string reasoningEffort,
        IReadOnlyList<ChatCompletionMessage> messages,
        CancellationToken cancellationToken)
    {
        var compressionMessages = messages
            .Append(new ChatCompletionMessage("user", CompressionPrompt))
            .ToArray();
        var summary = await _completion.GenerateTextAsync(
            new ChatCompletionRequest(providerName, modelId, reasoningEffort, compressionMessages),
            cancellationToken);
        summary = summary.Trim();
        if (summary.Length == 0)
        {
            throw new BridgeRequestException(
                BridgeErrorCodes.LlmProviderError,
                "上下文压缩失败：模型返回了空摘要。",
                retryable: true);
        }

        return summary;
    }

    private async ValueTask<IReadOnlyList<ChatCompletionMessage>> PersistMainCompressionAsync(
        string sessionId,
        long novelId,
        int turnId,
        string summary,
        IReadOnlyList<ChatCompletionMessage> retained,
        CancellationToken cancellationToken)
    {
        var systemMessages = await BuildMainInitialSystemMessagesAsync(novelId, cancellationToken);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var session = FindSession(store, sessionId)
                ?? throw new InvalidOperationException($"Session '{sessionId}' disappeared during compression.");
            var newVersion = checked(session.ActiveVersion + 1);
            session.ActiveVersion = newVersion;
            session.Summary = summary;
            session.UpdatedAt = UtcNow();

            foreach (var systemMessage in systemMessages)
            {
                store.Messages.Add(CreateMessage(
                    store,
                    sessionId,
                    turnId,
                    role: "system",
                    content: systemMessage,
                    thinkingContent: null,
                    version: newVersion,
                    toApi: true,
                    toFrontend: false,
                    eventType: null,
                    agentType: "main"));
            }

            store.Messages.Add(CreateMessage(
                store,
                sessionId,
                turnId,
                role: "user",
                content: CompressionReminder,
                thinkingContent: null,
                version: newVersion,
                toApi: true,
                toFrontend: false,
                eventType: null,
                agentType: "main"));
            store.Messages.Add(CreateMessage(
                store,
                sessionId,
                turnId,
                role: "user",
                content: $"<system-reminder>\n{summary}\n</system-reminder>",
                thinkingContent: null,
                version: newVersion,
                toApi: true,
                toFrontend: false,
                eventType: null,
                agentType: "main"));

            foreach (var message in retained)
            {
                store.Messages.Add(CreateMessage(
                    store,
                    sessionId,
                    turnId,
                    message.Role,
                    message.Content,
                    message.ThinkingContent,
                    newVersion,
                    toApi: true,
                    toFrontend: false,
                    eventType: null,
                    agentType: "main",
                    extraMetadata: BuildApiMessageMetadata(message)));
            }

            store.Messages.Add(CreateMessage(
                store,
                sessionId,
                turnId,
                role: "system",
                content: string.Empty,
                thinkingContent: null,
                version: newVersion,
                toApi: false,
                toFrontend: true,
                eventType: "compression",
                agentType: "main"));

            var apiMessages = store.Messages
                .Where(message => string.Equals(message.SessionId, sessionId, StringComparison.Ordinal) &&
                    message.ToApi &&
                    message.Version == session.ActiveVersion)
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id)
                .Select(ToCompletionMessage)
                .ToArray();
            await SaveAsync(store, cancellationToken);
            return apiMessages;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<int> EmitCompressionEventAsync(
        int turnId,
        int currentSeq,
        string phase,
        string? summary,
        string? subTaskId,
        CancellationToken cancellationToken)
    {
        var seq = currentSeq + 1;
        await _events.EmitAsync(
            $"agent:{turnId.ToString(CultureInfo.InvariantCulture)}",
            new AgentEventPayload
            {
                TurnId = turnId,
                SubTaskId = string.IsNullOrWhiteSpace(subTaskId) ? null : subTaskId,
                Seq = seq,
                Type = 6,
                CompressionPhase = phase,
                Summary = string.IsNullOrWhiteSpace(summary) ? null : summary,
                Timestamp = UtcNow()
            },
            cancellationToken);
        return seq;
    }

    private static IReadOnlyList<ChatCompletionMessage> RetainMessages(
        IReadOnlyList<ChatCompletionMessage> messages)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        var systemEnd = 0;
        while (systemEnd < messages.Count &&
            string.Equals(messages[systemEnd].Role, "system", StringComparison.Ordinal))
        {
            systemEnd++;
        }

        var history = messages.Skip(systemEnd).ToArray();
        if (history.Length == 0)
        {
            return [];
        }

        var userIndexes = history
            .Select((message, index) => (message, index))
            .Where(item => string.Equals(item.message.Role, "user", StringComparison.Ordinal))
            .Select(item => item.index)
            .ToArray();
        if (userIndexes.Length == 0)
        {
            return [];
        }

        var keepFrom = 0;
        if (userIndexes.Length > MaxRetainedUserMessagesAfterCompression)
        {
            keepFrom = userIndexes[^MaxRetainedUserMessagesAfterCompression];
        }

        if (userIndexes.Length >= MinRetainedConversationTurnsAfterCompression)
        {
            var minKeep = userIndexes[^MinRetainedConversationTurnsAfterCompression];
            if (minKeep < keepFrom)
            {
                keepFrom = minKeep;
            }
        }

        return history.Skip(keepFrom).ToArray();
    }

    private static string? BuildApiMessageMetadata(ChatCompletionMessage message)
    {
        var metadata = new Dictionary<string, object?>();
        if (string.Equals(message.Role, "assistant", StringComparison.Ordinal) &&
            message.ToolCalls is { Count: > 0 })
        {
            metadata["tool_calls"] = message.ToolCalls.Select(call => new Dictionary<string, object?>
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = call.Name,
                    ["arguments"] = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson
                }
            }).ToArray();
        }

        if (string.Equals(message.Role, "tool", StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                metadata["tool_call_id"] = message.ToolCallId;
            }

            if (!string.IsNullOrWhiteSpace(message.ToolName))
            {
                metadata["tool_name"] = message.ToolName;
            }
        }

        return metadata.Count == 0
            ? null
            : JsonSerializer.Serialize(metadata, BridgeJson.SerializerOptions);
    }

    private static JsonElement BuildUsagePayload(JsonElement rawUsage, AvailableModelPayload model)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (rawUsage.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in rawUsage.EnumerateObject())
            {
                payload[property.Name] = JsonElementToObject(property.Value);
            }
        }

        var totalTokens = ReadNumeric(rawUsage, "total_tokens");
        var hitTokens = ReadNumeric(rawUsage, "prompt_cache_hit_tokens");
        var missTokens = ReadNumeric(rawUsage, "prompt_cache_miss_tokens");
        payload["prompt_cache_hit_tokens"] = hitTokens;
        payload["prompt_cache_miss_tokens"] = missTokens;
        payload["context_window"] = model.ContextWindow;
        if (model.ContextWindow > 0 && totalTokens > 0)
        {
            payload["usage_ratio"] = totalTokens / model.ContextWindow * 100.0;
        }

        if (hitTokens + missTokens > 0)
        {
            payload["cache_hit_ratio"] = hitTokens / (hitTokens + missTokens) * 100.0;
        }
        else
        {
            payload["cache_hit_ratio"] = 0;
        }

        return JsonSerializer.SerializeToElement(payload, BridgeJson.SerializerOptions);
    }

    private static object? JsonElementToObject(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.Clone()
        };
    }

    private static bool ShouldAutoCompress(JsonElement? usage)
    {
        if (usage is null || usage.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var ratio = ReadNumeric(usage.Value, "usage_ratio");
        return ratio >= AutoCompressionUsageRatio;
    }

    private static double ReadNumeric(JsonElement source, string propertyName)
    {
        if (source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        return property.TryGetDouble(out var value) ? value : 0;
    }

    private async ValueTask<CompressionRunResult> CompressSubagentContextAsync(
        SubagentRunRequest request,
        IReadOnlyList<ChatCompletionMessage> messages,
        int currentSeq,
        CancellationToken cancellationToken)
    {
        var seq = await EmitCompressionEventAsync(
            request.TurnId,
            currentSeq,
            phase: "compressing",
            summary: null,
            request.ToolId,
            cancellationToken);
        var summary = await GenerateCompressionSummaryAsync(
            request.ProviderName,
            request.ModelId,
            request.ReasoningEffort,
            messages,
            cancellationToken);
        var retained = RetainMessages(messages);
        var systemEnd = 0;
        while (systemEnd < messages.Count &&
            string.Equals(messages[systemEnd].Role, "system", StringComparison.Ordinal))
        {
            systemEnd++;
        }

        var compressed = new List<ChatCompletionMessage>(systemEnd + retained.Count + 2);
        compressed.AddRange(messages.Take(systemEnd));
        compressed.Add(new ChatCompletionMessage("user", CompressionReminder));
        compressed.Add(new ChatCompletionMessage("user", $"<system-reminder>\n{summary}\n</system-reminder>"));
        compressed.AddRange(retained);
        await PersistSubagentCompressionMarkerAsync(request, cancellationToken);
        seq = await EmitCompressionEventAsync(
            request.TurnId,
            seq,
            phase: "done",
            summary,
            request.ToolId,
            cancellationToken);
        return new CompressionRunResult(compressed, seq);
    }

    private IReadOnlyList<ChatToolDefinition> GetSubagentToolDefinitions(long novelId, string agentType)
    {
        if (_toolExecutor is null)
        {
            return [];
        }

        var allowed = SubagentAllowedTools(agentType);
        return _toolExecutor
            .GetToolDefinitions(novelId)
            .Where(tool => allowed.Contains(tool.Name))
            .ToArray();
    }

    private async ValueTask PersistSubagentAssistantMessageAsync(
        SubagentRunRequest request,
        string content,
        string thinkingText,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var session = FindSession(store, request.SessionId)
                ?? throw new InvalidOperationException($"Session '{request.SessionId}' disappeared during subagent run.");
            store.Messages.Add(CreateMessage(
                store,
                request.SessionId,
                request.TurnId,
                role: "assistant",
                content: content,
                thinkingContent: string.IsNullOrEmpty(thinkingText) ? null : thinkingText,
                version: session.ActiveVersion,
                toApi: false,
                toFrontend: true,
                eventType: null,
                agentType: request.AgentType,
                subTaskId: request.ToolId));
            session.UpdatedAt = UtcNow();
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask PersistSubagentToolRoundAsync(
        SubagentRunRequest request,
        string assistantText,
        string thinkingText,
        IReadOnlyList<ExecutedChatToolCall> toolOutputs,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var session = FindSession(store, request.SessionId)
                ?? throw new InvalidOperationException($"Session '{request.SessionId}' disappeared during subagent run.");
            store.Messages.Add(CreateMessage(
                store,
                request.SessionId,
                request.TurnId,
                role: "assistant",
                content: assistantText,
                thinkingContent: string.IsNullOrEmpty(thinkingText) ? null : thinkingText,
                version: session.ActiveVersion,
                toApi: false,
                toFrontend: true,
                eventType: null,
                agentType: request.AgentType,
                extraMetadata: BuildToolRoundMetadata(toolOutputs),
                subTaskId: request.ToolId));

            foreach (var output in toolOutputs)
            {
                store.Messages.Add(CreateMessage(
                    store,
                    request.SessionId,
                    request.TurnId,
                    role: "tool",
                    content: FormatToolResultJson(output.Result),
                    thinkingContent: null,
                    version: session.ActiveVersion,
                    toApi: false,
                    toFrontend: false,
                    eventType: null,
                    agentType: request.AgentType,
                    extraMetadata: JsonSerializer.Serialize(
                        new Dictionary<string, object?>
                        {
                            ["tool_call_id"] = output.Call.Id,
                            ["tool_name"] = output.Call.Name
                        },
                        BridgeJson.SerializerOptions),
                    subTaskId: request.ToolId));
            }

            session.UpdatedAt = UtcNow();
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask PersistSubagentCompressionMarkerAsync(
        SubagentRunRequest request,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var session = FindSession(store, request.SessionId)
                ?? throw new InvalidOperationException($"Session '{request.SessionId}' disappeared during subagent compression.");
            store.Messages.Add(CreateMessage(
                store,
                request.SessionId,
                request.TurnId,
                role: "system",
                content: string.Empty,
                thinkingContent: null,
                version: session.ActiveVersion,
                toApi: false,
                toFrontend: true,
                eventType: "compression",
                agentType: request.AgentType,
                subTaskId: request.ToolId));
            session.UpdatedAt = UtcNow();
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private void RegisterActiveChat(string sessionId, ActiveChatOperation active)
    {
        if (_activeChats.TryRemove(sessionId, out var previous))
        {
            previous.UserCancelled = true;
            previous.Cancellation.Cancel();
        }

        _activeChats[sessionId] = active;
    }

    private async ValueTask EnsureNovelExistsAsync(long novelId, CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        var novels = await _novels.GetNovelsAsync(cancellationToken);
        if (!novels.Any(novel => novel.Id == novelId))
        {
            throw new ArgumentException($"Novel '{novelId}' does not exist.", nameof(novelId));
        }
    }

    private async ValueTask CommitUserChangesAtTurnStartAsync(
        long novelId,
        int turnId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await _versionControl.CommitIfChangedAsync(
            novelId,
            $"turn {turnId.ToString(CultureInfo.InvariantCulture)}: user manual changes\n\nSession: {sessionId}",
            cancellationToken);
    }

    private async ValueTask CommitAiChangesAtTurnEndAsync(
        long novelId,
        int turnId,
        string sessionId,
        string modelName,
        CancellationToken cancellationToken)
    {
        var displayModelName = string.IsNullOrWhiteSpace(modelName) ? "unknown model" : modelName.Trim();
        await _versionControl.CommitIfChangedAsync(
            novelId,
            $"turn {turnId.ToString(CultureInfo.InvariantCulture)}: AI changes\n\nSession: {sessionId}\n\nCo-authored-by: {displayModelName}",
            cancellationToken);
    }

    private async ValueTask<AvailableModelPayload> GetConfiguredModelAsync(
        string providerName,
        string modelId,
        CancellationToken cancellationToken)
    {
        var key = $"{providerName}/{modelId}";
        var models = await _llm.GetModelsAsync(cancellationToken);
        var model = models.FirstOrDefault(model => string.Equals(model.Key, key, StringComparison.Ordinal));
        if (model is null)
        {
            throw new BridgeRequestException(
                BridgeErrorCodes.LlmProviderError,
                $"模型未找到或未配置: {key}",
                retryable: false,
                details: new { provider_name = providerName, model_id = modelId });
        }

        return model;
    }

    private async ValueTask<int> EmitAgentEventAsync(
        int turnId,
        int currentSeq,
        int type,
        string? data,
        JsonElement? usage,
        string? error,
        string? subTaskId,
        CancellationToken cancellationToken)
    {
        var seq = currentSeq;
        foreach (var chunk in SplitEventData(data))
        {
            seq++;
            await _events.EmitAsync(
                $"agent:{turnId.ToString(CultureInfo.InvariantCulture)}",
                new AgentEventPayload
                {
                    TurnId = turnId,
                    SubTaskId = string.IsNullOrWhiteSpace(subTaskId) ? null : subTaskId,
                    Seq = seq,
                    Type = type,
                    Data = chunk,
                    Usage = usage,
                    Error = error,
                    Timestamp = UtcNow()
                },
                cancellationToken);
        }

        if (data is null)
        {
            seq++;
            await _events.EmitAsync(
                $"agent:{turnId.ToString(CultureInfo.InvariantCulture)}",
                new AgentEventPayload
                {
                    TurnId = turnId,
                    SubTaskId = string.IsNullOrWhiteSpace(subTaskId) ? null : subTaskId,
                    Seq = seq,
                    Type = type,
                    Usage = usage,
                    Error = error,
                    Timestamp = UtcNow()
                },
                cancellationToken);
        }

        return seq;
    }

    private async ValueTask<ExecutedChatToolCall> ExecuteToolCallAsync(
        long novelId,
        string sessionId,
        int turnId,
        string providerName,
        string modelId,
        string reasoningEffort,
        int currentSeq,
        ChatToolCall call,
        IReadOnlySet<string>? allowedToolNames,
        string? subTaskId,
        string agentType,
        CancellationToken cancellationToken)
    {
        var args = ParseToolArguments(call.ArgumentsJson);
        var activeDisplay = ToolDisplay.For(call.Name, args, active: true);
        var seq = await EmitToolEventAsync(
            turnId,
            currentSeq,
            call,
            phase: "selected",
            args: null,
            success: null,
            error: null,
            display: activeDisplay,
            subTaskId,
            cancellationToken);
        seq = await EmitToolEventAsync(
            turnId,
            seq,
            call,
            phase: "executing",
            args,
            success: null,
            error: null,
            display: activeDisplay,
            subTaskId,
            cancellationToken);

        ChatToolExecutionResult result;
        if (allowedToolNames is not null && !allowedToolNames.Contains(call.Name))
        {
            result = ChatToolExecutionResult.Failure($"Tool '{call.Name}' is not allowed for {agentType} agent.");
        }
        else if (_toolExecutor is null)
        {
            result = ChatToolExecutionResult.Failure("Tool execution is not configured.");
        }
        else
        {
            try
            {
                result = await _toolExecutor.ExecuteAsync(
                    new ChatToolExecutionContext(
                        novelId,
                        sessionId,
                        turnId,
                        providerName,
                        modelId,
                        reasoningEffort,
                        seq),
                    call,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = ChatToolExecutionResult.Failure(ex.Message);
            }
        }

        if (result.LastSequence is { } lastSequence && lastSequence > seq)
        {
            seq = lastSequence;
        }

        var completedDisplay = ToolDisplay.For(call.Name, args, active: false);
        seq = await EmitToolEventAsync(
            turnId,
            seq,
            call,
            result.Success ? "completed" : "failed",
            args,
            result.Success,
            result.Error,
            completedDisplay,
            subTaskId,
            cancellationToken);

        return new ExecutedChatToolCall(
            call,
            result,
            completedDisplay.DisplayText,
            completedDisplay.ActivityKind,
            completedDisplay.Metadata,
            seq);
    }

    private async ValueTask<int> EmitToolEventAsync(
        int turnId,
        int currentSeq,
        ChatToolCall call,
        string phase,
        JsonElement? args,
        bool? success,
        string? error,
        ToolDisplay display,
        string? subTaskId,
        CancellationToken cancellationToken)
    {
        var seq = currentSeq + 1;
        await _events.EmitAsync(
            $"agent:{turnId.ToString(CultureInfo.InvariantCulture)}",
            new AgentEventPayload
            {
                TurnId = turnId,
                SubTaskId = string.IsNullOrWhiteSpace(subTaskId) ? null : subTaskId,
                Seq = seq,
                Type = 3,
                ToolName = call.Name,
                ToolId = call.Id,
                Phase = phase,
                ToolArgs = args,
                Success = success,
                Error = string.IsNullOrWhiteSpace(error) ? null : error,
                DisplayText = display.DisplayText,
                ActivityKind = display.ActivityKind,
                Metadata = display.Metadata,
                Timestamp = UtcNow()
            },
            cancellationToken);
        return seq;
    }

    private async ValueTask PersistToolRoundAsync(
        string sessionId,
        int turnId,
        string assistantText,
        string thinkingText,
        JsonElement? usage,
        IReadOnlyList<ExecutedChatToolCall> toolOutputs,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var session = FindSession(store, sessionId)
                ?? throw new InvalidOperationException($"Session '{sessionId}' disappeared during chat.");
            store.Messages.Add(CreateMessage(
                store,
                sessionId,
                turnId,
                role: "assistant",
                content: assistantText,
                thinkingContent: string.IsNullOrEmpty(thinkingText) ? null : thinkingText,
                version: session.ActiveVersion,
                toApi: true,
                toFrontend: true,
                eventType: null,
                agentType: "main",
                extraMetadata: BuildToolRoundMetadata(toolOutputs)));

            foreach (var output in toolOutputs)
            {
                store.Messages.Add(CreateMessage(
                    store,
                    sessionId,
                    turnId,
                    role: "tool",
                    content: FormatToolResultJson(output.Result),
                    thinkingContent: null,
                    version: session.ActiveVersion,
                    toApi: true,
                    toFrontend: true,
                    eventType: null,
                    agentType: "main",
                    extraMetadata: JsonSerializer.Serialize(
                        new Dictionary<string, object?>
                        {
                            ["tool_call_id"] = output.Call.Id,
                            ["tool_name"] = output.Call.Name
                        },
                        BridgeJson.SerializerOptions)));
            }

            if (usage is not null)
            {
                session.UsageJson = usage.Value.GetRawText();
            }

            session.UpdatedAt = UtcNow();
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static string BuildToolRoundMetadata(IReadOnlyList<ExecutedChatToolCall> outputs)
    {
        var toolCalls = outputs.Select(output => new Dictionary<string, object?>
        {
            ["id"] = output.Call.Id,
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = output.Call.Name,
                ["arguments"] = string.IsNullOrWhiteSpace(output.Call.ArgumentsJson) ? "{}" : output.Call.ArgumentsJson
            }
        }).ToArray();

        var toolDisplays = outputs.Select(output =>
        {
            var display = new Dictionary<string, object?>
            {
                ["tool_id"] = output.Call.Id,
                ["tool_name"] = output.Call.Name,
                ["display_text"] = output.DisplayText,
                ["activity_kind"] = output.ActivityKind,
                ["phase"] = output.Result.Success ? "completed" : "failed"
            };
            if (output.Metadata is not null)
            {
                display["metadata"] = output.Metadata;
            }

            return display;
        }).ToArray();

        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["tool_calls"] = toolCalls,
                ["tool_displays"] = toolDisplays
            },
            BridgeJson.SerializerOptions);
    }

    private static string FormatToolResultJson(ChatToolExecutionResult result)
    {
        var payload = new Dictionary<string, object?>
        {
            ["success"] = result.Success
        };
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            payload["error"] = result.Error;
        }

        if (result.Data is not null)
        {
            payload["data"] = result.Data.Value;
        }

        return JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions);
    }

    private static JsonElement ParseToolArguments(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : JsonSerializer.SerializeToElement(new { }, BridgeJson.SerializerOptions);
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { }, BridgeJson.SerializerOptions);
        }
    }

    private async ValueTask PersistAssistantMessageAsync(
        string sessionId,
        int turnId,
        string finalText,
        string thinkingText,
        JsonElement? usage,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var session = FindSession(store, sessionId)
                ?? throw new InvalidOperationException($"Session '{sessionId}' disappeared during chat.");
            store.Messages.Add(CreateMessage(
                store,
                sessionId,
                turnId,
                role: "assistant",
                content: finalText,
                thinkingContent: string.IsNullOrEmpty(thinkingText) ? null : thinkingText,
                version: session.ActiveVersion,
                toApi: true,
                toFrontend: true,
                eventType: null,
                agentType: "main"));

            if (usage is not null)
            {
                session.UsageJson = usage.Value.GetRawText();
            }

            session.UpdatedAt = UtcNow();
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask PersistSystemEventAsync(
        string sessionId,
        int turnId,
        string eventType,
        string content,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var session = FindSession(store, sessionId)
                ?? throw new InvalidOperationException($"Session '{sessionId}' disappeared during chat.");
            store.Messages.Add(CreateMessage(
                store,
                sessionId,
                turnId,
                role: "system",
                content: content,
                thinkingContent: null,
                version: session.ActiveVersion,
                toApi: false,
                toFrontend: true,
                eventType: eventType,
                agentType: "main"));
            session.UpdatedAt = UtcNow();
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask PersistAndEmitErrorAsync(
        string sessionId,
        int turnId,
        int seq,
        string message,
        CancellationToken cancellationToken)
    {
        await PersistSystemEventAsync(sessionId, turnId, "system_interrupted", message, cancellationToken);
        await EmitAgentEventAsync(
            turnId,
            seq,
            type: 5,
            data: null,
            usage: null,
            error: message,
            subTaskId: null,
            cancellationToken);
    }

    private async ValueTask GenerateAndPersistTitleAsync(
        string sessionId,
        ChatInputPayload input,
        CancellationToken cancellationToken)
    {
        var title = string.Empty;
        try
        {
            using var titleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            titleTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            title = await _completion.GenerateTextAsync(
                new ChatCompletionRequest(
                    input.ProviderName,
                    input.ModelId,
                    input.ReasoningEffort,
                    [
                        new ChatCompletionMessage(
                            "system",
                            "基于用户消息，生成一个不超过10个字的对话标题。只需输出标题文本，不要添加引号、标点或者额外解释。"),
                        new ChatCompletionMessage("user", input.Message)
                    ]),
                titleTimeout.Token);
        }
        catch
        {
            title = string.Empty;
        }

        title = NormalizeTitle(title);
        if (title.Length == 0)
        {
            title = NormalizeTitle(input.Message);
        }

        if (title.Length == 0)
        {
            return;
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var session = FindSession(store, sessionId)
                ?? throw new InvalidOperationException($"Session '{sessionId}' disappeared during title generation.");
            if (session.Title.Length > 0)
            {
                return;
            }

            session.Title = title;
            session.UpdatedAt = UtcNow();
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }

        await _events.EmitAsync(
            "chat:title_updated",
            new { session_id = sessionId, title },
            cancellationToken);
    }

    private async ValueTask<ChatSessionStoreDocument> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var path = await StorePathAsync(cancellationToken);
        if (!File.Exists(path))
        {
            var empty = new ChatSessionStoreDocument();
            await SaveAsync(empty, cancellationToken);
            return empty;
        }

        await using var stream = File.OpenRead(path);
        var store = await JsonSerializer.DeserializeAsync<ChatSessionStoreDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Chat session store is empty or malformed.");
        ValidateStore(store);
        return store;
    }

    private async ValueTask SaveAsync(ChatSessionStoreDocument store, CancellationToken cancellationToken)
    {
        ValidateStore(store);
        var path = await StorePathAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, store, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async ValueTask<string> StorePathAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(
            await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken),
            "sessions",
            "index.json");
    }

    private ChatSessionDocument CreateSession(ChatInputPayload input)
    {
        var now = UtcNow();
        return new ChatSessionDocument
        {
            SessionId = $"sess_{input.NovelId.ToString(CultureInfo.InvariantCulture)}_{now.ToUnixTimeMilliseconds():x}_{Guid.NewGuid():N}",
            NovelId = input.NovelId,
            Title = string.Empty,
            Model = input.ModelId,
            ReasoningEffort = input.ReasoningEffort,
            ActiveVersion = 1,
            LastTurnId = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private ChatMessageDocument CreateMessage(
        ChatSessionStoreDocument store,
        string sessionId,
        int turnId,
        string role,
        string content,
        string? thinkingContent,
        int version,
        bool toApi,
        bool toFrontend,
        string? eventType,
        string agentType,
        string? extraMetadata = null,
        string? subTaskId = null)
    {
        var id = AllocateMessageId(store);
        return new ChatMessageDocument
        {
            Id = id,
            SessionId = sessionId,
            TurnId = turnId,
            Role = role,
            Content = content,
            ThinkingContent = thinkingContent,
            Version = version,
            ToApi = toApi,
            ToFrontend = toFrontend,
            EventType = eventType,
            AgentType = agentType,
            SubTaskId = subTaskId,
            ExtraMetadata = extraMetadata,
            CreatedAt = UtcNow()
        };
    }

    private static long AllocateMessageId(ChatSessionStoreDocument store)
    {
        var maxExisting = store.Messages.Count == 0 ? 0 : store.Messages.Max(message => message.Id);
        var next = Math.Max(store.NextMessageId, maxExisting + 1);
        if (next <= 0 || next == long.MaxValue)
        {
            throw new InvalidOperationException("Chat message id allocation is exhausted.");
        }

        store.NextMessageId = checked(next + 1);
        return next;
    }

    private static ChatSessionDocument? FindSession(ChatSessionStoreDocument store, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return store.Sessions.SingleOrDefault(session =>
            string.Equals(session.SessionId, sessionId, StringComparison.Ordinal));
    }

    private static ChatInputPayload NormalizeChatInput(ChatInputPayload input)
    {
        return input with
        {
            SessionId = NormalizeSessionId(input.SessionId, allowEmpty: true),
            NovelId = NormalizeNovelId(input.NovelId),
            Message = NormalizeRequiredText(input.Message, nameof(input.Message), MaxMessageLength, allowLineBreaks: true),
            ProviderName = NormalizeProviderName(input.ProviderName),
            ModelId = NormalizeRequiredText(input.ModelId, nameof(input.ModelId), MaxModelIdLength, allowLineBreaks: false),
            ReasoningEffort = NormalizeOptionalText(input.ReasoningEffort, nameof(input.ReasoningEffort), MaxReasoningEffortLength)
        };
    }

    private static CompressInputPayload NormalizeCompressInput(CompressInputPayload input)
    {
        return input with
        {
            SessionId = NormalizeSessionId(input.SessionId, allowEmpty: false),
            ProviderName = NormalizeProviderName(input.ProviderName),
            ModelId = NormalizeRequiredText(input.ModelId, nameof(input.ModelId), MaxModelIdLength, allowLineBreaks: false)
        };
    }

    private static SubagentRunRequest NormalizeSubagentRunRequest(SubagentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateNovelId(request.NovelId);
        var agentType = NormalizeOptionalText(request.AgentType, nameof(request.AgentType), 32);
        if (agentType is not ("memory" or "review"))
        {
            throw new ArgumentException("Subagent agent_type must be memory or review.", nameof(request));
        }

        return request with
        {
            SessionId = NormalizeSessionId(request.SessionId, allowEmpty: false),
            ToolId = NormalizeRequiredText(request.ToolId, nameof(request.ToolId), MaxSessionIdLength, allowLineBreaks: false),
            AgentType = agentType,
            Instruction = NormalizeRequiredText(request.Instruction, nameof(request.Instruction), MaxMessageLength, allowLineBreaks: true),
            ProviderName = NormalizeProviderName(request.ProviderName),
            ModelId = NormalizeRequiredText(request.ModelId, nameof(request.ModelId), MaxModelIdLength, allowLineBreaks: false),
            ReasoningEffort = NormalizeOptionalText(request.ReasoningEffort, nameof(request.ReasoningEffort), MaxReasoningEffortLength),
            StartSequence = Math.Max(0, request.StartSequence)
        };
    }

    private static IReadOnlySet<string> SubagentAllowedTools(string agentType)
    {
        return agentType switch
        {
            "memory" => MemorySubagentTools,
            "review" => ReviewSubagentTools,
            _ => throw new ArgumentException("Subagent agent_type must be memory or review.", nameof(agentType))
        };
    }

    private static string SubagentIdentityPrompt(string agentType)
    {
        return agentType switch
        {
            "memory" => MemorySubagentPrompt,
            "review" => ReviewSubagentPrompt,
            _ => throw new ArgumentException("Subagent agent_type must be memory or review.", nameof(agentType))
        };
    }

    private static string TrimLongState(string content)
    {
        const int maxStateChars = 20_000;
        var normalized = content.Trim();
        return normalized.Length <= maxStateChars
            ? normalized
            : normalized[..maxStateChars] + "\n\n[goink.md 内容过长，已截断。可用 read 工具读取完整内容。]";
    }

    private static string NormalizeSessionId(string? value, bool allowEmpty)
    {
        var normalized = NormalizeOptionalText(value, nameof(value), MaxSessionIdLength);
        if (!allowEmpty && normalized.Length == 0)
        {
            throw new ArgumentException("Session id is required.", nameof(value));
        }

        if (normalized.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch)))
        {
            throw new ArgumentException("Session id must not contain whitespace or control characters.", nameof(value));
        }

        return normalized;
    }

    private static string NormalizeProviderName(string? value)
    {
        var providerName = NormalizeRequiredText(
            value,
            nameof(value),
            MaxProviderNameLength,
            allowLineBreaks: false).ToLowerInvariant();
        if (providerName.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            throw new ArgumentException("Provider name may only contain letters, digits, hyphen, underscore, and dot.", nameof(value));
        }

        return providerName;
    }

    private static string NormalizeRequiredText(
        string? value,
        string name,
        int maxLength,
        bool allowLineBreaks)
    {
        var normalized = NormalizeOptionalText(value, name, maxLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value must be a non-empty string.", name);
        }

        if (normalized.Any(ch => IsDisallowedControl(ch, allowLineBreaks)))
        {
            throw new ArgumentException("Value must not contain unsupported control characters.", name);
        }

        return normalized;
    }

    private static string NormalizeOptionalText(string? value, string name, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(name, normalized.Length, $"Value must be at most {maxLength} characters.");
        }

        if (normalized.Any(ch => IsDisallowedControl(ch, allowLineBreaks: true)))
        {
            throw new ArgumentException("Value must not contain unsupported control characters.", name);
        }

        return normalized;
    }

    private static bool IsDisallowedControl(char value, bool allowLineBreaks)
    {
        return char.IsControl(value) &&
            (!allowLineBreaks || value is not ('\r' or '\n' or '\t'));
    }

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }
    }

    private static long NormalizeNovelId(long novelId)
    {
        ValidateNovelId(novelId);
        return novelId;
    }

    private static string NormalizeTitle(string value)
    {
        var title = value.Trim().Trim('"', '\'', '“', '”', '‘', '’', '。', '，', ',', '.', '!', '！', '?', '？', ':', '：');
        if (title.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var rune in title.EnumerateRunes())
        {
            if (builder.Length >= MaxTitleLength)
            {
                break;
            }

            builder.Append(rune);
        }

        return builder.ToString();
    }

    private static IEnumerable<string?> SplitEventData(string? data)
    {
        if (data is null)
        {
            yield break;
        }

        if (data.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        for (var index = 0; index < data.Length; index += MaxEventChunkChars)
        {
            yield return data.Substring(index, Math.Min(MaxEventChunkChars, data.Length - index));
        }
    }

    private DateTimeOffset UtcNow()
    {
        return _timeProvider.GetUtcNow();
    }

    private static string FormatLegacyTime(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static SessionMetaPayload ToSessionMetaPayload(ChatSessionDocument session)
    {
        return new SessionMetaPayload(
            session.SessionId,
            session.Title,
            FormatLegacyTime(session.UpdatedAt));
    }

    private static SessionDetailPayload ToSessionDetailPayload(ChatSessionDocument session)
    {
        return new SessionDetailPayload(
            session.SessionId,
            session.NovelId,
            session.Title,
            session.Model,
            session.ReasoningEffort,
            session.ActiveVersion,
            session.LastTurnId,
            ParseUsage(session.UsageJson),
            FormatLegacyTime(session.CreatedAt),
            FormatLegacyTime(session.UpdatedAt));
    }

    private static SessionMessagePayload ToMessagePayload(ChatMessageDocument message)
    {
        return new SessionMessagePayload(
            message.Id,
            message.SessionId,
            message.TurnId,
            message.Role,
            message.Content,
            NullIfEmpty(message.ThinkingContent),
            message.TokenCount,
            NullIfEmpty(message.ExtraMetadata),
            message.Version,
            message.ToApi,
            message.ToFrontend,
            NullIfEmpty(message.EventType),
            message.AgentType,
            NullIfEmpty(message.SubTaskId),
            message.CreatedAt);
    }

    private static ChatCompletionMessage ToCompletionMessage(ChatMessageDocument message)
    {
        var metadata = ParseExtraMetadata(message.ExtraMetadata);
        return new ChatCompletionMessage(
            message.Role,
            message.Content,
            NullIfEmpty(message.ThinkingContent),
            ParseToolCalls(metadata),
            ReadString(metadata, "tool_call_id"),
            ReadString(metadata, "tool_name"));
    }

    private static JsonElement? ParseExtraMetadata(string? extraMetadata)
    {
        if (string.IsNullOrWhiteSpace(extraMetadata))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(extraMetadata);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ChatToolCall>? ParseToolCalls(JsonElement? metadata)
    {
        if (metadata is null ||
            !metadata.Value.TryGetProperty("tool_calls", out var calls) ||
            calls.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var results = new List<ChatToolCall>();
        foreach (var call in calls.EnumerateArray())
        {
            if (!call.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                !call.TryGetProperty("function", out var function) ||
                function.ValueKind != JsonValueKind.Object ||
                !function.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var arguments = "{}";
            if (function.TryGetProperty("arguments", out var args))
            {
                arguments = args.ValueKind == JsonValueKind.String
                    ? args.GetString() ?? "{}"
                    : args.GetRawText();
            }

            results.Add(new ChatToolCall(
                id.GetString() ?? string.Empty,
                name.GetString() ?? string.Empty,
                string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments));
        }

        return results.Count == 0 ? null : results;
    }

    private static string? ReadString(JsonElement? metadata, string propertyName)
    {
        return metadata is not null &&
            metadata.Value.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? NullIfEmpty(value.GetString())
                : null;
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static JsonElement? ParseUsage(string? usageJson)
    {
        if (string.IsNullOrWhiteSpace(usageJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(usageJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateStore(ChatSessionStoreDocument store)
    {
        if (store.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported chat session store version '{store.Version}'.");
        }

        if (store.NextMessageId <= 0)
        {
            throw new InvalidOperationException("Chat session store next_message_id must be positive.");
        }

        if (store.Sessions.Select(session => session.SessionId).Distinct(StringComparer.Ordinal).Count() != store.Sessions.Count)
        {
            throw new InvalidOperationException("Chat session store contains duplicate session ids.");
        }

        if (store.Messages.Any(message => message.Id <= 0))
        {
            throw new InvalidOperationException("Chat session store contains an invalid message id.");
        }

        if (store.Messages.Select(message => message.Id).Distinct().Count() != store.Messages.Count)
        {
            throw new InvalidOperationException("Chat session store contains duplicate message ids.");
        }
    }

    private sealed record ExecutedChatToolCall(
        ChatToolCall Call,
        ChatToolExecutionResult Result,
        string DisplayText,
        string ActivityKind,
        IReadOnlyDictionary<string, object?>? Metadata,
        int Seq);

    private sealed record CompressionRunResult(
        IReadOnlyList<ChatCompletionMessage> Messages,
        int Seq);

    private sealed record SlashInjection(string InjectContent, string SkillName)
    {
        public static SlashInjection Empty { get; } = new(string.Empty, string.Empty);
    }

    private sealed record ResolvedSkillDocument(
        string Name,
        string Description,
        string Mode,
        string Source,
        string Content,
        string RawContent);

    private sealed record ToolDisplay(
        string DisplayText,
        string ActivityKind,
        IReadOnlyDictionary<string, object?>? Metadata)
    {
        public static ToolDisplay For(string toolName, JsonElement? args, bool active)
        {
            var (text, kind, metadata) = toolName switch
            {
                "search_story_memory" => ("搜索故事记忆", "memory", null),
                "read" => (ReadPathDisplay(args), "view", null),
                "edit" => (EditPathDisplay(args), "write", null),
                "run_subagent" => SubagentDisplay(args),
                _ => (toolName, "general", null)
            };
            return new ToolDisplay(active ? $"正在{text}" : text, kind, metadata);
        }

        private static (string Text, string Kind, IReadOnlyDictionary<string, object?> Metadata) SubagentDisplay(JsonElement? args)
        {
            var agentType = ReadString(args, "agent_type");
            var text = agentType switch
            {
                "review" => "审核章节内容",
                "memory" => "探索故事记忆",
                _ => "调度AI子任务"
            };
            return (text, "plan", new Dictionary<string, object?> { ["agent_type"] = agentType });
        }

        private static string ReadPathDisplay(JsonElement? args)
        {
            var path = ReadString(args, "path");
            return path switch
            {
                "goink.md" => "查看 故事状态",
                _ when path.StartsWith("chapters/", StringComparison.Ordinal) =>
                    $"查看 第{ParsePathNumber(path, "chapters/").ToString(CultureInfo.InvariantCulture)}章",
                _ when path.StartsWith("outlines/", StringComparison.Ordinal) =>
                    $"查看 第{ParsePathNumber(path, "outlines/").ToString(CultureInfo.InvariantCulture)}章大纲",
                _ => "读取文件内容"
            };
        }

        private static string EditPathDisplay(JsonElement? args)
        {
            var path = ReadString(args, "path");
            return path switch
            {
                "goink.md" => "编辑 故事状态",
                _ when path.StartsWith("chapters/", StringComparison.Ordinal) =>
                    $"编辑 第{ParsePathNumber(path, "chapters/").ToString(CultureInfo.InvariantCulture)}章",
                _ when path.StartsWith("outlines/", StringComparison.Ordinal) =>
                    $"编辑 第{ParsePathNumber(path, "outlines/").ToString(CultureInfo.InvariantCulture)}章大纲",
                _ => "编辑文件内容"
            };
        }

        private static string ReadString(JsonElement? args, string propertyName)
        {
            return args is not null &&
                args.Value.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;
        }

        private static int ParsePathNumber(string path, string prefix)
        {
            var fileName = path[prefix.Length..];
            var dot = fileName.IndexOf('.', StringComparison.Ordinal);
            return dot > 0 &&
                int.TryParse(fileName.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    ? number
                    : 0;
        }
    }

    private sealed class ActiveChatOperation
    {
        public ActiveChatOperation(CancellationTokenSource cancellation)
        {
            Cancellation = cancellation;
        }

        public CancellationTokenSource Cancellation { get; }

        public bool UserCancelled { get; set; }
    }

    private sealed class ChatSessionStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_message_id")]
        public long NextMessageId { get; set; } = 1;

        [JsonPropertyName("sessions")]
        public List<ChatSessionDocument> Sessions { get; set; } = [];

        [JsonPropertyName("messages")]
        public List<ChatMessageDocument> Messages { get; set; } = [];
    }

    private sealed class ChatSessionDocument
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("novel_id")]
        public long NovelId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("reasoning_effort")]
        public string ReasoningEffort { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("pending_changes")]
        public string PendingChanges { get; set; } = string.Empty;

        [JsonPropertyName("extra_metadata")]
        public string ExtraMetadata { get; set; } = string.Empty;

        [JsonPropertyName("active_version")]
        public int ActiveVersion { get; set; } = 1;

        [JsonPropertyName("last_turn_id")]
        public int LastTurnId { get; set; }

        [JsonPropertyName("usage")]
        public string UsageJson { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class ChatMessageDocument
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("turn_id")]
        public int TurnId { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("thinking_content")]
        public string? ThinkingContent { get; set; }

        [JsonPropertyName("token_count")]
        public int TokenCount { get; set; }

        [JsonPropertyName("extra_metadata")]
        public string? ExtraMetadata { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("to_api")]
        public bool ToApi { get; set; }

        [JsonPropertyName("to_frontend")]
        public bool ToFrontend { get; set; }

        [JsonPropertyName("event_type")]
        public string? EventType { get; set; }

        [JsonPropertyName("agent_type")]
        public string AgentType { get; set; } = "main";

        [JsonPropertyName("sub_task_id")]
        public string? SubTaskId { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }
    }
}
