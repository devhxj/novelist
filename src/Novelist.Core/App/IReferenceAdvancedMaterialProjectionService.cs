namespace Novelist.Core.App;

public sealed record ReferenceAdvancedMaterialProjectionResult(
    int ObservationCount,
    int SpecimenCount,
    int InvalidEvidenceCount);

// 把既有生产侧明细表（reference_feature_observations / reference_technique_specimens）
// 投影进统一外壳表 reference_advanced_materials。证据无法解析的行整条作废并计数。
public interface IReferenceAdvancedMaterialProjectionService
{
    ValueTask<ReferenceAdvancedMaterialProjectionResult> ProjectAnchorAsync(
        long anchorId,
        CancellationToken cancellationToken);
}
