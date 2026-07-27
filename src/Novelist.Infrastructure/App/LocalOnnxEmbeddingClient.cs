using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using HuggingFaceTokenizer = Tokenizers.HuggingFace.Tokenizer.Tokenizer;

namespace Novelist.Infrastructure.App;

public sealed class LocalOnnxEmbeddingClient : IEmbeddingClient, IDisposable
{
    private const int MaxBatchSize = 512;
    private const int MaxInputLength = 200_000;
    private const int MaxModelIdLength = 256;
    private const int MaxPathLength = 2_048;
    private const int DefaultMaxSequenceLength = 512;
    private const int MaxSequenceLength = Qwen3OnnxEmbeddingModel.MaxSequenceLength;
    private const int MaxDimensions = 1_000_000;
    private const string ProviderTypeOnnx = "onnx";

    private readonly ILocalOnnxEmbeddingRunnerFactory _runnerFactory;
    private readonly ConcurrentDictionary<string, Lazy<LocalOnnxModel>> _models = new(StringComparer.Ordinal);
    private readonly object _modelGate = new();
    private bool _disposed;

    public LocalOnnxEmbeddingClient(ILocalOnnxEmbeddingRunnerFactory? runnerFactory = null)
    {
        _runnerFactory = runnerFactory ?? new LocalOnnxEmbeddingRunnerFactory();
    }

    public async ValueTask<EmbeddingBatchResult> EmbedAsync(
        IReadOnlyList<string> inputs,
        EmbeddingRequestOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);
        var normalizedInputs = NormalizeInputs(inputs);
        var normalizedOptions = NormalizeOptions(options);
        LocalOnnxModel model;
        lock (_modelGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            model = _models.GetOrAdd(
                normalizedOptions.CacheKey,
                _ => new Lazy<LocalOnnxModel>(() => CreateModel(normalizedOptions))).Value;
        }

        var encoded = normalizedInputs
            .Select(input => model.Tokenizer.Encode(PrepareInput(input, normalizedOptions), normalizedOptions.MaxSequenceLength))
            .ToArray();
        var items = new List<EmbeddingItemResult>(normalizedInputs.Count);
        int? outputDimensions = null;
        for (var offset = 0; offset < encoded.Length; offset += normalizedOptions.MicroBatchSize)
        {
            var microBatch = encoded
                .Skip(offset)
                .Take(normalizedOptions.MicroBatchSize)
                .ToArray();
            var tensorInputs = LocalOnnxTensorInputs.From(microBatch, normalizedOptions.PadTokenId);
            var output = await model.Runner.RunAsync(tensorInputs, cancellationToken);
            if (!IsValidOutputShape(output, microBatch.Length, tensorInputs.SequenceLength))
            {
                throw ProviderError("ONNX embedding output shape is invalid.", retryable: false);
            }

            if (normalizedOptions.Dimensions is not null && normalizedOptions.Dimensions.Value != output.HiddenSize)
            {
                throw ProviderError(
                    $"ONNX embedding dimensions mismatch: expected {normalizedOptions.Dimensions.Value}, got {output.HiddenSize}.",
                    retryable: false);
            }

            if (outputDimensions is not null && outputDimensions.Value != output.HiddenSize)
            {
                throw ProviderError("ONNX embedding dimensions changed between micro-batches.", retryable: false);
            }

            outputDimensions = output.HiddenSize;
            for (var batch = 0; batch < microBatch.Length; batch++)
            {
                var vector = ProjectVector(
                    output,
                    microBatch[batch].AttentionMask,
                    batch,
                    normalizedOptions.NormalizeEmbeddings,
                    normalizedOptions.PoolingStrategy);
                items.Add(new EmbeddingItemResult(offset + batch, vector));
            }
        }

