from sqlalchemy.ext.asyncio import create_async_engine, AsyncSession, async_sessionmaker
from sqlalchemy.orm import declarative_base
from typing import Annotated
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
    class_=AsyncSession
)

async def get_async_session() -> AsyncSession:
    async with AsyncSessionLocal() as session:
        yield session

Base = declarative_base()


async def get_db():
    async with AsyncSessionLocal() as session:
        try:
            yield session
        finally:
            await session.close()


async def init_db():
    from app.auth.models import User
    from app.novels.models import Novel
    from app.characters.models import Character
    from app.chapters.models import Chapter
    from app.plot_events.models import PlotEvent
    from app.memory.models import MemoryChunk
    from app.rag.models import RAGContext
    from app.agents.models import AgentTaskRecord
    from app.foreshadowing.models import Foreshadowing
    from app.planning.models import PlotLine, PlotNode, PlotOutline
    from app.editor.models import EditSession, EditChange
    
    async with engine.begin() as conn:
        await conn.run_sync(Base.metadata.create_all)


DBSession = Annotated[AsyncSession, Depends(get_db)]
