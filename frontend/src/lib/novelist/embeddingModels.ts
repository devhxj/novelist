// 已知固定维度的 API 向量模型：服务商不接受 dimensions 参数，传了会被拒绝
// （如 SiliconFlow 对 BAAI/bge-m3 返回 20015 参数错误）。键为模型 ID 最后一段（小写）。
const FIXED_DIMENSION_MODELS: Record<string, number> = {
  'bge-m3': 1024,
}

export function resolveFixedEmbeddingDimensions(modelId: string): number | null {
  const tail = (modelId ?? '').trim().split('/').pop()?.toLowerCase() ?? ''
  return Object.prototype.hasOwnProperty.call(FIXED_DIMENSION_MODELS, tail)
    ? FIXED_DIMENSION_MODELS[tail]
    : null
}

export function normalizeEmbeddingDimensionsForModel(modelId: string, dimensions: number | null): number | null {
  return resolveFixedEmbeddingDimensions(modelId) === null ? dimensions : null
}
