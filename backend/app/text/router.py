"""
文本生成API路由 - 对话、描写、大纲、摘要、角色档案
章节生成请使用WebSocket: ws://host/ws/generation
"""
import logging
from fastapi import APIRouter

from app.core.response import ApiResponse
from app.core.database import DBSession
from app.core.dependencies import NovelOwner
from app.text.service import TextGenerator, GenerationType, GenerationConfig

router = APIRouter(prefix="/text", tags=["text-generation"])
logger = logging.getLogger(__name__)


@router.post("/novels/{novel_id}/generate/dialogue")
async def generate_dialogue(
    novel: NovelOwner,
    db: DBSession,
    characters: list,
    context: str,
    style: str = "natural"
):
    """
    生成对话
    
    - characters: 参与对话的角色列表
    - context: 对话场景背景
    - style: 对话风格
    """
    generator = TextGenerator(db, novel.id)
    result = await generator.generate_dialogue(
        characters=characters,
        context=context,
        style=style
    )
    
    return ApiResponse.success(result)


@router.post("/novels/{novel_id}/generate/description")
async def generate_description(
    novel: NovelOwner,
    db: DBSession,
    subject: str,
    style: str = "vivid"
):
    """
    生成描写
    
    - subject: 描写对象
    - style: 描写风格
    """
    generator = TextGenerator(db, novel.id)
    result = await generator.generate_description(
        subject=subject,
        style=style
    )
    
    return ApiResponse.success(result)


@router.post("/novels/{novel_id}/generate/outline")
async def generate_outline(
    novel: NovelOwner,
    db: DBSession,
    premise: str,
    genre: str,
    total_chapters: int = 20,
    style: str = "narrative"
):
    """
    生成大纲
    
    - premise: 故事前提
    - genre: 类型
    - total_chapters: 总章节数
    - style: 写作风格
    """
    generator = TextGenerator(db, novel.id)
    result = await generator.generate_outline(
        premise=premise,
        genre=genre,
        total_chapters=total_chapters,
        style=style
    )
    
    return ApiResponse.success(result)


@router.post("/novels/{novel_id}/generate/summary")
async def generate_summary(
    novel: NovelOwner,
    db: DBSession,
    content: str,
    max_length: int = 500
):
    """
    生成摘要
    
    - content: 原文内容
    - max_length: 最大长度
    """
    generator = TextGenerator(db, novel.id)
    result = await generator.generate_summary(
        content=content,
        max_length=max_length
    )
    
    return ApiResponse.success(result)


@router.post("/novels/{novel_id}/generate/character-profile")
async def generate_character_profile(
    novel: NovelOwner,
    db: DBSession,
    name: str,
    role: str,
    novel_context: str,
    style: str = "narrative"
):
    """
    生成角色档案
    
    - name: 角色名
    - role: 角色定位
    - novel_context: 小说背景
    - style: 写作风格
    """
    generator = TextGenerator(db, novel.id)
    result = await generator.generate_character_profile(
        name=name,
        role=role,
        novel_context=novel_context,
        style=style
    )
    
    return ApiResponse.success(result)


@router.get("/generation-types")
def get_generation_types():
    """获取支持的生成类型"""
    return ApiResponse.success({
        "types": [
            {"value": "dialogue", "label": "对话生成"},
            {"value": "description", "label": "描写生成"},
            {"value": "outline", "label": "大纲生成"},
            {"value": "summary", "label": "摘要生成"},
            {"value": "character_profile", "label": "角色档案生成"}
        ],
        "styles": [
            {"value": "narrative", "label": "叙述性"},
            {"value": "descriptive", "label": "描写性"},
            {"value": "dialogue", "label": "对话式"},
            {"value": "poetic", "label": "诗意"},
            {"value": "dramatic", "label": "戏剧性"},
            {"value": "natural", "label": "自然"},
            {"value": "vivid", "label": "生动"}
        ],
        "note": "章节生成请使用WebSocket: ws://host/ws/generation"
    })
