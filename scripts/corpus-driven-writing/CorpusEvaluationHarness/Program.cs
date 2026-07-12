using Novelist.Infrastructure.App;

if (args.Length > 0 && string.Equals(args[0], "--materialization-v1-baseline", StringComparison.Ordinal))
{
    return await ReferenceMaterializationV1BaselineCommand.RunAsync(
        args.Skip(1).ToArray(),
        Console.Out,
        Console.Error,
        CancellationToken.None);
}

return await CorpusDrivenWritingEvaluationCommand.RunAsync(
    args,
    Console.Out,
    Console.Error,
    DateTimeOffset.UtcNow,
    CancellationToken.None);
