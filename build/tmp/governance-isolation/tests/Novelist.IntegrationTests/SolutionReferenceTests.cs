using Novelist.App;
using Novelist.Infrastructure;
using System.Text.Json;
using System.Xml.Linq;

namespace Novelist.IntegrationTests;

public sealed class SolutionReferenceTests
{
    [Fact]
    public void IntegrationTestProjectCanReferenceAppAndInfrastructure()
    {
        Assert.Equal("Novelist.App", typeof(AppAssembly).Assembly.GetName().Name);
        Assert.Equal("Novelist.Infrastructure", typeof(InfrastructureAssembly).Assembly.GetName().Name);
    }

    [Fact]
    public void AppProjectIsRunnableDesktopExecutable()
    {
        var project = XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "Novelist.App", "Novelist.App.csproj"));
        var outputType = project
            .Descendants("OutputType")
            .Select(element => element.Value)
            .FirstOrDefault();

        Assert.Equal("Exe", outputType);
    }

    [Fact]
    public void LaunchSettingsUseDesktopProfiles()
    {
        var launchSettingsPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Novelist.App",
            "Properties",
            "launchSettings.json");
        var launchSettingsJson = File.ReadAllText(launchSettingsPath);
        using var document = JsonDocument.Parse(launchSettingsJson);
        var profiles = document.RootElement.GetProperty("profiles");

        Assert.True(profiles.TryGetProperty("Novelist.App", out var desktopProfile));
        Assert.False(desktopProfile.GetProperty("launchBrowser").GetBoolean());
        Assert.Equal("--desktop", desktopProfile.GetProperty("commandLineArgs").GetString());

        Assert.False(profiles.TryGetProperty("http", out _));
        Assert.False(profiles.TryGetProperty("https", out _));
        Assert.DoesNotContain("ASPNETCORE_ENVIRONMENT", launchSettingsJson, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Novelist.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }
}
