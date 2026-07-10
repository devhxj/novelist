using System.Diagnostics;

namespace Novelist.App.Desktop;

public sealed class SystemExternalUrlOpener : IExternalUrlOpener
{
    public ValueTask OpenAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        cancellationToken.ThrowIfCancellationRequested();

        var started = Process.Start(new ProcessStartInfo
        {
            FileName = url.AbsoluteUri,
            UseShellExecute = true
        });

        if (started is null)
        {
            throw new InvalidOperationException("Unable to open external URL.");
        }

        return ValueTask.CompletedTask;
    }
}
