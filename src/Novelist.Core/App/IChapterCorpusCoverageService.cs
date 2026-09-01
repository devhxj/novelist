using Novelist.Contracts.App;

namespace Novelist.Core.App;

/// <summary>
/// 细纲 beat 级语料覆盖度：对章节计划的每个 beat 走与写作注入相同的检索通路，
/// 命中即视为该 beat 已覆盖；覆盖率低于 50% 视为语料不足。
/// </summary>
public interface IChapterCorpusCoverageService
{
    ValueTask<ChapterCorpusCoveragePayload> ComputeCoverageAsync(
        GetChapterCorpusCoveragePayload input,
        CancellationToken cancellationToken);
}
