"""
会话管理核心模块 - 支持三层粒度会话、上下文压缩、会话持久化

三层会话粒度：
1. 小说级会话 - 绑定novel_id，包含小说设定、角色、大纲、世界观
2. 章节级会话 - 绑定novel_id + chapter_number，包含前文摘要、本章对话
3. 自由对话 - 无绑定，纯对话历史

上下文管理策略：滑动窗口 + 摘要压缩 + 重要性评分
"""
import logging
import json
import uuid
from datetime import datetime, timedelta
from typing import Dict, Any, List, Optional
from dataclasses import dataclass, field
from enum import Enum

logger = logging.getLogger(__name__)


class MessageRole(str, Enum):
    """消息角色"""
    SYSTEM = "system"
    USER = "user"
    ASSISTANT = "assistant"
    TOOL = "tool"


class SessionLevel(str, Enum):
    """会话层级"""
    NOVEL = "novel"          # 小说级 - 全局讨论、大纲生成
    CHAPTER = "chapter"      # 章节级 - 章节生成、修改
    FREE = "free"            # 自由对话 - 通用问答


@dataclass
class Message:
    """对话消息"""
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
    
    def to_api_format(self) -> Dict[str, str]:
        """转换为API调用格式"""
        return {"role": self.role.value, "content": self.content}


@dataclass
class NovelContext:
    """小说级上下文 - 长期记忆"""
    title: str = ""
    description: str = ""
    genre: str = ""
    outline: str = ""
    world_setting: str = ""
    characters_summary: str = ""
    main_plot: str = ""
    
    def to_prompt(self) -> str:
        """生成提示词"""
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
    """章节级上下文"""
    chapter_number: int = 0
    chapter_title: str = ""
    previous_summary: str = ""
    current_outline: str = ""
    key_events: List[str] = field(default_factory=list)
    focus_characters: List[str] = field(default_factory=list)
    
    def to_prompt(self) -> str:
        """生成提示词"""
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
    """模型上下文配置"""
    name: str
    context_window: int
    max_output_tokens: int
    description: str


MODEL_CONFIGS: Dict[str, ModelContextConfig] = {
    "deepseek-chat": ModelContextConfig(
        name="deepseek-chat",
        context_window=131072,
        max_output_tokens=8192,
        description="DeepSeek-V3.2 - 128K上下文窗口，默认4K输出，最大8K"
    ),
    "deepseek-reasoner": ModelContextConfig(
        name="deepseek-reasoner",
        context_window=131072,
        max_output_tokens=65536,
        description="DeepSeek-V3.2 思考模式 - 128K上下文窗口，默认32K输出，最大64K"
    ),
}


@dataclass
class SessionConfig:
    """会话配置"""
    max_messages: int = 100
    max_tokens: int = 50000
    context_window: int = 64000
    summary_threshold: float = 0.8
    keep_recent_messages: int = 30
    session_ttl: int = 3600 * 24
    enable_auto_summary: bool = True
    min_compress_ratio: float = 0.8
    
    @classmethod
    def for_model(cls, model: str) -> "SessionConfig":
        """根据模型创建配置"""
        model_config = MODEL_CONFIGS.get(model, MODEL_CONFIGS["deepseek-chat"])
        context_window = model_config.context_window
        max_output = model_config.max_output_tokens
        
        return cls(
            max_messages=200,
            max_tokens=int(context_window * 0.75),
            context_window=context_window,
            summary_threshold=0.8,
            keep_recent_messages=30,
            session_ttl=3600 * 24,
            enable_auto_summary=True,
            min_compress_ratio=0.8
        )


