"""
会话存储服务 - Redis缓存 + 数据库持久化
策略：读缓存优先，写双写，保证数据一致性
"""
import logging
from datetime import datetime, timezone

from core.redis_service import redis_service
from core.database import AsyncSessionLocal
from sessions.manager import SessionConfig
from sessions.models import ChatSession as DBChatSession, ChatMessage as DBChatMessage
from sqlalchemy import delete, func, select
from sqlalchemy.orm import selectinload

from sessions.schema import Session

logger = logging.getLogger(__name__)


class SessionStorage:
    """会话存储 - Redis缓存 + DB持久化"""

    KEY_PREFIX = "session:"
    USER_SESSIONS_PREFIX = "user_sessions:"

    _SAVE_EXCLUDE = {
        "messages", "edit_mode", "chapter_ids", "subtitle", "current_chapter_id",
    }

    def __init__(self, config: SessionConfig | None = None):
        self.config = config or SessionConfig()
        self.cache_ttl = 3600

    def _get_session_key(self, session_id: str) -> str:
        return f"{self.KEY_PREFIX}{session_id}"

    def _get_user_sessions_key(
        self,
        user_id: int,
        novel_id: int | None = None,
    ) -> str:
        if novel_id:
            return f"{self.USER_SESSIONS_PREFIX}{user_id}:novel:{novel_id}"
        return f"{self.USER_SESSIONS_PREFIX}{user_id}"

    async def _save_to_cache(self, session: Session) -> bool:
        """保存到Redis缓存"""
        try:
            session.updated_at = datetime.now(timezone.utc)
            session_key = self._get_session_key(session.session_id)
            await redis_service.set(session_key, session.model_dump(mode="json"), ttl=self.cache_ttl)
            return True
        except Exception as e:
            logger.warning(f"Failed to save to cache: {e}")
            return False

    async def save(self, session: Session) -> bool:
        """保存会话 - 双写DB和Redis"""
        try:
            # DB
            data = session.model_dump(exclude=self._SAVE_EXCLUDE)

            async with AsyncSessionLocal() as db:
                result = await db.execute(
                    select(DBChatSession).where(DBChatSession.session_id == session.session_id)
                )
                db_session = result.scalar_one_or_none()

                if db_session:
                    for k, v in data.items():
                        setattr(db_session, k, v)
                else:
                    db.add(DBChatSession(**data))

                await db.flush()

                await db.execute(
                    delete(DBChatMessage).where(DBChatMessage.session_id == db_session.id)
                )

                for msg in session.messages:
                    db.add(DBChatMessage(
                        session_id=db_session.id,
                        role=msg.role.value,
                        content=msg.content,
                        token_count=msg.token_count,
                        extra_metadata=msg.extra_metadata,
                    ))

                await db.commit()

            # Cache
            await self._save_to_cache(session)

            # User sessions index
            user_key = self._get_user_sessions_key(session.user_id, session.novel_id)
            await redis_service.zadd(
                user_key,
                {session.session_id: datetime.now(timezone.utc).timestamp()},
                ttl=self.cache_ttl,
            )

            logger.debug(f"Session saved: {session.session_id}")
            return True
        except Exception as e:
            logger.error(f"Failed to save session: {e}")
            return False

    async def load(self, session_id: str) -> Session | None:
        """加载会话 - 先缓存后数据库"""
        try:
            # Cache first
            session_key = self._get_session_key(session_id)
            data = await redis_service.get(session_key)
            if data:
                logger.debug(f"Session loaded from cache: {session_id}")
                return Session.model_validate(data)

            # DB fallback
            async with AsyncSessionLocal() as db:
                result = await db.execute(
                    select(DBChatSession)
                    .options(selectinload(DBChatSession.messages))
                    .where(DBChatSession.session_id == session_id)
                )
                db_session = result.scalar_one_or_none()

                if not db_session:
                    return None

                session = Session.model_validate(db_session)

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

            # DB
            async with AsyncSessionLocal() as db:
                result = await db.execute(
                    select(DBChatSession).where(DBChatSession.session_id == session_id)
                )
                db_session = result.scalar_one_or_none()
                if db_session:
                    await db.delete(db_session)
                    await db.commit()

            # Cache
            session_key = self._get_session_key(session_id)
            await redis_service.delete(session_key)

            # User sessions index
            if session.user_id:
                user_key = self._get_user_sessions_key(session.user_id, session.novel_id)
                await redis_service.zrem(user_key, session_id)

            logger.info(f"Session deleted: {session_id}")
            return True
        except Exception as e:
            logger.error(f"Failed to delete session: {e}")
            return False

    async def list_by_user(
        self,
        user_id: int,
        novel_id: int | None = None,
        limit: int = 20,
    ) -> list[Session]:
        """列出用户会话"""
        try:
            async with AsyncSessionLocal() as db:
                query = select(DBChatSession).options(
                    selectinload(DBChatSession.messages)
                ).where(DBChatSession.user_id == user_id)

                if novel_id:
                    query = query.where(DBChatSession.novel_id == novel_id)

                query = query.order_by(DBChatSession.updated_at.desc()).limit(limit)

                result = await db.execute(query)
                db_sessions = result.scalars().all()

                return [Session.model_validate(s) for s in db_sessions]
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

            async with AsyncSessionLocal() as db:
                result = await db.execute(
                    select(DBChatSession.id).where(DBChatSession.session_id == session_id)
                )
                return result.scalar_one_or_none() is not None
        except Exception as e:
            logger.error(f"Failed to check session existence: {e}")
            return False

    async def get_session_count(
        self,
        user_id: int,
        novel_id: int | None = None,
    ) -> int:
        """获取会话数量"""
        try:
            async with AsyncSessionLocal() as db:
                query = select(func.count(DBChatSession.id)).where(DBChatSession.user_id == user_id)
                if novel_id:
                    query = query.where(DBChatSession.novel_id == novel_id)
                result = await db.execute(query)
                return result.scalar() or 0
        except Exception as e:
            logger.error(f"Failed to get session count: {e}")
            return 0


session_storage = SessionStorage()
