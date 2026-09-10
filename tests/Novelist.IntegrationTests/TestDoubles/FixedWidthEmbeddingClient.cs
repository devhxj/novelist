using Novelist.Core.App;

namespace Novelist.IntegrationTests.TestDoubles;

/// <summary>
/// 模拟"向量宽度由模型固定、不接受 dimensions 入参"的服务商：无论请求声明多少，
/// 只返回自己的宽度。用来验证写入侧按实测宽度落库，而不是拿内置默认值猜。
/// </summary>
public sealed class FixedWidthEmbeddingClient : IEmbeddingClient
{
    private readonly object _gate = new();
    private readonly List<DeterministicHashEmbeddingCall> _calls = [];
    private readonly int _width;

    public FixedWidthEmbeddingClient(int width)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Embedding width must be positive.");
        }

        _width = width;
    }

    public int Width => _width;

    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count;
            }
        }
    }

    public IReadOnlyList<DeterministicHashEmbeddingCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public ValueTask<EmbeddingBatchResult> EmbedAsync(
        IReadOnlyList<string> inputs,
        EmbeddingRequestOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = inputs
            .Select(input => input ?? throw new ArgumentException("Embedding inputs cannot contain null values.", nameof(inputs)))
            .ToArray();
        lock (_gate)
        {
            _calls.Add(new DeterministicHashEmbeddingCall(snapshot, options));
        }

        var items = snapshot
            .Select((input, index) => new EmbeddingItemResult(
                index,
                DeterministicHashEmbeddingClient.CreateVector(input, _width, options.NormalizeEmbeddings)))
            .ToArray();
        var model = string.IsNullOrWhiteSpace(options.ModelId) ? "fixed-width-embedding" : options.ModelId;

        return ValueTask.FromResult(new EmbeddingBatchResult(
            model,
            _width,
            items,
            new EmbeddingUsage(snapshot.Length, snapshot.Length)));
    }
}
