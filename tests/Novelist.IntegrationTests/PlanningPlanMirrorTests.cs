using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class PlanningPlanMirrorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"novelist-plan-mirror-{Guid.NewGuid():N}");

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data")
        };
    }

    [Fact]
    public async Task UpdateChapterPlanWritesMarkdownMirrorsForAllThreeScopes()
    {
        var options = CreateOptions();
        Directory.CreateDirectory(options.DefaultDataDirectory);
        var settings = new FileSystemAppSettingsService(options);
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
        var novels = new FileSystemNovelService(options, settings);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("镜像计划", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);

        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("far", "全书大纲：三卷本"),
            CancellationToken.None);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "- 林岚在雨夜门口对峙\r\n- 灯影停顿收尾"),
            CancellationToken.None);

        var plansDirectory = Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(), "plans");
        var outline = await File.ReadAllTextAsync(Path.Combine(plansDirectory, "大纲.md"));
        var fine = await File.ReadAllTextAsync(Path.Combine(plansDirectory, "细纲.md"));

        Assert.Contains("全书大纲：三卷本", outline, StringComparison.Ordinal);
        // 细纲镜像保留换行结构（CRLF 归一为 LF），beat 一行一条。
        Assert.Contains("- 林岚在雨夜门口对峙\n- 灯影停顿收尾", fine, StringComparison.Ordinal);
        Assert.False(fine.Contains('\r'), "mirror content should be normalized to LF");
        // 未写入的部纲镜像为空文件占位。
        Assert.True(File.Exists(Path.Combine(plansDirectory, "部纲.md")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
