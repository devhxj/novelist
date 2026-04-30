"""
会话存储服务 - Redis缓存 + 数据库持久化
策略：读缓存优先，写双写，保证数据一致性
"""
import logging
from typing import Optional, List
from datetime import datetime, timezone

from app.core.redis_service import redis_service
from app.core.database import AsyncSessionLocal
from app.core.session_manager import Session, SessionConfig, ScopeType, Message, MessageRole, NovelContext, ChapterContext, SessionScope
from app.chat.models import ChatSession as DBChatSession, ChatMessage as DBChatMessage
from sqlalchemy import select, delete
from sqlalchemy.orm import selectinload

logger = logging.getLogger(__name__)


class SessionStorage:
    """会话存储 - Redis缓存 + DB持久化"""
    
    KEY_PREFIX = "session:"
    USER_SESSIONS_PREFIX = "user_sessions:"
    
    def __init__(self, config: SessionConfig | None = None):
        self.config = config or SessionConfig()
        self.cache_ttl = 3600
    
    def _get_session_key(self, session_id: str) -> str:
        return f"{self.KEY_PREFIX}{session_id}"
    
    def _get_user_sessions_key(
        self, 
        user_id: int, 
        novel_id: Optional[int] = None,
        scope_type: Optional[ScopeType] = None
    ) -> str:
        if scope_type:
            if novel_id:
                return f"{self.USER_SESSIONS_PREFIX}{user_id}:novel:{novel_id}:scope:{scope_type.value}"
            return f"{self.USER_SESSIONS_PREFIX}{user_id}:scope:{scope_type.value}"
        if novel_id:
            return f"{self.USER_SESSIONS_PREFIX}{user_id}:novel:{novel_id}"
        return f"{self.USER_SESSIONS_PREFIX}{user_id}"
    
    async def save(self, session: Session) -> bool:
        """保存会话 - 双写DB和Redis"""
        try:
            await self._save_to_db(session)
            await self._save_to_cache(session)
            await self._update_user_sessions_index(session)
            logger.debug(f"Session saved: {session.session_id}")
            return True
        except Exception as e:
            logger.error(f"Failed to save session: {e}")
            return False
    
    async def load(self, session_id: str) -> Optional[Session]:
        """加载会话 - 先缓存后数据库"""
        try:
            cached = await self._load_from_cache(session_id)
            if cached:
                logger.debug(f"Session loaded from cache: {session_id}")
                return cached
            
            session = await self._load_from_db(session_id)
            if session:
                await self._save_to_cache(session)
                logger.debug(f"Session loaded from DB: {session_id}")
            return session
        except Exception as e:
            logger.error(f"Failed to load session: {e}")
            return None
    
    async def delete(self, session_id: str) -> bool:
        """删除会话 - 双删"""
        try:
            session = await self.load(session_id)
            if not session:
                return False
            
            await self._delete_from_db(session_id)
            await self._delete_from_cache(session_id)
            
            if session.user_id:
                user_key = self._get_user_sessions_key(session.user_id, session.novel_id)
                await redis_service.zrem(user_key, session_id)
                
                if session.scope and session.scope.type:
                    scope_key = self._get_user_sessions_key(
                        session.user_id, session.novel_id, session.scope.type
                    )
                    await redis_service.zrem(scope_key, session_id)
            
            logger.info(f"Session deleted: {session_id}")
            return True
        except Exception as e:
            logger.error(f"Failed to delete session: {e}")
            return False
    
    async def list_by_user(
        self,
        user_id: int,
        novel_id: Optional[int] = None,
        scope_type: Optional[ScopeType] = None,
        limit: int = 20
    ) -> List[Session]:
        """列出用户会话"""
        try:
            sessions = await self._list_from_db(user_id, novel_id, scope_type, limit)
            for session in sessions:
                await self._save_to_cache(session)
            return sessions
        except Exception as e:
            logger.error(f"Failed to list sessions: {e}")
            return []
    
    async def update_ttl(self, session_id: str) -> bool:
        """更新缓存TTL"""
        try:
            cache_key = self._get_session_key(session_id)
            await redis_service.expire(cache_key, self.cache_ttl)
            return True
        except Exception as e:
            logger.error(f"Failed to update TTL: {e}")
            return False
    
    async def exists(self, session_id: str) -> bool:
        """检查会话是否存在"""
        try:
            cache_key = self._get_session_key(session_id)
            if await redis_service.exists(cache_key):
                return True
            return await self._exists_in_db(session_id)
        except Exception as e:
            logger.error(f"Failed to check session existence: {e}")
            return False
    
    async def get_session_count(
        self, 
        user_id: int, 
        novel_id: Optional[int] = None,
        scope_type: Optional[ScopeType] = None
    ) -> int:
        """获取会话数量"""
        try:
            return await self._count_from_db(user_id, novel_id, scope_type)
        except Exception as e:
            logger.error(f"Failed to get session count: {e}")
            return 0
    
    async def _save_to_cache(self, session: Session) -> bool:
        """保存到Redis缓存"""
        try:
            session.updated_at = datetime.now(timezone.utc)
            session_key = self._get_session_key(session.session_id)
            await redis_service.set(session_key, session.to_dict(), ttl=self.cache_ttl)
            return True
        except Exception as e:
            logger.warning(f"Failed to save to cache: {e}")
            return False
    
    async def _load_from_cache(self, session_id: str) -> Optional[Session]:
        """从Redis缓存加载"""
        try:
            session_key = self._get_session_key(session_id)
            data = await redis_service.get(session_key)
            if not data:
                return None
            return Session.from_dict(data)
        except Exception as e:
            logger.warning(f"Failed to load from cache: {e}")
            return None
    
    async def _delete_from_cache(self, session_id: str) -> bool:
        """从Redis缓存删除"""
        try:
            session_key = self._get_session_key(session_id)
            await redis_service.delete(session_key)
            return True
        except Exception as e:
            logger.warning(f"Failed to delete from cache: {e}")
            return False
    
    async def _update_user_sessions_index(self, session: Session) -> bool:
        """更新用户会话索引"""
        try:
            user_key = self._get_user_sessions_key(session.user_id, session.novel_id)
            await redis_service.zadd(
                user_key,
                {session.session_id: datetime.now(timezone.utc).timestamp()},
                ttl=self.cache_ttl
            )
            
            if session.scope and session.scope.type:
                scope_key = self._get_user_sessions_key(
                    session.user_id, session.novel_id, session.scope.type
                )
                await redis_service.zadd(
                    scope_key,
                    {session.session_id: datetime.now(timezone.utc).timestamp()},
                    ttl=self.cache_ttl
                )
            return True
        except Exception as e:
            logger.warning(f"Failed to update user sessions index: {e}")
            return False
    
    async def _save_to_db(self, session: Session) -> bool:
        """保存到数据库"""
        try:
            async with AsyncSessionLocal() as db:
                result = await db.execute(
                    select(DBChatSession).where(DBChatSession.session_id == session.session_id)
                )
                db_session = result.scalar_one_or_none()
                
                if db_session:
                    db_session.scope_type = session.scope.type.value
                    db_session.chapter_start = session.scope.chapter_start
                    db_session.chapter_end = session.scope.chapter_end
                    db_session.title = session.title
                    db_session.model = session.model
                    db_session.summary = session.summary
                    db_session.novel_context = session.novel_context.to_dict() if session.novel_context else None
                    db_session.chapter_context = session.chapter_context.to_dict() if session.chapter_context else None
                    db_session.pending_changes = session.pending_changes
                    db_session.extra_metadata = session.metadata
                    db_session.updated_at = datetime.now(timezone.utc)
                else:
                    db_session = DBChatSession(
                        session_id=session.session_id,
                        user_id=session.user_id,
                        novel_id=session.novel_id,
                        scope_type=session.scope.type.value,
                        chapter_start=session.scope.chapter_start,
                        chapter_end=session.scope.chapter_end,
                        title=session.title,
                        model=session.model,
                        summary=session.summary,
                        novel_context=session.novel_context.to_dict() if session.novel_context else None,
                        chapter_context=session.chapter_context.to_dict() if session.chapter_context else None,
                        pending_changes=session.pending_changes,
                        extra_metadata=session.metadata
                    )
                    db.add(db_session)
                
                await db.flush()

                await db.execute(
                    delete(DBChatMessage).where(DBChatMessage.session_id == db_session.id)
                )
                
                for msg in session.messages:
                    db_msg = DBChatMessage(
                        session_id=db_session.id,
                        role=msg.role.value,
                        content=msg.content,
                        token_count=msg.token_count,
                        importance=int(msg.importance * 100),
                        extra_metadata=msg.metadata
                    )
                    db.add(db_msg)
                
                await db.commit()
                return True
        except Exception as e:
            logger.error(f"Failed to save to DB: {e}")
            return False
    
    async def _load_from_db(self, session_id: str) -> Optional[Session]:
        """从数据库加载"""
        try:
            async with AsyncSessionLocal() as db:
                result = await db.execute(
                    select(DBChatSession)
                    .options(selectinload(DBChatSession.messages))
                    .where(DBChatSession.session_id == session_id)
                )
                db_session = result.scalar_one_or_none()
                
                if not db_session:
                    return None
                
                return self._db_to_session(db_session)
        except Exception as e:
            logger.error(f"Failed to load from DB: {e}")
            return None
    
    async def _delete_from_db(self, session_id: str) -> bool:
        """从数据库删除"""
        try:
            async with AsyncSessionLocal() as db:
                result = await db.execute(
                    select(DBChatSession).where(DBChatSession.session_id == session_id)
                )
                db_session = result.scalar_one_or_none()
                
                if db_session:
                    await db.delete(db_session)
                    await db.commit()
                return True
        except Exception as e:
            logger.error(f"Failed to delete from DB: {e}")
            return False
    
    async def _list_from_db(
        self,
        user_id: int,
        novel_id: Optional[int] = None,
        scope_type: Optional[ScopeType] = None,
        limit: int = 20
    ) -> List[Session]:
        """从数据库列出会话"""
        try:
            async with AsyncSessionLocal() as db:
                query = select(DBChatSession).options(
                    selectinload(DBChatSession.messages)
                ).where(DBChatSession.user_id == user_id)
                
                if novel_id:
                    query = query.where(DBChatSession.novel_id == novel_id)
                
                if scope_type:
                    query = query.where(DBChatSession.scope_type == scope_type.value)
                
                query = query.order_by(DBChatSession.updated_at.desc()).limit(limit)
                
                result = await db.execute(query)
                db_sessions = result.scalars().all()
                
                return [self._db_to_session(s) for s in db_sessions]
        except Exception as e:
            logger.error(f"Failed to list from DB: {e}")
            return []
    
    async def _exists_in_db(self, session_id: str) -> bool:
        """检查数据库中是否存在"""
        try:
            async with AsyncSessionLocal() as db:
                result = await db.execute(
                    select(DBChatSession.id).where(DBChatSession.session_id == session_id)
                )
                return result.scalar_one_or_none() is not None
        except Exception as e:
            logger.error(f"Failed to check existence in DB: {e}")
            return False
    
    async def _count_from_db(
        self,
        user_id: int,
        novel_id: Optional[int] = None,
        scope_type: Optional[ScopeType] = None
    ) -> int:
        """从数据库统计数量"""
        try:
            async with AsyncSessionLocal() as db:
                query = select(DBChatSession).where(DBChatSession.user_id == user_id)
                
                if novel_id:
                    query = query.where(DBChatSession.novel_id == novel_id)
                
                if scope_type:
                    query = query.where(DBChatSession.scope_type == scope_type.value)
                
                result = await db.execute(query)
                return len(result.scalars().all())
        except Exception as e:
            logger.error(f"Failed to count from DB: {e}")
            return 0
    
    def _db_to_session(self, db_session: DBChatSession) -> Session:
        """数据库模型转Session对象"""
        scope = SessionScope(
            type=ScopeType(db_session.scope_type),
            chapter_start=db_session.chapter_start,
            chapter_end=db_session.chapter_end
        )
        
        novel_context = None
        if db_session.novel_context:
            novel_context = NovelContext.from_dict(db_session.novel_context)
        
        chapter_context = None
        if db_session.chapter_context:
            chapter_context = ChapterContext.from_dict(db_session.chapter_context)
        
        messages = []
        for msg in sorted(db_session.messages, key=lambda m: m.created_at):
            messages.append(Message(
                role=MessageRole(msg.role),
                content=msg.content,
                timestamp=msg.created_at,
                token_count=msg.token_count,
                importance=msg.importance / 100.0,
                metadata=msg.extra_metadata or {}
            ))
        
        return Session(
            session_id=db_session.session_id,
            user_id=db_session.user_id,
            novel_id=db_session.novel_id,
            scope=scope,
            title=db_session.title or "",
            messages=messages,
            summary=db_session.summary,
            novel_context=novel_context,
            chapter_context=chapter_context,
            pending_changes=db_session.pending_changes or [],
            created_at=db_session.created_at,
            updated_at=db_session.updated_at,
            metadata=db_session.extra_metadata or {},
            model=db_session.model
        )


session_storage = SessionStorage()
