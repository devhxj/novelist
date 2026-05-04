from sqlalchemy.ext.asyncio import create_async_engine, AsyncSession, async_sessionmaker
from sqlalchemy.orm import declarative_base
from typing import Annotated, AsyncGenerator
from fastapi import Depends
import os
from dotenv import load_dotenv

load_dotenv()

DATABASE_URL = os.getenv("DATABASE_URL", "mysql+aiomysql://root:password@localhost:3306/ai_novel_generator")

engine = create_async_engine(
    DATABASE_URL, 
    echo=os.getenv("DB_ECHO", "false").lower() == "true",
    pool_pre_ping=True,
    pool_size=10,
    max_overflow=20
)

AsyncSessionLocal = async_sessionmaker(
    autocommit=False,
    autoflush=False,
    bind=engine,
    class_=AsyncSession,
    expire_on_commit=False
)

async def get_async_session() -> AsyncGenerator[AsyncSession, None]:
    async with AsyncSessionLocal() as session:
        yield session

Base = declarative_base()


async def get_db() -> AsyncGenerator[AsyncSession, None]:
    async with AsyncSessionLocal() as session:
        try:
            yield session
        finally:
            await session.close()


async def init_db():
    from auth.models import User
    from novels.models import Novel, NovelCreativeProfile
    from characters.models import Character, CharacterRelation
    from locations.models import Location
    from chapters.models import Chapter
    from memory.models import MemoryChunk
    from rag.models import RAGContext
    from agents.models import AgentTaskRecord
    from story_arcs.models import StoryArc
    from editor.models import EditSession, EditChange
    from timeline.models import TimelineEntry
    from novels.models import UserCreativeProfile
    
    async with engine.begin() as conn:
        await conn.run_sync(Base.metadata.create_all)


DBSession = Annotated[AsyncSession, Depends(get_db)]
