using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Infrastructure.App;

public sealed class LocalOnnxEmbeddingClient : IEmbeddingClient
{
    private const int MaxBatchSize = 512;
    private const int MaxInputLength = 200_000;
    private const int MaxModelIdLength = 256;
    private const int MaxPathLength = 2_048;
    private const int DefaultMaxSequenceLength = 512;
    private const int MaxSequenceLength = 8_192;
    private const int MaxDimensions = 1_000_000;
    private const string ProviderTypeOnnx = "onnx";

    private readonly ILocalOnnxEmbeddingRunnerFactory _runnerFactory;
    private readonly ConcurrentDictionary<string, Lazy<LocalOnnxModel>> _models = new(StringComparer.Ordinal);

    public LocalOnnxEmbeddingClient(ILocalOnnxEmbeddingRunnerFactory? runnerFactory = null)
    {
        _runnerFactory = runnerFactory ?? new ReflectionLocalOnnxEmbeddingRunnerFactory();
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
        var model = _models.GetOrAdd(
            normalizedOptions.CacheKey,
            _ => new Lazy<LocalOnnxModel>(() => CreateModel(normalizedOptions))).Value;

        var encoded = normalizedInputs
            .Select(input => model.Tokenizer.Encode(PrepareInput(input, normalizedOptions), normalizedOptions.MaxSequenceLength))
            .ToArray();
        var tensorInputs = LocalOnnxTensorInputs.From(encoded);
        var output = await model.Runner.RunAsync(tensorInputs, cancellationToken);
        if (!IsValidOutputShape(output, normalizedInputs.Count, tensorInputs.SequenceLength))
        {
            throw ProviderError("ONNX embedding output shape is invalid.", retryable: false);
        }

        if (normalizedOptions.Dimensions is not null && normalizedOptions.Dimensions.Value != output.HiddenSize)
        {
            throw ProviderError(
                $"ONNX embedding dimensions mismatch: expected {normalizedOptions.Dimensions.Value}, got {output.HiddenSize}.",
                retryable: false);
        }

        var items = new List<EmbeddingItemResult>(normalizedInputs.Count);
        for (var batch = 0; batch < normalizedInputs.Count; batch++)
        {
            var vector = ProjectVector(
                output,
                encoded[batch].AttentionMask,
                batch,
                normalizedOptions.NormalizeEmbeddings,
                normalizedOptions.PoolingStrategy);
            items.Add(new EmbeddingItemResult(batch, vector));
        }

        return new EmbeddingBatchResult(
            normalizedOptions.ModelId,
            output.HiddenSize,
            items,
            new EmbeddingUsage(
                encoded.Sum(item => item.TokenCount),
                encoded.Sum(item => item.TokenCount)));
    }

