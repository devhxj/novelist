"""
LLM 服务 - 支持 DeepSeek 和 GLM 多模型
支持多模型选择、流式输出、多轮对话、会话管理、工具调用
内置Prompt Cache监控
"""
import os
import logging
import hashlib
import httpx
import json
from typing import Dict, Any, List, Optional, AsyncGenerator
from dataclasses import dataclass, field
from datetime import datetime
from collections import defaultdict

from app.core.prompt_templates import LLMModel
from app.core.session_manager import (
    Session, SessionManager, SessionConfig, MessageRole,
    session_manager
)
from app.core.session_storage import session_storage

logger = logging.getLogger(__name__)


class PromptCacheMonitor:
    """Prompt Cache命中率监控器"""
    
    def __init__(self):
        self._stats: Dict[str, Dict[str, Any]] = defaultdict(lambda: {
            "total_calls": 0,
            "cache_hit_calls": 0,
            "total_input_tokens": 0,
            "cache_hit_tokens": 0,
            "prefix_hashes": set(),
            "last_seen": None
        })
    
    def compute_prefix_hash(self, messages: List[Dict], tools: Optional[List] = None) -> str:
        """计算消息前缀的hash（用于识别是否命中缓存）"""
        tool_names = []
        if tools:
            for tool in tools:
                if isinstance(tool, dict):
                    name = (((tool.get("function") or {}).get("name")) or "")
                    if name:
                        tool_names.append(name)
        prefix_data = {
            "messages": json.dumps(messages[:4], ensure_ascii=False, sort_keys=True) if messages else "",
            "tools": tool_names,
            "model": ""
        }
        return hashlib.md5(json.dumps(prefix_data, sort_keys=True).encode()).hexdigest()[:12]
    
    def record_call(
        self,
        model: str,
        prefix_hash: str,
        usage: Optional[Dict[str, Any]] = None
    ):
        """记录一次API调用及其缓存情况"""
        stats = self._stats[model]
        stats["total_calls"] += 1
        stats["prefix_hashes"].add(prefix_hash)
        stats["last_seen"] = datetime.now().isoformat()
        
        if usage:
            prompt_tokens = usage.get("prompt_tokens", 0)
            cache_tokens = usage.get("prompt_cache_hit_tokens", 0) or usage.get("cached_tokens", 0)
            
            stats["total_input_tokens"] += prompt_tokens
            
            if cache_tokens > 0:
                stats["cache_hit_calls"] += 1
                stats["cache_hit_tokens"] += cache_tokens
                
                hit_rate = (cache_tokens / prompt_tokens * 100) if prompt_tokens > 0 else 0
                logger.info(
                    f"🎯 CACHE HIT | model={model} | "
                    f"prefix={prefix_hash} | "
                    f"hit_tokens={cache_tokens}/{prompt_tokens} ({hit_rate:.1f}%)"
                )
            else:
                logger.debug(
                    f"❌ CACHE MISS | model={model} | "
                    f"prefix={prefix_hash} | "
                    f"tokens={prompt_tokens}"
                )
    
    def get_stats(self, model: str) -> Dict[str, Any]:
        """获取指定模型的缓存统计"""
        stats = self._stats.get(model, {})
        total_calls = stats.get("total_calls", 0)
        hit_calls = stats.get("cache_hit_calls", 0)
        total_tokens = stats.get("total_input_tokens", 0)
        hit_tokens = stats.get("cache_hit_tokens", 0)
        
        return {
            "model": model,
            "total_calls": total_calls,
            "cache_hit_calls": hit_calls,
            "cache_hit_rate": (hit_calls / total_calls * 100) if total_calls > 0 else 0,
            "total_input_tokens": total_tokens,
            "cache_hit_tokens": hit_tokens,
            "token_cache_hit_rate": (hit_tokens / total_tokens * 100) if total_tokens > 0 else 0,
            "unique_prefixes": len(stats.get("prefix_hashes", set())),
            "last_call": stats.get("last_seen")
        }
    
    def get_summary(self) -> Dict[str, Any]:
        """获取所有模型的汇总统计"""
        summary = {}
        for model in self._stats.keys():
            summary[model] = self.get_stats(model)
        return summary


cache_monitor = PromptCacheMonitor()


class LLMServiceError(Exception):
    def __init__(
        self,
        message: str,
        *,
        status_code: int = 502,
        provider: Optional[str] = None,
        retryable: bool = False,
        details: Optional[Dict[str, Any]] = None
    ):
        super().__init__(message)
        self.message = message
        self.status_code = status_code
        self.provider = provider
        self.retryable = retryable
        self.details = details or {}


def _provider_name(model: str) -> str:
    model_lower = model.lower()
    if any(q in model_lower for q in _QWEN_MODELS):
        return "Qwen"
    if model.startswith("glm"):
        return "GLM"
    return "DeepSeek"


def _extract_error_message(payload: Dict[str, Any]) -> Optional[str]:
    error = payload.get("error")
    if isinstance(error, dict):
        return error.get("message") or error.get("code")
    if isinstance(error, str):
        return error
    return payload.get("message")


