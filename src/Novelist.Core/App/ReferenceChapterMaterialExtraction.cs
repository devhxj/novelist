using Novelist.Core.App;

namespace Novelist.Core.App;

public interface IReferenceChapterMaterialExtractor
{
    // 计划阶段：模型通读整章，把整章划分为若干连续字符区间（轮），不产出材料。
    // 返回的区间必须无缝无叠覆盖 [0, 章长)。
    ValueTask<IReadOnlyList<ReferenceChapterExtractionRound>> PlanChapterExtractionAsync(
        ReferenceChapterExtractionRequest request,
        CancellationToken cancellationToken);

    // 按计划区间执行一轮提取：只产出完全落在区间内的摘录。
    ValueTask<ReferenceChapterExtractionResult> ExtractChapterRoundAsync(
        ReferenceChapterExtractionRequest request,
        ReferenceChapterExtractionRound round,
        CancellationToken cancellationToken);

    // 兜底分轮提取（无计划时）：alreadyExtractedExcerpts 为已持久化的摘录。
    ValueTask<ReferenceChapterExtractionResult> ExtractChapterMaterialsAsync(
        ReferenceChapterExtractionRequest request,
        IReadOnlyList<string> alreadyExtractedExcerpts,
        Func<IReadOnlyList<ReferenceChapterExtractedMaterial>, CancellationToken, ValueTask<IReadOnlyList<string>>>? persistPageAsync,
        CancellationToken cancellationToken);
}

// 一轮提取计划：整章上的连续字符区间（相对章文本的零基偏移）与简要主题提示。
public sealed record ReferenceChapterExtractionRound(
    int Start,
    int End,
    string Focus);

public sealed record ReferenceChapterExtractionRequest(
    long AnchorId,
    int ChapterIndex,
    string ChapterTitle,
    string ChapterText,
    ReferenceMaterializationLlmSelection Model);

public sealed record ReferenceChapterExtractedMaterial(
    string Excerpt,
    string MaterialType,
    ReferenceMaterializationQualificationTags Tags,
    ReferenceMaterializationQualityScores Scores,
    double Confidence,
    IReadOnlyList<string> ReasonCodes);

public sealed record ReferenceChapterExtractionResult(
    IReadOnlyList<ReferenceChapterExtractedMaterial> Materials,
    int ModelCallCount = 1);

public sealed record ReferenceChapterExtractionWorkItem(
    long AnchorId,
    int ChapterIndex,
    string ChapterTitle,
    string ChapterText,
    int ContentStart,
    int ContentEnd,
    ReferenceMaterializationLlmSelection Model);

public sealed record ReferenceChapterExtractionPersistenceResult(
    int ChapterIndex,
    int CandidateCount,
    int AcceptedCount,
    int ReviewCount,
    int SkippedCount);
