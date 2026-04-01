"""
LLM 服务 - 支持 DeepSeek 和 GLM 多模型
支持多模型选择、流式输出、多轮对话、会话管理、工具调用
"""
import os
import logging
import httpx
import json
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
    api_key: str = os.getenv("DEEPSEEK_API_KEY", "")
    api_base: str = os.getenv("DEEPSEEK_API_BASE", "https://api.deepseek.com")
    default_model: str = os.getenv("DEEPSEEK_MODEL", "deepseek-chat")
    max_tokens: int = int(os.getenv("DEEPSEEK_MAX_TOKENS", "4096"))
    temperature: float = float(os.getenv("DEEPSEEK_TEMPERATURE", "0.7"))
    timeout: int = int(os.getenv("DEEPSEEK_TIMEOUT", "120"))
    
    # GLM 配置
    glm_api_key: str = os.getenv("GLM_API_KEY", "")
    glm_api_base: str = os.getenv("GLM_API_BASE", "https://open.bigmodel.cn/api/paas/v4")
    glm_model: str = os.getenv("GLM_MODEL", "glm-4-flash")
    
    @classmethod
    def validate(cls):
        if not cls.api_key and not cls.glm_api_key:
            raise ValueError("DEEPSEEK_API_KEY or GLM_API_KEY is required")


