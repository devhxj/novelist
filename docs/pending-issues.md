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

## 4. Memory 和 Review Agent 能力未充分利用

**问题**：章节工作流 `post_process` 节点目前用简单的 `llm_service.generate_text/generate_json` 做摘要和 review，没有复用 `agents/memory.py`（向量记忆入库、上下文检索）和 `agents/reviewer.py`（结构化评分、多维度审核、修改建议）的完整能力。

**影响**：
- Memory Agent：向量记忆更新不完整，长跨章节伏笔检索能力弱
- Reviewer Agent：review 结果只有简单的 JSON 输出，没有结构化评分和迭代修订建议

**需要做的**：
- [ ] post_process 中接入 `agents/reviewer.py` 的 `ReviewerAgent`，获取结构化 review（scores/issues/suggestions）
- [ ] post_process 中接入 `agents/memory.py` 的完整记忆更新流程
- [ ] 或在 review/memory agent 中暴露可复用的独立函数
**优先级**：中（当前能跑，但质量有提升空间）

## 5. system2 上下文注入与 LLM 自主查询冗余

**问题**：system2 注入的故事状态、读者认知、角色索引等信息，LLM 在对话中仍会主动调用 MCP 工具重新查询。注入的小说级创作偏好也面临同样问题——system1 已包含，但 LLM 仍调用 `get_creative_profile`。

**原因**：system1 明确指示 LLM"优先读取 get_creative_profile"，且 LLM 天然不信任快照的时效性。注入收益不明显。

**当前评估**：可能不需要全面注入。让 LLM 自主决定查什么更灵活。system2 保留 LLM 自己查不到的内容（如故事状态这种叙事性 markdown），其他让工具自然覆盖。

**优先级**：低（不影响功能，待架构方向确定后再优化）

## 6. Memory Agent 未实现独立 LLM 能力

**问题**：`agents/memory.py` 当前只是一个薄包装，直接调 `vector_store.delete_chapter_chunks` / `build_chapter_chunks` / `add_chunks`，没有任何 LLM 调用。

**本意设计**：Memory Agent 应该是一个独立 LLM，有自己的上下文，接受主 agent 的指令后自主探索——类似 Coding Agent 的 Search Agent 或 Explore Agent。它应该能主动搜索向量库、分析上下文关联、决定存储策略。

**当前实现**：纯后端操作，和 MCP 工具没有本质区别，只是数据层包装。

**需要做的**：
- [ ] Memory Agent 需要独立的 LLM 实例和自己的上下文
- [ ] 接受主 agent 指令后自主探索向量库
- [ ] 智能决定 chunk 策略、关联标记
**优先级**：中（当前能跑，但记忆检索质量受限）
