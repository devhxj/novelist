using System.Text.Json;
using Novelist.App.Desktop;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class PhotinoReferenceWorkflowSmokeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DesktopCompositionStartsAndStopsMaterializationWorkerWithInitialization()
    {
        var options = CreateOptions();
        var window = new RecordingWindow();
        var runtime = DesktopBridgeComposition.CreateRuntime(window, options);
        var disposed = false;

        try
        {
            Assert.False(runtime.MaterializationWorker.IsRunning);

            await SendAsync(runtime.Bridge, window, "Initialize", options.DefaultDataDirectory);

            Assert.True(runtime.MaterializationWorker.IsRunning);

            await runtime.DisposeAsync();
            disposed = true;
            Assert.False(runtime.MaterializationWorker.IsRunning);
        }
        finally
        {
            if (!disposed)
            {
                await runtime.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task DesktopCompositionRegistersAndConfirmsAReferenceSourceThroughPhotinoBridge()
    {
        var options = CreateOptions();
        var window = new RecordingWindow();
        await using var runtime = DesktopBridgeComposition.CreateRuntime(window, options);
        var bridge = runtime.Bridge;

        await SendAsync(bridge, window, "Initialize", options.DefaultDataDirectory);
        var novel = await SendAsync(bridge, window, "CreateNovel", new
        {
            title = "桌面参考烟测",
            description = "",
            genre = "悬疑"
        });
        var novelId = novel.GetProperty("id").GetInt64();
        var sourcePath = CreateSourceFile(
            "desktop-reference.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。

            # 第二章

            林岚没有回答，喉咙却发紧。
            """);

        var anchor = await SendAsync(bridge, window, "RegisterReferenceMaterializationSource", new
        {
            novel_id = novelId,
            title = "雨夜参考",
            author = (string?)null,
            source_path = sourcePath,
            source_kind = "markdown",
            license_status = "user_provided"
        });
        var anchorId = anchor.GetProperty("anchor_id").GetInt64();

        Assert.Equal(ReferenceAnchorBuildStates.PendingSplit, anchor.GetProperty("status").GetString());
        Assert.Equal(string.Empty, anchor.GetProperty("source_path").GetString());
        Assert.False(string.IsNullOrWhiteSpace(anchor.GetProperty("source_file_hash").GetString()));

        var split = await SendAsync(bridge, window, "PreviewReferenceChapterSplit", new
        {
            novel_id = novelId,
            anchor_id = anchorId,
            delimiter_template = "# {title}"
        });
        Assert.Equal(2, split.GetProperty("chapter_count").GetInt32());

        await SendAsync(bridge, window, "ConfirmReferenceChapterSplit", new
        {
            novel_id = novelId,
            anchor_id = anchorId,
            split_profile_id = split.GetProperty("split_profile_id").GetString()
        });
        var anchors = await SendAsync(bridge, window, "GetReferenceAnchors", novelId);

        var listed = Assert.Single(anchors.EnumerateArray());
        Assert.Equal(ReferenceAnchorBuildStates.PendingMaterialization, listed.GetProperty("status").GetString());
        Assert.Equal(string.Empty, listed.GetProperty("source_path").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data")
        };
    }

    private string CreateSourceFile(string fileName, string content)
    {
        var sourceDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var path = Path.Combine(sourceDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static async ValueTask<JsonElement> SendAsync(
        PhotinoWebMessageBridge bridge,
        RecordingWindow window,
        string method,
        params object?[] args)
    {
        var requestId = "req_" + method + "_" + Guid.NewGuid().ToString("N");
        window.RequestedMethods.Add(method);
        await bridge.ReceiveAsync(JsonSerializer.Serialize(
            new
            {
                kind = "request",
                id = requestId,
                method,
                payload = new { args }
            },
            BridgeJson.SerializerOptions));
        Assert.NotEmpty(window.SentMessages);
        var message = window.SentMessages[^1];
        using var response = JsonDocument.Parse(message);
        Assert.Equal(requestId, response.RootElement.GetProperty("id").GetString());
        Assert.True(
            response.RootElement.GetProperty("ok").GetBoolean(),
            response.RootElement.TryGetProperty("error", out var error)
                ? error.GetRawText()
                : message);
        return response.RootElement.GetProperty("result").Clone();
    }

    private sealed class RecordingWindow : IPhotinoWindow
    {
        public List<string> SentMessages { get; } = [];

        public List<string> RequestedMethods { get; } = [];

        public bool Minimized { get; private set; }

        public bool Maximized { get; private set; }

        public bool Closed { get; private set; }

        public void WaitForClose()
        {
        }

        public void SendWebMessage(string message)
        {
            SentMessages.Add(message);
        }

        public ValueTask<string?> ShowSaveFileAsync(
            string title,
            string defaultPath,
            IReadOnlyList<NovelExportFileFilter> filters,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask<string?> ShowOpenFileAsync(
            string title,
            string defaultPath,
            IReadOnlyList<WorkspaceFileFilter> filters,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(null);
        }

        public void Minimize()
        {
            Minimized = true;
        }

        public void ToggleMaximize()
        {
            Maximized = !Maximized;
        }

        public bool IsMaximized()
        {
            return Maximized;
        }

        public PhotinoWindowBounds GetBounds()
        {
            return new PhotinoWindowBounds(160, 120, 1280, 840, Maximized);
        }

        public void Close()
        {
            Closed = true;
        }
    }
}
