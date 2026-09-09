using Novelist.Core.App;

namespace Novelist.Core.App;

public interface IReferenceChapterMaterialExtractor
{
    // 计划阶段：模型通读整章，把要产出的素材按种类划分成若干趟（pass）。
    // 每趟是素材类型的分组，全部素材类型必须恰好各属一趟——计划划分的是
    // 输出（素材种类），不是输入（章节文本）。
    ValueTask<IReadOnlyList<ReferenceChapterExtractionRound>> PlanChapterExtractionAsync(
        ReferenceChapterExtractionRequest request,
        CancellationToken cancellationToken);

    // 执行一趟提取：全章照常输入，但只收集本趟计划类型的素材（代码强制过滤）。
    ValueTask<ReferenceChapterExtractionResult> ExtractChapterRoundAsync(
        ReferenceChapterExtractionRequest request,
        ReferenceChapterExtractionRound round,
        CancellationToken cancellationToken);
}

// 一趟提取计划：本趟要收集的素材类型分组与简要说明。
public sealed record ReferenceChapterExtractionRound(
    IReadOnlyList<string> MaterialTypes,
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
