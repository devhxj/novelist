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
    public async Task CharacterRelationsCanBeEvolvedEditedAndDeleted()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var service = new FileSystemWorldEntityService(options, novelService);

        var source = await service.CreateCharacterAsync(
            novel.Id,
            new CreateCharacterPayload("林岚", "记者", "", "[]"),
            CancellationToken.None);
        var target = await service.CreateCharacterAsync(
            novel.Id,
            new CreateCharacterPayload("阿七", "线人", "", "[]"),
            CancellationToken.None);

        var first = await service.UpdateCharacterRelationshipAsync(
            novel.Id,
            new UpdateCharacterRelationshipPayload(
                SourceCharacterId: source.Id,
                TargetCharacterId: target.Id,
                RelationDescribe: "互相试探",
                Description: "交换情报但互不信任",
                ChapterId: 3),
            CancellationToken.None);
        var second = await service.UpdateCharacterRelationshipAsync(
            novel.Id,
            new UpdateCharacterRelationshipPayload(
                SourceCharacterId: source.Id,
                TargetCharacterId: target.Id,
                RelationDescribe: "临时盟友",
                Description: "共同追查旧城门线索",
                ChapterId: 7),
            CancellationToken.None);

        var current = Assert.Single(await service.GetCharacterRelationsAsync(novel.Id, CancellationToken.None));
        Assert.Equal(second.Id, current.Id);
        Assert.True(current.IsCurrent);
        Assert.Equal("临时盟友", current.RelationDescribe);

        var edited = await service.UpdateCharacterRelationshipAsync(
            novel.Id,
            new UpdateCharacterRelationshipPayload(
                RelationId: second.Id,
                RelationDescribe: "临时同盟",
                Description: "共享线索但保留底牌"),
            CancellationToken.None);
        Assert.Equal(second.Id, edited.Id);
        Assert.Equal("临时同盟", edited.RelationDescribe);
        Assert.Equal("共享线索但保留底牌", edited.Description);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.UpdateCharacterRelationshipAsync(
                novel.Id,
                new UpdateCharacterRelationshipPayload(
                    SourceCharacterId: source.Id,
                    TargetCharacterId: source.Id,
                    RelationDescribe: "自我关系"),
                CancellationToken.None));

        await service.DeleteCharacterRelationAsync(novel.Id, first.Id, CancellationToken.None);
        await service.DeleteCharacterRelationAsync(novel.Id, second.Id, CancellationToken.None);
        Assert.Empty(await service.GetCharacterRelationsAsync(novel.Id, CancellationToken.None));
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
    public async Task LocationRelationsAreUndirectedUniquePatchableAndDeletable()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("群星边境", "", ""), CancellationToken.None);
        var service = new FileSystemWorldEntityService(options, novelService);

        var station = await service.CreateLocationAsync(
            novel.Id,
            new CreateLocationPayload("边境站", "空间站", "", "{}", null, "[]"),
            CancellationToken.None);
        var gate = await service.CreateLocationAsync(
            novel.Id,
            new CreateLocationPayload("星门", "设施", "", "{}", null, "[]"),
            CancellationToken.None);

        var relation = await service.CreateLocationRelationAsync(
            novel.Id,
            new CreateLocationRelationPayload(
                LocationAId: gate.Id,
                LocationBId: station.Id,
                RelationType: "跃迁航线",
                Description: "需要军方许可"),
            CancellationToken.None);
        Assert.Equal(Math.Min(station.Id, gate.Id), relation.LocationAId);
        Assert.Equal(Math.Max(station.Id, gate.Id), relation.LocationBId);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.CreateLocationRelationAsync(
                novel.Id,
                new CreateLocationRelationPayload(
                    LocationAId: station.Id,
                    LocationBId: gate.Id,
                    RelationType: "重复航线",
                    Description: ""),
                CancellationToken.None));

        var updated = await service.UpdateLocationRelationAsync(
            novel.Id,
            relation.Id,
            new UpdateLocationRelationPayload(RelationType: "封锁航线", Description: "临时封闭"),
            CancellationToken.None);
        Assert.Equal("封锁航线", updated.RelationType);
        Assert.Equal("临时封闭", updated.Description);

        await service.DeleteLocationRelationAsync(novel.Id, relation.Id, CancellationToken.None);
        Assert.Empty(await service.GetLocationRelationsAsync(novel.Id, CancellationToken.None));
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
