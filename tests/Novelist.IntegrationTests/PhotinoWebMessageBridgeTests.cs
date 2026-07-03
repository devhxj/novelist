using System.Text.Json;
using Novelist.App.Desktop;
using Novelist.Core.App;
using Novelist.Core.Bridge;

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

    private sealed class RecordingWindow : IPhotinoWindow
    {
        public List<string> SentMessages { get; } = [];

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
