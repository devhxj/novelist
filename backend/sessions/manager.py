"""
会话管理核心模块 - AI IDE风格

核心概念：
1. Session - 会话对象，包含对话历史和待确认变更
2. TextChange - 文本变更记录，支持diff
"""
import logging
import uuid
from datetime import datetime, timezone
from typing import Any
from dataclasses import dataclass


from sessions.schema import MessageRole
from sessions.schema import Message
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




class SessionManager:
    def __init__(self, config: SessionConfig | None = None):
        self.config = config or SessionConfig()

    def create_session(
        self,
        user_id: int,
        novel_id: int | None = None,
        model: str = "deepseek-v4-flash",
        metadata: dict[str, Any] | None = None,
    ) -> Session:
        session_id = f"sess_{user_id}_{uuid.uuid4().hex[:8]}"

        session = Session(
            session_id=session_id,
            user_id=user_id,
            novel_id=novel_id,
            model=model,
            extra_metadata={"created_from": "session_manager", **(metadata or {})}
        )

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
    
    def get_messages_for_api(self, session: Session) -> list[dict[str, str]]:
        history_messages = self._select_messages_for_api(session.messages)
        return [msg.to_api_format() for msg in history_messages]

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
            "should_compress": session.get_context_usage_ratio() >= self.config.min_compress_ratio,
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
    

session_manager = SessionManager()
