using System.Diagnostics;
using Novelist.Core.App;

namespace Novelist.Tests;

// 前后台配额仲裁：后台（语料材料化）必须给前台（写作/聊天/高级素材分析）让路，
// 且前台永远不因后台而等待。这里是纯逻辑回归，不涉及真实 HTTP。
public sealed class ModelRequestArbiterTests
{
    [Fact]
    public void DefaultFlowIsForeground()
    {
        Assert.False(ModelRequestArbiter.IsBackground);
    }

    [Fact]
    public async Task BackgroundScopePropagatesIntoChildFlowsAndUnwinds()
    {
        Assert.False(ModelRequestArbiter.IsBackground);

        await Task.Run(async () =>
        {
            using (ModelRequestArbiter.BeginBackground())
            {
                Assert.True(ModelRequestArbiter.IsBackground);

                // 标记沿异步流下传：子调用无需显式传参。
                await Task.Yield();
                Assert.True(ModelRequestArbiter.IsBackground);
            }

            Assert.False(ModelRequestArbiter.IsBackground);
        });

        // 异步方法内的标记不影响调用方的执行上下文。
        Assert.False(ModelRequestArbiter.IsBackground);
    }

    [Fact]
    public async Task ForegroundCallsNeverWaitForTheBackground()
    {
        ModelRequestArbiter.Enter();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            await ModelRequestArbiter.YieldToForegroundAsync(CancellationToken.None);
            stopwatch.Stop();

            // 前台即使自己有请求在途也立即放行——让路只对后台生效。
            Assert.True(stopwatch.ElapsedMilliseconds < 1_000, $"前台被阻塞了 {stopwatch.ElapsedMilliseconds}ms。");
        }
        finally
        {
            ModelRequestArbiter.Exit();
        }
    }

    [Fact]
    public async Task BackgroundWaitsUntilForegroundDrains()
    {
        ModelRequestArbiter.Enter();
        try
        {
            var entered = new TaskCompletionSource();
            var background = Task.Run(async () =>
            {
                using (ModelRequestArbiter.BeginBackground())
                {
                    entered.SetResult();
                    await ModelRequestArbiter.YieldToForegroundAsync(CancellationToken.None);
                }
            });

            await entered.Task;
            await Task.Delay(200);

            // 前台仍有请求在途：后台必须继续让路，不能抢跑。
            Assert.False(background.IsCompleted);

            ModelRequestArbiter.Exit();
            await background.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(background.IsCompletedSuccessfully);
        }
        finally
        {
            ModelRequestArbiter.Enter();
            ModelRequestArbiter.Exit();
        }
    }

    [Fact]
    public void CountersTrackForegroundAndBackgroundSeparately()
    {
        var foregroundBefore = ModelRequestArbiter.ForegroundInFlight;
        var backgroundBefore = ModelRequestArbiter.BackgroundInFlight;

        ModelRequestArbiter.Enter();
        try
        {
            Assert.Equal(foregroundBefore + 1, ModelRequestArbiter.ForegroundInFlight);
            Assert.Equal(backgroundBefore, ModelRequestArbiter.BackgroundInFlight);
        }
        finally
        {
            ModelRequestArbiter.Exit();
        }

        Assert.Equal(foregroundBefore, ModelRequestArbiter.ForegroundInFlight);
    }
}
