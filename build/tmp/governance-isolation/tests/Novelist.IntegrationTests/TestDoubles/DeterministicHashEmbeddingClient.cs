using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Novelist.Core.App;

namespace Novelist.IntegrationTests.TestDoubles;

public sealed class DeterministicHashEmbeddingClient : IEmbeddingClient
{
    public const string DefaultModelId = "deterministic-hash-embedding";

    private const string HashDomain = "novelist.integration-tests.deterministic-hash-embedding.v1";
    private readonly object _gate = new();
    private readonly List<DeterministicHashEmbeddingCall> _calls = [];
    private readonly int _defaultDimensions;

    public DeterministicHashEmbeddingClient(int defaultDimensions = 16)
    {
        if (defaultDimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultDimensions), "Embedding dimensions must be positive.");
        }

        _defaultDimensions = defaultDimensions;
    }

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

        var dimensions = options.Dimensions ?? _defaultDimensions;
        if (dimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Embedding dimensions must be positive.");
        }

        var inputSnapshot = inputs
            .Select(input => input ?? throw new ArgumentException("Embedding inputs cannot contain null values.", nameof(inputs)))
            .ToArray();

        lock (_gate)
        {
            _calls.Add(new DeterministicHashEmbeddingCall(inputSnapshot, options));
        }

        var items = inputSnapshot
            .Select((input, index) => new EmbeddingItemResult(
                index,
                CreateVector(input, dimensions, options.NormalizeEmbeddings)))
            .ToArray();
        var promptTokens = inputSnapshot.Sum(EstimatePromptTokens);
        var model = string.IsNullOrWhiteSpace(options.ModelId) ? DefaultModelId : options.ModelId;

        return ValueTask.FromResult(new EmbeddingBatchResult(
            model,
            dimensions,
            items,
            new EmbeddingUsage(promptTokens, promptTokens)));
    }

    public static IReadOnlyList<float> CreateVector(string input, int dimensions, bool normalize = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (dimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Embedding dimensions must be positive.");
        }

        var vector = new float[dimensions];
        var filled = 0;
        var block = 0;
        while (filled < dimensions)
        {
            var payload = Encoding.UTF8.GetBytes($"{HashDomain}\u001f{block}\u001f{input}");
            var hash = SHA256.HashData(payload);
            for (var offset = 0; offset + 4 <= hash.Length && filled < dimensions; offset += 4)
            {
                var raw = BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(offset, 4));
                vector[filled] = ((raw / (float)uint.MaxValue) * 2f) - 1f;
                filled++;
            }

            block++;
        }

        if (normalize)
        {
            NormalizeInPlace(vector);
        }

        return vector;
    }

    private static int EstimatePromptTokens(string input)
    {
        return Math.Max(1, (input.Length + 3) / 4);
    }

    private static void NormalizeInPlace(float[] vector)
    {
        var sumSquares = 0.0;
        foreach (var value in vector)
        {
            sumSquares += value * value;
        }

        if (sumSquares <= 0)
        {
            return;
        }

        var scale = 1.0 / Math.Sqrt(sumSquares);
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] = (float)(vector[index] * scale);
        }
    }
}

public sealed record DeterministicHashEmbeddingCall(
    IReadOnlyList<string> Inputs,
    EmbeddingRequestOptions Options);
