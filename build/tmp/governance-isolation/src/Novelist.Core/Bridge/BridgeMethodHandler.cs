namespace Novelist.Core.Bridge;

public delegate ValueTask<object?> BridgeMethodHandler(
    BridgeInvocationContext context,
    CancellationToken cancellationToken);