@dataclass
class Session:
    """
    会话对象 - 支持三层粒度
    
    层级说明：
    - NOVEL: 小说级，绑定novel_id，用于全局讨论、大纲生成
    - CHAPTER: 章节级，绑定novel_id + chapter_number，用于章节生成、修改
    - FREE: 自由对话，无绑定，用于通用问答
    """
    session_id: str
    user_id: int
    level: SessionLevel = SessionLevel.FREE
    novel_id: Optional[int] = None
    chapter_number: Optional[int] = None
    chapter_number_end: Optional[int] = None
    title: str = ""
    generation_type: str = "chat"
    messages: List[Message] = field(default_factory=list)
    summary: Optional[str] = None
    
    novel_context: Optional[NovelContext] = None
    chapter_context: Optional[ChapterContext] = None
    
    created_at: datetime = field(default_factory=datetime.now)
    updated_at: datetime = field(default_factory=datetime.now)
    metadata: Dict[str, Any] = field(default_factory=dict)
    model: str = "deepseek-chat"
    
    def to_dict(self) -> Dict[str, Any]:
        return {
            "session_id": self.session_id,
            "user_id": self.user_id,
            "level": self.level.value,
            "novel_id": self.novel_id,
            "chapter_number": self.chapter_number,
            "chapter_number_end": self.chapter_number_end,
            "title": self.title,
            "generation_type": self.generation_type,
            "messages": [m.to_dict() for m in self.messages],
            "summary": self.summary,
            "novel_context": self.novel_context.__dict__ if self.novel_context else None,
            "chapter_context": self.chapter_context.__dict__ if self.chapter_context else None,
            "created_at": self.created_at.isoformat(),
            "updated_at": self.updated_at.isoformat(),
            "metadata": self.metadata,
            "model": self.model
        }
    
    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "Session":
        novel_context = None
        if data.get("novel_context"):
            novel_context = NovelContext(**data["novel_context"])
        
        chapter_context = None
        if data.get("chapter_context"):
            chapter_context = ChapterContext(**data["chapter_context"])
        
        return cls(
            session_id=data["session_id"],
            user_id=data["user_id"],
            level=SessionLevel(data.get("level", "free")),
            novel_id=data.get("novel_id"),
            chapter_number=data.get("chapter_number"),
            chapter_number_end=data.get("chapter_number_end"),
            title=data.get("title", ""),
            generation_type=data.get("generation_type", "chat"),
            messages=[Message.from_dict(m) for m in data.get("messages", [])],
            summary=data.get("summary"),
            novel_context=novel_context,
            chapter_context=chapter_context,
            created_at=datetime.fromisoformat(data["created_at"]),
            updated_at=datetime.fromisoformat(data["updated_at"]),
            metadata=data.get("metadata", {}),
            model=data.get("model", "deepseek-chat")
        )
    
    def get_token_count(self) -> int:
        """获取总token数"""
        return sum(m.token_count for m in self.messages)
    
    def get_message_count(self) -> int:
        """获取消息数量"""
        return len(self.messages)
    
    def get_context_usage_ratio(self) -> float:
        """获取上下文使用率"""
        model_config = MODEL_CONFIGS.get(self.model, MODEL_CONFIGS["deepseek-chat"])
        return self.get_token_count() / model_config.context_window
    
    def generate_default_title(self) -> str:
        """生成默认标题"""
        if self.level == SessionLevel.FREE:
            return "自由对话"
        elif self.level == SessionLevel.NOVEL:
            return "小说全局讨论"
        elif self.level == SessionLevel.CHAPTER:
            if self.chapter_number_end and self.chapter_number_end != self.chapter_number:
                return f"第{self.chapter_number}-{self.chapter_number_end}章"
            return f"第{self.chapter_number}章"
        return "新会话"
    
    def get_display_name(self) -> str:
        """获取会话显示名称"""
        if self.title:
            return self.title
        return self.generate_default_title()


class ContextCompressor:
    """上下文压缩器 - 滑动窗口 + 摘要压缩 + 重要性评分"""
    
    def __init__(self, config: SessionConfig):
        self.config = config
    
    def estimate_tokens(self, text: str) -> int:
        """估算token数量"""
        chinese_chars = sum(1 for c in text if '\u4e00' <= c <= '\u9fff')
        other_chars = len(text) - chinese_chars
        return int(chinese_chars / 1.5 + other_chars / 4)
    
    def calculate_importance(self, message: Message) -> float:
        """计算消息重要性评分"""
        score = 0.5
        
        if message.role == MessageRole.SYSTEM:
            score = 1.0
        elif message.role == MessageRole.USER:
            score = 0.8
        elif message.role == MessageRole.TOOL:
            score = 0.7
        
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
        """判断是否需要压缩"""
        usage_ratio = session.get_context_usage_ratio()
        return usage_ratio >= self.config.min_compress_ratio
    
    def compress(self, session: Session, summary_text: Optional[str] = None) -> Session:
        """压缩会话上下文"""
        if not self.should_compress(session):
            return session
        
        messages = session.messages
        if len(messages) <= self.config.keep_recent_messages:
            return session
        
        system_messages = [m for m in messages if m.role == MessageRole.SYSTEM]
        recent_messages = messages[-self.config.keep_recent_messages:]
        
        older_messages = messages[len(system_messages):-self.config.keep_recent_messages]
        
        important_messages = [
            m for m in older_messages
            if m.importance >= 0.7
        ]
        
        if summary_text:
            session.summary = summary_text
        elif older_messages and not session.summary:
            session.summary = self._generate_summary_prompt(older_messages)
        
        new_messages = system_messages + important_messages + recent_messages
        
        session.messages = new_messages
        session.updated_at = datetime.now()
        
        old_tokens = sum(m.token_count for m in messages)
        new_tokens = sum(m.token_count for m in new_messages)
        
        logger.info(
            f"Session {session.session_id} compressed: "
            f"{len(messages)} -> {len(new_messages)} messages, "
            f"{old_tokens} -> {new_tokens} tokens"
        )
        
        return session
    
    def _generate_summary_prompt(self, messages: List[Message]) -> str:
        """生成摘要提示"""
        content = "\n".join([
            f"[{m.role.value}]: {m.content[:200]}..."
            for m in messages[-5:]
        ])
        return f"[历史对话摘要]\n{content}"


