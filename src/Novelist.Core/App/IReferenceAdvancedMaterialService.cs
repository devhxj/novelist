using Novelist.Contracts.App;

namespace Novelist.Core.App;

// 高级写作素材（L2）读写入口：对外只有浏览、明细、复核三类操作。
// 生产与投影由后台管线负责，不通过本接口写入。
public interface IReferenceAdvancedMaterialService
{
    ValueTask<PageResultPayload<ReferenceAdvancedMaterialSummaryPayload>> ListAsync(
        ListReferenceAdvancedMaterialsPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceAdvancedMaterialDetailPayload?> GetAsync(
        GetReferenceAdvancedMaterialDetailPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceAdvancedMaterialReviewResultPayload> ReviewAsync(
        ReviewReferenceAdvancedMaterialPayload input,
        CancellationToken cancellationToken);
}
