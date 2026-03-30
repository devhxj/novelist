"""
工作流模块 - LangGraph工作流实现
"""
from app.workflows.langgraph_workflow import workflow, LANGGRAPH_AVAILABLE, ChapterWorkflow
from app.workflows.router import router

__all__ = ["workflow", "LANGGRAPH_AVAILABLE", "ChapterWorkflow", "router"]
