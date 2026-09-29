namespace Novelist.Core.App;

/// <summary>
/// 前台 / 后台模型请求的轻量仲裁点。
///
/// 背景：语料材料化（后台）与作者的写作、聊天、高级素材分析（前台）共用同一个
/// provider 与同一份配额。后台一旦并发跑起来，会把作者正在等待的前台生成挤到
/// 后面排队——表现为"一跑语料，写作就变慢"。
///
/// 约定：**默认视为前台，只有后台 worker 显式标记自己为后台。** 这样不必给
/// 每一个 bridge 入口打标，也不改动任何契约。
///
/// 该仲裁点刻意不做配置、不进 UI（AGENTS.md：不扩张专家控制面）。
/// </summary>
public static class ModelRequestArbiter
{
    // 沿异步流传递：worker 在章节处理入口标记后，其内部所有模型调用都自动继承。
    private static readonly AsyncLocal<bool> BackgroundFlow = new();

    private static int _foregroundInFlight;
    private static int _backgroundInFlight;

    // 后台单次最长让路时间。达到上限即放行，避免作者长时间连续写作时后台被永久饿死。
    private static readonly TimeSpan MaxYieldDuration = TimeSpan.FromSeconds(30);

    public static bool IsBackground => BackgroundFlow.Value;

    public static int ForegroundInFlight => Volatile.Read(ref _foregroundInFlight);
    public static int BackgroundInFlight => Volatile.Read(ref _backgroundInFlight);

    /// <summary>把当前异步流及其所有子调用标记为后台请求。</summary>
    public static IDisposable BeginBackground()
    {
        BackgroundFlow.Value = true;
        return new BackgroundScope();
    }

    public static void Enter()
    {
        if (BackgroundFlow.Value)
        {
            Interlocked.Increment(ref _backgroundInFlight);
        }
        else
        {
            Interlocked.Increment(ref _foregroundInFlight);
        }
    }

    public static void Exit()
    {
        if (BackgroundFlow.Value)
        {
            Interlocked.Decrement(ref _backgroundInFlight);
        }
        else
        {
            Interlocked.Decrement(ref _foregroundInFlight);
        }
    }

    /// <summary>
    /// 后台请求在发起前调用：若有前台请求在途，短暂让路，把配额先给作者。
    /// 前台请求永不调用本方法——前台不被后台排队阻塞。
    /// </summary>
    public static async ValueTask YieldToForegroundAsync(CancellationToken cancellationToken)
    {
        // 前台调用进来直接返回：前台永不因后台而等待。
        if (!BackgroundFlow.Value)
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow.Add(MaxYieldDuration);
        while (Volatile.Read(ref _foregroundInFlight) > 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken);
        }
    }

    private sealed class BackgroundScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            BackgroundFlow.Value = false;
        }
    }
}
