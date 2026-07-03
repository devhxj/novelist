namespace Novelist.Core.Bridge;

public sealed class BridgeValidationException : Exception
{
    public BridgeValidationException(string message, object? details = null)
        : base(message)
    {
        Details = details;
    }

    public object? Details { get; }
}
