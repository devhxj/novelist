using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterializationEmbeddingProcessorTests
{
    [Fact]
    public async Task EmbedAsyncAcceptsEveryMaterialExtractedFromOneChapter()
    {
        var client = new RecordingEmbeddingClient();
        var processor = new ReferenceMaterializationEmbeddingProcessor(
            new FixedEmbeddingConfiguration(),
            client);
        var items = Enumerable.Range(1, 74)
            .Select(index => new ReferenceMaterializationEmbeddingItem($"material-{index}", $"Material {index}"))
            .ToArray();

        var result = await processor.EmbedAsync(
            new ReferenceMaterializationEmbeddingRequest(
                new ReferenceMaterializationEmbeddingModel("onnx", "Qwen/Qwen3-Embedding-0.6B", 3),
                items),
            CancellationToken.None);

        Assert.Equal(74, result.Embeddings.Count);
        Assert.Equal(74, client.InputCount);
    }

    [Fact]
    public async Task EmbedAsyncAcceptsACompleteArchiveInputLongerThanTheRetiredPerItemLimit()
    {
        var client = new RecordingEmbeddingClient();
        var processor = new ReferenceMaterializationEmbeddingProcessor(
            new FixedEmbeddingConfiguration(),
            client);

        var result = await processor.EmbedAsync(
            new ReferenceMaterializationEmbeddingRequest(
                new ReferenceMaterializationEmbeddingModel("onnx", "Qwen/Qwen3-Embedding-0.6B", 3),
                [new ReferenceMaterializationEmbeddingItem("material-1", new string('材', 1_201))]),
            CancellationToken.None);

        Assert.Single(result.Embeddings);
        Assert.Equal(1, client.InputCount);
    }

    [Fact]
    public async Task EmbedAsyncReportsInvalidInputAsAnEmbeddingFailure()
    {
        var processor = new ReferenceMaterializationEmbeddingProcessor(
            new FixedEmbeddingConfiguration(),
            new RecordingEmbeddingClient());

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await processor.EmbedAsync(
                new ReferenceMaterializationEmbeddingRequest(
                    new ReferenceMaterializationEmbeddingModel("onnx", "Qwen/Qwen3-Embedding-0.6B", 3),
                    []),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.EmbeddingRequestFailed, exception.ErrorCode);
    }

    [Fact]
    public async Task EmbedAsyncPreservesTheClientFailureForDiagnostics()
    {
        var processor = new ReferenceMaterializationEmbeddingProcessor(
            new FixedEmbeddingConfiguration(),
            new ThrowingEmbeddingClient());

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await processor.EmbedAsync(
                new ReferenceMaterializationEmbeddingRequest(
                    new ReferenceMaterializationEmbeddingModel("onnx", "Qwen/Qwen3-Embedding-0.6B", 3),
                    [new ReferenceMaterializationEmbeddingItem("material-1", "Material 1")]),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.EmbeddingRequestFailed, exception.ErrorCode);
        Assert.Equal("Injected DirectML failure.", exception.InnerException?.Message);
    }

    private sealed class FixedEmbeddingConfiguration : IEmbeddingConfigurationService
    {
        public ValueTask<EmbeddingRequestOptions?> GetActiveEmbeddingOptionsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<EmbeddingRequestOptions?>(new EmbeddingRequestOptions(
                "onnx",
                string.Empty,
                string.Empty,
                "Qwen/Qwen3-Embedding-0.6B",
                3,
                null));
        }
    }

    private sealed class RecordingEmbeddingClient : IEmbeddingClient
    {
        public int InputCount { get; private set; }

        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InputCount = inputs.Count;
            return ValueTask.FromResult(new EmbeddingBatchResult(
                options.ModelId,
                3,
                inputs.Select((_, index) => new EmbeddingItemResult(index, [1f, 0f, 0f])).ToArray(),
                new EmbeddingUsage(inputs.Count, inputs.Count)));
        }
    }

    private sealed class ThrowingEmbeddingClient : IEmbeddingClient
    {
        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Injected DirectML failure.");
    }
}
