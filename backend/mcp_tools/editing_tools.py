"""
编辑类MCP工具
"""
from contextvars import ContextVar
from typing import Any
import uuid
from pydantic import BaseModel, Field
from typing import Literal
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select

from .base import BaseMCPTool, MCPToolResult, MCPToolCategory, MCPToolRegistry
from chapters.models import Chapter
from editor.service import get_edit_session_manager
from editor.diff_engine import diff_engine

_subagent_running_var: ContextVar[bool] = ContextVar("_subagent_running_var", default=False)


def _build_agent_task_id(prefix: str = "task") -> str:
    return f"{prefix}_{uuid.uuid4().hex}"


def _normalize_subagent_task_type(task_type: str) -> str:
    type_aliases = {
        "write": "write_chapter",
        "writing": "write_chapter",
        "generate": "write_chapter",
        "generate_chapter": "write_chapter",
        "review_chapter": "review",
        "check_consistency": "review",
        "manage_foreshadowing": "review",
        "review": "review",
    }
    return type_aliases.get(task_type, task_type)


async def _execute_subagent_task(
    *,
    db: AsyncSession,
    user_id: int,
    task_type: str,
    novel_id: int,
    chapter_id: int | None = None,
    instruction: str | None = None,
    parameters: dict[str, Any] | None = None,
    agent_role: str | None = None,
    agent_id: str | None = None,
    model: str | None = None,
) -> MCPToolResult:
    if _subagent_running_var.get():
        return MCPToolResult(
            success=False,
            error="子Agent不允许启动子Agent，避免无限递归",
        )

    normalized_type = _normalize_subagent_task_type(task_type)
    from agents.registry import get_agent_for_task, get_all_specs
    from agents.context_provider import build_subagent_context
    from agents.base import AgentTask, TaskType, SubAgentReport

    registry_entry = get_agent_for_task(normalized_type)
    if not registry_entry:
        available = list(get_all_specs().keys())
        return MCPToolResult(
            success=False,
            error=f"未知的任务类型 '{task_type}'，可用类型: {', '.join(available)}",
        )

    agent_cls, spec = registry_entry
    if spec.requires_chapter_id and not chapter_id:
        return MCPToolResult(
            success=False,
            error=f"任务类型 '{normalized_type}' 需要 chapter_id 参数",
        )

    task_parameters = dict(parameters or {})
    if instruction:
        task_parameters.setdefault("instruction", instruction)
    if model:
        task_parameters["model"] = model
    if agent_role:
        task_parameters["agent_role"] = agent_role
    if agent_id:
        task_parameters["agent_id"] = agent_id

    registry_to_task_type = {
        "write_chapter": TaskType.GENERATE_CHAPTER,
        "review": TaskType.REVIEW_CHAPTER,
    }

    try:
        context = await build_subagent_context(
            db=db,
            novel_id=novel_id,
            spec=spec,
            chapter_id=chapter_id,
            instruction=instruction,
            extra_parameters=task_parameters,
        )

        task = AgentTask(
            task_id=_build_agent_task_id("sub"),
            task_type=registry_to_task_type.get(normalized_type, TaskType.GENERATE_CHAPTER),
            novel_id=novel_id,
            chapter_id=chapter_id,
            parameters=task_parameters,
            context=context,
        )

        agent_factory = agent_cls
        token = _subagent_running_var.set(True)
        try:
            agent = agent_factory()  # type: ignore[call-arg]
            result = await agent.execute(task)
        finally:
            _subagent_running_var.reset(token)

        report = SubAgentReport(
            task_type=normalized_type,
            success=result.success,
            summary=result.result.get("summary", "任务完成") if result.success else f"任务失败: {result.error}",
            key_findings=result.result.get("key_findings", []) if result.success else [],
            suggestions=result.suggestions,
            data=result.result,
            error=result.error,
        )

        report_data = report.to_dict()
        report_data["capability_profile"] = {
            "allowed_tools": spec.allowed_tools,
            "allowed_resources": spec.allowed_resources,
            "allow_subagent_spawn": spec.allow_subagent_spawn,
        }

        return MCPToolResult(
            success=report.success,
            data=report_data,
            error=report.error,
        )
    except Exception as e:
        return MCPToolResult(success=False, error=f"子Agent执行失败: {str(e)}")


