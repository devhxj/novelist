using System.Text.Json;
using Novelist.App.Desktop;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class PhotinoReferenceWorkflowSmokeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DesktopCompositionRunsReferenceWorkflowThroughPhotinoWebMessageBridgeWithoutSavingChapterContent()
    {
        var options = CreateOptions();
        var window = new RecordingWindow();
        var bridge = DesktopBridgeComposition.CreateBridge(window, options);

        await SendAsync(bridge, window, "Initialize", options.DefaultDataDirectory);
        var novel = await SendAsync(bridge, window, "CreateNovel", new
        {
            title = "桌面参考烟测",
            description = "",
            genre = "悬疑"
        });
        var novelId = novel.GetProperty("id").GetInt64();
        var chapter = await SendAsync(bridge, window, "CreateChapter", new
        {
            novel_id = novelId,
            title = "第一章"
        });
        var chapterPath = chapter.GetProperty("file_path").GetString() ?? throw new InvalidOperationException("Chapter path is missing.");
        var sourcePath = CreateSourceFile(
            "desktop-reference.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。

            林岚没有回答，喉咙却发紧。
            """);

        await SendAsync(bridge, window, "UpdateChapterPlan", novelId, new
        {
            scope = "next",
            content = "林岚在雨夜门口压住情绪，确认线索后决定行动。"
        });
        var anchor = await SendAsync(bridge, window, "CreateReferenceAnchor", new
        {
            novel_id = novelId,
            title = "雨夜参考",
            author = (string?)null,
            source_path = sourcePath,
            source_kind = "markdown",
            license_status = "user_provided"
        });
        var anchorId = anchor.GetProperty("anchor_id").GetInt64();
        Assert.False(string.IsNullOrWhiteSpace(anchor.GetProperty("source_file_hash").GetString()));

        var buildStatus = await SendAsync(bridge, window, "GetReferenceAnchorBuildStatus", novelId, anchorId);
        Assert.Equal("ready", buildStatus.GetProperty("status").GetString());
        Assert.True(buildStatus.GetProperty("source_segment_count").GetInt32() >= 3);
        Assert.True(buildStatus.GetProperty("material_count").GetInt32() >= 2);

        var materials = await SendAsync(bridge, window, "SearchReferenceMaterials", new
        {
            novel_id = novelId,
            anchor_ids = Array.Empty<long>(),
            query = "雨声",
            material_types = new[] { "sentence" },
            emotion_tags = Array.Empty<string>(),
            function_tags = Array.Empty<string>(),
            pov_tags = Array.Empty<string>(),
            technique_tags = Array.Empty<string>(),
            page = 1,
            size = 10
        });
        Assert.True(materials.GetProperty("items").GetArrayLength() > 0);
        var material = materials.GetProperty("items").EnumerateArray().First();
        var materialId = material.GetProperty("material_id").GetString() ?? throw new InvalidOperationException("Material id is missing.");
        Assert.Equal(anchorId, material.GetProperty("anchor_id").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(material.GetProperty("source_segment_id").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(material.GetProperty("source_hash").GetString()));

        var blueprint = await SendAsync(bridge, window, "GenerateReferenceChapterBlueprint", new
        {
            novel_id = novelId,
            chapter_number = 1,
            title = "第一章蓝图",
            chapter_goal = "雨声压低了整条街的呼吸",
            anchor_ids = new[] { anchorId },
            known_facts = new[] { "雨声压低了整条街的呼吸", "林岚在雨夜门口" },
            forbidden_facts = Array.Empty<string>()
        });
        var blueprintId = blueprint.GetProperty("blueprint_id").GetInt64();
        var review = await SendAsync(bridge, window, "ReviewReferenceChapterBlueprint", new
        {
            novel_id = novelId,
            blueprint_id = blueprintId
        });
        Assert.Equal("passed", review.GetProperty("status").GetString());

        var reviewId = review.GetProperty("review_id").GetString() ?? throw new InvalidOperationException("Review id is missing.");
        await SendAsync(bridge, window, "ApproveReferenceChapterBlueprint", new
        {
            novel_id = novelId,
            blueprint_id = blueprintId,
            review_id = reviewId,
            approver_origin = "smoke_test"
        });
        var binding = await SendAsync(bridge, window, "BindReferenceBlueprintMaterials", new
        {
            novel_id = novelId,
            blueprint_id = blueprintId,
            max_results_per_beat = 2,
            select_top_candidate = true
        });
        Assert.Contains(binding.GetProperty("links").EnumerateArray(), link => link.GetProperty("selected").GetBoolean());
        Assert.Contains(
            binding.GetProperty("links").EnumerateArray(),
            link => string.Equals(link.GetProperty("material_id").GetString(), materialId, StringComparison.Ordinal));

        var draft = await SendAsync(bridge, window, "GenerateReferenceAnchoredDraft", new
        {
            novel_id = novelId,
            blueprint_id = blueprintId,
            beat_ids = Array.Empty<string>()
        });
        Assert.True(draft.GetProperty("candidates").GetArrayLength() > 0);
        Assert.True(draft.TryGetProperty("audit", out var audit));
        Assert.NotEqual(JsonValueKind.Null, audit.ValueKind);

        var chapterContent = await SendAsync(bridge, window, "GetContent", novelId, chapterPath);
        Assert.Equal(string.Empty, chapterContent.GetString());
        Assert.DoesNotContain(window.RequestedMethods, method => string.Equals(method, "SaveContent", StringComparison.Ordinal));
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

    private string CreateSourceFile(string fileName, string content)
    {
        var sourceDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var path = Path.Combine(sourceDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static async ValueTask<JsonElement> SendAsync(
        PhotinoWebMessageBridge bridge,
        RecordingWindow window,
        string method,
        params object?[] args)
    {
        var requestId = "req_" + method + "_" + Guid.NewGuid().ToString("N");
        window.RequestedMethods.Add(method);
        await bridge.ReceiveAsync(JsonSerializer.Serialize(
            new
            {
                kind = "request",
                id = requestId,
                method,
                payload = new { args }
            },
            BridgeJson.SerializerOptions));
        Assert.NotEmpty(window.SentMessages);
        var message = window.SentMessages[^1];
        using var response = JsonDocument.Parse(message);
        Assert.Equal(requestId, response.RootElement.GetProperty("id").GetString());
        Assert.True(
            response.RootElement.GetProperty("ok").GetBoolean(),
            response.RootElement.TryGetProperty("error", out var error)
                ? error.GetRawText()
                : message);
        return response.RootElement.GetProperty("result").Clone();
    }

    private sealed class RecordingWindow : IPhotinoWindow
    {
        public List<string> SentMessages { get; } = [];

        public List<string> RequestedMethods { get; } = [];

        public bool Minimized { get; private set; }

        public bool Maximized { get; private set; }

        public bool Closed { get; private set; }

        public void WaitForClose()
        {
        }

        public void SendWebMessage(string message)
        {
            SentMessages.Add(message);
        }

        public ValueTask<string?> ShowSaveFileAsync(
            string title,
            string defaultPath,
            IReadOnlyList<NovelExportFileFilter> filters,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask<string?> ShowOpenFileAsync(
            string title,
            string defaultPath,
            IReadOnlyList<WorkspaceFileFilter> filters,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(null);
        }

        public void Minimize()
        {
            Minimized = true;
        }

        public void ToggleMaximize()
        {
            Maximized = !Maximized;
        }

        public bool IsMaximized()
        {
            return Maximized;
        }

        public void Close()
        {
            Closed = true;
        }
    }
}
