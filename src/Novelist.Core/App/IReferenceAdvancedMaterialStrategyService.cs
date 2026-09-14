namespace Novelist.Core.App;

public sealed record ReferenceAdvancedMaterialStrategyResult(
    int GroupCount,
    int EvidenceRefCount);

// 书级策略聚合：把某本书的观测/机理按 (family, feature_key, layer) 归并成策略层记录，
// 直写统一外壳表；每条策略的证据为来源行的证据并集。
public interface IReferenceAdvancedMaterialStrategyService
{
    ValueTask<ReferenceAdvancedMaterialStrategyResult> AggregateStrategyAsync(
        long anchorId,
        CancellationToken cancellationToken);
}
