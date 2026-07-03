using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class WorldEntityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CharacterCrudPersistsAndRelationsAreStable()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var service = new FileSystemWorldEntityService(options, novelService);

        var character = await service.CreateCharacterAsync(
            novel.Id,
            new CreateCharacterPayload("  林岚  ", "旧城记者", "", """["追踪"]"""),
            CancellationToken.None);
        await service.CreateCharacterAsync(
            novel.Id,
            new CreateCharacterPayload("阿七", "线人", "", "[]"),
            CancellationToken.None);

        await service.UpdateCharacterAsync(
            novel.Id,
            character.Id,
            new UpdateCharacterPayload(Name: "", Description: "调查记者", Personality: "", Abilities: """["追踪","速记"]"""),
            CancellationToken.None);

        var reloaded = new FileSystemWorldEntityService(options, novelService);
        var characters = await reloaded.GetCharactersAsync(novel.Id, CancellationToken.None);
        Assert.Equal(["林岚", "阿七"], characters.Select(item => item.Name));
        Assert.Equal("调查记者", characters.Single(item => item.Id == character.Id).Description);
        Assert.Equal("""["追踪","速记"]""", characters.Single(item => item.Id == character.Id).Abilities);
        Assert.Empty(await reloaded.GetCharacterRelationsAsync(novel.Id, CancellationToken.None));

        await reloaded.DeleteCharacterAsync(novel.Id, character.Id, CancellationToken.None);
        Assert.DoesNotContain(await reloaded.GetCharactersAsync(novel.Id, CancellationToken.None), item => item.Id == character.Id);
    }

    [Fact]
    public async Task LocationCrudPersistsParentChangesAndRelationsAreStable()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("群星边境", "", ""), CancellationToken.None);
        var service = new FileSystemWorldEntityService(options, novelService);

        var station = await service.CreateLocationAsync(
            novel.Id,
            new CreateLocationPayload("边境站", "空间站", "舰队补给点", "{}", null, """["补给"]"""),
            CancellationToken.None);
        var dock = await service.CreateLocationAsync(
            novel.Id,
            new CreateLocationPayload("停泊港", "港口", "", "{}", station.Id, "[]"),
            CancellationToken.None);

        await service.UpdateLocationAsync(
            novel.Id,
            dock.Id,
            new UpdateLocationPayload(Name: "停泊港A区", LocationType: "", Description: "维修区", DetailJson: "", ParentLocationId: null, Tags: "", ClearParent: true),
            CancellationToken.None);

        var reloaded = new FileSystemWorldEntityService(options, novelService);
        var locations = await reloaded.GetLocationsAsync(novel.Id, CancellationToken.None);
        var updatedDock = locations.Single(item => item.Id == dock.Id);
        Assert.Equal("停泊港A区", updatedDock.Name);
        Assert.Null(updatedDock.ParentLocationId);
        Assert.Empty(await reloaded.GetLocationRelationsAsync(novel.Id, CancellationToken.None));

        var child = await reloaded.CreateLocationAsync(
            novel.Id,
            new CreateLocationPayload("维修库", "房间", "", "{}", station.Id, "[]"),
            CancellationToken.None);
        await reloaded.DeleteLocationAsync(novel.Id, station.Id, CancellationToken.None);
        var afterDelete = await reloaded.GetLocationsAsync(novel.Id, CancellationToken.None);
        Assert.Null(afterDelete.Single(item => item.Id == child.Id).ParentLocationId);
        Assert.DoesNotContain(afterDelete, item => item.Id == station.Id);
    }

    [Fact]
    public async Task BridgeWorldEntityHandlersCreateListUpdateAndDelete()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterWorldEntityHandlers(new FileSystemWorldEntityService(options, novelService));

        using var createCharacter = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_create_character",
              "method": "CreateCharacter",
              "payload": { "args": [{{novel.Id}}, { "name": "林岚", "description": "记者", "abilities": "[\"追踪\"]" }] }
            }
            """));
        var characterId = createCharacter.RootElement.GetProperty("result").GetProperty("id").GetInt64();

        using var updateCharacter = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_update_character",
              "method": "UpdateCharacter",
              "payload": { "args": [{{novel.Id}}, {{characterId}}, { "description": "调查记者" }] }
            }
            """));
        Assert.True(updateCharacter.RootElement.GetProperty("ok").GetBoolean());

        using var characters = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_characters",
              "method": "GetCharacters",
              "payload": { "args": [{{novel.Id}}] }
            }
            """));
        Assert.Equal("调查记者", characters.RootElement.GetProperty("result")[0].GetProperty("description").GetString());

        using var createLocation = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_create_location",
              "method": "CreateLocation",
              "payload": { "args": [{{novel.Id}}, { "name": "旧城门", "location_type": "城门", "tags": "[\"线索\"]" }] }
            }
            """));
        var locationId = createLocation.RootElement.GetProperty("result").GetProperty("id").GetInt64();

        using var deleteLocation = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_delete_location",
              "method": "DeleteLocation",
              "payload": { "args": [{{novel.Id}}, {{locationId}}] }
            }
            """));
        Assert.True(deleteLocation.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task BridgeWorldEntityHandlersReturnStableErrors()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterWorldEntityHandlers(new FileSystemWorldEntityService(options, novelService));

        using var invalidCharacter = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_bad_character",
              "method": "CreateCharacter",
              "payload": { "args": [{{novel.Id}}, { "name": "   " }] }
            }
            """));
        AssertBridgeError(invalidCharacter.RootElement, "req_bad_character", BridgeErrorCodes.ValidationError);

        using var invalidLocation = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_bad_location",
              "method": "CreateLocation",
              "payload": { "args": [{{novel.Id}}, { "name": "" }] }
            }
            """));
        AssertBridgeError(invalidLocation.RootElement, "req_bad_location", BridgeErrorCodes.ValidationError);
    }

    [Fact]
    public async Task BridgeWorldEntityHandlersReturnStableErrorWhenAppIsNotInitialized()
    {
        var options = CreateOptions();
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterWorldEntityHandlers(new FileSystemWorldEntityService(
                options,
                new FileSystemNovelService(options, new FileSystemAppSettingsService(options))));

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_characters",
              "method": "GetCharacters",
              "payload": { "args": [1] }
            }
            """);

        using var json = ParseOutbound(result);
        AssertBridgeError(json.RootElement, "req_characters", BridgeErrorCodes.AppNotInitialized);
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

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private static void AssertBridgeError(JsonElement root, string expectedId, string expectedCode)
    {
        Assert.Equal("response", root.GetProperty("kind").GetString());
        Assert.Equal(expectedId, root.GetProperty("id").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedCode, root.GetProperty("error").GetProperty("code").GetString());
    }
}
