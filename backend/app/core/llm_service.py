"""
LLM服务 - DeepSeek API集成
支持多模型选择、流式输出、多轮对话、会话管理
"""
import os
import logging
import httpx
from typing import Dict, Any, List, Optional, AsyncGenerator
from dataclasses import dataclass

from app.core.prompt_templates import LLMModel
from app.core.session_manager import (
    Session, SessionManager, SessionConfig, MessageRole,
    session_manager
)
from app.core.session_storage import session_storage

logger = logging.getLogger(__name__)


@dataclass
class LLMConfig:
    """LLM配置"""
    api_key: str = os.getenv("DEEPSEEK_API_KEY", "")
    api_base: str = os.getenv("DEEPSEEK_API_BASE", "https://api.deepseek.com")
    default_model: str = os.getenv("DEEPSEEK_MODEL", "deepseek-chat")
    max_tokens: int = int(os.getenv("DEEPSEEK_MAX_TOKENS", "4096"))
    temperature: float = float(os.getenv("DEEPSEEK_TEMPERATURE", "0.7"))
    timeout: int = int(os.getenv("DEEPSEEK_TIMEOUT", "120"))
    
    @classmethod
    def validate(cls):
        """验证配置"""
        if not cls.api_key:
            raise ValueError("DEEPSEEK_API_KEY is required")


