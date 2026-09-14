using Novelist.Contracts.App;

namespace Novelist.Core.App;

// 抽取落库层：接收分析器产出的候选草稿，按冻结词表与证据边界校验后写入统一外壳表。
// 校验不通过的草稿直接拒收（计入 Rejected），不进库、不产生影子记录。
public interface IReferenceAdvancedMaterialIngestionService
{
    ValueTask<AdvancedMaterialIngestionResult> IngestObservationsAsync(
        long anchorId,
        string runId,
        IReadOnlyList<AdvancedMaterialObservationDraft> drafts,
        CancellationToken cancellationToken);

    ValueTask<AdvancedMaterialIngestionResult> IngestSpecimensAsync(
        long anchorId,
        string runId,
        IReadOnlyList<AdvancedMaterialSpecimenDraft> drafts,
        CancellationToken cancellationToken);
}