class EditChapterArgs(BaseModel):
    chapter_id: int = Field(description="章节ID（必填）")
    change_type: Literal["full_replace", "search_replace", "multi_search_replace",
                          "line_range_replace", "insert", "delete", "undo"] = Field(
        default="full_replace", description="变更类型，默认 full_replace")
    new_content: str | None = Field(default=None, description="新内容（full_replace 为完整替换文本；search_replace 为替换后的内容）")
    search_text: str | None = Field(default=None, description="要搜索的原文片段（search_replace 模式必填）")
    match_mode: Literal["first", "all"] = Field(default="first", description="匹配模式：first=只替换第一处，all=替换所有匹配")
    edits: list[dict[str, str]] | None = Field(default=None, description="多块编辑数组（multi_search_replace 模式必填）")
    start_line: int | None = Field(default=None, description="起始行号（line_range_replace/insert/delete 时必填，1-based）")
    end_line: int | None = Field(default=None, description="结束行号（line_range_replace/delete 时必填，1-based）")
    reason: str | None = Field(default=None, description="修改原因")
    dry_run: bool = Field(default=False, description="设为 true 只预览变更 diff，不实际修改")
    undo: bool = Field(default=False, description="设为 true 撤销最近一次编辑")
    undo_from_snapshot: str | None = Field(default=None, description="要回退到的快照ID")


