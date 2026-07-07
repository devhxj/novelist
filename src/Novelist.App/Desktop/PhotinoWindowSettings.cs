using Novelist.Infrastructure.App;

namespace Novelist.App.Desktop;

public sealed record PhotinoWindowSettings(
    string Title,
    int Width,
    int Height,
    string StartUrl,
    string? WebViewDataPathKey = null,
    AppInitializationOptions? AppOptions = null);
