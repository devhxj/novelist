using System.Text.Json;
using Novelist.App.Desktop;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Contracts.Bridge;

namespace Novelist.IntegrationTests;

public sealed class PhotinoWebMessageBridgeTests
{
    [Fact]
    public async Task ReceiveAsyncSendsDispatcherResponseToWindow()
    {
        var dispatcher = new BridgeDispatcher();
        dispatcher.Register("Echo", (_, _) => ValueTask.FromResult<object?>(new { ok = true }));
        var window = new RecordingWindow();
        var bridge = new PhotinoWebMessageBridge(dispatcher, window);

        await bridge.ReceiveAsync("""
            {
              "kind": "request",
              "id": "req_echo",
              "method": "Echo",
              "payload": {}
            }
            """);

        Assert.Single(window.SentMessages);
        using var json = JsonDocument.Parse(window.SentMessages[0]);
        Assert.Equal("req_echo", json.RootElement.GetProperty("id").GetString());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ReceiveAsyncDoesNotSendMessageForCancelEnvelope()
    {
        var window = new RecordingWindow();
        var bridge = new PhotinoWebMessageBridge(new BridgeDispatcher(), window);

        await bridge.ReceiveAsync("""
            {
              "kind": "cancel",
              "id": "req_cancel"
            }
            """);

        Assert.Empty(window.SentMessages);
    }

    [Fact]
    public async Task ReceiveAsyncCancelsRunningRequestWhenCancelEnvelopeArrives()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new BridgeDispatcher();
        dispatcher.Register("Block", async (_, cancellationToken) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                observedCancellation.SetResult();
                throw;
            }

            return null;
        });
        var window = new RecordingWindow();
        var bridge = new PhotinoWebMessageBridge(dispatcher, window);

        var requestTask = bridge.ReceiveAsync("""
            {
              "kind": "request",
              "id": "req_block",
              "method": "Block",
              "payload": {}
            }
            """).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await bridge.ReceiveAsync("""
            {
              "kind": "cancel",
              "id": "req_block"
            }
            """);

        await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await requestTask.WaitAsync(TimeSpan.FromSeconds(3));

        var sent = Assert.Single(window.SentMessages);
        using var json = JsonDocument.Parse(sent);
        Assert.Equal("req_block", json.RootElement.GetProperty("id").GetString());
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            BridgeErrorCodes.Cancelled,
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ReceiveAsyncRejectsDuplicatePendingRequestIdWithoutInvokingHandlerTwice()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var dispatcher = new BridgeDispatcher();
        dispatcher.Register("Block", async (_, _) =>
        {
            Interlocked.Increment(ref callCount);
            started.SetResult();
            await release.Task;
            return new { done = true };
        });
        var window = new RecordingWindow();
        var bridge = new PhotinoWebMessageBridge(dispatcher, window);
        const string request = """
            {
              "kind": "request",
              "id": "req_duplicate",
              "method": "Block",
              "payload": {}
            }
            """;

        var firstRequest = bridge.ReceiveAsync(request).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await bridge.ReceiveAsync(request);
        release.SetResult();
        await firstRequest.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, callCount);
        Assert.Equal(2, window.SentMessages.Count);
        Assert.Contains(window.SentMessages, message =>
        {
            using var json = JsonDocument.Parse(message);
            return json.RootElement.GetProperty("id").GetString() == "req_duplicate" &&
                !json.RootElement.GetProperty("ok").GetBoolean() &&
                json.RootElement.GetProperty("error").GetProperty("code").GetString() == BridgeErrorCodes.ValidationError;
        });
        Assert.Contains(window.SentMessages, message =>
        {
            using var json = JsonDocument.Parse(message);
            return json.RootElement.GetProperty("id").GetString() == "req_duplicate" &&
                json.RootElement.GetProperty("ok").GetBoolean();
        });
    }

    [Fact]
    public async Task RuntimeHostRoutesActionsToWindowAndUrlOpener()
    {
        var window = new RecordingWindow();
        var opener = new RecordingExternalUrlOpener();
        var host = new PhotinoBridgeRuntimeHost(window, opener);

        await host.MinimizeWindowAsync(CancellationToken.None);
        await host.ToggleMaximizeWindowAsync(CancellationToken.None);
        var isMaximized = await host.IsWindowMaximizedAsync(CancellationToken.None);
        await host.OpenExternalAsync(new Uri("https://example.com/"), CancellationToken.None);
        await host.QuitApplicationAsync(CancellationToken.None);

        Assert.True(window.Minimized);
        Assert.True(isMaximized);
        Assert.True(window.Closed);
        Assert.Equal(new Uri("https://example.com/"), opener.LastUrl);
    }

    [Fact]
    public async Task ReferenceSourceFilePickerReturnsSelectedWindowPath()
    {
        var selectedPath = Path.Combine(Path.GetTempPath(), "reference.md");
        var window = new RecordingWindow { OpenFilePath = selectedPath };
        var picker = new PhotinoReferenceSourceFilePicker(window);

        var path = await picker.PickSourceFileAsync(CancellationToken.None);

        Assert.Equal(selectedPath, path);
        Assert.Equal("选择参考源文件", window.LastOpenFileTitle);
        Assert.Contains(window.LastOpenFileFilters, filter => filter.Patterns.Contains("*.md", StringComparer.Ordinal));
        Assert.Contains(window.LastOpenFileFilters, filter => filter.Patterns.Contains("*.txt", StringComparer.Ordinal));
    }

    private sealed class RecordingWindow : IPhotinoWindow
    {
        public List<string> SentMessages { get; } = [];

        public string? OpenFilePath { get; init; }

        public string? LastOpenFileTitle { get; private set; }

        public IReadOnlyList<WorkspaceFileFilter> LastOpenFileFilters { get; private set; } = [];

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
            LastOpenFileTitle = title;
            LastOpenFileFilters = filters;
            return ValueTask.FromResult(OpenFilePath);
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

        public void Close()
        {
            Closed = true;
        }
    }

    private sealed class RecordingExternalUrlOpener : IExternalUrlOpener
    {
        public Uri? LastUrl { get; private set; }

        public ValueTask OpenAsync(Uri url, CancellationToken cancellationToken)
        {
            LastUrl = url;
            return ValueTask.CompletedTask;
        }
    }
}
