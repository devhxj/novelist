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
from typing import Any
from collections.abc import AsyncGenerator
from dataclasses import dataclass
from datetime import datetime, timezone
from collections import defaultdict

from core.exceptions import SystemError
from chat.session_manager import session_manager
from sessions.session_storage import session_storage

logger = logging.getLogger(__name__)


class PromptCacheMonitor:
    """Prompt Cache命中率监控器"""
    
    def __init__(self):
        self._stats: dict[str, dict[str, Any]] = defaultdict(lambda: {
            "total_calls": 0,
            "cache_hit_calls": 0,
            "total_input_tokens": 0,
            "cache_hit_tokens": 0,
            "prefix_hashes": set(),
            "last_seen": None
        })
    
    def compute_prefix_hash(self, messages: list[dict], tools: list | None = None) -> str:
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
        usage: dict[str, Any] | None = None
    ):
        """记录一次API调用及其缓存情况"""
        stats = self._stats[model]
        stats["total_calls"] += 1
        stats["prefix_hashes"].add(prefix_hash)
        stats["last_seen"] = datetime.now(timezone.utc).isoformat()
        
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
    
    def get_stats(self, model: str) -> dict[str, Any]:
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
    
    def get_summary(self) -> dict[str, Any]:
        """获取所有模型的汇总统计"""
        summary = {}
        for model in self._stats.keys():
            summary[model] = self.get_stats(model)
        return summary


cache_monitor = PromptCacheMonitor()


class LLMServiceError(SystemError):
    def __init__(
        self,
        message: str,
        *,
        status_code: int = 502,
        provider: str | None = None,
        retryable: bool = False,
        details: dict[str, Any] | None = None
    ):
        super().__init__(message, code="LLM_UPSTREAM_ERROR", status_code=status_code)
        self.provider = provider
        self.retryable = retryable
        self.details = details or {}


def _provider_name(model: str) -> str:
    if model.startswith("glm"):
        return "GLM"
    return "DeepSeek"


def _extract_error_message(payload: dict[str, Any]) -> str | None:
    error = payload.get("error")
    if isinstance(error, dict):
        return error.get("message") or error.get("code")
    if isinstance(error, str):
        return error
    return payload.get("message")


_REASONING_MODELS = {
    "deepseek-v4-pro", "deepseek-v4-flash",
    "o1", "o1-mini", "o1-preview", "o3", "o3-mini",
}


def _apply_reasoning_params(
    payload: dict[str, Any],
    model: str,
    *,
    thinking_enabled: bool | None = None,
    reasoning_effort: str | None = None
) -> None:
    """应用推理/思考模式参数。

    DeepSeek V4 要求同时发送 thinking 和 reasoning_effort。
    thinking 模式下不支持 temperature/top_p/presence_penalty/frequency_penalty。
    """
    model_lower = model.lower()
    is_reasoning = any(r in model_lower for r in _REASONING_MODELS)

    if not is_reasoning and thinking_enabled is None and reasoning_effort is None:
        return

    normalized_effort = None
    if reasoning_effort in {"high", "max"}:
        normalized_effort = reasoning_effort
    elif reasoning_effort in {"low", "medium"}:
        normalized_effort = "high"
    elif reasoning_effort == "xhigh":
        normalized_effort = "max"

    thinking_type = None
    if "deepseek" in model_lower:
        if thinking_enabled is None:
            thinking_type = "enabled"
        else:
            thinking_type = "enabled" if thinking_enabled else "disabled"

    if thinking_type is not None:
        payload["thinking"] = {"type": thinking_type}
        payload["reasoning_effort"] = normalized_effort or "high"
        payload.pop("temperature", None)
        payload.pop("top_p", None)
        payload.pop("presence_penalty", None)
        payload.pop("frequency_penalty", None)
    elif normalized_effort:
        payload["reasoning_effort"] = normalized_effort


