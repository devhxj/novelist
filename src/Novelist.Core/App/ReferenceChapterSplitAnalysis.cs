namespace Novelist.Core.App;

public interface IReferenceChapterSplitAnalyzer
{
    ValueTask<ReferenceChapterSplitModelResult> AnalyzeAsync(
        ReferenceChapterSplitModelRequest input,
        CancellationToken cancellationToken);
}

public sealed record ReferenceChapterSplitModelRequest(
    long AnchorId,
    string SourceHash,
    string NormalizedSample);

public sealed record ReferenceChapterSplitModelResult(
    string PatternKind,
    string DelimiterTemplate,
    double Confidence,
    IReadOnlyList<int> EvidenceOffsets,
    string ProviderName = "test",
    string ModelId = "test")
{
    public static ReferenceChapterSplitModelResult Empty { get; } = new(
        string.Empty,
        string.Empty,
        0,
        [],
        string.Empty,
        string.Empty);
}
