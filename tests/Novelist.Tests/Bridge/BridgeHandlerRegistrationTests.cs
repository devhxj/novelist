using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;

namespace Novelist.Tests.Bridge;

public sealed class BridgeHandlerRegistrationTests
{
    [Fact]
    public async Task RuntimeHandlersRouteWindowActionsThroughRuntimeHost()
    {
        var runtime = new RecordingRuntimeHost();
        var dispatcher = new BridgeDispatcher().RegisterRuntimeHandlers(runtime);

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_minimize",
              "method": "runtime.window.minimize",
              "payload": {}
            }
            """);

        using var json = ParseOutbound(result);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, runtime.MinimizeCalls);
    }

    [Fact]
    public async Task RuntimeHandlerReturnsWindowMaximizedState()
    {
        var runtime = new RecordingRuntimeHost { IsMaximized = true };
        var dispatcher = new BridgeDispatcher().RegisterRuntimeHandlers(runtime);

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_is_maximized",
              "method": "runtime.window.isMaximized",
              "payload": {}
            }
            """);

        using var json = ParseOutbound(result);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(json.RootElement.GetProperty("result").GetBoolean());
    }

    [Fact]
    public async Task RuntimeHandlerReturnsWindowBounds()
    {
        var runtime = new RecordingRuntimeHost();
        var dispatcher = new BridgeDispatcher().RegisterRuntimeHandlers(runtime);

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_window_bounds",
              "method": "runtime.window.getBounds",
              "payload": {}
            }
            """);

        using var json = ParseOutbound(result);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        var bounds = json.RootElement.GetProperty("result");
        Assert.Equal(160, bounds.GetProperty("x").GetInt32());
        Assert.Equal(120, bounds.GetProperty("y").GetInt32());
        Assert.Equal(1280, bounds.GetProperty("width").GetInt32());
        Assert.Equal(840, bounds.GetProperty("height").GetInt32());
        Assert.False(bounds.GetProperty("maximized").GetBoolean());
    }

    [Fact]
    public async Task OpenExternalRejectsNonHttpsUrls()
    {
        var runtime = new RecordingRuntimeHost();
        var dispatcher = new BridgeDispatcher().RegisterRuntimeHandlers(runtime);

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_open_external",
              "method": "runtime.shell.openExternal",
              "payload": { "url": "http://example.com" }
            }
            """);

        using var json = ParseOutbound(result);
        var error = AssertBridgeError(json.RootElement, "req_open_external", BridgeErrorCodes.ValidationError);
        Assert.Equal("Only absolute https:// URLs are allowed.", error.GetProperty("details").GetProperty("url").GetString());
        Assert.Null(runtime.LastOpenedUrl);
    }

    [Fact]
    public async Task OpenExternalAcceptsHttpsUrls()
    {
        var runtime = new RecordingRuntimeHost();
        var dispatcher = new BridgeDispatcher().RegisterRuntimeHandlers(runtime);

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_open_external",
              "method": "runtime.shell.openExternal",
              "payload": { "url": "https://example.com/docs" }
            }
            """);

        using var json = ParseOutbound(result);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(new Uri("https://example.com/docs"), runtime.LastOpenedUrl);
    }

    [Fact]
    public async Task CompatibilityAppMethodReturnsStableNotImplementedError()
    {
        var dispatcher = new BridgeDispatcher().RegisterCompatibilityAppMethodHandlers();

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_get_novels",
              "method": "GetNovels",
              "payload": {}
            }
            """);

        using var json = ParseOutbound(result);
        var error = AssertBridgeError(json.RootElement, "req_get_novels", BridgeErrorCodes.MethodNotImplemented);
        Assert.Equal("GetNovels", error.GetProperty("details").GetProperty("method").GetString());
    }

    [Fact]
    public void CompatibilityAppMethodListHasExpectedCoverage()
    {
        Assert.Equal(215, BridgeCompatibilityAppMethods.MethodNames.Count);
        Assert.Equal(BridgeCompatibilityAppMethods.MethodNames.Count, BridgeCompatibilityAppMethods.MethodNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("Chat", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetCover", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("CreateReferenceAnchor", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("CreateReferenceAnchors", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("CreateReferenceAnchorsWithResult", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("AnalyzeReferenceChapterSplit", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("PreviewReferenceChapterSplit", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("ConfirmReferenceChapterSplit", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("EnqueueReferenceMaterialization", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetReferenceMaterializationStatus", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("RetryReferenceMaterialization", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("ListReferenceMaterializationChapterProgress", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("ListReferenceMaterializationCandidates", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("ReviewReferenceMaterializationCandidate", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("ListActiveReferenceMaterializationMaterials", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("SearchActiveReferenceMaterializationMaterials", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GenerateReferenceMaterializationBlueprintPreview", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetReferenceMaterializationBlueprintPreview", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("RecordReferenceUserFeedback", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetReferenceUserFeedback", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("UpdateReferenceMaterialTags", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetReferenceMaterialCoverage", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetReferenceMaterialTagReviewQueue", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetReferenceSourceSegmentDetail", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("SearchReferenceCorpusCandidates", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("BackfillReferenceCorpusTechniqueVectorIndex", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("StartReferenceCorpusFeatureAnalysis", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetReferenceCorpusFeatureAnalysisRun", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("StartReferenceCorpusTechniqueSpecimenAnalysis", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetReferenceCorpusTechniqueSpecimenAnalysisRun", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("ListReferenceCorpusFeatureObservations", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("ListReferenceCorpusTechniqueSpecimens", BridgeCompatibilityAppMethods.MethodNames);
Assert.Contains("GenerateReferenceCorpusBlueprintCandidates", BridgeCompatibilityAppMethods.MethodNames);
 Assert.Contains("AdvanceReferenceCorpusBlueprintSession", BridgeCompatibilityAppMethods.MethodNames);
 Assert.Contains("GetReferenceCorpusBlueprintSession", BridgeCompatibilityAppMethods.MethodNames);
 Assert.Contains("GetReferenceCorpusCascadeImpact", BridgeCompatibilityAppMethods.MethodNames);
 Assert.Contains("GetReferenceCorpusGovernance", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GenerateReferenceCorpusInsertionDraft", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GenerateReferenceCorpusInsertionDraftCandidates", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("DeleteReferenceMaterials", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("RestoreReferenceMaterials", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GenerateReferenceChapterBlueprint", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("ReviseReferenceChapterBlueprint", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GenerateReferenceAnchoredDraft", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetReferenceAnchoredDraftAudits", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetReferenceDraftCandidates", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetReferenceStyleAuditFindings", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("BuildReferenceStyleProfile", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetReferenceStyleProfileBuildStatus", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("CancelReferenceStyleProfileBuild", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetReferenceStyleProfiles", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetReferenceStyleProfile", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("ArchiveReferenceStyleProfile", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("RestoreReferenceStyleProfile", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("CompareReferenceStyleProfiles", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("StartReferenceOrchestrationRun", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("GetReferenceOrchestrationRunEvents", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("ResumeReferenceOrchestrationRun", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("PickReferenceSourceFile", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("SearchAll", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("DiscoverModels", BridgeCompatibilityAppMethods.MethodNames);
        Assert.Contains("UpdateTimelineEntry", BridgeCompatibilityAppMethods.MethodNames);
    }

    [Fact]
    public void RuntimeMethodListHasExpectedCoverage()
    {
        Assert.Equal(6, BridgeRuntimeMethodNames.All.Count);
        Assert.Contains(BridgeRuntimeMethodNames.WindowMinimize, BridgeRuntimeMethodNames.All);
        Assert.Contains(BridgeRuntimeMethodNames.WindowToggleMaximize, BridgeRuntimeMethodNames.All);
        Assert.Contains(BridgeRuntimeMethodNames.WindowIsMaximized, BridgeRuntimeMethodNames.All);
        Assert.Contains(BridgeRuntimeMethodNames.WindowGetBounds, BridgeRuntimeMethodNames.All);
        Assert.Contains(BridgeRuntimeMethodNames.AppQuit, BridgeRuntimeMethodNames.All);
        Assert.Contains(BridgeRuntimeMethodNames.ShellOpenExternal, BridgeRuntimeMethodNames.All);
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private static JsonElement AssertBridgeError(JsonElement root, string expectedId, string expectedCode)
    {
        Assert.Equal("response", root.GetProperty("kind").GetString());
        Assert.Equal(expectedId, root.GetProperty("id").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());
        var error = root.GetProperty("error");
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        return error;
    }

    private sealed class RecordingRuntimeHost : IBridgeRuntimeHost
    {
        public int MinimizeCalls { get; private set; }

        public bool IsMaximized { get; init; }

        public Uri? LastOpenedUrl { get; private set; }

        public ValueTask MinimizeWindowAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MinimizeCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask ToggleMaximizeWindowAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> IsWindowMaximizedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(IsMaximized);
        }

        public ValueTask<WindowSettingsPayload> GetWindowBoundsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new WindowSettingsPayload(160, 120, 1280, 840, IsMaximized));
        }

        public ValueTask QuitApplicationAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask OpenExternalAsync(Uri url, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastOpenedUrl = url;
            return ValueTask.CompletedTask;
        }
    }
}
