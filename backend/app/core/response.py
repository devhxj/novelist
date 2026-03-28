"""
统一响应格式
"""
from typing import Any, Optional
from fastapi import status
from fastapi.responses import JSONResponse

class ApiResponse:
    @staticmethod
    def success(data: Any, message: str = "操作成功"):
        return {
            "success": True,
            "data": data,
            "message": message
        }
    
    @staticmethod
    def error(code: str, message: str, details: Optional[Any] = None, status_code: int = 400):
        return JSONResponse(
            status_code=status_code,
            content={
                "success": False,
                "error": {
                    "code": code,
                    "message": message,
                    "details": details
                }
            }
        )
    
    @staticmethod
    def paginated(items: list, total: int, page: int, page_size: int):
        return {
            "success": True,
            "data": {
                "items": items,
                "total": total,
                "page": page,
                "page_size": page_size,
                "total_pages": (total + page_size - 1) // page_size
            }
        }