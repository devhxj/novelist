"""
LLM服务 - DeepSeek API集成
"""
import os
import logging
import httpx
from typing import Dict, Any, List, Optional
from dataclasses import dataclass

logger = logging.getLogger(__name__)


@dataclass
class LLMConfig:
    """LLM配置"""
    api_key: str = os.getenv("DEEPSEEK_API_KEY", "")
    api_base: str = os.getenv("DEEPSEEK_API_BASE", "https://api.deepseek.com")
    model: str = os.getenv("DEEPSEEK_MODEL", "deepseek-chat")
    max_tokens: int = int(os.getenv("DEEPSEEK_MAX_TOKENS", "4096"))
    temperature: float = float(os.getenv("DEEPSEEK_TEMPERATURE", "0.7"))
    timeout: int = int(os.getenv("DEEPSEEK_TIMEOUT", "60"))
    
    @classmethod
    def validate(cls):
        """验证配置"""
        if not cls.api_key:
            raise ValueError("DEEPSEEK_API_KEY is required")


class LLMService:
    """LLM服务 - 调用DeepSeek API"""
    
    _instance = None
    
    def __new__(cls):
        if cls._instance is None:
            cls._instance = super().__new__(cls)
            cls._instance._initialized = False
        return cls._instance
    
    def __init__(self):
        if self._initialized:
            return
        
        LLMConfig.validate()
        self.config = LLMConfig()
        self.client = httpx.AsyncClient(timeout=self.config.timeout)
        self._initialized = True
        
        logger.info(f"LLM Service initialized with model: {self.config.model}")
    
    async def chat_completion(
        self,
        messages: List[Dict[str, str]],
        temperature: Optional[float] = None,
        max_tokens: Optional[int] = None,
        stream: bool = False
    ) -> Dict[str, Any]:
        """
        调用Chat Completion API
        
        Args:
            messages: 消息列表，格式: [{"role": "user", "content": "..."}]
            temperature: 温度参数
            max_tokens: 最大token数
            stream: 是否流式输出
            
        Returns:
            API响应
        """
        url = f"{self.config.api_base}/v1/chat/completions"
        
        headers = {
            "Authorization": f"Bearer {self.config.api_key}",
            "Content-Type": "application/json"
        }
        
        payload = {
            "model": self.config.model,
            "messages": messages,
            "temperature": temperature or self.config.temperature,
            "max_tokens": max_tokens or self.config.max_tokens,
            "stream": stream
        }
        
        try:
            logger.debug(f"Calling LLM API with {len(messages)} messages")
            
            response = await self.client.post(url, json=payload, headers=headers)
            response.raise_for_status()
            
            result = response.json()
            
            if "choices" in result and len(result["choices"]) > 0:
                content = result["choices"][0]["message"]["content"]
                logger.info(f"LLM response received: {len(content)} chars")
                return {
                    "success": True,
                    "content": content,
                    "usage": result.get("usage", {}),
                    "model": result.get("model", self.config.model)
                }
            else:
                logger.error(f"Unexpected API response: {result}")
                return {
                    "success": False,
                    "error": "Unexpected API response format"
                }
                
        except httpx.HTTPStatusError as e:
            logger.error(f"HTTP error calling LLM API: {e}")
            return {
                "success": False,
                "error": f"HTTP error: {e.response.status_code}"
            }
        except httpx.RequestError as e:
            logger.error(f"Request error calling LLM API: {e}")
            return {
                "success": False,
                "error": f"Request error: {str(e)}"
            }
        except Exception as e:
            logger.error(f"Unexpected error calling LLM API: {e}")
            return {
                "success": False,
                "error": str(e)
            }
    
    async def generate_text(
        self,
        prompt: str,
        system_prompt: Optional[str] = None,
        temperature: Optional[float] = None,
        max_tokens: Optional[int] = None
    ) -> str:
        """
        生成文本（简化接口）
        
        Args:
            prompt: 用户提示
            system_prompt: 系统提示
            temperature: 温度参数
            max_tokens: 最大token数
            
        Returns:
            生成的文本
        """
        messages = []
        
        if system_prompt:
            messages.append({"role": "system", "content": system_prompt})
        
        messages.append({"role": "user", "content": prompt})
        
        result = await self.chat_completion(
            messages=messages,
            temperature=temperature,
            max_tokens=max_tokens
        )
        
        if result["success"]:
            return result["content"]
        else:
            raise Exception(f"LLM generation failed: {result.get('error', 'Unknown error')}")
    
    async def close(self):
        """关闭客户端"""
        await self.client.aclose()


llm_service = LLMService()
