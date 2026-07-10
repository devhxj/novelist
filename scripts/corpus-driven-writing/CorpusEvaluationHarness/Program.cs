using Novelist.Infrastructure.App;

return await CorpusDrivenWritingEvaluationCommand.RunAsync(
    args,
    Console.Out,
    Console.Error,
    DateTimeOffset.UtcNow,
    CancellationToken.None);
