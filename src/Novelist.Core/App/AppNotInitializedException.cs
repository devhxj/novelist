namespace Novelist.Core.App;

public sealed class AppNotInitializedException : InvalidOperationException
{
    public AppNotInitializedException()
        : base("Application is not initialized.")
    {
    }
}