class EditChapterTool(BaseMCPTool):
    """编辑章节内容 - 统一写入入口"""

    name = "edit_chapter"
    description = (
        "编辑指定章节的内容。"
        "\n必须提供 chapter_id；若不清楚章节ID，先调用 get_chapter_list 获取。"
        "\n【变更类型选择指南】"
        "\n- full_replace：你有完整的修改后全文 → 传 new_content 为完整替换文本"
        "\n- search_replace（推荐）：你知道要改的是哪段原文 → 传 search_text(要找的原文) + new_content(替换内容)，支持跨行匹配"
        "\n- multi_search_replace：一次替换多处 → 传 edits 数组，每个元素含 search_text 和 new_content"
        "\n- line_range_replace：你知道精确的行号范围 → 传 start_line + end_line + new_content"
        "\n- insert：在指定位置插入新内容"
        "\n- delete：删除指定范围的内容"
        "\n设置 dry_run=true 可预览变更而不实际修改。"
        "\n设置 undo=true 可撤销最近一次编辑。"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    args_schema = EditChapterArgs

    async def _execute(
        self,
        args: EditChapterArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        try:
            result = await db.execute(select(Chapter).where(Chapter.id == args.chapter_id))
            chapter = result.scalar_one_or_none()
            if not chapter:
                return MCPToolResult(success=False, error=f"章节不存在: {args.chapter_id}")
            if chapter.novel_id != novel_id:
                return MCPToolResult(success=False, error="无权编辑此章节")

            manager = get_edit_session_manager(db)
            edit_session = await manager.get_edit_session(args.chapter_id)
            reused = edit_session is not None
            if not edit_session:
                session_id = extra.get("session_id", "")
                edit_session = await manager.create_edit_session(args.chapter_id, session_id)

            if args.undo or args.change_type == "undo":
                return await self._handle_undo(db, manager, edit_session, args.dry_run, args.undo_from_snapshot)

            if args.change_type == "multi_search_replace":
                return await self._handle_multi_search_replace(db, manager, edit_session, args.edits, args.match_mode, args.dry_run)

            if args.change_type == "search_replace":
                return await self._handle_search_replace(db, manager, edit_session, args.search_text, args.new_content, args.match_mode, args.dry_run)

            if args.change_type == "line_range_replace":
                if args.start_line is None or args.end_line is None:
                    return MCPToolResult(success=False, error="line_range_replace 必须提供 start_line 和 end_line")

            await manager.apply_change(
                edit_session=edit_session,
                change_type="partial_edit" if args.change_type == "line_range_replace" else args.change_type,
                new_content=args.new_content or "",
                start_line=args.start_line,
                end_line=args.end_line,
                reason=args.reason,
            )

            diff_data = await manager.get_diff(edit_session.edit_session_id)
            data: dict[str, Any] = {
                "edit_session_id": edit_session.edit_session_id,
                "chapter_id": args.chapter_id,
                "change_count": edit_session.change_count,
                "working_content": edit_session.working_content,
                "diff": diff_data.get("diff", {}),
                "reused_existing": reused,
                "message": f"已应用，共 {edit_session.change_count} 处改动。",
            }

            inject: list[dict[str, Any]] | None = None
            if args.change_type == "full_replace" and len(args.new_content or "") > 500:
                inject = [{
                    "role": "user",
                    "content": (
                        "已写入大量内容，请：\n"
                        "1. 调用 run_subagent（task_type=\"review\"）对本章进行审核\n"
                        "2. 全面检查并维护小说状态：\n"
                        "   - 新出现的角色 → 创建角色；角色属性变化 → 更新角色\n"
                        "   - 角色关系变化 → 更新关系\n"
                        "   - 伏笔埋下/推进/回收 → 更新时间线\n"
                        "   - 更新故事状态文档\n"
                        "   - 更新读者认知（已知信息、悬念、误知）\n"
                        "   - 故事弧线推进或新增 → 更新或创建弧线\n"
                        "   - 如有创作偏好变化 → 更新 creative profile\n"
                        "3. 向用户汇报本章成果"
                    ),
                    "workflow_event": "maintenance_reminder",
                }]

            return MCPToolResult(
                success=True,
                data=data,
                inject=inject,
                metadata={
                    "tool": self.name,
                    "change_count": edit_session.change_count,
                    "edit_session_id": edit_session.edit_session_id,
                },
            )
        except Exception as e:
            return MCPToolResult(success=False, error=str(e))

    async def _handle_undo(self, db, manager, edit_session, dry_run, undo_from_snapshot):
        snapshot_key = undo_from_snapshot
        if not snapshot_key:
            snapshots = edit_session.extra_metadata.get("snapshots", {}) if edit_session.extra_metadata else {}
            if snapshots:
                snapshot_key = list(snapshots.keys())[-1]
        if not snapshot_key:
            return MCPToolResult(success=False, error="没有可撤销的编辑")

        snapshots = edit_session.extra_metadata.get("snapshots", {}) if edit_session.extra_metadata else {}
        if snapshot_key not in snapshots:
            return MCPToolResult(success=False, error=f"快照 {snapshot_key} 不存在")

        restored = snapshots[snapshot_key]
        if dry_run:
            diff_result = diff_engine.compute_diff(edit_session.working_content or "", restored)
            return MCPToolResult(success=True, data={"dry_run": True, "message": "撤销预览（未实际修改）", "diff": diff_result})

        edit_session.working_content = restored
        edit_session.change_count = max(0, (edit_session.change_count or 0) - 1)
        await db.commit()
        await db.refresh(edit_session)
        return MCPToolResult(success=True, data={
            "edit_session_id": edit_session.edit_session_id,
            "change_count": edit_session.change_count,
            "message": f"已撤销到快照 {snapshot_key}，当前 {edit_session.change_count} 处改动。",
        })

    async def _handle_search_replace(self, db, manager, edit_session, search_text, new_content, match_mode, dry_run):
        if not search_text:
            return MCPToolResult(success=False, error="search_replace 必须提供 search_text")

        from editor.diff_engine import DiffEngine
        working = edit_session.working_content or ""

        if dry_run:
            new_working, count, err = DiffEngine.search_and_replace(working, search_text, new_content or "", match_mode)
            if err:
                return MCPToolResult(success=False, error=err)
            diff_result = diff_engine.compute_diff(working, new_working)
            return MCPToolResult(success=True, data={"dry_run": True, "replacements_preview": count, "diff": diff_result, "message": f"预览：将替换 {count} 处匹配"})

        snapshot_id = f"snap_{uuid.uuid4().hex[:8]}"
        snapshots = dict(edit_session.extra_metadata.get("snapshots", {})) if edit_session.extra_metadata else {}
        snapshots[snapshot_id] = working

        new_working, count, err = DiffEngine.search_and_replace(working, search_text, new_content or "", match_mode)
        if err:
            return MCPToolResult(success=False, error=err)

        edit_session.working_content = new_working
        edit_session.change_count = (edit_session.change_count or 0) + count
        if not edit_session.extra_metadata:
            edit_session.extra_metadata = {}
        edit_session.extra_metadata["snapshots"] = snapshots
        await db.commit()
        await db.refresh(edit_session)

        diff_data = await manager.get_diff(edit_session.edit_session_id)
        return MCPToolResult(success=True, data={
            "edit_session_id": edit_session.edit_session_id,
            "change_count": edit_session.change_count,
            "replacements_made": count,
            "snapshot_id": snapshot_id,
            "working_content": edit_session.working_content,
            "diff": diff_data.get("diff", {}),
            "message": f"替换了 {count} 处匹配。共 {edit_session.change_count} 处改动。",
        }, metadata={"tool": self.name, "change_count": edit_session.change_count, "edit_session_id": edit_session.edit_session_id})

    async def _handle_multi_search_replace(self, db, manager, edit_session, edits, match_mode, dry_run):
        if not edits:
            return MCPToolResult(success=False, error="multi_search_replace 必须提供 edits 数组")

        from editor.diff_engine import DiffEngine
        working = edit_session.working_content or ""
        snapshot_id = f"snap_{uuid.uuid4().hex[:8]}"
        snapshots = dict(edit_session.extra_metadata.get("snapshots", {})) if edit_session.extra_metadata else {}
        snapshots[snapshot_id] = working

        total = 0
        errors: list[str] = []
        for i, item in enumerate(edits):
            s_text = item.get("search_text", "")
            r_text = item.get("new_content", "")
            if not s_text:
                errors.append(f"编辑 #{i+1}: search_text 不能为空")
                continue
            new_working, count, err = DiffEngine.search_and_replace(working, s_text, r_text, match_mode)
            if err:
                errors.append(f"编辑 #{i+1}: {err}")
                continue
            working = new_working
            total += count

        if dry_run:
            diff_result = diff_engine.compute_diff(edit_session.working_content or "", working)
            return MCPToolResult(success=True, data={"dry_run": True, "message": f"预览：{total} 处替换", "total_replacements": total, "errors": errors or None, "diff": diff_result})

        if total == 0 and errors:
            return MCPToolResult(success=False, error="所有编辑均失败: " + "; ".join(errors))

        edit_session.working_content = working
        edit_session.change_count = (edit_session.change_count or 0) + total
        if not edit_session.extra_metadata:
            edit_session.extra_metadata = {}
        edit_session.extra_metadata["snapshots"] = snapshots
        await db.commit()
        await db.refresh(edit_session)

        diff_data = await manager.get_diff(edit_session.edit_session_id)
        result_data: dict[str, Any] = {
            "edit_session_id": edit_session.edit_session_id,
            "change_count": edit_session.change_count,
            "total_replacements": total,
            "snapshot_id": snapshot_id,
            "working_content": edit_session.working_content,
            "diff": diff_data.get("diff", {}),
            "message": f"批量替换了 {total} 处匹配。共 {edit_session.change_count} 处改动。",
        }
        if errors:
            result_data["partial_errors"] = errors
        return MCPToolResult(success=True, data=result_data, metadata={"tool": self.name, "change_count": edit_session.change_count, "edit_session_id": edit_session.edit_session_id})




class RunSubagentArgs(BaseModel):
    task_type: str = Field(description="任务类型：write_chapter / review")
    chapter_id: int | None = Field(default=None, description="目标章节ID（写作/审核/一致性检查时必填）")
    instruction: str | None = Field(default=None, description="给子Agent的额外指令（如写作要求、审核重点、修订意见等）")
    parameters: dict[str, Any] | None = Field(default=None, description="任务特定参数（可选，如 model、style、target_length 等）")
    agent_role: str | None = Field(default=None, description="指定Agent角色（可选）")
    agent_id: str | None = Field(default=None, description="指定Agent ID（可选）")
    model: str | None = Field(default=None, description="指定模型（可选）")


class RunSubagentTool(BaseMCPTool):
    """调度子Agent执行专业任务"""

    name = "run_subagent"
    description = (
        "调度子Agent执行专业任务。可用任务类型：\n"
        "- write_chapter: 写作/续写章节内容\n"
        "- review: 全量审核章节（规则初筛+LLM语义深审+一致性检查+伏笔管理）\n\n"
        "你只需指定任务类型和目标（如章节ID），后端会自动准备上下文。\n"
        "子Agent会返回结构化报告，包含摘要、关键发现和建议。"
    )
    category = MCPToolCategory.WRITING_ASSISTANT
    args_schema = RunSubagentArgs

    async def _execute(
        self,
        args: RunSubagentArgs,
        *,
        db: AsyncSession,
        user_id: int,
        novel_id: int,
        **extra,
    ) -> MCPToolResult:
        return await _execute_subagent_task(
            db=db,
            user_id=user_id,
            task_type=args.task_type,
            novel_id=novel_id,
            chapter_id=args.chapter_id,
            instruction=args.instruction,
            parameters=args.parameters,
            agent_role=args.agent_role,
            agent_id=args.agent_id,
            model=args.model,
        )






class EditingTools:
    """编辑工具集合 - 只包含AI可调用的工具"""
    
    @staticmethod
    def register_all(registry: MCPToolRegistry) -> None:
        """注册所有编辑工具（不包括accept/reject，那是用户操作）"""
        registry.register(EditChapterTool())
        registry.register(RunSubagentTool())