    private LocalOnnxModel CreateModel(LocalOnnxEmbeddingOptions options)
    {
        var tokenizer = BertWordPieceTokenizer.Load(options.VocabPath);
        var runner = _runnerFactory.Create(options);
        return new LocalOnnxModel(tokenizer, runner);
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
        var modelPath = ResolveOnnxModelFile(
            options.OnnxModelPath,
            "model.onnx",
            "NOVELIST_ONNX_MODEL_PATH",
            nameof(options.OnnxModelPath));
        var vocabPath = ResolveOnnxModelFile(
            options.OnnxVocabPath,
            "vocab.txt",
            "NOVELIST_ONNX_VOCAB_PATH",
            nameof(options.OnnxVocabPath));
        var runtimePath = string.IsNullOrWhiteSpace(options.OnnxRuntimePath)
            ? string.Empty
            : NormalizeLocalPath(options.OnnxRuntimePath, nameof(options.OnnxRuntimePath), mustExist: false);
        var maxSequenceLength = isBuiltinModel
            ? BuiltinOnnxEmbeddingModel.MaxSequenceLength
            : options.MaxSequenceLength ?? DefaultMaxSequenceLength;
        if (maxSequenceLength is <= 2 or > MaxSequenceLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaxSequenceLength),
                options.MaxSequenceLength,
                $"Max sequence length must be between 3 and {MaxSequenceLength}.");
        }

        var dimensions = isBuiltinModel
            ? BuiltinOnnxEmbeddingModel.Dimensions
            : options.Dimensions;
        if (dimensions is <= 0 or > MaxDimensions)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Dimensions), dimensions, $"Dimensions must be between 1 and {MaxDimensions}.");
        }

        return new LocalOnnxEmbeddingOptions(
            modelId,
            modelPath,
            vocabPath,
            runtimePath,
            maxSequenceLength,
            dimensions,
            isBuiltinModel ? BuiltinOnnxEmbeddingModel.NormalizeEmbeddings : options.NormalizeEmbeddings,
            isBuiltinModel ? BuiltinOnnxEmbeddingModel.PoolingStrategy : "mean",
            NormalizeInputKind(options.InputKind));
    }

    private static string PrepareInput(string input, LocalOnnxEmbeddingOptions options)
    {
        return IsBuiltinModelId(options.ModelId) &&
            string.Equals(options.InputKind, BuiltinOnnxEmbeddingModel.QueryInputKind, StringComparison.Ordinal)
                ? BuiltinOnnxEmbeddingModel.QueryInstruction + input
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
        string fileName,
        string environmentVariableName,
        string parameterName)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return NormalizeExistingFile(configuredPath, parameterName);
        }

        foreach (var candidate in CandidateBuiltinModelFiles(fileName, environmentVariableName)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw ProviderError(
            $"内置 ONNX embedding 模型文件缺失：{fileName}。请确认发布目录包含 runtime/models/{fileName}，或通过 {environmentVariableName} / NOVELIST_ONNX_MODELS_DIR 指向固定 BGE 模型文件。",
            retryable: false);
    }

    private static IEnumerable<string> CandidateBuiltinModelFiles(string fileName, string environmentVariableName)
    {
        var configuredFile = Environment.GetEnvironmentVariable(environmentVariableName);
        if (!string.IsNullOrWhiteSpace(configuredFile))
        {
            yield return Path.GetFullPath(ExpandLocalPath(configuredFile));
        }

        var configuredDirectory = Environment.GetEnvironmentVariable("NOVELIST_ONNX_MODELS_DIR");
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            yield return Path.Combine(Path.GetFullPath(ExpandLocalPath(configuredDirectory)), fileName);
        }

        foreach (var root in CandidateBuiltinModelDirectories())
        {
            yield return Path.Combine(root, fileName);
        }
    }

    private static IEnumerable<string> CandidateBuiltinModelDirectories()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "runtime", "models");
        yield return Path.Combine(baseDirectory, "runtime", "onnx", "models", BuiltinOnnxEmbeddingModel.ModelId);
        yield return Path.Combine(baseDirectory, "runtime", "onnx", "models");
        yield return Path.Combine(baseDirectory, "models");

        var currentDirectory = Directory.GetCurrentDirectory();
        yield return Path.Combine(currentDirectory, "build", "runtime", "models");
        yield return Path.Combine(currentDirectory, "build", "runtime", "onnx", "models", BuiltinOnnxEmbeddingModel.ModelId);
        yield return Path.Combine(currentDirectory, "build", "runtime", "onnx", "models");
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

    private sealed record LocalOnnxModel(BertWordPieceTokenizer Tokenizer, ILocalOnnxEmbeddingRunner Runner);
}

