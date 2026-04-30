"""
会话管理核心模块 - AI IDE风格

核心概念：
1. SessionScope - 会话作用域（整本小说/章节范围/单章节）
2. Session - 会话对象，包含对话历史和待确认变更
3. TextChange - 文本变更记录，支持diff

会话作用域类型：
- novel: 整本小说
- chapters: 章节范围（第X章到第Y章）
- chapter: 单章节
"""
import logging
import json
import uuid
from datetime import datetime, timedelta, timezone
from typing import Dict, Any, List, Optional
from dataclasses import dataclass, field
from enum import Enum

logger = logging.getLogger(__name__)


class MessageRole(str, Enum):
    SYSTEM = "system"
    USER = "user"
    ASSISTANT = "assistant"
    TOOL = "tool"


class ScopeType(str, Enum):
    NOVEL = "novel"
    CHAPTERS = "chapters"
    CHAPTER = "chapter"


@dataclass
class SessionScope:
    type: ScopeType = ScopeType.NOVEL
    chapter_start: Optional[int] = None
    chapter_end: Optional[int] = None
    
    def to_dict(self) -> Dict[str, Any]:
        return {
            "type": self.type.value,
            "chapter_start": self.chapter_start,
            "chapter_end": self.chapter_end
        }
    
    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "SessionScope":
        return cls(
            type=ScopeType(data.get("type", "novel")),
            chapter_start=data.get("chapter_start"),
            chapter_end=data.get("chapter_end")
        )
    
    def get_display_name(self) -> str:
        if self.type == ScopeType.NOVEL:
            return "整本小说"
        elif self.type == ScopeType.CHAPTERS:
            return f"第{self.chapter_start}-{self.chapter_end}章"
        elif self.type == ScopeType.CHAPTER:
            return f"第{self.chapter_start}章"
        return "未知作用域"
    
    def get_chapter_range(self) -> List[int]:
        if self.type == ScopeType.NOVEL:
            return []
        elif self.type == ScopeType.CHAPTERS:
            if self.chapter_start is None or self.chapter_end is None:
                return []
            return list(range(self.chapter_start, self.chapter_end + 1))
        elif self.type == ScopeType.CHAPTER:
            return [self.chapter_start] if self.chapter_start else []
        return []
    
    def includes_chapter(self, chapter_number: int) -> bool:
        if self.type == ScopeType.NOVEL:
            return True
        elif self.type == ScopeType.CHAPTERS:
            if self.chapter_start is None or self.chapter_end is None:
                return False
            return self.chapter_start <= chapter_number <= self.chapter_end
        elif self.type == ScopeType.CHAPTER:
            return chapter_number == self.chapter_start
        return False


@dataclass
class Message:
    role: MessageRole
    content: str
    timestamp: datetime = field(default_factory=datetime.now)
    token_count: int = 0
    importance: float = 0.5
    metadata: Dict[str, Any] = field(default_factory=dict)
    
    def to_dict(self) -> Dict[str, Any]:
        return {
            "role": self.role.value,
            "content": self.content,
            "timestamp": self.timestamp.isoformat(),
            "token_count": self.token_count,
            "importance": self.importance,
            "metadata": self.metadata
        }
    
    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "Message":
        return cls(
            role=MessageRole(data["role"]),
            content=data["content"],
            timestamp=datetime.fromisoformat(data["timestamp"]),
            token_count=data.get("token_count", 0),
            importance=data.get("importance", 0.5),
            metadata=data.get("metadata", {})
        )
    
    def to_api_format(self) -> Dict[str, Any]:
        payload: Dict[str, Any] = {"role": self.role.value, "content": self.content}
        if self.role == MessageRole.ASSISTANT and self.metadata.get("tool_calls"):
            payload["tool_calls"] = self.metadata["tool_calls"]
        if self.role == MessageRole.TOOL:
            if self.metadata.get("tool_call_id"):
                payload["tool_call_id"] = self.metadata["tool_call_id"]
            if self.metadata.get("tool_name"):
                payload["name"] = self.metadata["tool_name"]
        return payload


@dataclass
class NovelContext:
    title: str = ""
    description: str = ""
    genre: str = ""
    outline: str = ""
    world_setting: str = ""
    characters_summary: str = ""
    main_plot: str = ""
    
    def to_dict(self) -> Dict[str, Any]:
        return {
            "title": self.title,
            "description": self.description,
            "genre": self.genre,
            "outline": self.outline,
            "world_setting": self.world_setting,
            "characters_summary": self.characters_summary,
            "main_plot": self.main_plot
        }
    
    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "NovelContext":
        return cls(
            title=data.get("title", ""),
            description=data.get("description", ""),
            genre=data.get("genre", ""),
            outline=data.get("outline", ""),
            world_setting=data.get("world_setting", ""),
            characters_summary=data.get("characters_summary", ""),
            main_plot=data.get("main_plot", "")
        )
    
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


