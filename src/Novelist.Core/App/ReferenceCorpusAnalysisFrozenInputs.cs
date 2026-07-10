namespace Novelist.Core.App;

public static class ReferenceCorpusAnalysisFrozenInputVersions
{
 public const string FeatureV1 = "reference-corpus-feature-work-item-v1";
 public const string TechniqueV1 = "reference-corpus-technique-work-item-v1";
}

public sealed record ReferenceCorpusFrozenFeatureWorkItem(
 string SchemaVersion,
 string RunId,
 long AnchorId,
 string NodeId,
 string? ChapterNodeId,
 string NodeType,
 string NodeText,
 string NodeTextHash,
 string FeatureFamily,
 ReferenceCorpusFeatureAnalysisContext Context,
 string AnalyzerVersion,
 string FeatureSchemaVersion,
 string ModelProvider,
 string ModelId);

public sealed record ReferenceCorpusFrozenTechniqueWorkItem(
 string SchemaVersion,
 string RunId,
 long AnchorId,
 string NodeId,
 string? ChapterNodeId,
 string NodeType,
 string NodeText,
 string NodeTextHash,
 IReadOnlyList<ReferenceCorpusTechniqueObservationEvidence> Observations,
 string EvidenceSetHash,
 string AnalyzerVersion,
 string TechniqueSchemaVersion,
 string ModelProvider,
 string ModelId);
