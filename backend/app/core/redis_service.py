"""
Redis缓存服务 - 支持热点数据缓存、分布式锁
"""
import os
import json
import logging
import asyncio
from typing import Optional, Any, Dict, List, Callable
from datetime import datetime, timedelta
from functools import wraps
import redis.asyncio as redis
from redis.asyncio import Redis
from redis.asyncio.lock import Lock
from fastapi.encoders import jsonable_encoder

logger = logging.getLogger(__name__)


class RedisConfig:
    """Redis配置"""
    REDIS_URL: str = os.getenv("REDIS_URL", "redis://localhost:6379/0")
    REDIS_PASSWORD: Optional[str] = os.getenv("REDIS_PASSWORD")
    REDIS_MAX_CONNECTIONS: int = int(os.getenv("REDIS_MAX_CONNECTIONS", "50"))
    
    CACHE_DEFAULT_TTL: int = int(os.getenv("CACHE_DEFAULT_TTL", "300"))
    CACHE_PREFIX: str = os.getenv("CACHE_PREFIX", "ai_novel:")
    
    LOCK_DEFAULT_TIMEOUT: int = int(os.getenv("LOCK_DEFAULT_TIMEOUT", "30"))
    LOCK_RETRY_INTERVAL: float = float(os.getenv("LOCK_RETRY_INTERVAL", "0.1"))


