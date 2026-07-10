using Novelist.Core.App;

namespace Novelist.IntegrationTests.TestDoubles;

public sealed class DeterministicHashEmbeddingClientTests
{
    [Fact]
    public async Task EmbedAsyncReturnsStableVectorsForRepeatedText()
    {
        var client = new DeterministicHashEmbeddingClient(defaultDimensions: 8);
        var options = new EmbeddingRequestOptions(
            ProviderKey: "fake",
            EndpointUrl: "",
            ApiKey: "",
            ModelId: "hash-model",
            Dimensions: 8,
            User: null,
            NormalizeEmbeddings: true);

        var first = await client.EmbedAsync(
            ["雨声压低了整条街的呼吸。", "雨声压低了整条街的呼吸。", "灯光落在纸上。"],
            options,
            CancellationToken.None);
        var second = await client.EmbedAsync(["雨声压低了整条街的呼吸。"], options, CancellationToken.None);

        Assert.Equal("hash-model", first.Model);
        Assert.Equal(8, first.Dimensions);
        Assert.Equal(3, first.Items.Count);
        Assert.Equal([0, 1, 2], first.Items.Select(item => item.Index).ToArray());
        Assert.Equal(first.Items[0].Vector, first.Items[1].Vector);
        Assert.NotEqual(first.Items[0].Vector, first.Items[2].Vector);
        Assert.Equal(first.Items[0].Vector, second.Items[0].Vector);
        Assert.Equal(2, client.CallCount);
        Assert.Equal(["雨声压低了整条街的呼吸。", "雨声压低了整条街的呼吸。", "灯光落在纸上。"], client.Calls[0].Inputs);
        Assert.Equal(1.0, Math.Sqrt(first.Items[0].Vector.Sum(value => value * value)), precision: 5);
    }

    [Fact]
    public async Task EmbedAsyncUsesDefaultDimensionsWhenRequestDoesNotSpecifyThem()
    {
        var client = new DeterministicHashEmbeddingClient(defaultDimensions: 6);
        var options = new EmbeddingRequestOptions(
            ProviderKey: "fake",
            EndpointUrl: "",
            ApiKey: "",
            ModelId: "",
            Dimensions: null,
            User: null,
            NormalizeEmbeddings: false);

        var result = await client.EmbedAsync(["节点文本"], options, CancellationToken.None);

        Assert.Equal(6, result.Dimensions);
        Assert.Equal("deterministic-hash-embedding", result.Model);
        Assert.Equal(6, result.Items.Single().Vector.Count);
        Assert.Contains(result.Items.Single().Vector, value => Math.Abs(value) > 0.0001f);
    }
}
