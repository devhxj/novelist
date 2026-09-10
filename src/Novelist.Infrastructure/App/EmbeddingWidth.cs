using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

// 向量宽度属于 (provider, model)：只有设置里显式声明的维度才有约束力。
// 未声明时必须实测——内置 ONNX 的 512 只对本地 bge-small 成立，
// 把它当默认值会让写入侧建出与读取侧实测宽度不同的向量表。
internal static class EmbeddingWidth
{
    private const string ProbeText = "novelist embedding width probe";

    internal static async ValueTask<int> ResolveAsync(
        IEmbeddingClient embeddings,
        EmbeddingRequestOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Dimensions is int declared)
        {
            return declared;
        }

        // 探测请求与真实批量请求保持同一形状（不带 dimensions 入参），只多送一个短字符串。
        var probe = await embeddings.EmbedAsync(
            [ProbeText],
            options with { InputKind = BuiltinOnnxEmbeddingModel.DocumentInputKind },
            cancellationToken);
        if (probe.Dimensions <= 0 ||
            probe.Items.Count != 1 ||
            probe.Items[0].Vector.Count != probe.Dimensions)
        {
            throw new InvalidOperationException("Embedding provider did not report a usable vector width.");
        }

        return probe.Dimensions;
    }
}
