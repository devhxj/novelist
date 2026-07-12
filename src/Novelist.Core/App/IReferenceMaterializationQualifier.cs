namespace Novelist.Core.App;

public interface IReferenceMaterializationQualifier
{
    ValueTask<ReferenceMaterializationQualificationResult> QualifyAsync(
        ReferenceMaterializationQualificationRequest input,
        CancellationToken cancellationToken);
}

public sealed record ReferenceMaterializationLlmSelection(
    string ProviderName,
    string ModelId,
    string ReasoningEffort);

public sealed record ReferenceMaterializationQualificationRequest(
    ReferenceMaterializationLlmSelection Model,
    IReadOnlyList<ReferenceMaterializationQualificationCandidate> Candidates);

public sealed record ReferenceMaterializationQualificationCandidate(
    string CandidateId,
    string CandidateType,
    string Text,
    IReadOnlyList<ReferenceMaterializationQualificationSourceNode> SourceNodes);

public sealed record ReferenceMaterializationQualificationSourceNode(
    string NodeId,
    string Text);

public sealed record ReferenceMaterializationQualificationResult(
    IReadOnlyList<ReferenceMaterializationCandidateQualification> Decisions);

public sealed record ReferenceMaterializationCandidateQualification(
    string CandidateId,
    string Decision,
    IReadOnlyList<ReferenceMaterializationQualificationSpan> SourceSpans,
    ReferenceMaterializationQualityScores Scores,
    ReferenceMaterializationQualificationTags Tags,
    double Confidence,
    IReadOnlyList<string> ReasonCodes);

public sealed record ReferenceMaterializationQualificationSpan(
    string NodeId,
    int Start,
    int End);

public sealed record ReferenceMaterializationQualityScores(
    double SemanticCompleteness,
    double InformationDensity,
    double NarrativeValue,
    double Transferability,
    double ContextIndependence,
    double TechniqueDistinctiveness);

public sealed record ReferenceMaterializationQualificationTags(
    IReadOnlyList<string> NarrativeFunctions,
    IReadOnlyList<string> EmotionMechanics,
    IReadOnlyList<string> Pov,
    IReadOnlyList<string> Techniques)
{
    public IReadOnlyList<string> SceneBeatRoles { get; init; } = [];

    public IReadOnlyList<string> CharacterRelations { get; init; } = [];

    public IReadOnlyList<string> CausalInformationRoles { get; init; } = [];
}
