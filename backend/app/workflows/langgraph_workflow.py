"""
LangGraph工作流 - 章节生成工作流
基于LangGraph实现的多Agent协作工作流
"""
import logging
from typing import TypedDict, Annotated, Literal, Optional, Dict, Any, List
from datetime import datetime

try:
    from langgraph.graph import StateGraph, END
    from langgraph.checkpoint.memory import MemorySaver
    LANGGRAPH_AVAILABLE = True
except ImportError:
    LANGGRAPH_AVAILABLE = False
    StateGraph = None
    END = None
    MemorySaver = None

try:
    from app.core.context_builder import ContextBuilder
    CONTEXT_BUILDER_AVAILABLE = True
except ImportError:
    CONTEXT_BUILDER_AVAILABLE = False
    ContextBuilder = None

try:
    from app.core.consistency_checker import ConsistencyChecker
    CONSISTENCY_CHECKER_AVAILABLE = True
except ImportError:
    CONSISTENCY_CHECKER_AVAILABLE = False
    ConsistencyChecker = None

try:
    from app.core.vector_store import vector_store
    VECTOR_STORE_AVAILABLE = True
except ImportError:
    VECTOR_STORE_AVAILABLE = False
    vector_store = None

from app.agents.base import AgentTask, AgentResult, TaskType
from app.agents.writer import WriterAgent
from app.agents.reviewer import ReviewerAgent
from app.core.llm_service import llm_service

logger = logging.getLogger(__name__)


class WorkflowState(TypedDict):
    """工作流状态"""
    task_id: str
    novel_id: int
    chapter_number: int
    target_length: int
    style: str
    
    context: Dict[str, Any]
    generated_content: Optional[str]
    review_result: Optional[Dict[str, Any]]
    consistency_result: Optional[Dict[str, Any]]
    
    iteration: int
    max_iterations: int
    feedback: Optional[str]
    
    status: str
    error: Optional[str]
    created_at: str
    updated_at: str


def create_initial_state(
    task_id: str,
    novel_id: int,
    chapter_number: int,
    target_length: int = 3000,
    style: str = "narrative",
    context: Dict[str, Any] = None
) -> WorkflowState:
    """创建初始状态"""
    now = datetime.now().isoformat()
    return WorkflowState(
        task_id=task_id,
        novel_id=novel_id,
        chapter_number=chapter_number,
        target_length=target_length,
        style=style,
        context=context or {},
        generated_content=None,
        review_result=None,
        consistency_result=None,
        iteration=0,
        max_iterations=3,
        feedback=None,
        status="initialized",
        error=None,
        created_at=now,
        updated_at=now
    )


