using Novelist.Infrastructure.App;

return await CorpusWritingUsabilityStudyCommand.RunAsync(
    args,
    Console.Out,
    Console.Error,
    DateTimeOffset.UtcNow,
    CancellationToken.None);
