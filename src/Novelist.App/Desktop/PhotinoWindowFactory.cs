using System.Drawing;
using Novelist.Agent;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;
using Photino.NET;

namespace Novelist.App.Desktop;

public sealed class PhotinoWindowFactory : IPhotinoWindowFactory
{
    public IPhotinoWindow Create(PhotinoWindowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var window = new PhotinoWindow();
        var adapter = new PhotinoWindowAdapter(window);
        var appOptions = new AppInitializationOptions();
        var settingsService = new FileSystemAppSettingsService(appOptions);
        var novelService = new FileSystemNovelService(appOptions, settingsService);
        var writingService = new FileSystemWritingStatisticsService(appOptions, novelService);
        var ragRefreshNotifier = new DeferredRagIndexRefreshNotifier();
        var chapterContentService = new FileSystemChapterContentService(
            appOptions,
            novelService,
            writingService,
            ragRefreshNotifier);
        var worldService = new FileSystemWorldEntityService(appOptions, novelService);
        var planningService = new FileSystemPlanningService(appOptions, novelService);
        var llmService = new FileSystemLlmConfigurationService(appOptions);
        var sqliteVecResolver = new PackagedSqliteVecExtensionResolver();
        var sqliteVecProvider = new SqliteVecTableProvisioner(sqliteVecResolver);
        var embeddingClient = new StandardEmbeddingClient();
        var embeddingService = new FileSystemEmbeddingSettingsService(
            appOptions,
            embeddingClient,
            sqliteVecResolver);
        var ragIndexService = new SqliteRagIndexService(
            appOptions,
            novelService,
            chapterContentService,
            embeddingService,
            embeddingClient,
            sqliteVecProvider,
            sqliteVecProvider);
        ragRefreshNotifier.SetTarget(ragIndexService);
        var eventSink = new PhotinoBridgeEventSink(adapter);
        var approvalCoordinator = new ToolApprovalCoordinator(eventSink);
        var skillService = new FileSystemSkillCatalogService(appOptions, novelService, llmService);
        var searchService = new FileSystemWorkspaceSearchService(
            appOptions,
            novelService,
            chapterContentService,
            worldService,
            planningService,
            ragIndexService,
            ragIndexService);
        var storyMemoryService = new RagStoryMemorySearchService(
            appOptions,
            novelService,
            chapterContentService,
            ragIndexService,
            ragIndexService);
        var chatToolExecutor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            storyMemoryService,
            chapterContentService,
            approvalCoordinator,
            eventSink));
        var chatService = new FileSystemChatSessionService(
            appOptions,
            novelService,
            settingsService,
            llmService,
            new StandardChatCompletionClient(llmService),
            eventSink,
            approvalCoordinator,
            chatToolExecutor);
        var exportService = new FileSystemNovelExportService(
            novelService,
            chapterContentService,
            settingsService,
            new PhotinoNovelExportDestinationPicker(adapter));
        var dispatcher = new BridgeDispatcher()
            .RegisterDefaultNovelistHandlers(new PhotinoBridgeRuntimeHost(adapter, new SystemExternalUrlOpener()))
            .RegisterAppInitializationHandlers(new FileSystemAppInitializationService(appOptions))
            .RegisterAppSettingsHandlers(settingsService)
            .RegisterNovelHandlers(novelService)
            .RegisterChapterContentHandlers(chapterContentService)
            .RegisterPreferenceHandlers(new FileSystemPreferenceService(appOptions, novelService))
            .RegisterWorldEntityHandlers(worldService)
            .RegisterPlanningHandlers(planningService)
            .RegisterLlmConfigurationHandlers(llmService)
            .RegisterEmbeddingConfigurationHandlers(embeddingService)
            .RegisterWorkspaceUtilityHandlers(skillService, searchService, exportService, writingService, storyMemoryService)
            .RegisterApprovalHandlers(approvalCoordinator)
            .RegisterChatSessionHandlers(chatService);
        var bridge = new PhotinoWebMessageBridge(dispatcher, adapter);

        window
            .SetTitle(settings.Title)
            .SetUseOsDefaultSize(false)
            .SetSize(new Size(settings.Width, settings.Height))
            .Center()
            .SetResizable(true)
            .RegisterWebMessageReceivedHandler((_, message) => bridge.Post(message))
            .Load(settings.StartUrl);

        return adapter;
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

    private sealed class PhotinoWindowAdapter : IPhotinoWindow
    {
        private readonly PhotinoWindow _window;

        public PhotinoWindowAdapter(PhotinoWindow window)
        {
            _window = window;
        }

        public void WaitForClose()
        {
            _window.WaitForClose();
        }

        public void SendWebMessage(string message)
        {
            _window.SendWebMessage(message);
        }

        public async ValueTask<string?> ShowSaveFileAsync(
            string title,
            string defaultPath,
            IReadOnlyList<Novelist.Core.App.NovelExportFileFilter> filters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var photinoFilters = filters
                .Select(filter => (filter.DisplayName, new[] { filter.Pattern }))
                .ToArray();
            var path = await _window.ShowSaveFileAsync(title, defaultPath, photinoFilters);
            cancellationToken.ThrowIfCancellationRequested();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        public void Minimize()
        {
            _window.Minimized = true;
        }

        public void ToggleMaximize()
        {
            _window.Maximized = !_window.Maximized;
        }

        public bool IsMaximized()
        {
            return _window.Maximized;
        }

        public void Close()
        {
            _window.Close();
        }
    }
}