public sealed record LocalOnnxEmbeddingOptions(
    string ModelId,
    string ModelPath,
    string VocabPath,
    string RuntimePath,
    int MaxSequenceLength,
    int? Dimensions,
    bool NormalizeEmbeddings,
    string PoolingStrategy = "mean",
    string InputKind = BuiltinOnnxEmbeddingModel.DocumentInputKind)
{
    public string CacheKey => string.Join(
        "|",
        ModelId,
        ModelPath,
        VocabPath,
        RuntimePath,
        MaxSequenceLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Dimensions?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        NormalizeEmbeddings ? "1" : "0",
        PoolingStrategy);
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
    int BatchSize,
    int SequenceLength)
{
    public static LocalOnnxTensorInputs From(IReadOnlyList<BertTokenizedInput> inputs)
    {
        if (inputs.Count == 0)
        {
            throw new ArgumentException("At least one tokenized input is required.", nameof(inputs));
        }

        var sequenceLength = inputs[0].InputIds.Count;
        if (inputs.Any(input => input.InputIds.Count != sequenceLength ||
            input.AttentionMask.Count != sequenceLength ||
            input.TokenTypeIds.Count != sequenceLength))
        {
            throw new ArgumentException("Tokenized inputs must have equal sequence lengths.", nameof(inputs));
        }

        var inputIds = new long[inputs.Count * sequenceLength];
        var attentionMask = new long[inputIds.Length];
        var tokenTypeIds = new long[inputIds.Length];
        for (var batch = 0; batch < inputs.Count; batch++)
        {
            for (var index = 0; index < sequenceLength; index++)
            {
                var offset = batch * sequenceLength + index;
                inputIds[offset] = inputs[batch].InputIds[index];
                attentionMask[offset] = inputs[batch].AttentionMask[index];
                tokenTypeIds[offset] = inputs[batch].TokenTypeIds[index];
            }
        }

        return new LocalOnnxTensorInputs(inputIds, attentionMask, tokenTypeIds, inputs.Count, sequenceLength);
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

public sealed class BertWordPieceTokenizer
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
            wordPieces.RemoveRange(available, wordPieces.Count - available);
        }

        var inputIds = new List<long>(maxSequenceLength) { _clsId };
        inputIds.AddRange(wordPieces);
        inputIds.Add(_sepId);

        var attentionMask = Enumerable.Repeat(1L, inputIds.Count).ToList();
        var tokenTypeIds = Enumerable.Repeat(0L, inputIds.Count).ToList();
        while (inputIds.Count < maxSequenceLength)
        {
            inputIds.Add(_padId);
            attentionMask.Add(0);
            tokenTypeIds.Add(0);
        }

        return new BertTokenizedInput(inputIds, attentionMask, tokenTypeIds);
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
}

internal sealed class ReflectionLocalOnnxEmbeddingRunnerFactory : ILocalOnnxEmbeddingRunnerFactory
{
    public ILocalOnnxEmbeddingRunner Create(LocalOnnxEmbeddingOptions options)
    {
        return new ReflectionLocalOnnxEmbeddingRunner(options);
    }
}

internal sealed class ReflectionLocalOnnxEmbeddingRunner : ILocalOnnxEmbeddingRunner, IDisposable
{
    private const string ManagedAssemblyName = "Microsoft.ML.OnnxRuntime";
    private static readonly object NativeResolverLock = new();
    private static readonly ConcurrentDictionary<Assembly, byte> NativeResolverAssemblies = new();
    private static readonly ConcurrentDictionary<string, byte> NativeSearchDirectories = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _session;
    private readonly Type _namedOnnxValueType;
    private readonly Type _denseTensorLongType;
    private readonly MethodInfo _createLongTensorValue;
    private readonly MethodInfo _runMethod;
    private readonly LocalOnnxSessionInputNames _inputNames;
    private readonly object _sync = new();

    public ReflectionLocalOnnxEmbeddingRunner(LocalOnnxEmbeddingOptions options)
    {
        try
        {
            var assembly = LoadOnnxRuntimeAssembly(options.RuntimePath);
            RegisterNativeLibraryResolver(assembly, options.RuntimePath);
            var sessionType = assembly.GetType("Microsoft.ML.OnnxRuntime.InferenceSession", throwOnError: true)!;
            _namedOnnxValueType = assembly.GetType("Microsoft.ML.OnnxRuntime.NamedOnnxValue", throwOnError: true)!;
            _denseTensorLongType = assembly.GetType("Microsoft.ML.OnnxRuntime.Tensors.DenseTensor`1", throwOnError: true)!
                .MakeGenericType(typeof(long));
            _createLongTensorValue = FindCreateFromTensorMethod(_namedOnnxValueType, _denseTensorLongType);
            _runMethod = FindRunMethod(sessionType, _namedOnnxValueType);
            _session = Activator.CreateInstance(sessionType, options.ModelPath)
                ?? throw ProviderError("Unable to create ONNX inference session.", retryable: false);
            _inputNames = LocalOnnxSessionInputNames.From(ReadSessionInputNames(_session));
        }
        catch (BridgeRequestException)
        {
            throw;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ProviderError($"ONNX Runtime 初始化失败: {SanitizeRuntimeError(ex.InnerException)}", retryable: false);
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
            var dimensions = new[] { inputs.BatchSize, inputs.SequenceLength };
            var requestItems = new List<object>(3)
            {
                CreateInput(_inputNames.InputIdsName, inputs.InputIds, dimensions)
            };
            if (_inputNames.AttentionMaskName is not null)
            {
                requestItems.Add(CreateInput(_inputNames.AttentionMaskName, inputs.AttentionMask, dimensions));
            }

            if (_inputNames.TokenTypeIdsName is not null)
            {
                requestItems.Add(CreateInput(_inputNames.TokenTypeIdsName, inputs.TokenTypeIds, dimensions));
            }

            var requestValues = Array.CreateInstance(_namedOnnxValueType, requestItems.Count);
            for (var index = 0; index < requestItems.Count; index++)
            {
                requestValues.SetValue(requestItems[index], index);
            }

            try
            {
                using var results = _runMethod.Invoke(_session, [requestValues]) as IDisposable
                    ?? throw ProviderError("ONNX inference did not return disposable results.", retryable: false);
                var output = ExtractOutput(results);
                var values = ExtractFloatValues(output.Value);
                var shape = ExtractTensorDimensions(output.Value);
                return ValueTask.FromResult(CreateTensorOutput(output.Name, values, shape, inputs));
            }
            catch (BridgeRequestException)
            {
                throw;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ProviderError($"ONNX inference failed: {SanitizeRuntimeError(ex.InnerException)}", retryable: false);
            }
            catch (Exception ex)
            {
                throw ProviderError($"ONNX inference failed: {SanitizeRuntimeError(ex)}", retryable: false);
            }
        }
    }