@dataclass
class ChapterContext:
    chapter_number: int = 0
    chapter_title: str = ""
    previous_summary: str = ""
    current_outline: str = ""
    key_events: List[str] = field(default_factory=list)
    focus_characters: List[str] = field(default_factory=list)
    
    def to_dict(self) -> Dict[str, Any]:
        return {
            "chapter_number": self.chapter_number,
            "chapter_title": self.chapter_title,
            "previous_summary": self.previous_summary,
            "current_outline": self.current_outline,
            "key_events": self.key_events,
            "focus_characters": self.focus_characters
        }
    
    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "ChapterContext":
        return cls(
            chapter_number=data.get("chapter_number", 0),
            chapter_title=data.get("chapter_title", ""),
            previous_summary=data.get("previous_summary", ""),
            current_outline=data.get("current_outline", ""),
            key_events=data.get("key_events", []),
            focus_characters=data.get("focus_characters", [])
        )
    
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


@dataclass
class ModelContextConfig:
    name: str
    context_window: int
    max_output_tokens: int
    description: str


MODEL_CONFIGS: Dict[str, ModelContextConfig] = {
    "deepseek-v4-flash": ModelContextConfig(
        name="deepseek-v4-flash",
        context_window=1048576,
        max_output_tokens=65536,
        description="DeepSeek-V4-Flash - 1M上下文窗口"
    ),
    "deepseek-v4-pro": ModelContextConfig(
        name="deepseek-v4-pro",
        context_window=1048576,
        max_output_tokens=65536,
        description="DeepSeek-V4-Pro - 1M上下文窗口"
    ),
    "deepseek-chat": ModelContextConfig(
        name="deepseek-chat",
        context_window=1048576,
        max_output_tokens=8192,
        description="DeepSeek-V3.2 - 1M上下文窗口"
    ),
    "deepseek-reasoner": ModelContextConfig(
        name="deepseek-reasoner",
        context_window=1048576,
        max_output_tokens=65536,
        description="DeepSeek-V3.2 思考模式 - 1M上下文窗口"
    ),
}


@dataclass
class SessionConfig:
    max_messages: int = 500
    max_tokens: int = 800000
    context_window: int = 1048576
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


@dataclass
class Session:
    session_id: str
    user_id: int
    novel_id: Optional[int] = None
    scope: SessionScope = field(default_factory=lambda: SessionScope(type=ScopeType.NOVEL))
    title: str = ""
    messages: List[Message] = field(default_factory=list)
    summary: Optional[str] = None
    novel_context: Optional[NovelContext] = None
    chapter_context: Optional[ChapterContext] = None
    pending_changes: List[str] = field(default_factory=list)
    created_at: datetime = field(default_factory=datetime.now)
    updated_at: datetime = field(default_factory=datetime.now)
    metadata: Dict[str, Any] = field(default_factory=dict)
    model: str = "deepseek-v4-flash"
    edit_mode: str = "agent"
    chapter_ids: List[int] = field(default_factory=list)
    subtitle: str = ""
    current_chapter_id: Optional[int] = None
    
    def to_dict(self) -> Dict[str, Any]:
        return {
            "session_id": self.session_id,
            "user_id": self.user_id,
            "novel_id": self.novel_id,
            "scope": self.scope.to_dict(),
            "title": self.title,
            "subtitle": self.subtitle,
            "messages": [m.to_dict() for m in self.messages],
            "summary": self.summary,
            "novel_context": self.novel_context.to_dict() if self.novel_context else None,
            "chapter_context": self.chapter_context.to_dict() if self.chapter_context else None,
            "pending_changes": self.pending_changes,
            "created_at": self.created_at.isoformat(),
            "updated_at": self.updated_at.isoformat(),
            "metadata": self.metadata,
            "model": self.model,
            "edit_mode": self.edit_mode,
            "current_chapter_id": self.current_chapter_id
        }
    
    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "Session":
        scope_data = data.get("scope", {})
        if isinstance(scope_data.get("type"), str):
            scope = SessionScope.from_dict(scope_data)
        else:
            level = data.get("level", "free")
            if level == "chapter":
                scope = SessionScope(
                    type=ScopeType.CHAPTER,
                    chapter_start=data.get("chapter_number"),
                    chapter_end=data.get("chapter_number_end")
                )
            elif level == "novel":
                scope = SessionScope(type=ScopeType.NOVEL)
            else:
                scope = SessionScope(type=ScopeType.NOVEL)
        
        novel_context = None
        if data.get("novel_context"):
            novel_context = NovelContext.from_dict(data["novel_context"])
        
        chapter_context = None
        if data.get("chapter_context"):
            chapter_context = ChapterContext.from_dict(data["chapter_context"])
        
        return cls(
            session_id=data["session_id"],
            user_id=data["user_id"],
            novel_id=data.get("novel_id"),
            scope=scope,
            title=data.get("title", ""),
            subtitle=data.get("subtitle", "") or data.get("metadata", {}).get("subtitle", ""),
            messages=[Message.from_dict(m) for m in data.get("messages", [])],
            summary=data.get("summary"),
            novel_context=novel_context,
            chapter_context=chapter_context,
            pending_changes=data.get("pending_changes", []),
            created_at=datetime.fromisoformat(data["created_at"]),
            updated_at=datetime.fromisoformat(data["updated_at"]),
            metadata=data.get("metadata", {}),
            model=data.get("model", "deepseek-v4-flash"),
            edit_mode=data.get("edit_mode", "agent"),
            current_chapter_id=data.get("current_chapter_id")
        )
    
    def get_token_count(self) -> int:
        return sum(m.token_count for m in self.messages)
    
    def get_message_count(self) -> int:
        return len(self.messages)
    
    def get_context_usage_ratio(self) -> float:
        model_config = MODEL_CONFIGS.get(self.model, MODEL_CONFIGS["deepseek-v4-flash"])
        return self.get_token_count() / model_config.context_window
    
    def get_display_name(self) -> str:
        if self.title:
            return self.title
        return self.scope.get_display_name()

    def get_subtitle(self) -> str:
        if self.subtitle:
            return self.subtitle
        return self.scope.get_display_name()


