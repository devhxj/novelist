"""
会话管理核心模块 - AI IDE风格

核心概念：
1. Session - 会话对象，包含对话历史和待确认变更
2. TextChange - 文本变更记录，支持diff
"""
import logging
import json
import uuid
from datetime import datetime, timezone
from typing import Any
from dataclasses import dataclass


from sessions.schema import MessageRole
from sessions.schema import Message
from sessions.schema import NovelContext
from sessions.schema import ChapterContext
from sessions.schema import Session

logger = logging.getLogger(__name__)


@dataclass
class ModelContextConfig:
    name: str
    context_window: int
    max_output_tokens: int
    description: str


MODEL_CONFIGS: dict[str, ModelContextConfig] = {
    "deepseek-v4-flash": ModelContextConfig(
        name="deepseek-v4-flash",
        context_window=1000000,
        max_output_tokens=65536,
        description="DeepSeek-V4-Flash - 1M上下文窗口"
    ),
    "deepseek-v4-pro": ModelContextConfig(
        name="deepseek-v4-pro",
        context_window=1000000,
        max_output_tokens=65536,
        description="DeepSeek-V4-Pro - 1M上下文窗口"
    ),
}


@dataclass
class SessionConfig:
    max_messages: int = 500
    max_tokens: int = 800000
    context_window: int = 1000000
    summary_threshold: float = 0.9
    keep_recent_messages: int = 50
    api_max_history_messages: int = 200
    session_ttl: int = 3600 * 24
    enable_auto_summary: bool = True
    min_compress_ratio: float = 0.8
    
    @classmethod
    def for_model(cls, model: str) -> "SessionConfig":
        model_config = MODEL_CONFIGS.get(model, MODEL_CONFIGS["deepseek-v4-flash"])
        context_window = model_config.context_window
        return cls(
            max_messages=200,
            max_tokens=int(context_window * 0.75),
            context_window=context_window,
            summary_threshold=0.8,
            keep_recent_messages=30,
            api_max_history_messages=60,
            session_ttl=3600 * 24,
            enable_auto_summary=True,
            min_compress_ratio=0.8
        )


