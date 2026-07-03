namespace Novelist.App.Desktop;

public interface IExternalUrlOpener
{
    ValueTask OpenAsync(Uri url, CancellationToken cancellationToken);
}
