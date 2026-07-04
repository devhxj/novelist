using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Novelist.App.Hosting;

namespace Novelist.IntegrationTests;

[Collection(WebApplicationFactoryCollection.Name)]
public sealed class AppContractTests
{
    [Fact]
    public async Task HealthEndpointReturnsStablePayload()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        var health = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.Equal("novelist", health.Service);
    }

    [Fact]
    public async Task EventsHubNegotiateEndpointIsMapped()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/hubs/events/negotiate?negotiateVersion=1", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(DisableHostLogging);
    }

    private static void DisableHostLogging(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.ClearProviders());
    }
}
