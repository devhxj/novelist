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
    timestamp: datetime = Field(default_factory=lambda: datetime.now(timezone.utc))
    token_count: int = 0
    importance: float = 0.5
    metadata: dict[str, Any] = Field(default_factory=dict)
    version: int = 1
    to_api: bool = True
    to_frontend: bool = True
    event_type: str | None = None

    def to_api_format(self) -> dict[str, Any]:
        payload: dict[str, Any] = {"role": self.role.value, "content": self.content}
        if self.role == MessageRole.ASSISTANT:
            if self.metadata.get("tool_calls"):
                payload["tool_calls"] = self.metadata["tool_calls"]
                thinking_content = self.metadata.get("thinking_content")
                payload["reasoning_content"] = thinking_content if thinking_content is not None else ""
            else:
                thinking_content = self.metadata.get("thinking_content")
                if thinking_content is not None:
                    payload["reasoning_content"] = thinking_content
        if self.role == MessageRole.TOOL:
            if self.metadata.get("tool_call_id"):
                payload["tool_call_id"] = self.metadata["tool_call_id"]
            if self.metadata.get("tool_name"):
                payload["name"] = self.metadata["tool_name"]
        return payload


class NovelContext(BaseModel):
    title: str = ""
    description: str = ""
    genre: str = ""
    outline: str = ""
    world_setting: str = ""
    characters_summary: str = ""
    main_plot: str = ""

    def to_prompt(self) -> str:
        parts = []
        if self.title:
            parts.append(f"【小说标题】{self.title}")
        if self.description:
            parts.append(f"【小说简介】{self.description}")
        if self.genre:
            parts.append(f"【小说类型】{self.genre}")
        if self.world_setting:
            parts.append(f"【世界观设定】{self.world_setting}")
        if self.outline:
            parts.append(f"【故事大纲】{self.outline}")
        if self.characters_summary:
            parts.append(f"【主要角色】{self.characters_summary}")
        if self.main_plot:
            parts.append(f"【主线情节】{self.main_plot}")
        return "\n".join(parts)


class ChapterContext(BaseModel):
    chapter_number: int = 0
    chapter_title: str = ""
    previous_summary: str = ""
    current_outline: str = ""
    key_events: list[str] = Field(default_factory=list)
    focus_characters: list[str] = Field(default_factory=list)

    def to_prompt(self) -> str:
        parts = [f"【当前章节】第{self.chapter_number}章"]
        if self.chapter_title:
            parts.append(f"章节标题：{self.chapter_title}")
        if self.previous_summary:
            parts.append(f"【前文摘要】\n{self.previous_summary}")
        if self.current_outline:
            parts.append(f"【本章大纲】\n{self.current_outline}")
        if self.key_events:
            parts.append(f"【关键事件】\n" + "\n".join(f"- {e}" for e in self.key_events))
        if self.focus_characters:
            parts.append(f"【重点角色】{', '.join(self.focus_characters)}")
        return "\n".join(parts)


class Session(BaseModel):
    model_config = {"extra": "ignore"}

    session_id: str
    user_id: int
    novel_id: int | None = None
    title: str = ""
    messages: list[Message] = Field(default_factory=list)
    summary: str | None = None
    novel_context: NovelContext | None = None
    chapter_context: ChapterContext | None = None
    pending_changes: list[str] = Field(default_factory=list)
    created_at: datetime = Field(default_factory=lambda: datetime.now(timezone.utc))
    updated_at: datetime = Field(default_factory=lambda: datetime.now(timezone.utc))
    metadata: dict[str, Any] = Field(default_factory=dict)
    model: str = "deepseek-v4-flash"
    edit_mode: str = "agent"
    chapter_ids: list[int] = Field(default_factory=list)
    subtitle: str = ""
    current_chapter_id: int | None = None
    active_version: int = 1
    last_usage: dict[str, Any] | None = None

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