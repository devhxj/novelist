using Novelist.Agent;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.App.Desktop;

public static class DesktopBridgeComposition
{
    public static PhotinoWebMessageBridge CreateBridge(
        IPhotinoWindow window,
        AppInitializationOptions? appOptions = null,
        IExternalUrlOpener? externalUrlOpener = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        var options = appOptions ?? new AppInitializationOptions { EnableLegacyMigration = true };
        var settingsService = new FileSystemAppSettingsService(options);
        var versionControl = new GitVersionControlService(options, settings: settingsService);
        var novelService = new FileSystemNovelService(options, settingsService, versionControl);
        var writingService = new FileSystemWritingStatisticsService(options, novelService);
        var ragRefreshNotifier = new DeferredRagIndexRefreshNotifier();
        var chapterContentService = new FileSystemChapterContentService(
            options,
            novelService,
            writingService,
            ragRefreshNotifier,
            versionControl);
        var preferenceService = new FileSystemPreferenceService(options, novelService);
        var worldService = new FileSystemWorldEntityService(options, novelService);
        var planningService = new FileSystemPlanningService(options, novelService);
        var llmService = new FileSystemLlmConfigurationService(options);
        var chatCompletionClient = new StandardChatCompletionClient(llmService);
        var sqliteVecResolver = new PackagedSqliteVecExtensionResolver();
        var sqliteVecProvider = new SqliteVecTableProvisioner(sqliteVecResolver);
        var embeddingClient = new HybridEmbeddingClient();
        var embeddingService = new FileSystemEmbeddingSettingsService(
            options,
            embeddingClient,
            sqliteVecResolver);
        var ragIndexService = new SqliteRagIndexService(
            options,
            novelService,
            chapterContentService,
            embeddingService,
            embeddingClient,
            sqliteVecProvider,
            sqliteVecProvider);
        ragRefreshNotifier.SetTarget(ragIndexService);
        var eventSink = new PhotinoBridgeEventSink(window);
        var approvalCoordinator = new ToolApprovalCoordinator(eventSink);
        var skillService = new FileSystemSkillCatalogService(options, novelService, llmService);
        var novelImportRunService = new FileSystemNovelImportService(
            options,
            novelService: novelService,
            versionControl: versionControl,
            writingDeltaRecorder: writingService,
            ragRefreshNotifier: ragRefreshNotifier,
            eventSink: eventSink);
        var novelImportRecoveryService = new FileSystemNovelImportRecoveryService(
            options,
            novelService);
        var styleSampleService = new FileSystemStyleSampleService(options, novelService);
        var styleSkillExtractionService = new FileSystemStyleSkillExtractionService(
            options,
            novelService,
            styleSampleService,
            chatCompletionClient,
            eventSink);
        var updateCheckService = new GitHubUpdateCheckService(
            options,
            settingsService);
        var narrativePatternService = new FileSystemNarrativePatternExtractionService(
            options,
            novelService,
            chapterContentService,
            chatCompletionClient,
            llmService,
            eventSink);
        var searchService = new FileSystemWorkspaceSearchService(
            options,
            novelService,
            chapterContentService,
            worldService,
            planningService,
            ragIndexService,
            ragIndexService);
        var storyMemoryService = new RagStoryMemorySearchService(
            options,
            novelService,
            chapterContentService,
            ragIndexService,
            ragIndexService);
        var referenceAnchorService = new SqliteReferenceAnchorService(
            options,
            novelService,
            embeddingService,
            embeddingClient,
            sqliteVecProvider);
        var referenceStyleProfileService = new SqliteReferenceStyleProfileService(
            options,
            novelService,
            new ReferenceStyleChatCompletionLlmAnalyzer(settingsService, chatCompletionClient));
        var referenceAnchoredDraftService = new SqliteReferenceAnchoredDraftService(
            options,
            novelService,
            planningService,
            referenceAnchorService,
            new AiReferenceBlueprintRevisionProposalProvider(settingsService, chatCompletionClient));
        var webFetchService = new HttpWebFetchService();
        var webSearchService = new DeepSeekWebSearchService(llmService);
        var subagentRunner = new DeferredSubagentRunner();
        var chatToolExecutor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            storyMemoryService,
            chapterContentService,
            approvalCoordinator,
            eventSink,
            subagentRunner,
            preferenceService,
            worldService,
            planningService,
            webFetchService,
            webSearchService,
            referenceAnchorService,
            referenceAnchoredDraftService,
            referenceStyleProfiles: referenceStyleProfileService));
        var chatService = new FileSystemChatSessionService(
            options,
            novelService,
            settingsService,
            llmService,
            chatCompletionClient,
            eventSink,
            approvalCoordinator,
            chatToolExecutor,
            chapterContentService,
            versionControl);
        subagentRunner.SetTarget(chatService);
        var exportService = new FileSystemNovelExportService(
            novelService,
            chapterContentService,
            settingsService,
            new PhotinoNovelExportDestinationPicker(window));
        var referenceSourceFilePicker = new PhotinoReferenceSourceFilePicker(window);
        var novelImportFilePicker = new PhotinoNovelImportFilePicker(window);
        var dispatcher = new BridgeDispatcher()
            .RegisterDefaultNovelistHandlers(new PhotinoBridgeRuntimeHost(
                window,
                externalUrlOpener ?? new SystemExternalUrlOpener()))
            .RegisterAppInitializationHandlers(new FileSystemAppInitializationService(
                options,
                importRecovery: novelImportRecoveryService))
            .RegisterAppSettingsHandlers(settingsService)
            .RegisterNovelHandlers(novelService)
            .RegisterChapterContentHandlers(chapterContentService)
            .RegisterPreferenceHandlers(preferenceService)
            .RegisterWorldEntityHandlers(worldService)
            .RegisterPlanningHandlers(planningService)
            .RegisterLlmConfigurationHandlers(llmService)
            .RegisterEmbeddingConfigurationHandlers(embeddingService)
            .RegisterWorkspaceUtilityHandlers(
                skillService,
                searchService,
                exportService,
                writingService,
                storyMemoryService,
                referenceSourceFilePicker)
            .RegisterNovelImportHandlers(
                novelImportRunService,
                novelImportFilePicker,
                novelImportRecoveryService)
            .RegisterStyleSampleHandlers(styleSampleService, styleSkillExtractionService)
            .RegisterUpdateCheckHandlers(updateCheckService)
            .RegisterNarrativePatternHandlers(narrativePatternService)
            .RegisterGitHistoryHandlers(versionControl)
            .RegisterReferenceAnchorHandlers(referenceAnchorService)
            .RegisterReferenceStyleProfileHandlers(referenceStyleProfileService)
            .RegisterReferenceAnchoredDraftHandlers(referenceAnchoredDraftService)
            .RegisterApprovalHandlers(approvalCoordinator)
            .RegisterChatSessionHandlers(chatService);
        return new PhotinoWebMessageBridge(dispatcher, window);
    }

    private sealed class DeferredRagIndexRefreshNotifier : IRagIndexRefreshNotifier
    {
        private IRagIndexRefreshNotifier? _target;

        public void SetTarget(IRagIndexRefreshNotifier target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
        }

        public ValueTask MarkNovelIndexStaleAsync(
            long novelId,
            string reason,
            CancellationToken cancellationToken)
        {
            var target = _target;
            return target is null
                ? ValueTask.CompletedTask
                : target.MarkNovelIndexStaleAsync(novelId, reason, cancellationToken);
        }
    }

    private sealed class DeferredSubagentRunner : ISubagentRunner
    {
        private ISubagentRunner? _target;

        public void SetTarget(ISubagentRunner target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
        }

        public ValueTask<SubagentRunResult> RunAsync(
            SubagentRunRequest request,
            CancellationToken cancellationToken)
        {
            var target = _target ?? throw new InvalidOperationException("Subagent runner is not configured.");
            return target.RunAsync(request, cancellationToken);
        }
    }
}
