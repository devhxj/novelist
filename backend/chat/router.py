"""
Chat 模块 HTTP 路由
"""
from fastapi import APIRouter

from core.llm_service import llm_service
from core.response import ApiResponse

router = APIRouter(tags=["chat"])


@router.get("/models")
async def get_available_models():
    """获取可用模型列表"""
    return ApiResponse.success({"models": llm_service.get_available_models()})
