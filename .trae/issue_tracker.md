# 问题追踪文档

## 已修复

### ✅ DeepSeek 400 错误 — reasoning_content 未回传
- **根因**：一个 API 响应的 assistant 消息被拆成两条（文本+tool_calls），导致带 tool_calls 的消息缺少 reasoning_content
- **修复**：合并为一条 assistant 消息，强制包含 reasoning_content；to_api_format 中有 tool_calls 的消息强制输出 reasoning_content
- **文件**：ws_chat.py, session_manager.py, llm_service.py

### ✅ 思考过程只第一轮展示
- **根因**：Loop 2+ 的 thinking_chunk 到达前端时，没有 isStreaming 的 assistant 消息来承载
- **修复**：当找不到 isStreaming 消息时，自动创建新的 assistant 消息
- **文件**：EditorPage.tsx

### ✅ add_timeline_entry MissingGreenlet 错误
- **根因**：auto_commit=False 时 flush 后缺少 refresh，导致访问 server_default 字段触发同步懒加载
- **修复**：flush 后始终 refresh，再按需 commit
- **文件**：timeline/service.py

### ✅ run_review offset-naive/aware datetime 错误
- **根因**：datetime.now(timezone.utc) 减去数据库返回的 naive datetime
- **修复**：统一用 .replace(tzinfo=timezone.utc) 处理数据库返回的 datetime
- **文件**：consistency/service.py, mcp/consistency_tools.py

### ✅ 系统提示词"闲聊"识别不准确
- **根因**：AUTHORING_INTENT_CUES 关键词太窄，"闲聊"措辞不精确
- **修复**：扩充关键词，修改措辞为"非创作操作意图"
- **文件**：ws_chat.py, edit_mode.py

### ✅ API 错误暴露给前端
- **根因**：LLMServiceError.message 和 str(exc) 直接返回前端
- **修复**：系统错误统一返回"服务器异常，请稍后重试。"，详细错误只记日志
- **文件**：ws_chat.py, main.py, llm_service.py

### ✅ 对话异常后 session 丢失
- **根因**：_run_chat_with_tools 的 except 块中没有调用 save_session，异常时内存中的消息未持久化
- **修复**：将 save_session 移到 finally 块，确保无论正常/异常/取消都会持久化
- **文件**：ws_chat.py

### ✅ 编辑确认（accept_edit）机制优化 — 极简 accept
- **问题**：用户确认后还同步调用最多 3 次 LLM（结尾补全、结构化分析、摘要生成），耗时 7-18 秒；complete_ending 会静默修改用户确认的内容
- **修复**：
  - 废弃 ChapterPostProcessor（结尾补全 + 结构化信息提取 + 时间线同步）
  - 摘要生成移到异步后台
  - 向量库刷新保留（已异步）
  - accept_edit 现在只做核心操作：写入正文 + 更新状态
- **文件**：editor/service.py

### ✅ 异常类继承方案实施
- **方案**：BusinessError（业务错误，消息可返回前端）和 SystemError（系统错误，消息脱敏）
- **实施**：
  - 新增 BusinessError/SystemError 基类（exceptions.py）
  - 新增 ConflictException（409）
  - LLMServiceError 改为继承 SystemError
  - VectorStoreError 改为继承SystemError
  - editor/service.py 9 处 ValueError → BusinessError 子类
  - characters/service.py 4 处 ValueError → BusinessError 子类
  - ws_chat.py 1 处 ValueError → ConflictException
  - _friendly_error_message 改为 isinstance 判断
  - main.py 新增 BusinessError/SystemError 异常处理器
- **文件**：exceptions.py, llm_service.py, vector_store.py, ws_chat.py, main.py, editor/service.py, characters/service.py

---

## 已知的预先存在问题（未修复）

### consistency_tools.py 类型错误（6处）
- Sequence[Chapter] vs List[Chapter] 不兼容
- dict[str, Unknown] | MCPToolResult 返回类型不匹配
- MCPToolResult 没有 .get 属性
- **优先级**：低（功能正常，仅类型标注问题）

### characters/service.py:267 类型错误
- TimelineEntryCategory.PLOT_NODE 赋值类型不兼容
- **优先级**：低（功能正常，仅类型标注问题）
