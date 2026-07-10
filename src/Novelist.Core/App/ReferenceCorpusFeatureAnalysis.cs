namespace Novelist.Core.App;

public interface IReferenceCorpusFeatureFamilyAnalyzer
{
    ValueTask<ReferenceCorpusFeatureFamilyAnalysisOutput> AnalyzeAsync(
        ReferenceCorpusFeatureFamilyAnalysisInput input,
        CancellationToken cancellationToken);
}

public sealed record ReferenceCorpusFeatureFamilyAnalysisInput(
    string RunId,
    long AnchorId,
    string NodeId,
    string NodeType,
    string NodeText,
    string Family,
    ReferenceCorpusFeatureFamilySchema Schema)
{
    public ReferenceCorpusFeatureAnalysisContext Context { get; init; } = ReferenceCorpusFeatureAnalysisContext.Empty;

    public int? MaxOutputTokens { get; init; }
}

public sealed record ReferenceCorpusFeatureAnalysisContext(
    string? SourceSegmentId,
    string? SourceSegmentType,
    ReferenceCorpusFeatureAnalysisContextNode? Parent,
    ReferenceCorpusFeatureAnalysisContextNode? Chapter,
    ReferenceCorpusFeatureAnalysisContextNode? ContainingScene,
    ReferenceCorpusFeatureAnalysisContextNode? PreviousParagraph,
    ReferenceCorpusFeatureAnalysisContextNode? NextParagraph)
{
    public static ReferenceCorpusFeatureAnalysisContext Empty { get; } = new(
        SourceSegmentId: null,
        SourceSegmentType: null,
        Parent: null,
        Chapter: null,
        ContainingScene: null,
        PreviousParagraph: null,
        NextParagraph: null);
}

public sealed record ReferenceCorpusFeatureAnalysisContextNode(
    string NodeId,
    string NodeType,
    string? SourceSegmentId,
    string? SourceSegmentType,
    int? ChapterIndex,
    int StartOffset,
    int EndOffset,
    string TextHash,
    string TextPreview);

public sealed record ReferenceCorpusFeatureFamilyAnalysisOutput(
    string ModelOutputJson,
    int TokensSpent);

public sealed record ReferenceCorpusFeatureAnalysisRunRequest(
    string RunId,
    long AnchorId,
    string NodeType,
    IReadOnlyList<string> Families,
    string AnalyzerVersion,
    string ModelProvider,
    string ModelId,
    int? TokenBudget,
 bool Resume,
 DateTimeOffset StartedAt,
 int MaxValidationAttempts = 2)
{
    public IReferenceCorpusAnalysisExecutionControl ExecutionControl { get; init; } =
 ContinueReferenceCorpusAnalysisExecutionControl.Instance;
}

public sealed record ReferenceCorpusFeatureAnalysisRunResult(
    string RunId,
    string Status,
    int TokensSpent,
    string? ResumeCursor,
    int ObservationCount,
    int ProcessedWorkItems,
    IReadOnlyList<string> Diagnostics);
