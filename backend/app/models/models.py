from sqlalchemy import Column, Integer, String, Text, TIMESTAMP, ForeignKey, JSON, UniqueConstraint, func
from sqlalchemy.orm import relationship
from backend.app.core.database import Base

class Novel(Base):
    __tablename__ = "novels"
    
    id = Column(Integer, primary_key=True, autoincrement=True)
    title = Column(String(255), nullable=False)
    genre = Column(String(100))
    description = Column(Text)
    author_id = Column(Integer)
    status = Column(String(50), default='draft')
    created_at = Column(TIMESTAMP, server_default=func.now())
    updated_at = Column(TIMESTAMP, server_default=func.now(), onupdate=func.now())
    
    characters = relationship("Character", back_populates="novel")
    chapters = relationship("Chapter", back_populates="novel")
    plot_events = relationship("PlotEvent", back_populates="novel")

class Character(Base):
    __tablename__ = "characters"
    
    id = Column(Integer, primary_key=True, autoincrement=True)
    novel_id = Column(Integer, ForeignKey("novels.id", ondelete="CASCADE"), nullable=False)
    name = Column(String(100), nullable=False)
    personality = Column(JSON)
    relationships = Column(JSON)
    abilities = Column(JSON)
    created_at = Column(TIMESTAMP, server_default=func.now())
    
    novel = relationship("Novel", back_populates="characters")

class Chapter(Base):
    __tablename__ = "chapters"
    
    id = Column(Integer, primary_key=True, autoincrement=True)
    novel_id = Column(Integer, ForeignKey("novels.id", ondelete="CASCADE"), nullable=False)
    chapter_number = Column(Integer, nullable=False)
    title = Column(String(255))
    content = Column(Text)
    summary = Column(Text)
    status = Column(String(50), default='draft')
    created_at = Column(TIMESTAMP, server_default=func.now())
    updated_at = Column(TIMESTAMP, server_default=func.now(), onupdate=func.now())
    
    novel = relationship("Novel", back_populates="chapters")
    plot_events = relationship("PlotEvent", back_populates="chapter")
    
    __table_args__ = (
        UniqueConstraint('novel_id', 'chapter_number', name='uk_novel_chapter'),
    )

class PlotEvent(Base):
    __tablename__ = "plot_events"
    
    id = Column(Integer, primary_key=True, autoincrement=True)
    novel_id = Column(Integer, ForeignKey("novels.id", ondelete="CASCADE"), nullable=False)
    chapter_id = Column(Integer, ForeignKey("chapters.id", ondelete="SET NULL"))
    event_type = Column(String(50))
    description = Column(Text)
    characters_involved = Column(JSON)
    timeline = Column(TIMESTAMP)
    consequences = Column(JSON)
    created_at = Column(TIMESTAMP, server_default=func.now())
    
    novel = relationship("Novel", back_populates="plot_events")
    chapter = relationship("Chapter", back_populates="plot_events")