class ContextCompressor:
    def __init__(self, config: SessionConfig):
        self.config = config

    def estimate_tokens(self, text: str) -> int:
        chinese_chars = sum(1 for c in text if '\u4e00' <= c <= '\u9fff')
        other_chars = len(text) - chinese_chars
        return int(chinese_chars / 1.5 + other_chars / 4)

    def calculate_importance(self, message: Message) -> float:
        score = 0.5
        if message.role == MessageRole.SYSTEM:
            score = 1.0
        elif message.role == MessageRole.USER:
            score = 0.8
        elif message.role == MessageRole.TOOL:
            score = 0.25
        elif message.role == MessageRole.ASSISTANT and message.metadata.get("tool_calls"):
            score = 0.35
        if len(message.content) > 500:
            score += 0.1
        if len(message.content) > 1000:
            score += 0.05
        keywords = ["重要", "关键", "必须", "核心", "设定", "角色", "情节", "注意", "记住"]
        for kw in keywords:
            if kw in message.content:
                score += 0.05
        return min(score, 1.0)

    def should_compress(self, session: Session) -> bool:
        usage_ratio = session.get_context_usage_ratio()
        return (
            usage_ratio >= self.config.min_compress_ratio
            or session.get_message_count() >= self.config.max_messages
        )

    def compress(self, session: Session, summary_text: Optional[str] = None) -> Session:
        if not self.should_compress(session):
            return session
        messages = session.messages
        if len(messages) <= self.config.keep_recent_messages:
            return session
        system_messages = [m for m in messages if m.role == MessageRole.SYSTEM]
        recent_messages = messages[-self.config.keep_recent_messages:]
        older_messages = messages[len(system_messages):-self.config.keep_recent_messages]
        important_messages = [m for m in older_messages if m.importance >= 0.7]
        if summary_text:
            session.summary = summary_text
        elif older_messages and not session.summary:
            session.summary = self._build_fallback_summary(older_messages)
        new_messages = system_messages + important_messages + recent_messages
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
        important_messages = [m for m in older_messages if m.importance >= 0.7]

        if older_messages:
            session.summary = await self._generate_llm_summary(
                older_messages, session.summary
            )

        new_messages = system_messages + important_messages + recent_messages
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
            from app.core.llm_service import llm_service
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
                max_tokens=500,
            )
        except Exception as e:
            logger.warning(f"LLM summary generation failed, using fallback: {e}")
            return self._build_fallback_summary(older_messages)

    def build_summary_request_prompt(self, messages: List[Message]) -> str:
        content = "\n".join([
            f"[{m.role.value}]: {m.content[:200]}..."
            for m in messages[-5:]
        ])
        return f"[历史对话摘要]\n{content}"

    def _build_fallback_summary(self, messages: List[Message]) -> str:
        lines: List[str] = ["【历史对话压缩摘要】"]
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
        novel_id: Optional[int] = None,
        scope: Optional[SessionScope] = None,
        novel_context: Optional[NovelContext] = None,
        chapter_context: Optional[ChapterContext] = None,
        system_prompt: Optional[str] = None,
        model: str = "deepseek-v4-flash",
        metadata: Optional[Dict[str, Any]] = None,
    ) -> Session:
        if scope is None:
            scope = SessionScope(type=ScopeType.NOVEL)
        
        scope_suffix = ""
        if scope.type == ScopeType.CHAPTER:
            scope_suffix = f"_ch{scope.chapter_start}"
        elif scope.type == ScopeType.CHAPTERS:
            scope_suffix = f"_ch{scope.chapter_start}-{scope.chapter_end}"
        
        session_id = f"sess_{user_id}{scope_suffix}_{uuid.uuid4().hex[:8]}"
        
        session = Session(
            session_id=session_id,
            user_id=user_id,
            novel_id=novel_id,
            scope=scope,
            novel_context=novel_context,
            chapter_context=chapter_context,
            model=model,
            metadata={"created_from": "session_manager", **(metadata or {})}
        )
        
        session.subtitle = scope.get_display_name()
        session.metadata["subtitle"] = session.subtitle
        
        if system_prompt:
            session.messages.append(Message(
                role=MessageRole.SYSTEM,
                content=system_prompt,
                importance=1.0,
                token_count=self.compressor.estimate_tokens(system_prompt)
            ))
        
        logger.info(f"Created session: {session_id}, scope: {scope.get_display_name()}")
        return session
    
    def add_message(
        self,
        session: Session,
        role: MessageRole,
        content: str,
        metadata: Optional[Dict[str, Any]] = None
    ) -> Message:
        message_metadata = metadata or {}
        probe = Message(role=role, content=content, metadata=message_metadata)
        message = Message(
            role=role,
            content=content,
            token_count=self.compressor.estimate_tokens(content),
            importance=self.compressor.calculate_importance(probe),
            metadata=message_metadata
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
        extra_context: Optional[str] = None
    ) -> List[Dict[str, str]]:
        messages: List[Dict[str, Any]] = []
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

    def _select_messages_for_api(self, session_messages: List[Message]) -> List[Message]:
        system_messages = [m for m in session_messages if m.role == MessageRole.SYSTEM]
        non_system_messages = [m for m in session_messages if m.role != MessageRole.SYSTEM]
        if len(non_system_messages) <= self.config.api_max_history_messages:
            return system_messages + non_system_messages

        selected: List[Message] = []
        required_tool_call_ids: set[str] = set()

        for msg in reversed(non_system_messages):
            tool_call_id = str(msg.metadata.get("tool_call_id", "")) if msg.metadata else ""
            tool_calls = msg.metadata.get("tool_calls") if msg.metadata else None
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

    def _trim_history_to_token_limit(self, messages: List[Message], max_tokens: int) -> List[Message]:
        if max_tokens <= 0:
            return [m for m in messages if m.role == MessageRole.SYSTEM]

        system_messages = [m for m in messages if m.role == MessageRole.SYSTEM]
        non_system_messages = [m for m in messages if m.role != MessageRole.SYSTEM]
        trimmed: List[Message] = []
        required_tool_call_ids: set[str] = set()
        total = 0

        for msg in reversed(non_system_messages):
            token_cost = self._estimate_message_tokens(msg)
            tool_call_id = str(msg.metadata.get("tool_call_id", "")) if msg.metadata else ""
            tool_calls = msg.metadata.get("tool_calls") if msg.metadata else None
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

        if message.metadata.get("tool_calls"):
            return self.compressor.estimate_tokens(
                json.dumps(message.metadata["tool_calls"], ensure_ascii=False)
            )

        return 0
    
    async def save_session(self, session: Session):
        if session.subtitle:
            session.metadata["subtitle"] = session.subtitle
        elif session.metadata.get("subtitle"):
            session.subtitle = session.metadata.get("subtitle", "")
        if self._storage:
            await self._storage.save(session)
        logger.debug(f"Session {session.session_id} saved")
    
    async def load_session(self, session_id: str) -> Optional[Session]:
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
        novel_id: Optional[int] = None,
        scope_type: Optional[ScopeType] = None
    ) -> List[Session]:
        if self._storage:
            return await self._storage.list_by_user(user_id, novel_id, scope_type)
        return []
    
    def compress_session(
        self,
        session: Session,
        summary: Optional[str] = None
    ) -> Session:
        return self.compressor.compress(session, summary)
    
    def get_session_stats(self, session: Session) -> Dict[str, Any]:
        model_config = MODEL_CONFIGS.get(session.model, MODEL_CONFIGS["deepseek-v4-flash"])
        token_count = session.get_token_count()
        return {
            "session_id": session.session_id,
            "scope": session.scope.to_dict(),
            "display_name": session.get_display_name(),
            "title": session.title,
            "subtitle": session.get_subtitle(),
            "novel_id": session.novel_id,
            "message_count": session.get_message_count(),
            "token_count": token_count,
            "context_window": model_config.context_window,
            "usage_ratio": round(token_count / model_config.context_window * 100, 2),
            "should_compress": self.compressor.should_compress(session),
            "pending_changes": len(session.pending_changes),
            "model": session.model
        }
    
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