class MCPToolFormatter:
    """MCP工具格式化器"""
    
    @staticmethod
    def format_tools_for_llm(tools: List[Dict[str, Any]]) -> str:
        """将MCP工具列表格式化为LLM可理解的提示词"""
        if not tools:
            return ""
        
        formatted = "【可用工具】\n你可以使用以下工具来获取信息或执行操作：\n\n"
        
        for tool in tools:
            formatted += f"### {tool.get('name', 'unknown')}\n"
            formatted += f"描述: {tool.get('description', '无描述')}\n"
            
            params = tool.get('parameters_schema', {}).get('properties', {})
            required = tool.get('parameters_schema', {}).get('required', [])
            
            if params:
                formatted += "参数:\n"
                for param_name, param_info in params.items():
                    req_mark = " (必填)" if param_name in required else " (可选)"
                    param_type = param_info.get('type', 'any')
                    param_desc = param_info.get('description', '无描述')
                    formatted += f"  - {param_name}{req_mark}: {param_type} - {param_desc}\n"
            
            formatted += "\n"
        
        formatted += "调用方式: 在对话中描述你需要使用的工具和参数，系统会自动调用并返回结果。\n"
        
        return formatted
    
    @staticmethod
    def format_tools_as_openai_functions(tools: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
        """将MCP工具转换为OpenAI Function Calling格式"""
        functions = []
        
        for tool in tools:
            func = {
                "type": "function",
                "function": {
                    "name": tool.get("name", "unknown"),
                    "description": tool.get("description", ""),
                    "parameters": tool.get("parameters_schema", {
                        "type": "object",
                        "properties": {},
                        "required": []
                    })
                }
            }
            functions.append(func)
        
        return functions


class SessionManager:
    """会话管理器 - 支持三层粒度"""
    
    def __init__(self, config: SessionConfig = None):
        self.config = config or SessionConfig()
        self.compressor = ContextCompressor(self.config)
        self._storage = None
    
    def set_storage(self, storage):
        """设置存储后端"""
        self._storage = storage
    
    def create_novel_session(
        self,
        user_id: int,
        novel_id: int,
        novel_context: Optional[NovelContext] = None,
        system_prompt: Optional[str] = None,
        model: str = "deepseek-chat"
    ) -> Session:
        """创建小说级会话"""
        session_id = f"sess_{user_id}_novel_{novel_id}_{uuid.uuid4().hex[:8]}"
        
        session = Session(
            session_id=session_id,
            user_id=user_id,
            level=SessionLevel.NOVEL,
            novel_id=novel_id,
            novel_context=novel_context,
            model=model,
            metadata={"created_from": "novel_session"}
        )
        
        if system_prompt:
            session.messages.append(Message(
                role=MessageRole.SYSTEM,
                content=system_prompt,
                importance=1.0,
                token_count=self.compressor.estimate_tokens(system_prompt)
            ))
        
        logger.info(f"Created novel-level session: {session_id}")
        return session
    
    def create_chapter_session(
        self,
        user_id: int,
        novel_id: int,
        chapter_number: int,
        novel_context: Optional[NovelContext] = None,
        chapter_context: Optional[ChapterContext] = None,
        system_prompt: Optional[str] = None,
        model: str = "deepseek-chat"
    ) -> Session:
        """创建章节级会话"""
        session_id = f"sess_{user_id}_ch{chapter_number}_{uuid.uuid4().hex[:8]}"
        
        session = Session(
            session_id=session_id,
            user_id=user_id,
            level=SessionLevel.CHAPTER,
            novel_id=novel_id,
            chapter_number=chapter_number,
            novel_context=novel_context,
            chapter_context=chapter_context,
            model=model,
            metadata={"created_from": "chapter_session"}
        )
        
        if system_prompt:
            session.messages.append(Message(
                role=MessageRole.SYSTEM,
                content=system_prompt,
                importance=1.0,
                token_count=self.compressor.estimate_tokens(system_prompt)
            ))
        
        logger.info(f"Created chapter-level session: {session_id}")
        return session
    
    def create_free_session(
        self,
        user_id: int,
        system_prompt: Optional[str] = None,
        model: str = "deepseek-chat"
    ) -> Session:
        """创建自由对话会话"""
        session_id = f"sess_{user_id}_free_{uuid.uuid4().hex[:8]}"
        
        session = Session(
            session_id=session_id,
            user_id=user_id,
            level=SessionLevel.FREE,
            model=model,
            metadata={"created_from": "free_session"}
        )
        
        if system_prompt:
            session.messages.append(Message(
                role=MessageRole.SYSTEM,
                content=system_prompt,
                importance=1.0,
                token_count=self.compressor.estimate_tokens(system_prompt)
            ))
        
        logger.info(f"Created free session: {session_id}")
        return session
    
    def create_session(
        self,
        user_id: int,
        novel_id: Optional[int] = None,
        chapter_number: Optional[int] = None,
        level: SessionLevel = SessionLevel.FREE,
        novel_context: Optional[NovelContext] = None,
        chapter_context: Optional[ChapterContext] = None,
        system_prompt: Optional[str] = None,
        model: str = "deepseek-chat"
    ) -> Session:
        """
        通用创建会话方法
        
        根据参数自动判断层级：
        - 有chapter_number -> 章节级
        - 有novel_id但无chapter_number -> 小说级
        - 都没有 -> 自由对话
        """
        if level == SessionLevel.CHAPTER or (novel_id and chapter_number):
            return self.create_chapter_session(
                user_id=user_id,
                novel_id=novel_id,
                chapter_number=chapter_number,
                novel_context=novel_context,
                chapter_context=chapter_context,
                system_prompt=system_prompt,
                model=model
            )
        elif level == SessionLevel.NOVEL or novel_id:
            return self.create_novel_session(
                user_id=user_id,
                novel_id=novel_id,
                novel_context=novel_context,
                system_prompt=system_prompt,
                model=model
            )
        else:
            return self.create_free_session(
                user_id=user_id,
                system_prompt=system_prompt,
                model=model
            )
    
    def add_message(
        self,
        session: Session,
        role: MessageRole,
        content: str,
        metadata: Optional[Dict[str, Any]] = None
    ) -> Message:
        """添加消息到会话"""
        message = Message(
            role=role,
            content=content,
            token_count=self.compressor.estimate_tokens(content),
            importance=self.compressor.calculate_importance(
                Message(role=role, content=content)
            ),
            metadata=metadata or {}
        )
        
        session.messages.append(message)
        session.updated_at = datetime.now()
        
        return message
    
    def build_context_prompt(self, session: Session) -> str:
        """构建完整上下文提示词"""
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
        include_context: bool = True
    ) -> List[Dict[str, str]]:
        """获取用于API调用的消息列表"""
        messages = []
        
        if include_context:
            context_prompt = self.build_context_prompt(session)
            if context_prompt:
                messages.append({
                    "role": "system",
                    "content": f"以下是相关的背景信息，请在回答时参考：\n\n{context_prompt}"
                })
        
        for msg in session.messages:
            messages.append(msg.to_api_format())
        
        return messages
    
    async def save_session(self, session: Session):
        """保存会话"""
        if self._storage:
            await self._storage.save(session)
        logger.debug(f"Session {session.session_id} saved")
    
    async def load_session(self, session_id: str) -> Optional[Session]:
        """加载会话"""
        if self._storage:
            return await self._storage.load(session_id)
        return None
    
    async def delete_session(self, session_id: str):
        """删除会话"""
        if self._storage:
            await self._storage.delete(session_id)
        logger.info(f"Session {session_id} deleted")
    
    async def list_user_sessions(
        self,
        user_id: int,
        novel_id: Optional[int] = None,
        level: Optional[SessionLevel] = None
    ) -> List[Session]:
        """列出用户会话"""
        if self._storage:
            return await self._storage.list_by_user(user_id, novel_id, level)
        return []
    
    def compress_session(
        self,
        session: Session,
        summary: Optional[str] = None
    ) -> Session:
        """压缩会话"""
        return self.compressor.compress(session, summary)
    
    def get_session_stats(self, session: Session) -> Dict[str, Any]:
        """获取会话统计信息"""
        model_config = MODEL_CONFIGS.get(session.model, MODEL_CONFIGS["deepseek-chat"])
        token_count = session.get_token_count()
        
        return {
            "session_id": session.session_id,
            "level": session.level.value,
            "display_name": session.get_display_name(),
            "novel_id": session.novel_id,
            "chapter_number": session.chapter_number,
            "message_count": session.get_message_count(),
            "token_count": token_count,
            "context_window": model_config.context_window,
            "usage_ratio": round(token_count / model_config.context_window * 100, 2),
            "should_compress": self.compressor.should_compress(session),
            "model": session.model
        }
    
    def update_novel_context(
        self,
        session: Session,
        novel_context: NovelContext
    ):
        """更新小说级上下文"""
        session.novel_context = novel_context
        session.updated_at = datetime.now()
    
    def update_chapter_context(
        self,
        session: Session,
        chapter_context: ChapterContext
    ):
        """更新章节级上下文"""
        session.chapter_context = chapter_context
        session.updated_at = datetime.now()


session_manager = SessionManager()