_REASONING_MODELS = {
    "deepseek-reasoner", "deepseek-r1",
    "o1", "o1-mini", "o1-preview", "o3", "o3-mini",
    "qwq", "qwen-qwq", "qwen3-235b-a22b",
}

_QWEN_MODELS = {"qwq", "qwen-qwq", "qwen3", "qwen3-235b-a22b", "qwen-plus", "qwen-turbo", "qwen-max", "qwen-long"}


def _apply_reasoning_params(payload: Dict[str, Any], model: str) -> None:
    model_lower = model.lower()
    is_reasoning = any(r in model_lower for r in _REASONING_MODELS)
    if not is_reasoning and "reasoner" not in model_lower:
        return
    if any(q in model_lower for q in _QWEN_MODELS):
        payload["enable_thinking"] = True
        payload["extra_body"] = {"enable_thinking": True}
    elif "deepseek" in model_lower or "reasoner" in model_lower:
        payload["reasoning_effort"] = "medium"
    else:
        payload["reasoning_effort"] = "medium"


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
        if not cls.api_key and not cls.glm_api_key and not os.getenv("QWEN_API_KEY"):
            raise ValueError("DEEPSEEK_API_KEY, GLM_API_KEY or QWEN_API_KEY is required")


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

        model_lower = model.lower()

        if any(q in model_lower for q in _QWEN_MODELS):
            return (
                os.getenv("QWEN_API_BASE", "https://dashscope.aliyuncs.com/compatible-mode/v1"),
                os.getenv("QWEN_API_KEY", ""),
                model
            )

        if model.startswith("glm") or model == self.config.glm_model:
            return self.config.glm_api_base, self.config.glm_api_key, self.config.glm_model

        return self.config.api_base, self.config.api_key, model

    def _build_llm_error(
        self,
        *,
        selected_model: str,
        status_code: Optional[int] = None,
        response_text: Optional[str] = None,
        response_json: Optional[Dict[str, Any]] = None,
        request_error: Optional[Exception] = None
    ) -> LLMServiceError:
        provider = _provider_name(selected_model)
        upstream_message = _extract_error_message(response_json or {})

        if request_error is not None:
            return LLMServiceError(
                "AI 服务暂时连接不上，请稍后再试。",
                status_code=503,
                provider=provider,
                retryable=True,
                details={"reason": str(request_error)}
            )

        if status_code == 400:
            return LLMServiceError(
                "这次请求暂时没法处理，请缩短内容或分步操作后再试。",
                status_code=400,
                provider=provider,
                retryable=False,
                details={"upstream_status": status_code, "upstream_message": upstream_message, "response_text": response_text}
            )
        if status_code == 401:
            return LLMServiceError(
                "AI 服务当前不可用，请稍后再试。",
                status_code=502,
                provider=provider,
                retryable=False,
                details={"upstream_status": status_code, "upstream_message": upstream_message}
            )
        if status_code == 403:
            return LLMServiceError(
                "AI 服务当前不可用，请稍后再试。",
                status_code=502,
                provider=provider,
                retryable=False,
                details={"upstream_status": status_code, "upstream_message": upstream_message}
            )
        if status_code == 404:
            return LLMServiceError(
                "AI 服务当前不可用，请稍后再试。",
                status_code=502,
                provider=provider,
                retryable=False,
                details={"upstream_status": status_code, "upstream_message": upstream_message}
            )
        if status_code == 408:
            return LLMServiceError(
                "AI 响应超时了，请稍后再试。",
                status_code=504,
                provider=provider,
                retryable=True,
                details={"upstream_status": status_code, "upstream_message": upstream_message}
            )
        if status_code == 429:
            return LLMServiceError(
                "当前服务繁忙，请稍后再试。",
                status_code=429,
                provider=provider,
                retryable=True,
                details={"upstream_status": status_code, "upstream_message": upstream_message}
            )
        if status_code is not None and status_code >= 500:
            return LLMServiceError(
                "AI 服务暂时开小差了，请稍后再试。",
                status_code=502,
                provider=provider,
                retryable=True,
                details={"upstream_status": status_code, "upstream_message": upstream_message}
            )

        return LLMServiceError(
            "AI 服务暂时不可用，请稍后再试。",
            status_code=502,
            provider=provider,
            retryable=False,
            details={"upstream_status": status_code, "upstream_message": upstream_message, "response_text": response_text}
        )
    
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
        
        _apply_reasoning_params(payload, selected_model)
        
        prefix_hash = cache_monitor.compute_prefix_hash(messages, tools)
        
        try:
            logger.debug(f"Calling LLM API: model={selected_model}, messages={len(messages)}, prefix={prefix_hash}")
            
            response = await self.client.post(url, json=payload, headers=headers)
            if response.is_error:
                response_json = None
                try:
                    response_json = response.json()
                except Exception:
                    response_json = None
                raise self._build_llm_error(
                    selected_model=selected_model,
                    status_code=response.status_code,
                    response_text=response.text,
                    response_json=response_json
                )
            
            result = response.json()
            
            if "choices" in result and len(result["choices"]) > 0:
                choice = result["choices"][0]
                message = choice.get("message", {})
                content = message.get("content", "")
                tool_calls = message.get("tool_calls", [])
                
                usage = result.get("usage", {})
                cache_monitor.record_call(selected_model, prefix_hash, usage)
                
                logger.info(f"LLM response: model={selected_model}, {len(content)} chars, {len(tool_calls)} tool calls")
                return {
                    "success": True,
                    "content": content,
                    "tool_calls": tool_calls,
                    "usage": usage,
                    "model": result.get("model", selected_model)
                }
            else:
                logger.error(f"Unexpected API response: {result}")
                return {
                    "success": False,
                    "error": "Unexpected API response format"
                }
                
        except LLMServiceError as e:
            logger.error(f"LLM API error calling {selected_model}: {e.message}, details={e.details}")
            return {
                "success": False,
                "error": e.message,
                "status_code": e.status_code,
                "provider": e.provider,
                "retryable": e.retryable
            }
        except httpx.RequestError as e:
            llm_error = self._build_llm_error(selected_model=selected_model, request_error=e)
            logger.error(f"Request error calling LLM API: {e}")
            return {
                "success": False,
                "error": llm_error.message,
                "status_code": llm_error.status_code,
                "provider": llm_error.provider,
                "retryable": llm_error.retryable
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
        raise LLMServiceError(
            result.get("error", "LLM generation failed"),
            status_code=result.get("status_code", 502),
            provider=result.get("provider"),
            retryable=result.get("retryable", False)
        )
    
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
        
        _apply_reasoning_params(payload, selected_model)
        
        logger.debug(f"Starting stream generation: model={selected_model}")
        
        try:
            async with self.client.stream("POST", url, json=payload, headers=headers) as response:
                if response.is_error:
                    response_json = None
                    response_text = await response.aread()
                    try:
                        response_json = json.loads(response_text.decode("utf-8"))
                    except Exception:
                        response_json = None
                    raise self._build_llm_error(
                        selected_model=selected_model,
                        status_code=response.status_code,
                        response_text=response_text.decode("utf-8", errors="ignore"),
                        response_json=response_json
                    )
                
                async for line in response.aiter_lines():
                    if line.startswith("data: "):
                        data = line[6:]
                        if data == "[DONE]":
                            break
                        
                        try:
                            chunk = json.loads(data)
                            if "choices" in chunk and len(chunk["choices"]) > 0:
                                delta = chunk["choices"][0].get("delta", {})
                                
                                reasoning = delta.get("reasoning_content") or delta.get("reasoning", "")
                                if reasoning:
                                    yield {"type": "thinking", "content": reasoning}
                                
                                content = delta.get("content", "")
                                if content:
                                    yield content
                        except json.JSONDecodeError:
                            continue
                            
        except LLMServiceError:
            raise
        except httpx.RequestError as e:
            logger.error(f"Stream generation request error: {e}")
            raise self._build_llm_error(selected_model=selected_model, request_error=e) from e
        except Exception as e:
            logger.error(f"Stream generation error: {e}")
            raise LLMServiceError("生成服务暂时不可用，请稍后再试。", status_code=502, provider=_provider_name(selected_model)) from e
    
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
                if response.is_error:
                    response_json = None
                    response_text = await response.aread()
                    try:
                        response_json = json.loads(response_text.decode("utf-8"))
                    except Exception:
                        response_json = None
                    raise self._build_llm_error(
                        selected_model=selected_model,
                        status_code=response.status_code,
                        response_text=response_text.decode("utf-8", errors="ignore"),
                        response_json=response_json
                    )
                
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
            {"type": "thinking", "content": "..."} - 思考/推理内容片段（DeepSeek Reasoner等模型）
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
        
        prefix_hash = cache_monitor.compute_prefix_hash(api_messages, tools)
        
        logger.info(f"Sending to API: model={selected_model}, messages={len(api_messages)}, tools={len(tools) if tools else 0}, prefix={prefix_hash}")
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
        
        _apply_reasoning_params(payload, selected_model)
        
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

                                reasoning = delta.get("reasoning_content", "")
                                if reasoning:
                                    yield {"type": "thinking", "content": reasoning}

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
                                        yield {
                                            "type": "tool_call_arguments",
                                            "tool_name": current_tool_calls[idx]["name"],
                                            "tool_id": current_tool_calls[idx]["id"],
                                            "arguments_text": current_tool_calls[idx]["arguments"]
                                        }
                                        
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
            
        except LLMServiceError:
            raise
        except httpx.RequestError as e:
            logger.error(f"Chat stream with tools request error: {e}")
            raise self._build_llm_error(selected_model=selected_model, request_error=e) from e
        except Exception as e:
            logger.error(f"Chat stream with tools error: {e}")
            raise LLMServiceError("对话服务暂时不可用，请稍后再试。", status_code=502, provider=_provider_name(selected_model)) from e
    
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
