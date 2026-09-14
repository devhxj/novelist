namespace Novelist.Core.App;

public sealed record ReferenceAdvancedMaterialPipelineResult(
    int ObservationAccepted,
    int ObservationRejected,
    int SpecimenAccepted,
    int SpecimenRejected,
    int StrategyGroups);

// 单本书的高级素材生产编排：走文本节点 → 逐 family 抽观测 → 抽机理 → 聚合书级策略。
// 三层共用一趟，产出全部进入统一外壳表（默认 unverified，人工复核后才可消费）。
public interface IReferenceAdvancedMaterialPipelineService
{
    ValueTask<ReferenceAdvancedMaterialPipelineResult> ProcessAnchorAsync(
        long anchorId,
        string runId,
        CancellationToken cancellationToken);
}
