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

    [Fact]
    public async Task AdvanceChapterPlanRotatesScopesAndIsIdempotent()
    {
        var options = CreateOptions();
        Directory.CreateDirectory(options.DefaultDataDirectory);
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
        var settings = new FileSystemAppSettingsService(options);
        var novels = new FileSystemNovelService(options, settings);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("章节推进", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(novel.Id, new UpdateChapterPlanPayload("far", "全书大纲卷一"), CancellationToken.None);
        await planning.UpdateChapterPlanAsync(novel.Id, new UpdateChapterPlanPayload("near", "部纲：旧城门线"), CancellationToken.None);
        await planning.UpdateChapterPlanAsync(novel.Id, new UpdateChapterPlanPayload("next", "- 本章细纲 beat"), CancellationToken.None);

        var result = await planning.AdvanceChapterPlanAsync(new AdvanceChapterPlanPayload(novel.Id), CancellationToken.None);

        // 轮转：细纲清空，旧细纲上移进部纲，旧部纲上移进大纲。
        Assert.True(result.NextPlanCleared);
        var plans = await planning.GetChapterPlansAsync(novel.Id, CancellationToken.None);
        Assert.Equal(string.Empty, plans.Single(plan => plan.Scope == "next").Content);
        Assert.Contains("- 本章细纲 beat", plans.Single(plan => plan.Scope == "near").Content, StringComparison.Ordinal);
        Assert.Contains("部纲：旧城门线", plans.Single(plan => plan.Scope == "near").Content, StringComparison.Ordinal);
        Assert.Contains("全书大纲卷一", plans.Single(plan => plan.Scope == "far").Content, StringComparison.Ordinal);
        Assert.Contains("部纲：旧城门线", plans.Single(plan => plan.Scope == "far").Content, StringComparison.Ordinal);

        // 幂等：细纲已空，重复调用无副作用。
        var repeat = await planning.AdvanceChapterPlanAsync(new AdvanceChapterPlanPayload(novel.Id), CancellationToken.None);
        Assert.False(repeat.NextPlanCleared);
        plans = await planning.GetChapterPlansAsync(novel.Id, CancellationToken.None);
        Assert.Equal(string.Empty, plans.Single(plan => plan.Scope == "next").Content);

        // 镜像同步：细纲镜像为空内容。
        var fine = await File.ReadAllTextAsync(Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(), "plans", "细纲.md"));
        Assert.Equal(string.Empty, fine);
    }

    [Fact]
    public async Task GetChapterPlansLazilyRecreatesMissingMirrorsForExistingNovels()
    {
        var options = CreateOptions();
        Directory.CreateDirectory(options.DefaultDataDirectory);
        var settings = new FileSystemAppSettingsService(options);
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
        var novels = new FileSystemNovelService(options, settings);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("存量镜像", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novels);
        await planning.UpdateChapterPlanAsync(novel.Id, new UpdateChapterPlanPayload("next", "- 存量细纲 beat"), CancellationToken.None);
        var plansDirectory = Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(), "plans");

        // 模拟存量小说：镜像从未生成过（第三轮残余事项）。
        File.Delete(Path.Combine(plansDirectory, "细纲.md"));
        Assert.False(File.Exists(Path.Combine(plansDirectory, "细纲.md")));

        // 读取计划即补写缺失镜像（空槽位不需要镜像文件）。
        var plans = await planning.GetChapterPlansAsync(novel.Id, CancellationToken.None);

        Assert.Contains("- 存量细纲 beat", plans.Single(plan => plan.Scope == "next").Content, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(plansDirectory, "细纲.md")));
        Assert.Contains("- 存量细纲 beat", await File.ReadAllTextAsync(Path.Combine(plansDirectory, "细纲.md")), StringComparison.Ordinal);

        // 幂等：镜像齐全时读取不再改写文件。
        var beforeWrite = File.GetLastWriteTimeUtc(Path.Combine(plansDirectory, "细纲.md"));
        await planning.GetChapterPlansAsync(novel.Id, CancellationToken.None);
        Assert.Equal(beforeWrite, File.GetLastWriteTimeUtc(Path.Combine(plansDirectory, "细纲.md")));
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
