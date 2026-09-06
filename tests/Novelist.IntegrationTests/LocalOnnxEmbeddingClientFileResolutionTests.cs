using System.Runtime.CompilerServices;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

/// <summary>
/// 2026-09-06 回归：开发运行与发布包把模型放进 runtime/models/&lt;模型子目录&gt;/ 布局
/// （如 bge-small-zh-v1.5-int8/model.onnx），而候选解析只找平铺的 model.onnx，
/// 导致向量模型预检误报"模型文件缺失"。本测试钉住子目录布局的解析。
/// </summary>
[Collection("onnx-runtime")]
public sealed class LocalOnnxEmbeddingClientFileResolutionTests : IDisposable
{
    private const string ModelsDirVariable = "NOVELIST_ONNX_MODELS_DIR";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));
    private readonly string? _originalModelsDir;

    public LocalOnnxEmbeddingClientFileResolutionTests()
    {
        _originalModelsDir = Environment.GetEnvironmentVariable(ModelsDirVariable);
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task EmbedAsyncResolvesBuiltinModelFilesFromModelSubdirectories()
    {
        var modelsDirectory = Path.Combine(_root, "runtime", "models");
        var modelDirectory = Path.Combine(modelsDirectory, $"{BuiltinOnnxEmbeddingModel.ModelId}-int8");
        Directory.CreateDirectory(modelDirectory);
        await File.WriteAllTextAsync(Path.Combine(modelDirectory, "model.onnx"), "not-a-real-model");
        await File.WriteAllTextAsync(
            Path.Combine(modelDirectory, "vocab.txt"),
            string.Join('\n', "[UNK]", "[CLS]", "[SEP]", "[PAD]"));
        Environment.SetEnvironmentVariable(ModelsDirVariable, modelsDirectory);
        try
        {
            using var client = new LocalOnnxEmbeddingClient();

            var exception = await Assert.ThrowsAsync<BridgeRequestException>(async () =>
                await client.EmbedAsync(
                    ["健康检查"],
                    new EmbeddingRequestOptions(
                        BuiltinOnnxEmbeddingModel.ProviderKey,
                        string.Empty,
                        string.Empty,
                        BuiltinOnnxEmbeddingModel.ModelId,
                        BuiltinOnnxEmbeddingModel.Dimensions,
                        null,
                        BuiltinOnnxEmbeddingModel.ProviderType,
                        string.Empty,
                        string.Empty,
                        BuiltinOnnxEmbeddingModel.MaxSequenceLength,
                        BuiltinOnnxEmbeddingModel.NormalizeEmbeddings),
                    CancellationToken.None));

            // 文件已从子目录解析成功才会走到 ONNX Runtime 初始化这一步。
            Assert.Contains("ONNX Runtime 初始化失败", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("模型文件缺失", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModelsDirVariable, _originalModelsDir);
        }
    }

    [Fact]
    public async Task EmbedAsyncReportsMissingModelWhenNoCandidateExists()
    {
        var modelsDirectory = Path.Combine(_root, "empty", "models");
        Directory.CreateDirectory(modelsDirectory);
        Environment.SetEnvironmentVariable(ModelsDirVariable, modelsDirectory);
        try
        {
            using var client = new LocalOnnxEmbeddingClient();

            var exception = await Assert.ThrowsAsync<BridgeRequestException>(async () =>
                await client.EmbedAsync(
                    ["健康检查"],
                    new EmbeddingRequestOptions(
                        BuiltinOnnxEmbeddingModel.ProviderKey,
                        string.Empty,
                        string.Empty,
                        BuiltinOnnxEmbeddingModel.ModelId,
                        BuiltinOnnxEmbeddingModel.Dimensions,
                        null,
                        BuiltinOnnxEmbeddingModel.ProviderType,
                        string.Empty,
                        string.Empty,
                        BuiltinOnnxEmbeddingModel.MaxSequenceLength,
                        BuiltinOnnxEmbeddingModel.NormalizeEmbeddings),
                    CancellationToken.None));

            Assert.Contains("模型文件缺失", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModelsDirVariable, _originalModelsDir);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
