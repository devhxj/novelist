from datetime import datetime, timezone
from enum import Enum

from pydantic import BaseModel, ConfigDict, Field
from typing import Any

from sessions.manager import MODEL_CONFIGS


class MessageRole(str, Enum):
    SYSTEM = "system"
    USER = "user"
    ASSISTANT = "assistant"
    TOOL = "tool"


class Message(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    role: MessageRole
    content: str
    created_at: datetime = Field(default_factory=lambda: datetime.now(timezone.utc))
    token_count: int = 0
    extra_metadata: dict[str, Any] = Field(default_factory=dict)
    version: int = 1
    to_api: bool = True
    to_frontend: bool = True
    event_type: str | None = None

    def to_api_format(self) -> dict[str, Any]:
        payload: dict[str, Any] = {"role": self.role.value, "content": self.content}
        if self.role == MessageRole.ASSISTANT:
            if self.extra_metadata.get("tool_calls"):
                payload["tool_calls"] = self.extra_metadata["tool_calls"]
                thinking_content = self.extra_metadata.get("thinking_content")
                payload["reasoning_content"] = thinking_content if thinking_content is not None else ""
            else:
                thinking_content = self.extra_metadata.get("thinking_content")
                if thinking_content is not None:
                    payload["reasoning_content"] = thinking_content
        if self.role == MessageRole.TOOL:
            if self.extra_metadata.get("tool_call_id"):
                payload["tool_call_id"] = self.extra_metadata["tool_call_id"]
            if self.extra_metadata.get("tool_name"):
                payload["name"] = self.extra_metadata["tool_name"]
        return payload




class Session(BaseModel):
    model_config = ConfigDict(from_attributes=True, extra="ignore")

    session_id: str
    user_id: int
    novel_id: int | None = None
    title: str = ""
    messages: list[Message] = Field(default_factory=list)
    summary: str | None = None
    pending_changes: list[str] = Field(default_factory=list)
    created_at: datetime = Field(default_factory=lambda: datetime.now(timezone.utc))
    updated_at: datetime = Field(default_factory=lambda: datetime.now(timezone.utc))
    extra_metadata: dict[str, Any] = Field(default_factory=dict)
    model: str = "deepseek-v4-flash"
    edit_mode: str = "agent"
    chapter_ids: list[int] = Field(default_factory=list)
    subtitle: str = ""
    current_chapter_id: int | None = None
    active_version: int = 1
    usage: dict[str, Any] | None = None

    def get_token_count(self) -> int:
        return sum(m.token_count for m in self.messages)

    def get_message_count(self) -> int:
        return len(self.messages)

    def get_context_usage_ratio(self) -> float:
        model_config = MODEL_CONFIGS.get(self.model, MODEL_CONFIGS["deepseek-v4-flash"])
        return self.get_token_count() / model_config.context_window

    def get_display_name(self) -> str:
        return self.title or "新对话"

    def get_subtitle(self) -> str:
        return self.subtitle or ""