class LLMService:
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
        
        logger.info(f"LLM Service initialized, default model: {self.config.default_model}, GLM model: {self.config.glm_model}")
    
    def _get_model_config(self, model: Optional[str] = None) -> tuple[str, str, str]:
        """获取模型配置 (api_base, api_key, model)"""
        if not model:
            model = self.config.default_model
        
        # GLM 模型 - GLM API 端点已经是 /api/paas/v4，不需要再加 /v1
        if model.startswith("glm") or model == self.config.glm_model:
            return self.config.glm_api_base, self.config.glm_api_key, self.config.glm_model
        
        # DeepSeek 模型
        return self.config.api_base, self.config.api_key, model
    
    async def chat_completion(
        self,
        messages: List[Dict[str, str]],
        model: Optional[str] = None,
        temperature: Optional[float] = None,
        max_tokens: Optional[int] = None,
        stream: bool = False,
        tools: Optional[List[Dict[str, Any]]] = None
    ) -> Dict[str, Any]:
        api_base, api_key, selected_model = self._get_model_config(model)
        
        # GLM API 路径是 /api/paas/v4/chat/completions，DeepSeek 是 /v1/chat/completions
        if selected_model.startswith("glm"):
            url = f"{api_base}/chat/completions"
        else:
            url = f"{api_base}/v1/chat/completions"
        
        headers = {
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json"
        }
        
        payload = {
            "model": selected_model,
            "messages": messages,
            "temperature": temperature or self.config.temperature,
            "max_tokens": max_tokens or self.config.max_tokens,
            "stream": stream
        }
        
        if tools:
            payload["tools"] = tools
        
        try:
            logger.debug(f"Calling LLM API: model={selected_model}, messages={len(messages)}")
            
            response = await self.client.post(url, json=payload, headers=headers)
            response.raise_for_status()
            
            result = response.json()
            
            if "choices" in result and len(result["choices"]) > 0:
                choice = result["choices"][0]
                message = choice.get("message", {})
                content = message.get("content", "")
                tool_calls = message.get("tool_calls", [])
                
                logger.info(f"LLM response: model={selected_model}, {len(content)} chars, {len(tool_calls)} tool calls")
                return {
                    "success": True,
                    "content": content,
                    "tool_calls": tool_calls,
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
        selected_model = model or self.config.default_model
        
        # GLM API 路径不同
        if selected_model.startswith("glm"):
            api_base = self.config.glm_api_base
            api_key = self.config.glm_api_key
            url = f"{api_base}/chat/completions"
        else:
            api_base = self.config.api_base
            api_key = self.config.api_key
            url = f"{api_base}/v1/chat/completions"
        
        headers = {
            "Authorization": f"Bearer {api_key}",
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
        selected_model = model or self.config.default_model
        
        # GLM API 路径不同
        if selected_model.startswith("glm"):
            api_base = self.config.glm_api_base
            api_key = self.config.glm_api_key
            url = f"{api_base}/chat/completions"
        else:
            api_base = self.config.api_base
            api_key = self.config.api_key
            url = f"{api_base}/v1/chat/completions"
        
        headers = {
            "Authorization": f"Bearer {api_key}",
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
    
    async def chat_stream_with_tools(
        self,
        messages: List[Dict[str, str]],
        model: Optional[str] = None,
        tools: Optional[List[Dict[str, Any]]] = None,
        temperature: Optional[float] = None,
        max_tool_iterations: int = 5,
        system_prompt: Optional[str] = None
    ) -> AsyncGenerator[Dict[str, Any], None]:
        """
        支持工具调用的流式对话
        
        Yields:
            {"type": "content", "content": "..."} - 文本片段
            {"type": "tool_call_start", "tool_name": "..."} - 工具调用开始
            {"type": "tool_call_arguments", "arguments": {...}} - 工具参数
            {"type": "tool_call_end"} - 工具调用结束
        """
        api_base, api_key, selected_model = self._get_model_config(model)
        
        # GLM API 路径不同
        if selected_model.startswith("glm"):
            url = f"{api_base}/chat/completions"
        else:
            url = f"{api_base}/v1/chat/completions"
        
        headers = {
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json"
        }
        
        api_messages = messages
        if system_prompt:
            api_messages = [{"role": "system", "content": system_prompt}] + messages
        
        logger.info(f"Sending to API: model={selected_model}, messages={len(api_messages)}, tools={len(tools) if tools else 0}")
        logger.debug(f"Messages: {api_messages[:3]}")  # 只记录前 3 条
        
        payload = {
            "model": selected_model,
            "messages": api_messages,
            "temperature": temperature or self.config.temperature,
            "max_tokens": self.config.max_tokens,
            "stream": True
        }
        
        if tools:
            payload["tools"] = tools
        
        full_content = ""
        current_tool_calls = []
        
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
                                    yield {"type": "content", "content": content}
                                
                                tool_calls_delta = delta.get("tool_calls", [])
                                for tc in tool_calls_delta:
                                    idx = tc.get("index", 0)
                                    
                                    while len(current_tool_calls) <= idx:
                                        current_tool_calls.append({
                                            "id": "",
                                            "name": "",
                                            "arguments": ""
                                        })
                                    
                                    if tc.get("id"):
                                        current_tool_calls[idx]["id"] = tc["id"]
                                        yield {
                                            "type": "tool_call_start",
                                            "tool_name": "",
                                            "tool_id": tc["id"]
                                        }
                                    
                                    if tc.get("function", {}).get("name"):
                                        current_tool_calls[idx]["name"] = tc["function"]["name"]
                                        yield {
                                            "type": "tool_call_start",
                                            "tool_name": tc["function"]["name"],
                                            "tool_id": current_tool_calls[idx]["id"]
                                        }
                                    
                                    if tc.get("function", {}).get("arguments"):
                                        current_tool_calls[idx]["arguments"] += tc["function"]["arguments"]
                                        
                        except json.JSONDecodeError:
                            continue
            
            for tc in current_tool_calls:
                if tc["name"] and tc["arguments"]:
                    try:
                        args = json.loads(tc["arguments"])
                        yield {
                            "type": "tool_call_end",
                            "tool_name": tc["name"],
                            "tool_id": tc["id"],
                            "arguments": args
                        }
                        logger.info(f"Tool call parsed: {tc['name']}, args: {args}")
                    except json.JSONDecodeError:
                        logger.warning(f"Failed to parse tool arguments: {tc['arguments']}")
            
            if not current_tool_calls and full_content:
                pass
            
        except Exception as e:
            logger.error(f"Chat stream with tools error: {e}")
            raise
    
    async def _generate_summary(self, session: Session) -> str:
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
        await self.client.aclose()


llm_service = LLMService()