class ContextCompressor:
    def __init__(self, config: SessionConfig):
        self.config = config

    def estimate_tokens(self, text: str) -> int:
        chinese_chars = sum(1 for c in text if '\u4e00' <= c <= '\u9fff')
        other_chars = len(text) - chinese_chars
        return int(chinese_chars / 1.5 + other_chars / 4)

    def should_compress(self, session: Session) -> bool:
        usage_ratio = session.get_context_usage_ratio()
        return (
            usage_ratio >= self.config.min_compress_ratio
            or session.get_message_count() >= self.config.max_messages
        )

    def compress(self, session: Session, summary_text: str | None = None) -> Session:
        if not self.should_compress(session):
            return session
        messages = session.messages
        if len(messages) <= self.config.keep_recent_messages:
            return session
        system_messages = [m for m in messages if m.role == MessageRole.SYSTEM]
        recent_messages = messages[-self.config.keep_recent_messages:]
        older_messages = messages[len(system_messages):-self.config.keep_recent_messages]
        if summary_text:
            session.summary = summary_text
        elif older_messages and not session.summary:
            session.summary = self._build_fallback_summary(older_messages)
        new_messages = system_messages + older_messages[-10:] + recent_messages
        session.messages = new_messages
        session.updated_at = datetime.now(timezone.utc)
        old_tokens = sum(m.token_count for m in messages)
        new_tokens = sum(m.token_count for m in new_messages)
        logger.info(
            f"Session {session.session_id} compressed: "
            f"{len(messages)} -> {len(new_messages)} messages, "
            f"{old_tokens} -> {new_tokens} tokens"
        )
        return session

    async def compress_with_llm(self, session: Session) -> Session:
        """Compress session using LLM-generated summary for older messages.

        Unlike the sync compress() which uses crude truncation,
        this method uses LLM to extract key facts from older messages,
        preserving important context while reducing token usage.
        """
        if not self.should_compress(session):
            return session
        messages = session.messages
        if len(messages) <= self.config.keep_recent_messages:
            return session

        system_messages = [m for m in messages if m.role == MessageRole.SYSTEM]
        recent_messages = messages[-self.config.keep_recent_messages:]
        older_messages = messages[len(system_messages):-self.config.keep_recent_messages]

        if older_messages:
            session.summary = await self._generate_llm_summary(
                older_messages, session.summary
            )

        new_messages = system_messages + older_messages[-10:] + recent_messages
        session.messages = new_messages
        session.updated_at = datetime.now(timezone.utc)
        old_tokens = sum(m.token_count for m in messages)
        new_tokens = sum(m.token_count for m in new_messages)
        logger.info(
            f"Session {session.session_id} LLM-compressed: "
            f"{len(messages)} -> {len(new_messages)} messages, "
            f"{old_tokens} -> {new_tokens} tokens"
        )
        return session

    async def _generate_llm_summary(
        self, older_messages: list[Message], existing_summary: str | None = None
    ) -> str:
        """Generate a fact-extraction summary using LLM.

        Instead of simple truncation, uses LLM to selectively extract
        key facts that are valuable for future creative decisions.
        """
        try:
            from core.llm_service import llm_service
        except ImportError:
            return self._build_fallback_summary(older_messages)

        summary_prompt = """请从以下对话历史中提取关键信息，包括：
1. 用户的核心创作意图和偏好
2. 已做出的重要决策（角色设定、情节方向等）
3. 已完成的操作（创建了什么、修改了什么）
4. 未解决的需求或待办事项

忽略日常寒暄和重复内容，只保留对后续创作有价值的要点。"""

        context_parts = []
        if existing_summary:
            context_parts.append(f"【已有摘要】\n{existing_summary}")
        context_parts.append("【新增对话】")
        for m in older_messages[-10:]:
            content = (m.content or "").strip()[:200]
            if content:
                context_parts.append(f"[{m.role.value}]: {content}")

        try:
            return await llm_service.generate_text(
                prompt="\n".join(context_parts),
                system_prompt=summary_prompt,
                temperature=0.3,
                max_tokens=50000,
            )
        except Exception as e:
            logger.warning(f"LLM summary generation failed, using fallback: {e}")
            return self._build_fallback_summary(older_messages)

    def build_summary_request_prompt(self, messages: list[Message]) -> str:
        content = "\n".join([
            f"[{m.role.value}]: {m.content[:200]}..."
            for m in messages[-5:]
        ])
        return f"[历史对话摘要]\n{content}"

    def _build_fallback_summary(self, messages: list[Message]) -> str:
        lines: list[str] = ["【历史对话压缩摘要】"]
        for message in messages[-8:]:
            snippet = (message.content or "").strip().replace("\n", " ")
            if not snippet:
                continue
            role = message.role.value
            lines.append(f"- {role}: {snippet[:120]}")
        return "\n".join(lines[:9])


