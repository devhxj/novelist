import { BridgeError } from './bridge'

export interface BridgeErrorDiagnostic {
  message: string
  code: string | null
  /** 命中错误码映射时的后端原始消息，UI 可折叠展示；未命中时为 null。 */
  detail: string | null
}

export interface BridgeErrorGuideEntry {
  /** 面向作者的一句话解释，替代后端透传的技术消息。 */
  message: string
  /** 作者下一步可以做什么；UI 以行动建议的形式呈现。 */
  action: string
}

// 已知错误码 → 作者可读的 { message, action }（U11）。
// 清单必须覆盖 Novelist.Contracts 中 ReferenceMaterializationErrorCodes 的全部取值，
// 契约由 tests/bridge-errors.test.mjs 守住：后端新增错误码时这里同步补齐。
export const bridgeErrorGuide: Record<string, BridgeErrorGuideEntry> = {
  materialization_chapter_split_output_invalid: {
    message: '自动切分返回的章节边界对不上原文标题。',
    action: '重新分析，或改用「预览模板」手动指定章节分隔',
  },
  materialization_llm_not_configured: {
    message: '还没有配置可用的大模型。',
    action: '到「设置 → 模型」配置后重试',
  },
  materialization_llm_health_check_failed: {
    message: '大模型连通检查没有通过。',
    action: '检查 API Key 与网络后重试',
  },
  materialization_llm_request_failed: {
    message: '大模型请求失败，可能是服务繁忙或额度不足。',
    action: '稍后重试',
  },
  materialization_llm_output_invalid: {
    message: '大模型返回了无法解析的内容。',
    action: '重试一次，持续失败时更换模型',
  },
  materialization_embedding_not_configured: {
    message: '还没有配置可用的向量模型。',
    action: '到「设置 → 模型」配置后重试',
  },
  materialization_embedding_health_check_failed: {
    message: '向量模型连通检查没有通过。',
    action: '检查本地模型服务或 API 配置后重试',
  },
  materialization_embedding_request_failed: {
    message: '向量模型请求失败。',
    action: '稍后重试',
  },
  materialization_embedding_invalid: {
    message: '向量模型返回的维度或格式不符合要求。',
    action: '检查向量模型配置后重试',
  },
  materialization_vector_index_failed: {
    message: '向量索引写入失败。',
    action: '重试；持续失败时在设置中重建索引',
  },
  materialization_lexical_index_failed: {
    message: '词汇索引写入失败。',
    action: '重试；持续失败时在设置中重建索引',
  },
  materialization_generation_incomplete: {
    message: '材料化在完成前中断了。',
    action: '修复问题后用「修复后重试」继续',
  },
  materialization_blueprint_material_not_ready: {
    message: '蓝图所需的语料材料还没有准备好。',
    action: '先完成参考书材料化，再生成蓝图',
  },
  materialization_blueprint_no_relevant_material: {
    message: '现有语料里没有与这份蓝图相关的材料。',
    action: '补充同类参考书，或调整蓝图方向',
  },
  materialization_chapter_split_profile_stale: {
    message: '参考书来源已变化，这份章节切分已过期。',
    action: '重新分析并重新确认章节边界',
  },
  materialization_candidate_window_invalid: {
    message: '候选材料对应的原文窗口已失效。',
    action: '刷新后重试；持续出现请反馈',
  },
  materialization_retry_requires_new_run: {
    message: '模型配置在启动后发生过变化，旧的一轮无法继续。',
    action: '用「重新材料化」新建一轮',
  },
  materialization_candidate_review_conflict: {
    message: '这条候选刚被其他窗口处理过了。',
    action: '刷新列表后重新决定',
  },
  materialization_candidate_review_invalid: {
    message: '这次复核请求已失效。',
    action: '刷新列表后重试',
  },
}

// 统一的 bridge 错误呈现：优先透出服务端错误码与消息，未知错误退回兜底文案。
// 与 TimelineView 的 buildVisibleError 同范式，供参考书/语料组件复用。
export function describeBridgeError(error: unknown, fallback: string): BridgeErrorDiagnostic {
  if (error instanceof BridgeError) {
    const guide = bridgeErrorGuide[error.code]
    if (guide) {
      // 后端原始消息降为折叠诊断：作者先看到"发生了什么 + 能做什么"。
      return { message: `${guide.message}（${guide.action}）`, code: error.code, detail: error.message }
    }
    return { message: error.message || fallback, code: error.code, detail: null }
  }
  if (error instanceof Error && error.message) {
    return { message: error.message, code: null, detail: null }
  }
  return { message: fallback, code: null, detail: null }
}
