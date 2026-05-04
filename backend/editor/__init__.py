"""
文本编辑模块 - AI IDE风格
支持副本编辑机制、diff展示、变更确认
"""
from editor.models import EditSession, EditChange, EditSessionStatus, ChangeSource
from editor.service import EditSessionManager, get_edit_session_manager

__all__ = [
    "EditSession",
    "EditChange",
    "EditSessionStatus",
    "ChangeSource",
    "EditSessionManager",
    "get_edit_session_manager"
]
