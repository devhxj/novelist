using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceMaterializationModelPreflight
{
    ValueTask<ReferenceMaterializationModelPreflightResult> VerifyAsync(CancellationToken cancellationToken);
}

public sealed record ReferenceMaterializationModelPreflightResult(
    ReferenceMaterializationModelIdentityPayload Llm,
    ReferenceMaterializationModelIdentityPayload Embedding);

public sealed class ReferenceMaterializationException : InvalidOperationException
{
    public ReferenceMaterializationException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