        return new EmbeddingBatchResult(
            normalizedOptions.ModelId,
            outputDimensions ?? throw ProviderError("ONNX inference returned no embeddings.", retryable: false),
            items,
            new EmbeddingUsage(
                encoded.Sum(item => item.TokenCount),
                encoded.Sum(item => item.TokenCount)));
    }

    public void Dispose()
    {
        Lazy<LocalOnnxModel>[] models;
        lock (_modelGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            models = _models.Values.ToArray();
            _models.Clear();
        }

        foreach (var model in models)
        {
            if (model.IsValueCreated)
            {
                model.Value.Dispose();
            }
        }
    }

    private LocalOnnxModel CreateModel(LocalOnnxEmbeddingOptions options)
    {
        ILocalOnnxTokenizer? tokenizer = null;
        try
        {
            tokenizer = options.TokenizerKind switch
            {
                BuiltinOnnxEmbeddingModel.TokenizerKind => BertWordPieceTokenizer.Load(options.TokenizerPath),
                Qwen3OnnxEmbeddingModel.TokenizerKind => HuggingFaceJsonTokenizer.Load(options.TokenizerPath),
                _ => throw ProviderError($"Unsupported ONNX tokenizer kind: {options.TokenizerKind}.", retryable: false)
            };
            var runner = _runnerFactory.Create(options);
            return new LocalOnnxModel(tokenizer, runner);
        }
        catch (BridgeRequestException)
        {
            tokenizer?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            tokenizer?.Dispose();
            throw ProviderError($"ONNX tokenizer initialization failed: {ex.Message}", retryable: false);
        }
    }

    private static IReadOnlyList<string> NormalizeInputs(IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0)
        {
            throw new ArgumentException("At least one embedding input is required.", nameof(inputs));
        }

        if (inputs.Count > MaxBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(inputs), inputs.Count, $"Embedding batch size must be at most {MaxBatchSize}.");
        }

        var normalized = new List<string>(inputs.Count);
        foreach (var input in inputs)
        {
            var value = input?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Embedding inputs must not be empty.", nameof(inputs));
            }

            if (value.Length > MaxInputLength)
            {
                throw new ArgumentOutOfRangeException(nameof(inputs), value.Length, $"Embedding input must be at most {MaxInputLength} characters.");
            }

            if (value.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')))
            {
                throw new ArgumentException("Embedding inputs must not contain unsupported control characters.", nameof(inputs));
            }

            normalized.Add(value);
        }

        return normalized;
    }

    private static LocalOnnxEmbeddingOptions NormalizeOptions(EmbeddingRequestOptions options)
    {
        var providerType = (options.ProviderType ?? string.Empty).Trim().ToLowerInvariant();
        if (providerType.Length > 0 && providerType is not (ProviderTypeOnnx or "local" or "local_onnx" or "local-onnx"))
        {
            throw new ArgumentException("Local ONNX embedding client only supports onnx provider type.", nameof(options));
        }

        var modelId = string.IsNullOrWhiteSpace(options.ModelId)
            ? BuiltinOnnxEmbeddingModel.ModelId
            : NormalizeRequiredText(options.ModelId, nameof(options.ModelId), MaxModelIdLength);
        var isBuiltinModel = IsBuiltinModelId(modelId);
        var isQwenModel = IsQwenModelId(modelId);
        var modelDirectoryName = isQwenModel
            ? Qwen3OnnxEmbeddingModel.ModelDirectoryName
            : isBuiltinModel
                ? BuiltinOnnxEmbeddingModel.ModelDirectoryName
                : string.Empty;
        var modelFileName = isQwenModel
            ? Qwen3OnnxEmbeddingModel.ModelFileName
            : BuiltinOnnxEmbeddingModel.ModelFileName;
        var tokenizerFileName = isQwenModel
            ? Qwen3OnnxEmbeddingModel.TokenizerFileName
            : BuiltinOnnxEmbeddingModel.TokenizerFileName;
        var modelPath = ResolveOnnxModelFile(
            options.OnnxModelPath,
            modelDirectoryName,
            modelFileName,
            isQwenModel ? "NOVELIST_QWEN3_ONNX_MODEL_PATH" : "NOVELIST_ONNX_MODEL_PATH",
            nameof(options.OnnxModelPath));
        var tokenizerPath = ResolveOnnxModelFile(
            options.OnnxVocabPath,
            modelDirectoryName,
            tokenizerFileName,
            isQwenModel ? "NOVELIST_QWEN3_TOKENIZER_PATH" : "NOVELIST_ONNX_VOCAB_PATH",
            nameof(options.OnnxVocabPath));
        var maxSequenceLength = isQwenModel
            ? Qwen3OnnxEmbeddingModel.MaxSequenceLength
            : isBuiltinModel
                ? BuiltinOnnxEmbeddingModel.MaxSequenceLength
                : options.MaxSequenceLength ?? DefaultMaxSequenceLength;
        if (maxSequenceLength is <= 2 or > MaxSequenceLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaxSequenceLength),
                options.MaxSequenceLength,
                $"Max sequence length must be between 3 and {MaxSequenceLength}.");
        }

        var dimensions = isQwenModel
            ? Qwen3OnnxEmbeddingModel.Dimensions
            : isBuiltinModel
                ? BuiltinOnnxEmbeddingModel.Dimensions
                : options.Dimensions;
        if (dimensions is <= 0 or > MaxDimensions)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Dimensions), dimensions, $"Dimensions must be between 1 and {MaxDimensions}.");
        }

        return new LocalOnnxEmbeddingOptions(
            modelId,
            modelPath,
            tokenizerPath,
            maxSequenceLength,
            dimensions,
            isBuiltinModel || isQwenModel ? true : options.NormalizeEmbeddings,
            isQwenModel
                ? Qwen3OnnxEmbeddingModel.PoolingStrategy
                : isBuiltinModel
                    ? BuiltinOnnxEmbeddingModel.PoolingStrategy
                    : "mean",
            isQwenModel ? Qwen3OnnxEmbeddingModel.TokenizerKind : BuiltinOnnxEmbeddingModel.TokenizerKind,
            isQwenModel ? Qwen3OnnxEmbeddingModel.ExecutionProvider : BuiltinOnnxEmbeddingModel.ExecutionProvider,
            isQwenModel
                ? Qwen3OnnxEmbeddingModel.MicroBatchSize
                : isBuiltinModel
                    ? BuiltinOnnxEmbeddingModel.MicroBatchSize
                    : MaxBatchSize,
            isQwenModel ? Qwen3OnnxEmbeddingModel.PadTokenId : BuiltinOnnxEmbeddingModel.PadTokenId,
            NormalizeInputKind(options.InputKind));
    }

    private static string PrepareInput(string input, LocalOnnxEmbeddingOptions options)
    {
        if (!string.Equals(options.InputKind, BuiltinOnnxEmbeddingModel.QueryInputKind, StringComparison.Ordinal))
        {
            return input;
        }

        if (IsBuiltinModelId(options.ModelId))
        {
            return BuiltinOnnxEmbeddingModel.QueryInstruction + input;
        }

        return IsQwenModelId(options.ModelId)
            ? Qwen3OnnxEmbeddingModel.QueryInstruction + input
            : input;
    }

    private static bool IsValidOutputShape(LocalOnnxTensorOutput output, int expectedBatchSize, int expectedSequenceLength)
    {
        if (output.BatchSize != expectedBatchSize || output.HiddenSize <= 0)
        {
            return false;
        }

        if (output.IsPooledOutput)
        {
            return output.SequenceLength == 1 &&
                output.Values.Length == output.BatchSize * output.HiddenSize;
        }

        return output.SequenceLength == expectedSequenceLength &&
            output.Values.Length == output.BatchSize * output.SequenceLength * output.HiddenSize;
    }

    private static IReadOnlyList<float> ProjectVector(
        LocalOnnxTensorOutput output,
        IReadOnlyList<long> attentionMask,
        int batch,
        bool normalize,
        string poolingStrategy)
    {
        if (output.IsPooledOutput)
        {
            return CopyPooledVector(output, batch, normalize);
        }

        return string.Equals(poolingStrategy, BuiltinOnnxEmbeddingModel.PoolingStrategy, StringComparison.Ordinal)
            ? ClsPool(output, batch, normalize)
            : string.Equals(poolingStrategy, Qwen3OnnxEmbeddingModel.PoolingStrategy, StringComparison.Ordinal)
                ? LastTokenPool(output, attentionMask, batch, normalize)
                : MeanPool(output, attentionMask, batch, normalize);
    }

    private static IReadOnlyList<float> CopyPooledVector(
        LocalOnnxTensorOutput output,
        int batch,
        bool normalize)
    {
        var vector = new float[output.HiddenSize];
        Array.Copy(output.Values, batch * output.HiddenSize, vector, 0, output.HiddenSize);
        NormalizeVector(vector, normalize);
        return vector;
    }

    private static IReadOnlyList<float> ClsPool(
        LocalOnnxTensorOutput output,
        int batch,
        bool normalize)
    {
        var vector = new float[output.HiddenSize];
        var offset = batch * output.SequenceLength * output.HiddenSize;
        Array.Copy(output.Values, offset, vector, 0, output.HiddenSize);
        NormalizeVector(vector, normalize);
        return vector;
    }

    private static IReadOnlyList<float> MeanPool(
        LocalOnnxTensorOutput output,
        IReadOnlyList<long> attentionMask,
        int batch,
        bool normalize)
    {
        var vector = new float[output.HiddenSize];
        var tokenCount = 0;
        for (var token = 0; token < output.SequenceLength && token < attentionMask.Count; token++)
        {
            if (attentionMask[token] == 0)
            {
                continue;
            }

            tokenCount++;
            var offset = ((batch * output.SequenceLength) + token) * output.HiddenSize;
            for (var dimension = 0; dimension < output.HiddenSize; dimension++)
            {
                vector[dimension] += output.Values[offset + dimension];
            }
        }

        if (tokenCount == 0)
        {
            throw ProviderError("ONNX embedding attention mask is empty.", retryable: false);
        }

        for (var dimension = 0; dimension < vector.Length; dimension++)
        {
            vector[dimension] /= tokenCount;
        }

        NormalizeVector(vector, normalize);
        return vector;
    }

    private static IReadOnlyList<float> LastTokenPool(
        LocalOnnxTensorOutput output,
        IReadOnlyList<long> attentionMask,
        int batch,
        bool normalize)
    {
        var token = -1;
        for (var index = 0; index < attentionMask.Count && index < output.SequenceLength; index++)
        {
            if (attentionMask[index] != 0)
            {
                token = index;
            }
        }

        if (token < 0)
        {
            throw ProviderError("ONNX embedding attention mask is empty.", retryable: false);
        }

        var vector = new float[output.HiddenSize];
        var offset = ((batch * output.SequenceLength) + token) * output.HiddenSize;
        Array.Copy(output.Values, offset, vector, 0, output.HiddenSize);
        NormalizeVector(vector, normalize);
        return vector;
    }

    private static void NormalizeVector(float[] vector, bool normalize)
    {
        if (normalize)
        {
            var norm = Math.Sqrt(vector.Sum(value => value * value));
            if (norm > 0)
            {
                for (var dimension = 0; dimension < vector.Length; dimension++)
                {
                    vector[dimension] = (float)(vector[dimension] / norm);
                }
            }
        }
    }

    private static string NormalizeExistingFile(string? raw, string name)
    {
        var path = NormalizeLocalPath(raw, name, mustExist: true);
        if (!File.Exists(path))
        {
            throw new ArgumentException($"Local ONNX embedding file was not found: {path}", name);
        }

        return path;
    }

    private static string ResolveOnnxModelFile(
        string? configuredPath,
        string modelDirectoryName,
        string fileName,
        string environmentVariableName,
        string parameterName)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return NormalizeExistingFile(configuredPath, parameterName);
        }

        foreach (var candidate in CandidateBuiltinModelFiles(modelDirectoryName, fileName, environmentVariableName)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw ProviderError(
            $"内置 ONNX embedding 模型文件缺失：runtime/models/{modelDirectoryName}/{fileName}。请部署完整模型目录，或通过 {environmentVariableName} / NOVELIST_ONNX_MODELS_DIR 指向对应文件。",
            retryable: false);
    }

    private static IEnumerable<string> CandidateBuiltinModelFiles(
        string modelDirectoryName,
        string fileName,
        string environmentVariableName)
    {
        var configuredFile = Environment.GetEnvironmentVariable(environmentVariableName);
        if (!string.IsNullOrWhiteSpace(configuredFile))
        {
            yield return Path.GetFullPath(ExpandLocalPath(configuredFile));
        }

        var configuredDirectory = Environment.GetEnvironmentVariable("NOVELIST_ONNX_MODELS_DIR");
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            yield return Path.Combine(
                Path.GetFullPath(ExpandLocalPath(configuredDirectory)),
                modelDirectoryName,
                fileName);
        }

        foreach (var root in CandidateBuiltinModelRoots())
        {
            yield return Path.Combine(root, modelDirectoryName, fileName);
        }
    }

    private static IEnumerable<string> CandidateBuiltinModelRoots()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "runtime", "models");

        var currentDirectory = Directory.GetCurrentDirectory();
        yield return Path.Combine(currentDirectory, "build", "runtime", "models");
    }

    private static string NormalizeLocalPath(string? raw, string name, bool mustExist)
    {
        var value = NormalizeRequiredText(raw, name, MaxPathLength);
        var fullPath = Path.GetFullPath(ExpandLocalPath(value));
        if (fullPath.Length > MaxPathLength)
        {
            throw new ArgumentOutOfRangeException(name, fullPath.Length, $"Path must be at most {MaxPathLength} characters.");
        }

        if (mustExist && !File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new ArgumentException($"Local ONNX embedding path was not found: {fullPath}", name);
        }

        return fullPath;
    }

    private static string ExpandLocalPath(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value);
        if (expanded == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (expanded.StartsWith("~/", StringComparison.Ordinal) ||
            expanded.StartsWith(@"~\", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[2..]);
        }

        return expanded;
    }

    private static string NormalizeRequiredText(string? value, string name, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value must be a non-empty string.", name);
        }

        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(name, normalized.Length, $"Value must be at most {maxLength} characters.");
        }

        if (normalized.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')))
        {
            throw new ArgumentException("Value must not contain unsupported control characters.", name);
        }

        return normalized;
    }

    private static bool IsBuiltinModelId(string modelId)
    {
        return string.Equals(modelId, BuiltinOnnxEmbeddingModel.ModelId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modelId, "builtin:" + BuiltinOnnxEmbeddingModel.ModelId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modelId, "Xenova/" + BuiltinOnnxEmbeddingModel.ModelId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQwenModelId(string modelId)
    {
        return string.Equals(modelId, Qwen3OnnxEmbeddingModel.ModelId, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeInputKind(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" => BuiltinOnnxEmbeddingModel.DocumentInputKind,
            "doc" or "docs" or "document" or "documents" or "index" or "chunk" or "chunks" => BuiltinOnnxEmbeddingModel.DocumentInputKind,
            "query" or "search" => BuiltinOnnxEmbeddingModel.QueryInputKind,
            _ => throw new ArgumentException("Embedding input kind must be document or query.", nameof(value))
        };
    }

    private static BridgeRequestException ProviderError(string message, bool retryable)
    {
        return new BridgeRequestException(
            BridgeErrorCodes.LlmProviderError,
            message,
            retryable: retryable);
    }

    private sealed record LocalOnnxModel(ILocalOnnxTokenizer Tokenizer, ILocalOnnxEmbeddingRunner Runner) : IDisposable
    {
        public void Dispose()
        {
            Tokenizer.Dispose();
            if (Runner is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}

public sealed record LocalOnnxEmbeddingOptions(
    string ModelId,
    string ModelPath,
    string TokenizerPath,
    int MaxSequenceLength,
    int? Dimensions,
    bool NormalizeEmbeddings,
    string PoolingStrategy = "mean",
    string TokenizerKind = BuiltinOnnxEmbeddingModel.TokenizerKind,
    string ExecutionProvider = BuiltinOnnxEmbeddingModel.ExecutionProvider,
    int MicroBatchSize = 512,
    long PadTokenId = BuiltinOnnxEmbeddingModel.PadTokenId,
    string InputKind = BuiltinOnnxEmbeddingModel.DocumentInputKind)
{
    public string CacheKey => string.Join(
        "|",
        ModelId,
        ModelPath,
        TokenizerPath,
        MaxSequenceLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Dimensions?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        NormalizeEmbeddings ? "1" : "0",
        PoolingStrategy,
        TokenizerKind,
        ExecutionProvider,
        MicroBatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public interface ILocalOnnxEmbeddingRunnerFactory
{
    ILocalOnnxEmbeddingRunner Create(LocalOnnxEmbeddingOptions options);
}

public interface ILocalOnnxEmbeddingRunner
{
    ValueTask<LocalOnnxTensorOutput> RunAsync(
        LocalOnnxTensorInputs inputs,
        CancellationToken cancellationToken);
}

public sealed record LocalOnnxTensorInputs(
    long[] InputIds,
    long[] AttentionMask,
    long[] TokenTypeIds,
    long[] PositionIds,
    int BatchSize,
    int SequenceLength)
{
    public static LocalOnnxTensorInputs From(IReadOnlyList<BertTokenizedInput> inputs, long padTokenId = 0)
    {
        if (inputs.Count == 0)
        {
            throw new ArgumentException("At least one tokenized input is required.", nameof(inputs));
        }

        if (inputs.Any(input => input.InputIds.Count == 0 ||
            input.InputIds.Count != input.AttentionMask.Count ||
            input.InputIds.Count != input.TokenTypeIds.Count))
        {
            throw new ArgumentException("Tokenized input tensor lengths are invalid.", nameof(inputs));
        }

        var sequenceLength = inputs.Max(input => input.InputIds.Count);
        var inputIds = new long[inputs.Count * sequenceLength];
        if (padTokenId != 0)
        {
            Array.Fill(inputIds, padTokenId);
        }

        var attentionMask = new long[inputIds.Length];
        var tokenTypeIds = new long[inputIds.Length];
        var positionIds = new long[inputIds.Length];
        for (var batch = 0; batch < inputs.Count; batch++)
        {
            for (var index = 0; index < inputs[batch].InputIds.Count; index++)
            {
                var offset = batch * sequenceLength + index;
                inputIds[offset] = inputs[batch].InputIds[index];
                attentionMask[offset] = inputs[batch].AttentionMask[index];
                tokenTypeIds[offset] = inputs[batch].TokenTypeIds[index];
            }

            for (var index = 0; index < sequenceLength; index++)
            {
                positionIds[(batch * sequenceLength) + index] = index;
            }
        }

        return new LocalOnnxTensorInputs(
            inputIds,
            attentionMask,
            tokenTypeIds,
            positionIds,
            inputs.Count,
            sequenceLength);
    }
}

public sealed record LocalOnnxTensorOutput(
    float[] Values,
    int BatchSize,
    int SequenceLength,
    int HiddenSize,
    bool IsPooledOutput = false);

public sealed record BertTokenizedInput(
    IReadOnlyList<long> InputIds,
    IReadOnlyList<long> AttentionMask,
    IReadOnlyList<long> TokenTypeIds)
{
    public int TokenCount => AttentionMask.Count(value => value != 0);
}

internal interface ILocalOnnxTokenizer : IDisposable
{
    BertTokenizedInput Encode(string text, int maxSequenceLength);
}

public sealed class BertWordPieceTokenizer : ILocalOnnxTokenizer
{
    private const string UnknownToken = "[UNK]";
    private const string ClsToken = "[CLS]";
    private const string SepToken = "[SEP]";
    private const string PadToken = "[PAD]";
    private const int MaxInputCharsPerWord = 100;

    private readonly IReadOnlyDictionary<string, long> _vocabulary;
    private readonly long _unknownId;
    private readonly long _clsId;
    private readonly long _sepId;
    private readonly long _padId;

    private BertWordPieceTokenizer(IReadOnlyDictionary<string, long> vocabulary)
    {
        _vocabulary = vocabulary;
        _unknownId = RequiredToken(vocabulary, UnknownToken);
        _clsId = RequiredToken(vocabulary, ClsToken);
        _sepId = RequiredToken(vocabulary, SepToken);
        _padId = RequiredToken(vocabulary, PadToken);
    }

    public static BertWordPieceTokenizer Load(string vocabPath)
    {
        var vocabulary = new Dictionary<string, long>(StringComparer.Ordinal);
        var index = 0L;
        foreach (var line in File.ReadLines(vocabPath))
        {
            var token = line.Trim();
            if (token.Length == 0 || vocabulary.ContainsKey(token))
            {
                index++;
                continue;
            }

            vocabulary[token] = index++;
        }

        return new BertWordPieceTokenizer(vocabulary);
    }

    public BertTokenizedInput Encode(string text, int maxSequenceLength)
    {
        if (maxSequenceLength <= 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSequenceLength), maxSequenceLength, "Max sequence length must be greater than 2.");
        }

        var wordPieces = new List<long>();
        foreach (var token in BasicTokenize(text))
        {
            wordPieces.AddRange(WordPieceTokenize(token));
        }

        var available = maxSequenceLength - 2;
        if (wordPieces.Count > available)
        {
            throw TokenLimitError(wordPieces.Count + 2, maxSequenceLength);
        }

        var inputIds = new List<long>(maxSequenceLength) { _clsId };
        inputIds.AddRange(wordPieces);
        inputIds.Add(_sepId);

        var attentionMask = Enumerable.Repeat(1L, inputIds.Count).ToList();
        var tokenTypeIds = Enumerable.Repeat(0L, inputIds.Count).ToList();

        return new BertTokenizedInput(inputIds, attentionMask, tokenTypeIds);
    }

    public void Dispose()
    {
    }

    private IEnumerable<string> BasicTokenize(string text)
    {
        var current = new List<char>();
        foreach (var raw in text)
        {
            var value = char.ToLowerInvariant(raw);
            if (char.IsWhiteSpace(value) || char.IsControl(value))
            {
                foreach (var token in FlushCurrent(current))
                {
                    yield return token;
                }

                continue;
            }

            if (IsCjk(value) || IsPunctuation(value))
            {
                foreach (var token in FlushCurrent(current))
                {
                    yield return token;
                }

                yield return value.ToString();
                continue;
            }

            current.Add(value);
        }

        foreach (var token in FlushCurrent(current))
        {
            yield return token;
        }
    }

    private IEnumerable<long> WordPieceTokenize(string token)
    {
        if (token.Length > MaxInputCharsPerWord)
        {
            yield return _unknownId;
            yield break;
        }

        var start = 0;
        var pieces = new List<long>();
        while (start < token.Length)
        {
            var end = token.Length;
            long? current = null;
            while (start < end)
            {
                var candidate = token[start..end];
                if (start > 0)
                {
                    candidate = "##" + candidate;
                }

                if (_vocabulary.TryGetValue(candidate, out var id))
                {
                    current = id;
                    break;
                }

                end--;
            }

            if (current is null)
            {
                yield return _unknownId;
                yield break;
            }

            pieces.Add(current.Value);
            start = end;
        }

        foreach (var piece in pieces)
        {
            yield return piece;
        }
    }

    private static IEnumerable<string> FlushCurrent(List<char> current)
    {
        if (current.Count == 0)
        {
            yield break;
        }

        yield return new string(current.ToArray());
        current.Clear();
    }

    private static bool IsCjk(char value)
    {
        return value is >= '\u4E00' and <= '\u9FFF' or
            >= '\u3400' and <= '\u4DBF' or
            >= '\uF900' and <= '\uFAFF';
    }

    private static bool IsPunctuation(char value)
    {
        return char.IsPunctuation(value) ||
            value is >= '\u3000' and <= '\u303F' or
            >= '\uFF00' and <= '\uFFEF';
    }

    private static long RequiredToken(IReadOnlyDictionary<string, long> vocabulary, string token)
    {
        return vocabulary.TryGetValue(token, out var value)
            ? value
            : throw new ArgumentException($"ONNX vocab is missing required token {token}.");
    }

    private static BridgeRequestException TokenLimitError(int tokenCount, int maxSequenceLength)
    {
        return new BridgeRequestException(
            BridgeErrorCodes.LlmProviderError,
            $"ONNX embedding input exceeds token limit {maxSequenceLength}: got {tokenCount} tokens.",
            retryable: false);
    }
}

internal sealed class HuggingFaceJsonTokenizer : ILocalOnnxTokenizer
{
    private readonly HuggingFaceTokenizer _tokenizer;

    private HuggingFaceJsonTokenizer(HuggingFaceTokenizer tokenizer)
    {
        _tokenizer = tokenizer;
    }

    public static HuggingFaceJsonTokenizer Load(string tokenizerPath)
    {
        return new HuggingFaceJsonTokenizer(HuggingFaceTokenizer.FromFile(tokenizerPath));
    }

    public BertTokenizedInput Encode(string text, int maxSequenceLength)
    {
        var encodings = _tokenizer.Encode(
            text,
            addSpecialTokens: true,
            includeAttentionMask: true);
        var encoding = encodings.SingleOrDefault()
            ?? throw new InvalidOperationException("Hugging Face tokenizer returned no encoding.");
        if (encoding.Ids.Count > maxSequenceLength)
        {
            throw new BridgeRequestException(
                BridgeErrorCodes.LlmProviderError,
                $"ONNX embedding input exceeds token limit {maxSequenceLength}: got {encoding.Ids.Count} tokens.",
                retryable: false);
        }

        var inputIds = encoding.Ids.Select(value => (long)value).ToArray();
        var attentionMask = encoding.AttentionMask.Select(value => (long)value).ToArray();
        if (attentionMask.Length != inputIds.Length)
        {
            throw new InvalidOperationException("Hugging Face tokenizer returned an invalid attention mask.");
        }

        return new BertTokenizedInput(
            inputIds,
            attentionMask,
            new long[inputIds.Length]);
    }

    public void Dispose()
    {
        _tokenizer.Dispose();
    }
}

internal sealed class LocalOnnxEmbeddingRunnerFactory : ILocalOnnxEmbeddingRunnerFactory
{
    public ILocalOnnxEmbeddingRunner Create(LocalOnnxEmbeddingOptions options)
    {
        return new LocalOnnxEmbeddingRunner(options);
    }
}

internal sealed class LocalOnnxEmbeddingRunner : ILocalOnnxEmbeddingRunner, IDisposable
{
    private readonly InferenceSession _session;
    private readonly LocalOnnxSessionInputNames _inputNames;
    private readonly object _sync = new();

    public LocalOnnxEmbeddingRunner(LocalOnnxEmbeddingOptions options)
    {
        try
        {
            using var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            if (string.Equals(
                options.ExecutionProvider,
                Qwen3OnnxEmbeddingModel.ExecutionProvider,
                StringComparison.Ordinal))
            {
                sessionOptions.EnableMemoryPattern = false;
                sessionOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                // DirectML leaves dynamic shape-tensor work on CPU; session and inference failures still propagate.
                sessionOptions.AppendExecutionProvider_DML(0);
            }
            else if (!string.Equals(
                options.ExecutionProvider,
                BuiltinOnnxEmbeddingModel.ExecutionProvider,
                StringComparison.Ordinal))
            {
                throw ProviderError(
                    $"Unsupported ONNX execution provider: {options.ExecutionProvider}.",
                    retryable: false);
            }

            _session = new InferenceSession(options.ModelPath, sessionOptions);
            _inputNames = LocalOnnxSessionInputNames.From(ReadSessionInputNames(_session));
        }
        catch (BridgeRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ProviderError($"ONNX Runtime 初始化失败: {SanitizeRuntimeError(ex)}", retryable: false);
        }
    }

    public ValueTask<LocalOnnxTensorOutput> RunAsync(
        LocalOnnxTensorInputs inputs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestItems = new List<NamedOnnxValue>(4)
            {
                CreateInput(_inputNames.InputIdsName, inputs.InputIds, inputs.BatchSize, inputs.SequenceLength)
            };
            if (_inputNames.AttentionMaskName is not null)
            {
                requestItems.Add(CreateInput(_inputNames.AttentionMaskName, inputs.AttentionMask, inputs.BatchSize, inputs.SequenceLength));
            }

            if (_inputNames.TokenTypeIdsName is not null)
            {
                requestItems.Add(CreateInput(_inputNames.TokenTypeIdsName, inputs.TokenTypeIds, inputs.BatchSize, inputs.SequenceLength));
            }

            if (_inputNames.PositionIdsName is not null)
            {
                requestItems.Add(CreateInput(_inputNames.PositionIdsName, inputs.PositionIds, inputs.BatchSize, inputs.SequenceLength));
            }

            try
            {
                using var results = _session.Run(requestItems);
                var output = ExtractOutput(results);
                return ValueTask.FromResult(CreateTensorOutput(output.Name, output.Values, output.Dimensions, inputs));
            }
            catch (BridgeRequestException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw ProviderError($"ONNX inference failed: {SanitizeRuntimeError(ex)}", retryable: false);
            }
        }
    }

    public void Dispose()
    {
        _session.Dispose();
    }

    private static NamedOnnxValue CreateInput(string name, long[] values, int batchSize, int sequenceLength)
    {
        var dimensions = new[] { batchSize, sequenceLength };
        var tensor = new DenseTensor<long>(values.AsMemory(), dimensions);
        return NamedOnnxValue.CreateFromTensor(name, tensor);
    }

    private static string SanitizeRuntimeError(Exception exception)
    {
        var message = exception.Message.Trim();
        return string.IsNullOrWhiteSpace(message)
            ? exception.GetType().Name
            : message;
    }

    private static LocalOnnxNamedTensorOutput ExtractOutput(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        var outputs = new List<LocalOnnxNamedTensorOutput>();
        foreach (var item in results)
        {
            if (item.Value is Tensor<float> tensor)
            {
                outputs.Add(new LocalOnnxNamedTensorOutput(
                    item.Name ?? string.Empty,
                    tensor.ToArray(),
                    tensor.Dimensions.ToArray()));
            }
            else if (item.Value is Tensor<Float16> halfTensor)
            {
                outputs.Add(new LocalOnnxNamedTensorOutput(
                    item.Name ?? string.Empty,
                    halfTensor.Select(value => (float)value).ToArray(),
                    halfTensor.Dimensions.ToArray()));
            }
        }

        if (outputs.Count == 0)
        {
            throw ProviderError("ONNX inference returned no outputs.", retryable: false);
        }

        return outputs.FirstOrDefault(item => IsPooledOutputName(item.Name)) ??
            outputs.FirstOrDefault(item => string.Equals(item.Name, "last_hidden_state", StringComparison.Ordinal)) ??
            outputs[0];
    }

    private static LocalOnnxTensorOutput CreateTensorOutput(
        string outputName,
        float[] values,
        IReadOnlyList<int> shape,
        LocalOnnxTensorInputs inputs)
    {
        if (shape.Count == 3)
        {
            ValidateDimension(shape[0], inputs.BatchSize, "batch");
            ValidateDimension(shape[1], inputs.SequenceLength, "sequence");
            ValidatePositiveDimension(shape[2], "hidden");
            var hiddenSize = shape[2];
            if (values.LongLength != (long)inputs.BatchSize * inputs.SequenceLength * hiddenSize)
            {
                throw ProviderError("ONNX last_hidden_state shape is not compatible with embedding inputs.", retryable: false);
            }

            return new LocalOnnxTensorOutput(values, inputs.BatchSize, inputs.SequenceLength, hiddenSize);
        }

        if (shape.Count == 2)
        {
            ValidateDimension(shape[0], inputs.BatchSize, "batch");
            ValidatePositiveDimension(shape[1], "hidden");
            var hiddenSize = shape[1];
            if (values.LongLength != (long)inputs.BatchSize * hiddenSize)
            {
                throw ProviderError("ONNX pooled embedding shape is not compatible with embedding inputs.", retryable: false);
            }

            return new LocalOnnxTensorOutput(values, inputs.BatchSize, 1, hiddenSize, IsPooledOutput: true);
        }

        if (shape.Count == 1 && inputs.BatchSize == 1)
        {
            ValidatePositiveDimension(shape[0], "hidden");
            if (values.LongLength != shape[0])
            {
                throw ProviderError("ONNX single embedding shape is not compatible with embedding inputs.", retryable: false);
            }

            return new LocalOnnxTensorOutput(values, inputs.BatchSize, 1, shape[0], IsPooledOutput: true);
        }

        if (IsPooledOutputName(outputName) && values.LongLength % inputs.BatchSize == 0)
        {
            var hiddenSize = checked((int)(values.LongLength / inputs.BatchSize));
            return new LocalOnnxTensorOutput(values, inputs.BatchSize, 1, hiddenSize, IsPooledOutput: true);
        }

        var sequenceVectorLength = (long)inputs.BatchSize * inputs.SequenceLength;
        if (values.LongLength % sequenceVectorLength == 0)
        {
            var hiddenSize = checked((int)(values.LongLength / sequenceVectorLength));
            return new LocalOnnxTensorOutput(values, inputs.BatchSize, inputs.SequenceLength, hiddenSize);
        }

        if (values.LongLength % inputs.BatchSize == 0)
        {
            var hiddenSize = checked((int)(values.LongLength / inputs.BatchSize));
            return new LocalOnnxTensorOutput(values, inputs.BatchSize, 1, hiddenSize, IsPooledOutput: true);
        }

        throw ProviderError("ONNX output shape is not compatible with embedding inputs.", retryable: false);
    }

    private static void ValidateDimension(int actual, int expected, string name)
    {
        if (actual != expected)
        {
            throw ProviderError($"ONNX output {name} dimension mismatch: expected {expected}, got {actual}.", retryable: false);
        }
    }

    private static void ValidatePositiveDimension(int value, string name)
    {
        if (value <= 0)
        {
            throw ProviderError($"ONNX output {name} dimension must be positive.", retryable: false);
        }
    }

    private static bool IsPooledOutputName(string outputName)
    {
        var normalized = NormalizeName(outputName);
        return normalized is "sentence_embedding" or "sentenceembedding" or "pooler_output" or
            "pooleroutput" or "pooled_output" or "pooledoutput" or "embedding" or "embeddings" ||
            normalized.Contains("sentence", StringComparison.Ordinal) ||
            normalized.Contains("pooler", StringComparison.Ordinal) ||
            normalized.Contains("pooled", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ReadSessionInputNames(InferenceSession session)
    {
        return session.InputMetadata.Keys
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
    }

    private static string NormalizeName(string value)
    {
        return value.Trim().ToLowerInvariant()
            .Replace('-', '_')
            .Replace('.', '_')
            .Replace(':', '_');
    }

    private static BridgeRequestException ProviderError(string message, bool retryable)
    {
        return new BridgeRequestException(
            BridgeErrorCodes.LlmProviderError,
            message,
            retryable: retryable);
    }

    private sealed record LocalOnnxNamedTensorOutput(
        string Name,
        float[] Values,
        IReadOnlyList<int> Dimensions);

    private sealed record LocalOnnxSessionInputNames(
        string InputIdsName,
        string? AttentionMaskName,
        string? TokenTypeIdsName,
        string? PositionIdsName)
    {
        public static LocalOnnxSessionInputNames From(IReadOnlyList<string> inputNames)
        {
            if (inputNames.Count == 0)
            {
                return new LocalOnnxSessionInputNames("input_ids", "attention_mask", "token_type_ids", null);
            }

            var inputIds = inputNames.FirstOrDefault(IsInputIdsName);
            var attentionMask = inputNames.FirstOrDefault(IsAttentionMaskName);
            var tokenTypeIds = inputNames.FirstOrDefault(IsTokenTypeIdsName);
            var positionIds = inputNames.FirstOrDefault(IsPositionIdsName);
            if (inputIds is null && inputNames.Count == 1)
            {
                inputIds = inputNames[0];
            }

            if (inputIds is null)
            {
                throw ProviderError(
                    "ONNX model input schema is not supported. Expected an input_ids tensor.",
                    retryable: false);
            }

            var recognized = new HashSet<string>(StringComparer.Ordinal)
            {
                inputIds
            };
            if (attentionMask is not null)
            {
                recognized.Add(attentionMask);
            }

            if (tokenTypeIds is not null)
            {
                recognized.Add(tokenTypeIds);
            }

            if (positionIds is not null)
            {
                recognized.Add(positionIds);
            }

            var unsupported = inputNames.Where(name => !recognized.Contains(name)).ToArray();
            if (unsupported.Length > 0)
            {
                throw ProviderError(
                    "ONNX model has unsupported required input tensors: " + string.Join(", ", unsupported),
                    retryable: false);
            }

            return new LocalOnnxSessionInputNames(inputIds, attentionMask, tokenTypeIds, positionIds);
        }

        private static bool IsInputIdsName(string name)
        {
            var normalized = NormalizeName(name);
            return normalized.Contains("input_ids", StringComparison.Ordinal) ||
                normalized.Contains("inputids", StringComparison.Ordinal) ||
                (normalized.Contains("input", StringComparison.Ordinal) &&
                    normalized.Contains("id", StringComparison.Ordinal));
        }

        private static bool IsAttentionMaskName(string name)
        {
            var normalized = NormalizeName(name);
            return normalized.Contains("attention_mask", StringComparison.Ordinal) ||
                normalized.Contains("attentionmask", StringComparison.Ordinal) ||
                normalized.Contains("mask", StringComparison.Ordinal);
        }

        private static bool IsTokenTypeIdsName(string name)
        {
            var normalized = NormalizeName(name);
            return normalized.Contains("token_type_ids", StringComparison.Ordinal) ||
                normalized.Contains("tokentypeids", StringComparison.Ordinal) ||
                normalized.Contains("segment_ids", StringComparison.Ordinal) ||
                normalized.Contains("segmentids", StringComparison.Ordinal);
        }

        private static bool IsPositionIdsName(string name)
        {
            var normalized = NormalizeName(name);
            return normalized.Contains("position_ids", StringComparison.Ordinal) ||
                normalized.Contains("positionids", StringComparison.Ordinal);
        }
    }
}
