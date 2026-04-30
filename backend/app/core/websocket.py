"""
WebSocket管理器 - 实时通信
"""
import logging
import json
from typing import Dict, Set, Optional, Any
from datetime import datetime, timezone
from fastapi import WebSocket, WebSocketDisconnect
from collections import defaultdict

logger = logging.getLogger(__name__)


MAX_CONNECTIONS_PER_USER = 5


class ConnectionManager:
    """WebSocket连接管理器"""
    
    def __init__(self):
        self.active_connections: Dict[int, Set[WebSocket]] = defaultdict(set)
        self.user_connections: Dict[int, Set[WebSocket]] = defaultdict(set)
    
    def get_user_connection_count(self, user_id: int) -> int:
        """获取用户当前连接数"""
        return len(self.user_connections.get(user_id, set()))
    
    def can_connect(self, user_id: int) -> bool:
        """检查用户是否可以建立新连接"""
        return self.get_user_connection_count(user_id) < MAX_CONNECTIONS_PER_USER
    
    async def connect(self, websocket: WebSocket, user_id: int, novel_id: Optional[int] = None) -> bool:
        """
        接受WebSocket连接
        
        Returns:
            bool: 连接是否成功（超过上限返回False）
        """
        if not self.can_connect(user_id):
            logger.warning(f"WebSocket rejected: user={user_id} exceeded max connections ({MAX_CONNECTIONS_PER_USER})")
            return False
        
        await websocket.accept()
        self.user_connections[user_id].add(websocket)
        if novel_id:
            self.active_connections[novel_id].add(websocket)
        logger.info(f"WebSocket connected: user={user_id}, novel={novel_id}, total_connections={self.get_user_connection_count(user_id)}")
        return True
    
    def disconnect(self, websocket: WebSocket, user_id: int, novel_id: Optional[int] = None):
        """断开WebSocket连接"""
        self.user_connections[user_id].discard(websocket)
        if novel_id:
            self.active_connections[novel_id].discard(websocket)
        logger.info(f"WebSocket disconnected: user={user_id}, novel={novel_id}")
    
    async def send_personal_message(self, message: dict, websocket: WebSocket):
        """发送个人消息"""
        try:
            await websocket.send_json(message)
        except Exception as e:
            logger.error(f"Failed to send personal message: {e}")
    
    async def broadcast_to_novel(self, novel_id: int, message: dict):
        """广播消息到小说的所有连接"""
        connections = self.active_connections.get(novel_id, set())
        disconnected = set()
        
        for connection in connections:
            try:
                await connection.send_json(message)
            except Exception:
                disconnected.add(connection)
        
        for conn in disconnected:
            self.active_connections[novel_id].discard(conn)
    
    async def broadcast_to_user(self, user_id: int, message: dict):
        """广播消息到用户的所有连接"""
        connections = self.user_connections.get(user_id, set())
        disconnected = set()
        
        for connection in connections:
            try:
                await connection.send_json(message)
            except Exception:
                disconnected.add(connection)
        
        for conn in disconnected:
            self.user_connections[user_id].discard(conn)


ws_manager = ConnectionManager()


class GenerationProgress:
    """生成进度消息构建器"""
    
    @staticmethod
    def started(task_id: str, generation_type: str, novel_id: int) -> dict:
        return {
            "type": "generation_started",
            "task_id": task_id,
            "novel_id": novel_id,
            "generation_type": generation_type,
            "status": "started",
            "progress": 0,
            "timestamp": datetime.now(timezone.utc).isoformat()
        }
    
    @staticmethod
    def progress(task_id: str, step: str, progress: int, message: str | None = None) -> dict:
        return {
            "type": "generation_progress",
            "task_id": task_id,
            "step": step,
            "progress": progress,
            "message": message,
            "timestamp": datetime.now(timezone.utc).isoformat()
        }
    
    @staticmethod
    def content_chunk(task_id: str, chunk: str, accumulated_length: int) -> dict:
        return {
            "type": "content_chunk",
            "task_id": task_id,
            "chunk": chunk,
            "accumulated_length": accumulated_length,
            "timestamp": datetime.now(timezone.utc).isoformat()
        }
    
    @staticmethod
    def completed(task_id: str, chapter_id: int | None, chapter_number: int | None,
                  content: str, word_count: int) -> dict:
        return {
            "type": "generation_completed",
            "task_id": task_id,
            "chapter_id": chapter_id,
            "chapter_number": chapter_number,
            "content": content,
            "word_count": word_count,
            "progress": 100,
            "timestamp": datetime.now(timezone.utc).isoformat()
        }
    
    @staticmethod
    def failed(task_id: str, error: str, step: str | None = None) -> dict:
        return {
            "type": "generation_failed",
            "task_id": task_id,
            "error": error,
            "step": step,
            "timestamp": datetime.now(timezone.utc).isoformat()
        }
    
    @staticmethod
    def review_result(task_id: str, approved: bool, score: float,
                      issues: list | None = None) -> dict:
        return {
            "type": "review_result",
            "task_id": task_id,
            "approved": approved,
            "score": score,
            "issues": issues or [],
            "timestamp": datetime.now(timezone.utc).isoformat()
        }
    
    @staticmethod
    def consistency_check(task_id: str, passed: bool, issues: list | None = None) -> dict:
        return {
            "type": "consistency_check",
            "task_id": task_id,
            "passed": passed,
            "issues": issues or [],
            "timestamp": datetime.now(timezone.utc).isoformat()
        }
