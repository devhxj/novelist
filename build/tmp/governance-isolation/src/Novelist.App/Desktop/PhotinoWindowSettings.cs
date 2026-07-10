using Novelist.Infrastructure.App;

namespace Novelist.App.Desktop;

public sealed record PhotinoWindowSettings(
    string Title,
    int? X,
    int? Y,
    int Width,
    int Height,
    string StartUrl,
    bool Maximized = false,
    string? WebViewDataPathKey = null,
    AppInitializationOptions? AppOptions = null);