class RedisService:
    """Redis服务 - 单例模式"""
    
    _instance = None
    _redis: Optional[Redis] = None
    
    def __new__(cls):
        if cls._instance is None:
            cls._instance = super().__new__(cls)
        return cls._instance
    
    async def connect(self):
        """连接Redis"""
        if self._redis is not None:
            return
        
        try:
            self._redis = await redis.from_url(
                RedisConfig.REDIS_URL,
                password=RedisConfig.REDIS_PASSWORD,
                max_connections=RedisConfig.REDIS_MAX_CONNECTIONS,
                decode_responses=True
            )
            await self._redis.ping()
            logger.info(f"Redis connected: {RedisConfig.REDIS_URL}")
        except Exception as e:
            logger.error(f"Failed to connect Redis: {e}")
            raise
    
    async def disconnect(self):
        """断开Redis连接"""
        if self._redis:
            await self._redis.close()
            self._redis = None
            logger.info("Redis disconnected")
    
    @property
    def client(self) -> Redis:
        """获取Redis客户端"""
        if self._redis is None:
            raise RuntimeError("Redis not connected. Call connect() first.")
        return self._redis
    
    async def get(self, key: str) -> Optional[Any]:
        """获取缓存"""
        try:
            full_key = f"{RedisConfig.CACHE_PREFIX}{key}"
            value = await self.client.get(full_key)
            if value:
                return json.loads(value)
            return None
        except Exception as e:
            logger.error(f"Redis get error: {e}")
            return None
    
    async def set(
        self, 
        key: str, 
        value: Any, 
        ttl: Optional[int] = None
    ) -> bool:
        """设置缓存"""
        try:
            full_key = f"{RedisConfig.CACHE_PREFIX}{key}"
            encoded_value = jsonable_encoder(value)
            serialized = json.dumps(encoded_value, ensure_ascii=False)
            ttl = ttl or RedisConfig.CACHE_DEFAULT_TTL
            
            await self.client.setex(full_key, ttl, serialized)
            return True
        except Exception as e:
            logger.error(f"Redis set error: {e}")
            return False
    
    async def delete(self, key: str) -> bool:
        """删除缓存"""
        try:
            full_key = f"{RedisConfig.CACHE_PREFIX}{key}"
            await self.client.delete(full_key)
            return True
        except Exception as e:
            logger.error(f"Redis delete error: {e}")
            return False
    
    async def exists(self, key: str) -> bool:
        """检查key是否存在"""
        try:
            full_key = f"{RedisConfig.CACHE_PREFIX}{key}"
            return await self.client.exists(full_key) > 0
        except Exception as e:
            logger.error(f"Redis exists error: {e}")
            return False
    
    async def expire(self, key: str, ttl: int) -> bool:
        """设置过期时间"""
        try:
            full_key = f"{RedisConfig.CACHE_PREFIX}{key}"
            return await self.client.expire(full_key, ttl)
        except Exception as e:
            logger.error(f"Redis expire error: {e}")
            return False
    
    async def incr(self, key: str, amount: int = 1) -> int:
        """计数器增加"""
        try:
            full_key = f"{RedisConfig.CACHE_PREFIX}{key}"
            return await self.client.incrby(full_key, amount)
        except Exception as e:
            logger.error(f"Redis incr error: {e}")
            return 0
    
    async def decr(self, key: str, amount: int = 1) -> int:
        """计数器减少"""
        try:
            full_key = f"{RedisConfig.CACHE_PREFIX}{key}"
            return await self.client.decrby(full_key, amount)
        except Exception as e:
            logger.error(f"Redis decr error: {e}")
            return 0
    
    async def mget(self, keys: List[str]) -> List[Optional[Any]]:
        """批量获取"""
        try:
            full_keys = [f"{RedisConfig.CACHE_PREFIX}{k}" for k in keys]
            values = await self.client.mget(full_keys)
            return [json.loads(v) if v else None for v in values]
        except Exception as e:
            logger.error(f"Redis mget error: {e}")
            return [None] * len(keys)
    
    async def mset(self, mapping: Dict[str, Any], ttl: Optional[int] = None) -> bool:
        """批量设置"""
        try:
            ttl = ttl or RedisConfig.CACHE_DEFAULT_TTL
            pipe = self.client.pipeline()
            
            for key, value in mapping.items():
                full_key = f"{RedisConfig.CACHE_PREFIX}{key}"
                serialized = json.dumps(value, ensure_ascii=False)
                pipe.setex(full_key, ttl, serialized)
            
            await pipe.execute()
            return True
        except Exception as e:
            logger.error(f"Redis mset error: {e}")
            return False
    
    async def clear_pattern(self, pattern: str, batch_size: int = 100) -> int:
        """
        清除匹配模式的所有key（分批删除，避免阻塞）
        
        Args:
            pattern: 匹配模式
            batch_size: 每批删除的key数量
            
        Returns:
            删除的key数量
        """
        try:
            full_pattern = f"{RedisConfig.CACHE_PREFIX}{pattern}"
            deleted_count = 0
            cursor = 0
            
            while True:
                cursor, keys = await self.client.scan(
                    cursor=cursor,
                    match=full_pattern,
                    count=batch_size
                )
                
                if keys:
                    deleted = await self.client.delete(*keys)
                    deleted_count += deleted
                
                if cursor == 0:
                    break
            
            return deleted_count
        except Exception as e:
            logger.error(f"Redis clear_pattern error: {e}")
            return 0
    
    async def zadd(
        self, 
        key: str, 
        mapping: Dict[str, float],
        ttl: Optional[int] = None
    ) -> int:
        """
        添加有序集合成员
        
        Args:
            key: 键名
            mapping: {member: score} 映射
            ttl: 过期时间
            
        Returns:
            新增成员数量
        """
        try:
            full_key = f"{RedisConfig.CACHE_PREFIX}{key}"
            result = await self.client.zadd(full_key, mapping)
            if ttl:
                await self.client.expire(full_key, ttl)
            return result
        except Exception as e:
            logger.error(f"Redis zadd error: {e}")
            return 0
    
    async def zrem(self, key: str, *members) -> int:
        """
        移除有序集合成员
        
        Args:
            key: 键名
            members: 要移除的成员
            
        Returns:
            移除成员数量
        """
        try:
            full_key = f"{RedisConfig.CACHE_PREFIX}{key}"
            return await self.client.zrem(full_key, *members)
        except Exception as e:
            logger.error(f"Redis zrem error: {e}")
            return 0
    
    async def zrevrange(
        self, 
        key: str, 
        start: int, 
        stop: int,
        withscores: bool = False
    ) -> List:
        """
        获取有序集合指定范围成员（按分数降序）
        
        Args:
            key: 键名
            start: 起始位置
            stop: 结束位置
            withscores: 是否返回分数
            
        Returns:
            成员列表
        """
        try:
            full_key = f"{RedisConfig.CACHE_PREFIX}{key}"
            return await self.client.zrevrange(full_key, start, stop, withscores)
        except Exception as e:
            logger.error(f"Redis zrevrange error: {e}")
            return []
    
    async def zcard(self, key: str) -> int:
        """
        获取有序集合成员数量
        
        Args:
            key: 键名
            
        Returns:
            成员数量
        """
        try:
            full_key = f"{RedisConfig.CACHE_PREFIX}{key}"
            return await self.client.zcard(full_key)
        except Exception as e:
            logger.error(f"Redis zcard error: {e}")
            return 0
    
    async def zrange(
        self, 
        key: str, 
        start: int, 
        stop: int,
        withscores: bool = False
    ) -> List:
        """
        获取有序集合指定范围成员（按分数升序）
        
        Args:
            key: 键名
            start: 起始位置
            stop: 结束位置
            withscores: 是否返回分数
            
        Returns:
            成员列表
        """
        try:
            full_key = f"{RedisConfig.CACHE_PREFIX}{key}"
            return await self.client.zrange(full_key, start, stop, withscores)
        except Exception as e:
            logger.error(f"Redis zrange error: {e}")
            return []
    
    def acquire_lock(
        self, 
        lock_name: str, 
        timeout: Optional[int] = None,
        blocking_timeout: Optional[float] = None
    ) -> Lock:
        """
        获取分布式锁
        
        Args:
            lock_name: 锁名称
            timeout: 锁超时时间（秒）
            blocking_timeout: 获取锁的等待超时时间（秒）
            
        Returns:
            Redis Lock对象
        """
        timeout = timeout or RedisConfig.LOCK_DEFAULT_TIMEOUT
        full_lock_name = f"{RedisConfig.CACHE_PREFIX}lock:{lock_name}"
        
        return self.client.lock(
            name=full_lock_name,
            timeout=timeout,
            blocking_timeout=blocking_timeout
        )


