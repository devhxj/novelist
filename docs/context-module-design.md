# Context 模块设计

## 背景

Python `context_builder.py` 1109 行，大量内容已过时或被 MCP 工具取代。Go 版砍掉 4 层缓存体系（STATIC/STABLE/SLIDING/DYNAMIC）、关键词意图检测、RAG 注入用户消息等，只保留核心职责。

## 核心职责

输入：novelID + Agent 类型
输出：初始消息序列（System1 + System2 + 可选 Layer3）

```
[system: System1, system: System2, system: Layer3(可选), ...history, user: 最新消息]
```

OpenAI 协议允许开头并列多条 system 消息，DeepSeek 兼容。

## 三个 Agent

| Agent | System1 | 工具白名单 |
|-------|---------|-----------|
| 主 Agent | 创作助手角色指令 | 全部工具（除子 agent 专用） |
| Review Agent | 审稿人角色指令 | 只读工具 + update 工具 |
| Memory Agent | 记忆管理员角色指令 | RAG 搜索 + timeline/storyarc 维护 |

三个 Agent 共享 System2（小说快照）和 Layer3（大纲注入）。

## 文件结构

```
internal/agentcfg/
    system1.go   // ✅ 已实现：三个 Agent 的 System1 模板 + 各自工具白名单
    system2.go   // ✅ 已实现：小说快照（novel info + 故事状态），不含角色/时间线等 MCP 可查数据
    layer3.go    // 暂未实现：大纲注入，等大纲系统完成后做
```

## 与 Python 的差异

| | Python | Go |
|---|---|---|
| 缓存体系 | 4 层 TTL 缓存 | 无缓存，实时查询 |
| RAG 注入 | 系统自动注入用户消息 | LLM 通过 MCP 工具自行检索 |
| 角色目录 | STABLE 层全量注入 | 不在 System2 中注入（LLM 通过 MCP get_characters 按需获取） |
| 时间线注入 | Layer 4 整层都是 timeline | 不在 System2 中注入（LLM 通过 MCP get_timeline 按需获取） |
| 关键词意图检测 | 有 | 砍掉，LLM 自己判断 |

## 当前状态

System1（三个 Agent 提示词）和 System2（小说基础信息 + Goink 故事状态）已实现。layer3 等大纲系统实现后再做。