@dataclass
class LLMConfig:
    api_key: str = os.getenv("DEEPSEEK_API_KEY", "")
    api_base: str = os.getenv("DEEPSEEK_API_BASE", "https://api.deepseek.com")
    default_model: str = os.getenv("DEEPSEEK_MODEL", "deepseek-v4-flash")
    max_tokens: int = int(os.getenv("DEEPSEEK_MAX_TOKENS", "100000"))
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

    def get_available_models(self) -> list[dict[str, Any]]:
        models: list[dict[str, Any]] = []
        if self.config.api_key:
            models.append({"id": "deepseek-v4-flash", "name": "DeepSeek V4 Flash", "provider": "DeepSeek", "supports_thinking": True})
            models.append({"id": "deepseek-v4-pro", "name": "DeepSeek V4 Pro", "provider": "DeepSeek", "supports_thinking": True})
        if self.config.glm_api_key:
            label = self.config.glm_model.split("-")[1].upper() if "-" in self.config.glm_model else self.config.glm_model
            models.append({"id": self.config.glm_model, "name": f"GLM {label}", "provider": "GLM", "supports_thinking": False})
        return models

    def _get_model_config(self, model: str | None = None) -> tuple[str, str, str]:
        if not model:
            model = self.config.default_model

        if model.startswith("glm") or model == self.config.glm_model:
            return self.config.glm_api_base, self.config.glm_api_key, self.config.glm_model

        return self.config.api_base, self.config.api_key, model

    def _build_chat_url(self, api_base: str, model: str) -> str:
        if model.startswith("glm"):
            return f"{api_base}/chat/completions"
        return f"{api_base}/v1/chat/completions"

    def _build_llm_error(
        self,
        *,
        selected_model: str,
        status_code: int | None = None,
        response_text: str | None = None,
        response_json: dict[str, Any] | None = None,
        request_error: Exception | None = None
    ) -> LLMServiceError:
        provider = _provider_name(selected_model)
        upstream_message = _extract_error_message(response_json or {})

        if status_code is not None:
            logger.error(
                f"LLM API error: model={selected_model}, status={status_code}, "
                f"upstream_message={upstream_message}, response_text={response_text}"
            )

        if request_error is not None:
            logger.error(f"LLM request error: {request_error}")
            return LLMServiceError(
                "服务异常，请稍后重试。",
                status_code=503,
                provider=provider,
                retryable=True,
                details={}
            )

        if status_code == 429:
            return LLMServiceError(
                "服务繁忙，请稍后重试。",
                status_code=503,
                provider=provider,
                retryable=True,
                details={}
            )

        if status_code == 408:
            return LLMServiceError(
                "服务异常，请稍后重试。",
                status_code=504,
                provider=provider,
                retryable=True,
                details={}
            )

        return LLMServiceError(
            "服务异常，请稍后重试。",
            status_code=502,
            provider=provider,
            retryable=status_code is None or status_code >= 500,
            details={}
        )
    
    async def chat_completion(
        self,
        messages: list[dict[str, str]],
        model: str | None = None,
        temperature: float | None = None,
        max_tokens: int | None = None,
        stream: bool = False,
        tools: list[dict[str, Any]] | None = None,
        response_format: dict[str, Any] | None = None,
        reasoning_effort: str | None = None,
        thinking_enabled: bool | None = None,
    ) -> dict[str, Any]:
        api_base, api_key, selected_model = self._get_model_config(model)
        
        url = self._build_chat_url(api_base, selected_model)
        
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
        
        if response_format:
            payload["response_format"] = response_format
        
        _apply_reasoning_params(
            payload,
            selected_model,
            thinking_enabled=thinking_enabled,
            reasoning_effort=reasoning_effort,
        )
        
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
                    "error": "服务器异常，请稍后重试。"
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
        except httpx.HTTPStatusError as e:
            response_text = ""
            try:
                response_text = e.response.text
            except Exception:
                pass
            response_json = None
            try:
                response_json = e.response.json()
            except Exception:
                pass
            llm_error = self._build_llm_error(
                selected_model=selected_model,
                status_code=e.response.status_code,
                response_text=response_text,
                response_json=response_json
            )
            logger.error(f"HTTP error calling LLM API: status={e.response.status_code}, response={response_text}")
            return {
                "success": False,
                "error": llm_error.message,
                "status_code": llm_error.status_code,
                "provider": llm_error.provider,
                "retryable": llm_error.retryable
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
            logger.error(f"Unexpected error calling LLM API: {e}", exc_info=True)
            return {
                "success": False,
                "error": "服务器异常，请稍后重试。"
            }
    
    async def generate_text(
        self,
        prompt: str,
        system_prompt: str | None = None,
        model: str | None = None,
        temperature: float | None = None,
        max_tokens: int | None = None,
        response_format: dict[str, Any] | None = None,
        reasoning_effort: str | None = None,
        thinking_enabled: bool | None = None,
    ) -> str:
        messages = []
        
        if system_prompt:
            messages.append({"role": "system", "content": system_prompt})
        
        messages.append({"role": "user", "content": prompt})
        
        result = await self.chat_completion(
            messages=messages,
            model=model,
            temperature=temperature,
            max_tokens=max_tokens,
            response_format=response_format,
            reasoning_effort=reasoning_effort,
            thinking_enabled=thinking_enabled,
        )
        
        if result["success"]:
            return result["content"]
        logger.error(f"generate_text failed: {result.get('error')}, provider={result.get('provider')}")
        raise LLMServiceError(
            "服务器异常，请稍后重试。",
            status_code=result.get("status_code", 502),
            provider=result.get("provider"),
            retryable=result.get("retryable", False)
        )

    async def generate_stream(
        self,
        messages: list[dict[str, str]],
        model: str | None = None,
        temperature: float | None = None,
        max_tokens: int | None = None,
        reasoning_effort: str | None = None,
        thinking_enabled: bool | None = None,
    ) -> AsyncGenerator[str, None]:
        async for event in self.chat_stream_with_tools(
            messages=messages,
            model=model,
            temperature=temperature,
            max_tokens=max_tokens,
            reasoning_effort=reasoning_effort,
            thinking_enabled=thinking_enabled,
        ):
            if event["type"] == "content":
                yield event["content"]

    async def chat_stream_with_tools(
        self,
        messages: list[dict[str, str]],
        model: str | None = None,
        tools: list[dict[str, Any]] | None = None,
        max_tokens: int | None = None,
        temperature: float | None = None,
        reasoning_effort: str | None = None,
        thinking_enabled: bool | None = None,
    ) -> AsyncGenerator[dict[str, Any], None]:
        """
        支持工具调用的流式对话
        
        Yields:
            {"type": "content", "content": "..."} - 文本片段
            {"type": "thinking", "content": "..."} - 思考/推理内容片段（DeepSeek Reasoner等模型）
            {"type": "tool_call_start", "tool_name": "..."} - 工具调用开始
            {"type": "tool_call_arguments", "arguments": {...}} - 工具参数
            {"type": "tool_call_end"} - 工具调用结束
            {"type": "usage", "usage": {...}} - 用量信息（流结束后，含 prompt_tokens/completion_tokens/total_tokens）
        """
        api_base, api_key, selected_model = self._get_model_config(model)
        
        url = self._build_chat_url(api_base, selected_model)
        
        headers = {
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json"
        }
        
        prefix_hash = cache_monitor.compute_prefix_hash(messages, tools)

        logger.info(f"Sending to API: model={selected_model}, messages={len(messages)}, tools={len(tools) if tools else 0}, prefix={prefix_hash}")
        for i, m in enumerate(messages):
            if m.get("role") == "assistant" and m.get("tool_calls"):
                has_rc = "reasoning_content" in m
                rc_len = len(m.get("reasoning_content", "")) if has_rc else -1
                logger.info(
                    f"  msg[{i}]: assistant+tool_calls, reasoning_content="
                    f"{'present(' + str(rc_len) + ' chars)' if has_rc else 'MISSING'}"
                )
        logger.debug(f"Messages: {messages[:3]}")  # 只记录前 3 条
        
        payload = {
            "model": selected_model,
            "messages": messages,
            "temperature": temperature or self.config.temperature,
            "max_tokens": max_tokens or self.config.max_tokens,
            "stream": True,
            "stream_options": {"include_usage": True}
        }

        if tools:
            payload["tools"] = tools

        _apply_reasoning_params(
            payload,
            selected_model,
            thinking_enabled=thinking_enabled,
            reasoning_effort=reasoning_effort,
        )
        
        full_content = ""
        current_tool_calls = []
        usage_data: dict[str, Any] | None = None

        try:
            async with self.client.stream("POST", url, json=payload, headers=headers) as response:
                if response.is_error:
                    response_text = await response.aread()
                    response_json = None
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
                        except json.JSONDecodeError:
                            continue

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

                        if "usage" in chunk and chunk["usage"] is not None:
                            usage_data = chunk["usage"]

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

            if usage_data:
                cache_monitor.record_call(selected_model, prefix_hash, usage_data)
                yield {"type": "usage", "usage": usage_data}

        except LLMServiceError:
            raise
        except httpx.HTTPStatusError as e:
            response_text = ""
            try:
                response_text = e.response.text
            except Exception:
                pass
            response_json = None
            try:
                response_json = e.response.json()
            except Exception:
                pass
            logger.error(
                f"Chat stream with tools HTTP error: status={e.response.status_code}, "
                f"url={e.request.url}, response={response_text}"
            )
            raise self._build_llm_error(
                selected_model=selected_model,
                status_code=e.response.status_code,
                response_text=response_text,
                response_json=response_json
            ) from e
        except httpx.RequestError as e:
            logger.error(f"Chat stream with tools request error: {e}")
            raise self._build_llm_error(selected_model=selected_model, request_error=e) from e
        except Exception as e:
            logger.error(f"Chat stream with tools error: {e}", exc_info=True)
            raise LLMServiceError("对话服务暂时不可用，请稍后再试。", status_code=502, provider=_provider_name(selected_model)) from e
    


llm_service = LLMService()
