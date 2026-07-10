namespace Novelist.Core.Bridge;

public sealed class BridgeRequestException : Exception
{
    public BridgeRequestException(
        string code,
        string message,
        object? details = null,
        bool retryable = false)
        : base(message)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Bridge error code is required.", nameof(code));
        }

        Code = code;
        Details = details;
        Retryable = retryable;
    }

    public string Code { get; }

    public object? Details { get; }

    public bool Retryable { get; }
}
