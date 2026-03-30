"""
会话存储服务 - Redis持久化
支持三层粒度会话存储
"""
import logging
from typing import Optional, List
from datetime import datetime

from app.core.redis_service import redis_service
from app.core.session_manager import Session, SessionConfig, SessionLevel

logger = logging.getLogger(__name__)


class SessionStorage:
    """会话存储 - Redis实现"""
    
    KEY_PREFIX = "session:"
    USER_SESSIONS_PREFIX = "user_sessions:"
    
    def __init__(self, config: SessionConfig = None):
        self.config = config or SessionConfig()
        self.ttl = self.config.session_ttl
    
    def _get_session_key(self, session_id: str) -> str:
        """获取会话存储键"""
        return f"{self.KEY_PREFIX}{session_id}"
    
    def _get_user_sessions_key(
        self, 
        user_id: int, 
        novel_id: Optional[int] = None,
        level: Optional[SessionLevel] = None
    ) -> str:
        """获取用户会话列表键"""
        if level:
            if novel_id:
                return f"{self.USER_SESSIONS_PREFIX}{user_id}:novel:{novel_id}:level:{level.value}"
            return f"{self.USER_SESSIONS_PREFIX}{user_id}:level:{level.value}"
        if novel_id:
            return f"{self.USER_SESSIONS_PREFIX}{user_id}:novel:{novel_id}"
        return f"{self.USER_SESSIONS_PREFIX}{user_id}"
    
    async def save(self, session: Session) -> bool:
        """保存会话"""
        try:
            session.updated_at = datetime.now()
            
            session_key = self._get_session_key(session.session_id)
            session_data = session.to_dict()
            
            await redis_service.set(
                session_key,
                session_data,
                ttl=self.ttl
            )
            
            user_sessions_key = self._get_user_sessions_key(
                session.user_id,
                session.novel_id
            )
            await redis_service.zadd(
                user_sessions_key,
                {session.session_id: datetime.now().timestamp()},
                ttl=self.ttl
            )
            
            if session.level:
                level_key = self._get_user_sessions_key(
                    session.user_id,
                    session.novel_id,
                    session.level
                )
                await redis_service.zadd(
                    level_key,
                    {session.session_id: datetime.now().timestamp()},
                    ttl=self.ttl
                )
            
            logger.debug(f"Session saved: {session.session_id}, level: {session.level.value}")
            return True
            
        except Exception as e:
            logger.error(f"Failed to save session: {e}")
            return False
    
    async def load(self, session_id: str) -> Optional[Session]:
        """加载会话"""
        try:
            session_key = self._get_session_key(session_id)
            session_data = await redis_service.get(session_key)
            
            if not session_data:
                return None
            
            session = Session.from_dict(session_data)
            
            await redis_service.expire(session_key, self.ttl)
            
            logger.debug(f"Session loaded: {session_id}")
            return session
            
        except Exception as e:
            logger.error(f"Failed to load session: {e}")
            return None
    
    async def delete(self, session_id: str) -> bool:
        """删除会话"""
        try:
            session = await self.load(session_id)
            if not session:
                return False
            
            session_key = self._get_session_key(session_id)
            await redis_service.delete(session_key)
            
            user_sessions_key = self._get_user_sessions_key(
                session.user_id,
                session.novel_id
            )
            await redis_service.zrem(user_sessions_key, session_id)
            
            if session.level:
                level_key = self._get_user_sessions_key(
                    session.user_id,
                    session.novel_id,
                    session.level
                )
                await redis_service.zrem(level_key, session_id)
            
            logger.info(f"Session deleted: {session_id}")
            return True
            
        except Exception as e:
            logger.error(f"Failed to delete session: {e}")
            return False
    
    async def list_by_user(
        self,
        user_id: int,
        novel_id: Optional[int] = None,
        level: Optional[SessionLevel] = None,
        limit: int = 20
    ) -> List[Session]:
        """列出用户会话"""
        try:
            user_sessions_key = self._get_user_sessions_key(user_id, novel_id, level)
            
            session_ids = await redis_service.zrevrange(
                user_sessions_key,
                0,
                limit - 1
            )
            
            sessions = []
            for session_id in session_ids:
                session = await self.load(session_id)
                if session:
                    sessions.append(session)
            
            return sessions
            
        except Exception as e:
            logger.error(f"Failed to list sessions: {e}")
            return []
    
    async def update_ttl(self, session_id: str) -> bool:
        """更新会话TTL"""
        try:
            session_key = self._get_session_key(session_id)
            await redis_service.expire(session_key, self.ttl)
            return True
        except Exception as e:
            logger.error(f"Failed to update TTL: {e}")
            return False
    
    async def exists(self, session_id: str) -> bool:
        """检查会话是否存在"""
        try:
            session_key = self._get_session_key(session_id)
            return await redis_service.exists(session_key)
        except Exception as e:
            logger.error(f"Failed to check session existence: {e}")
            return False
    
    async def get_session_count(
        self, 
        user_id: int, 
        novel_id: Optional[int] = None,
        level: Optional[SessionLevel] = None
    ) -> int:
        """获取用户会话数量"""
        try:
            user_sessions_key = self._get_user_sessions_key(user_id, novel_id, level)
            return await redis_service.zcard(user_sessions_key)
        except Exception as e:
            logger.error(f"Failed to get session count: {e}")
            return 0


session_storage = SessionStorage()