class SessionManager:
    def __init__(self, config: SessionConfig | None = None):
        self.config = config or SessionConfig()
        self.compressor = ContextCompressor(self.config)
        self._storage = None
    
    def set_storage(self, storage):
        self._storage = storage
    
    def create_session(
        self,
        user_id: int,
        novel_id: int | None = None,
        novel_context: NovelContext | None = None,
        chapter_context: ChapterContext | None = None,
        system_prompt: str | None = None,
        model: str = "deepseek-v4-flash",
        metadata: dict[str, Any] | None = None,
    ) -> Session:
        session_id = f"sess_{user_id}_{uuid.uuid4().hex[:8]}"

        session = Session(
            session_id=session_id,
            user_id=user_id,
            novel_id=novel_id,
            novel_context=novel_context,
            chapter_context=chapter_context,
            model=model,
            extra_metadata={"created_from": "session_manager", **(metadata or {})}
        )

        if system_prompt:
            session.messages.append(Message(
                role=MessageRole.SYSTEM,
                content=system_prompt,
                token_count=self.compressor.estimate_tokens(system_prompt)
            ))

        logger.info(f"Created session: {session_id}")
        return session
    
    def add_message(
        self,
        session: Session,
        role: MessageRole,
        content: str,
        metadata: dict[str, Any] | None = None
    ) -> Message:
        message_metadata = metadata or {}
        message = Message(
            role=role,
            content=content,
            token_count=self.compressor.estimate_tokens(content),
            extra_metadata=message_metadata
        )
        session.messages.append(message)
        session.updated_at = datetime.now(timezone.utc)
        if role == MessageRole.USER:
            normalized = content.strip().splitlines()[0] if content else ""
            if normalized:
                if not session.title or session.title in {"新对话"} or session.title.endswith(" 对话"):
                    session.title = normalized[:30]
        return message
    
    def build_context_prompt(self, session: Session) -> str:
        parts = []
        if session.novel_context:
            novel_prompt = session.novel_context.to_prompt()
            if novel_prompt:
                parts.append(novel_prompt)
        if session.chapter_context:
            chapter_prompt = session.chapter_context.to_prompt()
            if chapter_prompt:
                parts.append(chapter_prompt)
        if session.summary:
            parts.append(session.summary)
        return "\n\n".join(parts)
    
    def get_messages_for_api(
        self,
        session: Session,
        include_context: bool = True,
        extra_context: str | None = None
    ) -> list[dict[str, str]]:
        messages: list[dict[str, Any]] = []
        context_tokens = 0
        if include_context:
            context_prompt = self.build_context_prompt(session)
            if extra_context:
                context_prompt = f"{context_prompt}\n\n{extra_context}" if context_prompt else extra_context
            if context_prompt:
                context_message = {
                    "role": "system",
                    "content": f"以下是相关的背景信息，请在回答时参考：\n\n{context_prompt}"
                }
                messages.append(context_message)
                context_tokens = self.compressor.estimate_tokens(context_message["content"])
        history_messages = self._select_messages_for_api(session.messages)
        
        max_tokens = self.config.max_tokens
        if max_tokens:
            history_budget = max(max_tokens - context_tokens, 0)
            history_messages = self._trim_history_to_token_limit(history_messages, history_budget)

        for msg in history_messages:
            messages.append(msg.to_api_format())
        return messages

    def _select_messages_for_api(self, session_messages: list[Message]) -> list[Message]:
        system_messages = [m for m in session_messages if m.role == MessageRole.SYSTEM]
        non_system_messages = [m for m in session_messages if m.role != MessageRole.SYSTEM]
        if len(non_system_messages) <= self.config.api_max_history_messages:
            return system_messages + non_system_messages

        selected: list[Message] = []
        required_tool_call_ids: set[str] = set()

        for msg in reversed(non_system_messages):
            tool_call_id = str(msg.extra_metadata.get("tool_call_id", "")) if msg.extra_metadata else ""
            tool_calls = msg.extra_metadata.get("tool_calls") if msg.extra_metadata else None
            tool_call_ids = {
                str(call.get("id"))
                for call in tool_calls
                if isinstance(call, dict) and call.get("id")
            } if isinstance(tool_calls, list) else set()

            must_keep = False
            if msg.role == MessageRole.TOOL and tool_call_id:
                must_keep = True
                required_tool_call_ids.add(tool_call_id)
            elif tool_call_ids and required_tool_call_ids.intersection(tool_call_ids):
                must_keep = True
                required_tool_call_ids.difference_update(tool_call_ids)

            if not must_keep and len(selected) >= self.config.api_max_history_messages and not required_tool_call_ids:
                break

            selected.append(msg)

        selected.reverse()
        return system_messages + selected

    def _trim_history_to_token_limit(self, messages: list[Message], max_tokens: int) -> list[Message]:
        if max_tokens <= 0:
            return [m for m in messages if m.role == MessageRole.SYSTEM]

        system_messages = [m for m in messages if m.role == MessageRole.SYSTEM]
        non_system_messages = [m for m in messages if m.role != MessageRole.SYSTEM]
        trimmed: list[Message] = []
        required_tool_call_ids: set[str] = set()
        total = 0

        for msg in reversed(non_system_messages):
            token_cost = self._estimate_message_tokens(msg)
            tool_call_id = str(msg.extra_metadata.get("tool_call_id", "")) if msg.extra_metadata else ""
            tool_calls = msg.extra_metadata.get("tool_calls") if msg.extra_metadata else None
            tool_call_ids = {
                str(call.get("id"))
                for call in tool_calls
                if isinstance(call, dict) and call.get("id")
            } if isinstance(tool_calls, list) else set()

            must_keep = False
            if msg.role == MessageRole.TOOL and tool_call_id:
                must_keep = True
                required_tool_call_ids.add(tool_call_id)
            elif tool_call_ids and required_tool_call_ids.intersection(tool_call_ids):
                must_keep = True
                required_tool_call_ids.difference_update(tool_call_ids)

            if total + token_cost > max_tokens and not must_keep:
                continue

            trimmed.append(msg)
            total += token_cost

        trimmed.reverse()
        return system_messages + trimmed

    def _estimate_message_tokens(self, message: Message) -> int:
        if message.content:
            return self.compressor.estimate_tokens(message.content)

        if message.extra_metadata.get("tool_calls"):
            return self.compressor.estimate_tokens(
                json.dumps(message.extra_metadata["tool_calls"], ensure_ascii=False)
            )

        return 0
    
    async def save_session(self, session: Session):
        if session.subtitle:
            session.extra_metadata["subtitle"] = session.subtitle
        elif session.extra_metadata.get("subtitle"):
            session.subtitle = session.extra_metadata.get("subtitle", "")
        if self._storage:
            await self._storage.save(session)
        logger.debug(f"Session {session.session_id} saved")
    
    async def load_session(self, session_id: str) -> Session | None:
        if self._storage:
            return await self._storage.load(session_id)
        return None
    
    async def delete_session(self, session_id: str):
        if self._storage:
            await self._storage.delete(session_id)
        logger.info(f"Session {session_id} deleted")
    
    async def list_user_sessions(
        self,
        user_id: int,
        novel_id: int | None = None,
    ) -> list[Session]:
        if self._storage:
            return await self._storage.list_by_user(user_id, novel_id)
        return []
    
    def compress_session(
        self,
        session: Session,
        summary: str | None = None
    ) -> Session:
        return self.compressor.compress(session, summary)
    
    def get_session_stats(self, session: Session) -> dict[str, Any]:
        model_config = MODEL_CONFIGS.get(session.model, MODEL_CONFIGS["deepseek-v4-flash"])
        token_count = session.get_token_count()
        last_usage = session.usage or {}
        stats: dict[str, Any] = {
            "session_id": session.session_id,
            "display_name": session.get_display_name(),
            "title": session.title,
            "subtitle": session.get_subtitle(),
            "novel_id": session.novel_id,
            "message_count": session.get_message_count(),
            "token_count": token_count,
            "context_window": model_config.context_window,
            "should_compress": self.compressor.should_compress(session),
            "pending_changes": len(session.pending_changes),
            "model": session.model
        }
        if last_usage:
            stats["prompt_tokens"] = last_usage.get("prompt_tokens")
            stats["completion_tokens"] = last_usage.get("completion_tokens")
            stats["total_tokens"] = last_usage.get("total_tokens")
            stats["usage_ratio"] = round(last_usage.get("total_tokens", 0) / model_config.context_window * 100, 2) if model_config.context_window else 0
            detail = last_usage.get("detail")
            if detail:
                stats["detail"] = detail
        else:
            stats["usage_ratio"] = round(token_count / model_config.context_window * 100, 2)
        return stats
    
    def update_novel_context(
        self,
        session: Session,
        novel_context: NovelContext
    ):
        session.novel_context = novel_context
        session.updated_at = datetime.now(timezone.utc)
    
    def update_chapter_context(
        self,
        session: Session,
        chapter_context: ChapterContext
    ):
        session.chapter_context = chapter_context
        session.updated_at = datetime.now(timezone.utc)
    
    def add_pending_change(self, session: Session, change_id: str):
        if change_id not in session.pending_changes:
            session.pending_changes.append(change_id)
            session.updated_at = datetime.now(timezone.utc)
    
    def remove_pending_change(self, session: Session, change_id: str):
        if change_id in session.pending_changes:
            session.pending_changes.remove(change_id)
            session.updated_at = datetime.now(timezone.utc)


session_manager = SessionManager()