redis_service = RedisService()


def cache_result(
    key_prefix: str,
    ttl: Optional[int] = None,
    key_builder: Optional[Callable] = None
):
    """
    缓存装饰器 - 自动缓存函数返回值
    
    Args:
        key_prefix: 缓存key前缀
        ttl: 缓存过期时间（秒）
        key_builder: 自定义key构建函数
        
    Usage:
        @cache_result("novel_summary", ttl=600)
        async def get_novel_summary(novel_id: int):
            ...
    """
    def decorator(func: Callable):
        @wraps(func)
        async def wrapper(*args, **kwargs):
            if key_builder:
                cache_key = key_builder(*args, **kwargs)
            else:
                args_str = "_".join(str(a) for a in args)
                kwargs_str = "_".join(f"{k}:{v}" for k, v in sorted(kwargs.items()))
                cache_key = f"{key_prefix}:{args_str}_{kwargs_str}"
            
            cached = await redis_service.get(cache_key)
            if cached is not None:
                logger.debug(f"Cache hit: {cache_key}")
                return cached
            
            result = await func(*args, **kwargs)
            
            if result is not None:
                await redis_service.set(cache_key, result, ttl)
                logger.debug(f"Cache set: {cache_key}")
            
            return result
        
        return wrapper
    return decorator


class NovelCache:
    """小说相关缓存管理"""
    
    @staticmethod
    async def get_novel_summary(novel_id: int) -> Optional[Dict[str, Any]]:
        """获取小说摘要缓存"""
        return await redis_service.get(f"novel:{novel_id}:summary")
    
    @staticmethod
    async def set_novel_summary(novel_id: int, summary: Dict[str, Any], ttl: int = 600):
        """设置小说摘要缓存"""
        await redis_service.set(f"novel:{novel_id}:summary", summary, ttl)
    
    @staticmethod
    async def invalidate_novel(novel_id: int):
        """清除小说相关所有缓存"""
        await redis_service.clear_pattern(f"novel:{novel_id}:*")
    
    @staticmethod
    async def get_chapter_content(chapter_id: int) -> Optional[str]:
        """获取章节内容缓存"""
        return await redis_service.get(f"chapter:{chapter_id}:content")
    
    @staticmethod
    async def set_chapter_content(chapter_id: int, content: str, ttl: int = 300):
        """设置章节内容缓存"""
        await redis_service.set(f"chapter:{chapter_id}:content", content, ttl)
    
    @staticmethod
    async def get_character_memory(character_id: int) -> Optional[Dict[str, Any]]:
        """获取角色记忆缓存"""
        return await redis_service.get(f"character:{character_id}:memory")
    
    @staticmethod
    async def set_character_memory(character_id: int, memory: Dict[str, Any], ttl: int = 300):
        """设置角色记忆缓存"""
        await redis_service.set(f"character:{character_id}:memory", memory, ttl)


class GenerationLock:
    """章节生成分布式锁"""
    
    def __init__(self, novel_id: int, chapter_number: int):
        self.lock_name = f"generation:{novel_id}:{chapter_number}"
        self.lock: Optional[Lock] = None
    
    async def __aenter__(self):
        self.lock = redis_service.acquire_lock(
            self.lock_name,
            timeout=300,
            blocking_timeout=0
        )
        acquired = await self.lock.acquire()
        if not acquired:
            raise RuntimeError(f"Chapter generation already in progress: {self.lock_name}")
        return self
    
    async def __aexit__(self, exc_type, exc_val, exc_tb):
        if self.lock:
            try:
                await self.lock.release()
            except Exception as e:
                logger.warning(f"Failed to release lock: {e}")
    
    @staticmethod
    async def is_locked(novel_id: int, chapter_number: int) -> bool:
        """检查是否被锁定"""
        lock_name = f"generation:{novel_id}:{chapter_number}"
        full_lock_name = f"{RedisConfig.CACHE_PREFIX}lock:{lock_name}"
        return await redis_service.exists(full_lock_name)
