# 待解决问题追踪

## 1. 小说级大纲从未创建

**问题**：NovelCreativeProfile 模型中有故事大纲字段（premise, theme, beginning, middle, climax, ending），但从未被填充过——数据库中全是 NULL。

**原因**：
- 没有 UI 入口让用户填写
- update_creative_profile 工具理论上能更新，但 AI 从未被指示填这些字段
- _format_creative_profile_for_prompt 之前也没输出这些字段（已修复）

**需要做的**：
- [ ] 小说创建流程加"故事大纲"步骤（可选填写）
- [ ] 或让 AI 在第一次对话时主动询问并调 update_creative_profile 填入
- [ ] 考虑是否需要单独的"故事大纲编辑"页面

**优先级**：中（不影响当前功能，但填了能显著提升创作质量）

## 2. 章节工作流审批失败处理

**问题**：create_chapter_workflow 工具中，大纲审批被用户拒绝时，工具将失败结果和原大纲追加到 session 后返回 `__appended__`。LLM 在下一轮工具循环中看到 `大纲审批未通过，用户意见：xxx` 和原大纲内容，由 LLM 自行决定下一步（重新生成 / 文字讨论修改方向）。

**当前处理**：依赖 LLM 自主判断，不做自动重试。
**风险**：LLM 可能不调工具重新生成，而是直接文字讨论；多轮来回效率低。
**优先级**：低（先跑通，实际使用中观察 LLM 行为再决定是否需要自动重试循环）

## 3. ChapterGenerationService 待清理

**问题**：`backend/generation/service.py` 中 `ChapterGenerationService.generate_chapter()` 走旧的 AgentTask 路径，已被 LangGraph 工作流替代。`generate_chapter()` 仅 `generation/router.py` HTTP 端点调用，主线 `ws_chat.py` 已不再使用。

**当前状态**：`ws_generation.py` 仍使用 `ChapterGenerationService._generate_chapter_summary()` 辅助方法。其余逻辑可清理。

**需要做的**：
- [ ] 确认 `generation/router.py` 的 HTTP 生成端点是否需要保留
- [ ] 如不需要，可将 `_generate_chapter_summary` 移出，删除 `ChapterGenerationService` 和 `generate_chapter()`
**优先级**：低（不影响当前功能）
