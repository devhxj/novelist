namespace Novelist.Contracts.Bridge;

public static class BridgeErrorCodes
{
    public const string InvalidMessage = "INVALID_MESSAGE";
    public const string MethodNotFound = "METHOD_NOT_FOUND";
    public const string MethodNotImplemented = "METHOD_NOT_IMPLEMENTED";
    public const string AppNotInitialized = "APP_NOT_INITIALIZED";
    public const string ValidationError = "VALIDATION_ERROR";
    public const string InvalidPath = "INVALID_PATH";
    public const string LlmProviderError = "LLM_PROVIDER_ERROR";
    public const string RagUnavailable = "RAG_UNAVAILABLE";
    public const string Cancelled = "CANCELLED";
    public const string InternalError = "INTERNAL_ERROR";
}