    public void Dispose()
    {
        (_session as IDisposable)?.Dispose();
    }

    private object CreateInput(string name, long[] values, int[] dimensions)
    {
        try
        {
            var tensor = Activator.CreateInstance(_denseTensorLongType, values, dimensions)
                ?? throw ProviderError("Unable to create ONNX input tensor.", retryable: false);
            return _createLongTensorValue.Invoke(null, [name, tensor])
                ?? throw ProviderError("Unable to create ONNX input value.", retryable: false);
        }
        catch (BridgeRequestException)
        {
            throw;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ProviderError($"Unable to create ONNX input tensor: {SanitizeRuntimeError(ex.InnerException)}", retryable: false);
        }
        catch (Exception ex)
        {
            throw ProviderError($"Unable to create ONNX input tensor: {SanitizeRuntimeError(ex)}", retryable: false);
        }
    }

    private static MethodInfo FindCreateFromTensorMethod(Type namedOnnxValueType, Type denseTensorLongType)
    {
        var longTensorMethods = namedOnnxValueType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "CreateFromTensor" &&
                method.IsGenericMethodDefinition &&
                method.GetGenericArguments().Length == 1)
            .Select(method => method.MakeGenericMethod(typeof(long)))
            .Where(method =>
            {
                var parameters = method.GetParameters();
                return parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(string) &&
                    parameters[1].ParameterType.IsAssignableFrom(denseTensorLongType);
            })
            .ToArray();