class ChapterWorkflow:
    """章节生成工作流"""
    
    def __init__(self):
        if not LANGGRAPH_AVAILABLE:
            raise ImportError("LangGraph is not installed. Please install it with: pip install langgraph")
        
        self.writer_agent = WriterAgent()
        self.reviewer_agent = ReviewerAgent()
        self.memory_saver = MemorySaver()
        self.graph = self._build_graph()
    
    def _build_graph(self):
        """构建工作流图"""
        workflow = StateGraph(WorkflowState)
        
        workflow.add_node("prepare_context", self._prepare_context_node)
        workflow.add_node("generate_content", self._generate_content_node)
        workflow.add_node("review_content", self._review_content_node)
        workflow.add_node("check_consistency", self._check_consistency_node)
        workflow.add_node("save_chapter", self._save_chapter_node)
        workflow.add_node("update_memory", self._update_memory_node)
        workflow.add_node("handle_revision", self._handle_revision_node)
        
        workflow.set_entry_point("prepare_context")
        
        workflow.add_edge("prepare_context", "generate_content")
        workflow.add_edge("generate_content", "review_content")
        
        workflow.add_conditional_edges(
            "review_content",
            self._should_revise_after_review,
            {
                "revise": "handle_revision",
                "check": "check_consistency"
            }
        )
        
        workflow.add_conditional_edges(
            "check_consistency",
            self._should_revise_after_consistency,
            {
                "revise": "handle_revision",
                "save": "save_chapter"
            }
        )
        
        workflow.add_conditional_edges(
            "handle_revision",
            self._should_retry,
            {
                "retry": "generate_content",
                "end": END
            }
        )
        
        workflow.add_edge("save_chapter", "update_memory")
        workflow.add_edge("update_memory", END)
        
        return workflow.compile(checkpointer=self.memory_saver)
    
    async def _prepare_context_node(self, state: WorkflowState) -> Dict[str, Any]:
        """准备上下文节点"""
        logger.info(f"[{state['task_id']}] Preparing context for chapter {state['chapter_number']}")
        
        if not CONTEXT_BUILDER_AVAILABLE:
            logger.warning("ContextBuilder not available, using empty context")
            return {
                "context": {},
                "status": "context_prepared",
                "updated_at": datetime.now().isoformat()
            }
        
        try:
            from app.core.database import SessionLocal
            
            db = SessionLocal()
            try:
                builder = ContextBuilder(db, state["novel_id"])
                context = await builder.build_chapter_context(
                    chapter_number=state["chapter_number"],
                    context_types=["previous_summary", "characters", "plot_hints", "relevant_memory"]
                )
                
                return {
                    "context": context,
                    "status": "context_prepared",
                    "updated_at": datetime.now().isoformat()
                }
            finally:
                db.close()
                
        except Exception as e:
            logger.error(f"Failed to prepare context: {e}")
            return {
                "error": f"上下文准备失败: {str(e)}",
                "status": "error",
                "updated_at": datetime.now().isoformat()
            }
    
    async def _generate_content_node(self, state: WorkflowState) -> Dict[str, Any]:
        """生成内容节点"""
        logger.info(f"[{state['task_id']}] Generating content for chapter {state['chapter_number']}")
        
        try:
            task = AgentTask(
                task_id=f"{state['task_id']}_write",
                task_type=TaskType.GENERATE_CHAPTER,
                novel_id=state["novel_id"],
                parameters={
                    "chapter_number": state["chapter_number"],
                    "target_length": state["target_length"],
                    "style": state["style"]
                },
                context=state["context"]
            )
            
            result = await self.writer_agent.execute(task)
            
            if result.success:
                return {
                    "generated_content": result.result.get("content", ""),
                    "status": "content_generated",
                    "updated_at": datetime.now().isoformat()
                }
            else:
                return {
                    "error": result.error or "内容生成失败",
                    "status": "error",
                    "updated_at": datetime.now().isoformat()
                }
                
        except Exception as e:
            logger.error(f"Failed to generate content: {e}")
            return {
                "error": f"内容生成失败: {str(e)}",
                "status": "error",
                "updated_at": datetime.now().isoformat()
            }
    
    async def _review_content_node(self, state: WorkflowState) -> Dict[str, Any]:
        """审核内容节点"""
        logger.info(f"[{state['task_id']}] Reviewing content")
        
        try:
            task = AgentTask(
                task_id=f"{state['task_id']}_review",
                task_type=TaskType.REVIEW_CHAPTER,
                novel_id=state["novel_id"],
                parameters={
                    "content": state["generated_content"],
                    "chapter_number": state["chapter_number"]
                },
                context=state["context"]
            )
            
            result = await self.reviewer_agent.execute(task)
            
            return {
                "review_result": {
                    "approved": result.success and result.result.get("approved", False),
                    "score": result.result.get("score", 0),
                    "issues": result.result.get("issues", []),
                    "suggestions": result.suggestions
                },
                "status": "content_reviewed",
                "updated_at": datetime.now().isoformat()
            }
            
        except Exception as e:
            logger.error(f"Failed to review content: {e}")
            return {
                "review_result": {
                    "approved": False,
                    "issues": [str(e)]
                },
                "status": "review_failed",
                "updated_at": datetime.now().isoformat()
            }
    
    async def _check_consistency_node(self, state: WorkflowState) -> Dict[str, Any]:
        """一致性检查节点"""
        logger.info(f"[{state['task_id']}] Checking consistency")
        
        if not CONSISTENCY_CHECKER_AVAILABLE:
            logger.warning("ConsistencyChecker not available, skipping consistency check")
            return {
                "consistency_result": {"issues": []},
                "status": "consistency_skipped",
                "updated_at": datetime.now().isoformat()
            }
        
        try:
            from app.core.database import SessionLocal
            
            db = SessionLocal()
            try:
                checker = ConsistencyChecker(db, state["novel_id"])
                result = await checker.check_all(
                    check_types=["character", "plot", "timeline"]
                )
                
                has_errors = any(
                    issue.get("severity") == "error"
                    for issue in result.get("issues", [])
                )
                
                return {
                    "consistency_result": result,
                    "status": "consistency_checked",
                    "updated_at": datetime.now().isoformat()
                }
            finally:
                db.close()
                
        except Exception as e:
            logger.error(f"Failed to check consistency: {e}")
            return {
                "consistency_result": {
                    "issues": [{"severity": "error", "description": str(e)}]
                },
                "status": "consistency_check_failed",
                "updated_at": datetime.now().isoformat()
            }
    
    async def _save_chapter_node(self, state: WorkflowState) -> Dict[str, Any]:
        """保存章节节点"""
        logger.info(f"[{state['task_id']}] Saving chapter")
        
        try:
            from app.core.database import SessionLocal
            from app.chapters.models import Chapter
            
            db = SessionLocal()
            try:
                chapter = db.query(Chapter).filter(
                    Chapter.novel_id == state["novel_id"],
                    Chapter.chapter_number == state["chapter_number"]
                ).first()
                
                if chapter:
                    chapter.content = state["generated_content"]
                    chapter.status = "completed"
                    chapter.word_count = len(state["generated_content"])
                    chapter.updated_at = datetime.now()
                    db.commit()
                else:
                    chapter = Chapter(
                        novel_id=state["novel_id"],
                        chapter_number=state["chapter_number"],
                        title=f"第{state['chapter_number']}章",
                        content=state["generated_content"],
                        status="completed",
                        word_count=len(state["generated_content"])
                    )
                    db.add(chapter)
                    db.commit()
                    db.refresh(chapter)
                
                return {
                    "status": "chapter_saved",
                    "updated_at": datetime.now().isoformat()
                }
            finally:
                db.close()
                
        except Exception as e:
            logger.error(f"Failed to save chapter: {e}")
            return {
                "error": f"章节保存失败: {str(e)}",
                "status": "error",
                "updated_at": datetime.now().isoformat()
            }
    
    async def _update_memory_node(self, state: WorkflowState) -> Dict[str, Any]:
        """更新记忆节点"""
        logger.info(f"[{state['task_id']}] Updating memory")
        
        if not VECTOR_STORE_AVAILABLE:
            logger.warning("VectorStore not available, skipping memory update")
            return {
                "status": "memory_skipped",
                "updated_at": datetime.now().isoformat()
            }
        
        try:
            from app.core.database import SessionLocal
            from app.chapters.models import Chapter
            
            db = SessionLocal()
            try:
                chapter = db.query(Chapter).filter(
                    Chapter.novel_id == state["novel_id"],
                    Chapter.chapter_number == state["chapter_number"]
                ).first()
                
                if chapter and chapter.content:
                    chunks = vector_store.split_text(chapter.content)
                    chunk_data = [
                        {
                            "id": f"{chapter.id}_{i}",
                            "content": chunk,
                            "chapter_id": chapter.id,
                            "chunk_type": "content",
                            "chunk_index": i,
                            "metadata": {
                                "chapter_number": chapter.chapter_number,
                                "chapter_title": chapter.title
                            }
                        }
                        for i, chunk in enumerate(chunks)
                    ]
                    
                    if chunk_data:
                        vector_store.add_chunks(state["novel_id"], chunk_data)
                
                return {
                    "status": "completed",
                    "updated_at": datetime.now().isoformat()
                }
            finally:
                db.close()
                
        except Exception as e:
            logger.error(f"Failed to update memory: {e}")
            return {
                "status": "memory_update_failed",
                "error": f"记忆更新失败: {str(e)}",
                "updated_at": datetime.now().isoformat()
            }
    
    async def _handle_revision_node(self, state: WorkflowState) -> Dict[str, Any]:
        """处理修订节点"""
        logger.info(f"[{state['task_id']}] Handling revision, iteration {state['iteration'] + 1}")
        
        feedback_parts = []
        
        if state["review_result"] and not state["review_result"].get("approved", True):
            issues = state["review_result"].get("issues", [])
            suggestions = state["review_result"].get("suggestions", [])
            feedback_parts.append(f"审核问题: {', '.join(issues)}")
            feedback_parts.append(f"修改建议: {', '.join(suggestions)}")
        
        if state["consistency_result"]:
            consistency_issues = state["consistency_result"].get("issues", [])
            for issue in consistency_issues:
                if issue.get("severity") in ["error", "warning"]:
                    feedback_parts.append(f"一致性问题: {issue.get('description')}")
        
        feedback = "\n".join(feedback_parts) if feedback_parts else "需要改进内容质量"
        
        return {
            "iteration": state["iteration"] + 1,
            "feedback": feedback,
            "status": "revision_needed",
            "updated_at": datetime.now().isoformat()
        }
    
    def _should_revise_after_review(self, state: WorkflowState) -> Literal["revise", "check"]:
        """审核后是否需要修订"""
        if state.get("error"):
            return "revise"
        
        review_result = state.get("review_result", {})
        if not review_result.get("approved", False):
            if state["iteration"] < state["max_iterations"]:
                return "revise"
        
        return "check"
    
    def _should_revise_after_consistency(self, state: WorkflowState) -> Literal["revise", "save"]:
        """一致性检查后是否需要修订"""
        if state.get("error"):
            return "revise"
        
        consistency_result = state.get("consistency_result", {})
        issues = consistency_result.get("issues", [])
        
        has_critical_errors = any(
            issue.get("severity") == "error"
            for issue in issues
        )
        
        if has_critical_errors and state["iteration"] < state["max_iterations"]:
            return "revise"
        
        return "save"
    
    def _should_retry(self, state: WorkflowState) -> Literal["retry", "end"]:
        """是否重试"""
        if state["iteration"] >= state["max_iterations"]:
            return "end"
        return "retry"
    
    async def run(
        self,
        task_id: str,
        novel_id: int,
        chapter_number: int,
        target_length: int = 3000,
        style: str = "narrative",
        context: Dict[str, Any] = None
    ) -> Dict[str, Any]:
        """
        运行工作流
        
        Args:
            task_id: 任务ID
            novel_id: 小说ID
            chapter_number: 章节号
            target_length: 目标字数
            style: 写作风格
            context: 初始上下文
            
        Returns:
            工作流执行结果
        """
        initial_state = create_initial_state(
            task_id=task_id,
            novel_id=novel_id,
            chapter_number=chapter_number,
            target_length=target_length,
            style=style,
            context=context
        )
        
        config = {"configurable": {"thread_id": task_id}}
        
        try:
            final_state = await self.graph.ainvoke(initial_state, config)
            
            return {
                "success": final_state.get("status") == "completed",
                "task_id": task_id,
                "status": final_state.get("status"),
                "generated_content": final_state.get("generated_content"),
                "review_result": final_state.get("review_result"),
                "consistency_result": final_state.get("consistency_result"),
                "iterations": final_state.get("iteration", 0),
                "error": final_state.get("error")
            }
            
        except Exception as e:
            logger.error(f"Workflow execution failed: {e}")
            return {
                "success": False,
                "task_id": task_id,
                "status": "failed",
                "error": str(e)
            }
    
    def get_state(self, task_id: str) -> Optional[Dict[str, Any]]:
        """获取工作流状态"""
        try:
            config = {"configurable": {"thread_id": task_id}}
            state = self.graph.get_state(config)
            return state.values if state else None
        except Exception as e:
            logger.error(f"Failed to get workflow state: {e}")
            return None


workflow = ChapterWorkflow() if LANGGRAPH_AVAILABLE else None
