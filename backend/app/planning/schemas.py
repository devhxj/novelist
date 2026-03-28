"""
情节规划模块 - Pydantic验证模型
"""
from pydantic import BaseModel, Field
from typing import Optional, Dict, Any, List
from datetime import datetime
from enum import Enum


class PlotLineType(str, Enum):
    MAIN = "main"
    SUB = "sub"
    CHARACTER = "character"
    BACKGROUND = "background"


class PlotNodeStatus(str, Enum):
    PLANNED = "planned"
    IN_PROGRESS = "in_progress"
    COMPLETED = "completed"
    SKIPPED = "skipped"


class PlotLineCreate(BaseModel):
    """创建情节线请求"""
    name: str = Field(..., min_length=1, max_length=255, description="情节线名称")
    description: Optional[str] = Field(None, description="情节线描述")
    line_type: PlotLineType = Field(default=PlotLineType.SUB, description="情节线类型")
    start_chapter: Optional[int] = Field(None, ge=1, description="起始章节")
    end_chapter: Optional[int] = Field(None, ge=1, description="结束章节")
    importance: int = Field(default=1, ge=1, le=5, description="重要程度")
    metadata: Optional[Dict[str, Any]] = Field(None, description="额外元数据")


class PlotLineUpdate(BaseModel):
    """更新情节线请求"""
    name: Optional[str] = Field(None, min_length=1, max_length=255)
    description: Optional[str] = None
    line_type: Optional[PlotLineType] = None
    start_chapter: Optional[int] = Field(None, ge=1)
    end_chapter: Optional[int] = Field(None, ge=1)
    importance: Optional[int] = Field(None, ge=1, le=5)
    status: Optional[str] = None
    metadata: Optional[Dict[str, Any]] = None


class PlotLineResponse(BaseModel):
    """情节线响应"""
    id: int
    novel_id: int
    name: str
    description: Optional[str]
    line_type: str
    start_chapter: Optional[int]
    end_chapter: Optional[int]
    importance: int
    status: str
    metadata: Optional[Dict[str, Any]]
    created_at: datetime
    updated_at: Optional[datetime]
    
    class Config:
        from_attributes = True


class PlotNodeCreate(BaseModel):
    """创建情节节点请求"""
    plot_line_id: int = Field(..., description="情节线ID")
    title: str = Field(..., min_length=1, max_length=255, description="节点标题")
    description: Optional[str] = Field(None, description="节点描述")
    chapter_number: Optional[int] = Field(None, ge=1, description="章节号")
    sequence: int = Field(default=0, ge=0, description="顺序")
    characters_involved: Optional[List[int]] = Field(None, description="涉及角色ID列表")
    prerequisites: Optional[List[int]] = Field(None, description="前置节点ID列表")
    consequences: Optional[Dict[str, Any]] = Field(None, description="后果")
    notes: Optional[str] = Field(None, description="备注")
    metadata: Optional[Dict[str, Any]] = Field(None, description="额外元数据")


class PlotNodeUpdate(BaseModel):
    """更新情节节点请求"""
    title: Optional[str] = Field(None, min_length=1, max_length=255)
    description: Optional[str] = None
    chapter_number: Optional[int] = Field(None, ge=1)
    sequence: Optional[int] = Field(None, ge=0)
    status: Optional[PlotNodeStatus] = None
    characters_involved: Optional[List[int]] = None
    prerequisites: Optional[List[int]] = None
    consequences: Optional[Dict[str, Any]] = None
    notes: Optional[str] = None
    metadata: Optional[Dict[str, Any]] = None


class PlotNodeResponse(BaseModel):
    """情节节点响应"""
    id: int
    plot_line_id: int
    novel_id: int
    title: str
    description: Optional[str]
    chapter_number: Optional[int]
    sequence: int
    status: str
    characters_involved: Optional[List[int]]
    prerequisites: Optional[List[int]]
    consequences: Optional[Dict[str, Any]]
    notes: Optional[str]
    metadata: Optional[Dict[str, Any]]
    created_at: datetime
    updated_at: Optional[datetime]
    
    class Config:
        from_attributes = True


class PlotOutlineCreate(BaseModel):
    """创建情节大纲请求"""
    title: str = Field(..., min_length=1, max_length=255, description="大纲标题")
    premise: Optional[str] = Field(None, description="故事前提")
    theme: Optional[str] = Field(None, max_length=255, description="主题")
    act_structure: Optional[Dict[str, Any]] = Field(None, description="幕结构")
    beginning: Optional[str] = Field(None, description="开端")
    middle: Optional[str] = Field(None, description="发展")
    climax: Optional[str] = Field(None, description="高潮")
    ending: Optional[str] = Field(None, description="结局")
    total_chapters: Optional[int] = Field(None, ge=1, description="总章节数")
    notes: Optional[str] = Field(None, description="备注")
    metadata: Optional[Dict[str, Any]] = Field(None, description="额外元数据")


class PlotOutlineUpdate(BaseModel):
    """更新情节大纲请求"""
    title: Optional[str] = Field(None, min_length=1, max_length=255)
    premise: Optional[str] = None
    theme: Optional[str] = Field(None, max_length=255)
    act_structure: Optional[Dict[str, Any]] = None
    beginning: Optional[str] = None
    middle: Optional[str] = None
    climax: Optional[str] = None
    ending: Optional[str] = None
    total_chapters: Optional[int] = Field(None, ge=1)
    current_chapter: Optional[int] = Field(None, ge=1)
    notes: Optional[str] = None
    metadata: Optional[Dict[str, Any]] = None


class PlotOutlineResponse(BaseModel):
    """情节大纲响应"""
    id: int
    novel_id: int
    title: str
    premise: Optional[str]
    theme: Optional[str]
    act_structure: Optional[Dict[str, Any]]
    beginning: Optional[str]
    middle: Optional[str]
    climax: Optional[str]
    ending: Optional[str]
    total_chapters: Optional[int]
    current_chapter: int
    notes: Optional[str]
    metadata: Optional[Dict[str, Any]]
    created_at: datetime
    updated_at: Optional[datetime]
    
    class Config:
        from_attributes = True


class PlotSuggestionRequest(BaseModel):
    """情节建议请求"""
    context: str = Field(..., description="当前上下文")
    chapter_number: int = Field(..., ge=1, description="目标章节号")
    plot_line_id: Optional[int] = Field(None, description="情节线ID")


class PlotSuggestionResponse(BaseModel):
    """情节建议响应"""
    suggestions: List[Dict[str, Any]] = Field(default_factory=list, description="情节建议列表")
    reasoning: Optional[str] = Field(None, description="推理过程")