        return longTensorMethods
            .OrderByDescending(method => method.GetParameters()[1].ParameterType == denseTensorLongType)
            .FirstOrDefault()
            ?? throw ProviderError("ONNX Runtime does not expose a compatible NamedOnnxValue.CreateFromTensor<T> method.", retryable: false);
    }

    private static MethodInfo FindRunMethod(Type sessionType, Type namedOnnxValueType)
    {
        var requestArrayType = namedOnnxValueType.MakeArrayType();
        return sessionType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == "Run")
            .Select(method => new { Method = method, Parameters = method.GetParameters() })
            .Where(item => item.Parameters.Length == 1 &&
                item.Parameters[0].ParameterType.IsAssignableFrom(requestArrayType))
            .Select(item => item.Method)
            .FirstOrDefault()
            ?? throw ProviderError("ONNX Runtime does not expose a compatible InferenceSession.Run method.", retryable: false);
    }

    private static string SanitizeRuntimeError(Exception exception)
    {
        var message = exception.Message.Trim();
        return string.IsNullOrWhiteSpace(message)
            ? exception.GetType().Name
            : message;
    }

    private static LocalOnnxNamedOutput ExtractOutput(IDisposable results)
    {
        if (results is not IEnumerable enumerable)
        {
            throw ProviderError("ONNX inference results are not enumerable.", retryable: false);
        }

        var outputs = new List<LocalOnnxNamedOutput>();
        foreach (var item in enumerable)
        {
            var name = item.GetType().GetProperty("Name")?.GetValue(item) as string ?? string.Empty;
            var value = item.GetType().GetProperty("Value")?.GetValue(item);
            if (value is not null)
            {
                outputs.Add(new LocalOnnxNamedOutput(name, value));
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

    private static float[] ExtractFloatValues(object tensor)
    {
        if (tensor is IEnumerable<float> floats)
        {
            return floats.ToArray();
        }

        var toArray = tensor.GetType().GetMethod("ToArray", Type.EmptyTypes);
        if (toArray?.Invoke(tensor, []) is float[] values)
        {
            return values;
        }

        throw ProviderError("ONNX output tensor is not a float tensor.", retryable: false);
    }

    private static IReadOnlyList<int> ExtractTensorDimensions(object tensor)
    {
        try
        {
            var dimensions = tensor.GetType().GetProperty("Dimensions")?.GetValue(tensor);
            return ToIntList(dimensions);
        }
        catch (Exception ex) when (ex is NotSupportedException or TargetInvocationException)
        {
            return [];
        }
    }

    private static IReadOnlyList<int> ToIntList(object? value)
    {
        if (value is null)
        {
            return [];
        }

        if (value is IEnumerable<int> ints)
        {
            return ints.ToArray();
        }

        if (value is IEnumerable enumerable)
        {
            var result = new List<int>();
            foreach (var item in enumerable)
            {
                if (item is null)
                {
                    continue;
                }

                result.Add(Convert.ToInt32(item, System.Globalization.CultureInfo.InvariantCulture));
            }

            return result;
        }

        return [];
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

    private static Assembly LoadOnnxRuntimeAssembly(string runtimePath)
    {
        var candidates = CandidateManagedAssemblies(runtimePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
            }
        }

        try
        {
            return Assembly.Load(ManagedAssemblyName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException)
        {
            throw ProviderError(
                "本地 ONNX Runtime 不可用：请将 Microsoft.ML.OnnxRuntime.dll 与原生 onnxruntime 库放入应用目录或 ONNX Runtime 路径。",
                retryable: false);
        }
    }

    private static void RegisterNativeLibraryResolver(Assembly assembly, string runtimePath)
    {
        foreach (var directory in CandidateNativeSearchDirectories(runtimePath, assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(directory))
            {
                NativeSearchDirectories.TryAdd(Path.GetFullPath(directory), 0);
            }
        }

        lock (NativeResolverLock)
        {
            if (!NativeResolverAssemblies.TryAdd(assembly, 0))
            {
                return;
            }

            try
            {
                NativeLibrary.SetDllImportResolver(assembly, ResolveOnnxRuntimeNativeLibrary);
            }
            catch (InvalidOperationException)
            {
                // Another resolver was already registered for this assembly. Keep the
                // search directories recorded so any existing resolver can still use
                // normal platform probing.
            }
        }
    }

    private static IntPtr ResolveOnnxRuntimeNativeLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!IsOnnxRuntimeNativeLibrary(libraryName))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in CandidateNativeLibraries(libraryName, NativeSearchDirectories.Keys))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static bool IsOnnxRuntimeNativeLibrary(string libraryName)
    {
        return Path.GetFileNameWithoutExtension(libraryName)
            .Contains("onnxruntime", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CandidateManagedAssemblies(string runtimePath)
    {
        if (!string.IsNullOrWhiteSpace(runtimePath))
        {
            if (File.Exists(runtimePath) &&
                string.Equals(Path.GetFileName(runtimePath), ManagedAssemblyName + ".dll", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(runtimePath);
            }

            if (Directory.Exists(runtimePath))
            {
                foreach (var candidate in CandidateManagedAssemblyPaths(Path.GetFullPath(runtimePath)))
                {
                    yield return candidate;
                }
            }
        }

        foreach (var root in new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "runtime"),
            Path.Combine(AppContext.BaseDirectory, "runtime", "onnx")
        })
        {
            foreach (var candidate in CandidateManagedAssemblyPaths(root))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> CandidateManagedAssemblyPaths(string root)
    {
        yield return Path.Combine(root, ManagedAssemblyName + ".dll");
        yield return Path.Combine(root, "lib", "net10.0", ManagedAssemblyName + ".dll");
        yield return Path.Combine(root, "lib", "net8.0", ManagedAssemblyName + ".dll");
        yield return Path.Combine(root, "lib", "net6.0", ManagedAssemblyName + ".dll");
        yield return Path.Combine(root, "lib", "netstandard2.0", ManagedAssemblyName + ".dll");
    }

    private static IEnumerable<string> CandidateNativeSearchDirectories(
        string runtimePath,
        string assemblyLocation)
    {
        if (!string.IsNullOrWhiteSpace(runtimePath))
        {
            if (File.Exists(runtimePath))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(runtimePath));
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    foreach (var candidate in CandidateNativeSearchDirectoriesFromRoot(directory))
                    {
                        yield return candidate;
                    }
                }
            }

            if (Directory.Exists(runtimePath))
            {
                foreach (var candidate in CandidateNativeSearchDirectoriesFromRoot(Path.GetFullPath(runtimePath)))
                {
                    yield return candidate;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(assemblyLocation))
        {
            var assemblyDirectory = Path.GetDirectoryName(Path.GetFullPath(assemblyLocation));
            if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                foreach (var candidate in CandidateNativeSearchDirectoriesFromRoot(assemblyDirectory))
                {
                    yield return candidate;
                }

                yield return Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..", "runtimes", RuntimeInformation.RuntimeIdentifier, "native"));
            }
        }

        foreach (var root in new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "runtime"),
            Path.Combine(AppContext.BaseDirectory, "runtime", "onnx")
        })
        {
            foreach (var candidate in CandidateNativeSearchDirectoriesFromRoot(root))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> CandidateNativeSearchDirectoriesFromRoot(string root)
    {
        yield return root;
        yield return Path.Combine(root, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        yield return Path.Combine(root, RuntimeInformation.RuntimeIdentifier, "native");
        yield return Path.Combine(root, "native");
    }

    private static IEnumerable<string> CandidateNativeLibraries(
        string libraryName,
        IEnumerable<string> directories)
    {
        if (Path.IsPathRooted(libraryName))
        {
            yield return libraryName;
        }

        var fileNames = CandidateNativeFileNames(libraryName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var directory in directories)
        {
            foreach (var fileName in fileNames)
            {
                yield return Path.Combine(directory, fileName);
            }
        }
    }

    private static IEnumerable<string> CandidateNativeFileNames(string libraryName)
    {
        var fileName = Path.GetFileName(libraryName);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            yield return fileName;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "onnxruntime";
        }

        yield return baseName + ".dll";
        yield return "lib" + baseName + ".so";
        yield return "lib" + baseName + ".dylib";
    }

    private static IReadOnlyList<string> ReadSessionInputNames(object session)
    {
        var metadata = session.GetType().GetProperty("InputMetadata")?.GetValue(session);
        if (metadata is not IEnumerable enumerable)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var item in enumerable)
        {
            var key = item is DictionaryEntry entry
                ? entry.Key as string
                : item.GetType().GetProperty("Key")?.GetValue(item) as string;
            if (!string.IsNullOrWhiteSpace(key))
            {
                names.Add(key);
            }
        }

        return names;
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

    private sealed record LocalOnnxNamedOutput(string Name, object Value);

    private sealed record LocalOnnxSessionInputNames(
        string InputIdsName,
        string? AttentionMaskName,
        string? TokenTypeIdsName)
    {
        public static LocalOnnxSessionInputNames From(IReadOnlyList<string> inputNames)
        {
            if (inputNames.Count == 0)
            {
                return new LocalOnnxSessionInputNames("input_ids", "attention_mask", "token_type_ids");
            }

            var inputIds = inputNames.FirstOrDefault(IsInputIdsName);
            var attentionMask = inputNames.FirstOrDefault(IsAttentionMaskName);
            var tokenTypeIds = inputNames.FirstOrDefault(IsTokenTypeIdsName);
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

            var unsupported = inputNames.Where(name => !recognized.Contains(name)).ToArray();
            if (unsupported.Length > 0)
            {
                throw ProviderError(
                    "ONNX model has unsupported required input tensors: " + string.Join(", ", unsupported),
                    retryable: false);
            }

            return new LocalOnnxSessionInputNames(inputIds, attentionMask, tokenTypeIds);
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
    }
}