class LLMService:
    """LLM服务 - 调用DeepSeek API，支持会话管理"""
    
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
        
        session_manager.set_storage(session_storage)
        
        self._initialized = True
        
        logger.info(f"LLM Service initialized, default model: {self.config.default_model}")
    
    async def chat_completion(
        self,
        messages: List[Dict[str, str]],
        model: Optional[str] = None,
        temperature: Optional[float] = None,
        max_tokens: Optional[int] = None,
        stream: bool = False
    ) -> Dict[str, Any]:
        """
        调用Chat Completion API
        
        Args:
            messages: 消息列表，格式: [{"role": "user", "content": "..."}]
            model: 模型名称 (deepseek-chat / deepseek-reasoner)
            temperature: 温度参数
            max_tokens: 最大token数
            stream: 是否流式输出
            
        Returns:
            API响应
        """
        url = f"{self.config.api_base}/v1/chat/completions"
        
        selected_model = model or self.config.default_model
        
        headers = {
            "Authorization": f"Bearer {self.config.api_key}",
            "Content-Type": "application/json"
        }
        
        payload = {
            "model": selected_model,
            "messages": messages,
            "temperature": temperature or self.config.temperature,
            "max_tokens": max_tokens or self.config.max_tokens,
            "stream": stream
        }
        
        try:
            logger.debug(f"Calling LLM API: model={selected_model}, messages={len(messages)}")
            
            response = await self.client.post(url, json=payload, headers=headers)
            response.raise_for_status()
            
            result = response.json()
            
            if "choices" in result and len(result["choices"]) > 0:
                content = result["choices"][0]["message"]["content"]
                logger.info(f"LLM response: model={selected_model}, {len(content)} chars")
                return {
                    "success": True,
                    "content": content,
                    "usage": result.get("usage", {}),
                    "model": result.get("model", selected_model)
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
        model: Optional[str] = None,
        temperature: Optional[float] = None,
        max_tokens: Optional[int] = None
    ) -> str:
        """
        生成文本（简化接口）
        
        Args:
            prompt: 用户提示
            system_prompt: 系统提示
            model: 模型名称
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
            model=model,
            temperature=temperature,
            max_tokens=max_tokens
        )
        
        if result["success"]:
            return result["content"]
        else:
            raise Exception(f"LLM generation failed: {result.get('error', 'Unknown error')}")
    
    async def generate_stream(
        self,
        prompt: str,
        system_prompt: Optional[str] = None,
        model: Optional[str] = None,
        temperature: Optional[float] = None,
        max_tokens: Optional[int] = None
    ) -> AsyncGenerator[str, None]:
        """
        流式生成文本
        
        Args:
            prompt: 用户提示
            system_prompt: 系统提示
            model: 模型名称
            temperature: 温度参数
            max_tokens: 最大token数
            
        Yields:
            文本片段
        """
        import json
        
        url = f"{self.config.api_base}/v1/chat/completions"
        
        selected_model = model or self.config.default_model
        
        headers = {
            "Authorization": f"Bearer {self.config.api_key}",
            "Content-Type": "application/json"
        }
        
        messages = []
        if system_prompt:
            messages.append({"role": "system", "content": system_prompt})
        messages.append({"role": "user", "content": prompt})
        
        payload = {
            "model": selected_model,
            "messages": messages,
            "temperature": temperature or self.config.temperature,
            "max_tokens": max_tokens or self.config.max_tokens,
            "stream": True
        }
        
        logger.debug(f"Starting stream generation: model={selected_model}")
        
        try:
            async with self.client.stream("POST", url, json=payload, headers=headers) as response:
                response.raise_for_status()
                
                async for line in response.aiter_lines():
                    if line.startswith("data: "):
                        data = line[6:]
                        if data == "[DONE]":
                            break
                        
                        try:
                            chunk = json.loads(data)
                            if "choices" in chunk and len(chunk["choices"]) > 0:
                                delta = chunk["choices"][0].get("delta", {})
                                content = delta.get("content", "")
                                if content:
                                    yield content
                        except json.JSONDecodeError:
                            continue
                            
        except Exception as e:
            logger.error(f"Stream generation error: {e}")
            raise
    
    async def chat_with_session(
        self,
        session: Session,
        user_message: str,
        model: Optional[str] = None,
        temperature: Optional[float] = None,
        stream: bool = False
    ) -> AsyncGenerator[str, None] | str:
        """
        基于会话的多轮对话
        
        Args:
            session: 会话对象
            user_message: 用户消息
            model: 模型名称
            temperature: 温度参数
            stream: 是否流式输出
            
        Returns:
            生成的文本或异步生成器
        """
        session_manager.add_message(session, MessageRole.USER, user_message)
        
        messages = session_manager.get_messages_for_api(session)
        
        if session_manager.compressor.should_compress(session):
            summary = await self._generate_summary(session)
            session_manager.compress_session(session, summary)
            messages = session_manager.get_messages_for_api(session)
        
        if stream:
            return self._stream_with_session(session, messages, model, temperature)
        else:
            result = await self.chat_completion(
                messages=messages,
                model=model,
                temperature=temperature
            )
            
            if result["success"]:
                session_manager.add_message(
                    session, MessageRole.ASSISTANT, result["content"],
                    metadata={"usage": result.get("usage", {})}
                )
                await session_manager.save_session(session)
                return result["content"]
            else:
                raise Exception(f"LLM generation failed: {result.get('error')}")
    
    async def _stream_with_session(
        self,
        session: Session,
        messages: List[Dict[str, str]],
        model: Optional[str],
        temperature: Optional[float]
    ) -> AsyncGenerator[str, None]:
        """流式生成并保存到会话"""
        import json
        
        url = f"{self.config.api_base}/v1/chat/completions"
        selected_model = model or self.config.default_model
        
        headers = {
            "Authorization": f"Bearer {self.config.api_key}",
            "Content-Type": "application/json"
        }
        
        payload = {
            "model": selected_model,
            "messages": messages,
            "temperature": temperature or self.config.temperature,
            "max_tokens": self.config.max_tokens,
            "stream": True
        }
        
        full_content = ""
        
        try:
            async with self.client.stream("POST", url, json=payload, headers=headers) as response:
                response.raise_for_status()
                
                async for line in response.aiter_lines():
                    if line.startswith("data: "):
                        data = line[6:]
                        if data == "[DONE]":
                            break
                        
                        try:
                            chunk = json.loads(data)
                            if "choices" in chunk and len(chunk["choices"]) > 0:
                                delta = chunk["choices"][0].get("delta", {})
                                content = delta.get("content", "")
                                if content:
                                    full_content += content
                                    yield content
                        except json.JSONDecodeError:
                            continue
            
            session_manager.add_message(
                session, MessageRole.ASSISTANT, full_content
            )
            await session_manager.save_session(session)
            
        except Exception as e:
            logger.error(f"Stream with session error: {e}")
            raise
    
    async def _generate_summary(self, session: Session) -> str:
        """生成会话摘要"""
        messages_to_summarize = [
            m for m in session.messages
            if m.role != MessageRole.SYSTEM
        ][:-10]
        
        if not messages_to_summarize:
            return ""
        
        summary_prompt = f"""请总结以下对话内容，保留关键信息：

{chr(10).join([f"[{m.role.value}]: {m.content[:300]}..." for m in messages_to_summarize[-5:]])}

请用简洁的语言总结上述对话的关键内容，包括：
1. 讨论的主要话题
2. 重要的设定或决定
3. 用户的核心需求
"""
        
        try:
            summary = await self.generate_text(summary_prompt)
            return f"[历史对话摘要]\n{summary}"
        except Exception as e:
            logger.error(f"Failed to generate summary: {e}")
            return ""
    
    async def close(self):
        """关闭客户端"""
        await self.client.aclose()


llm_service = LLMService()
