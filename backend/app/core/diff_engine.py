"""
Diff引擎 - 文本差异计算

支持多种diff模式：
1. full_replace - 全量替换
2. partial_edit - 部分编辑（行级别）
3. insert - 插入内容
4. delete - 删除内容

输出格式兼容前端diff展示库
"""
import difflib
from typing import Dict, Any, List, Optional, Tuple
from dataclasses import dataclass, field
from enum import Enum


class ChangeType(str, Enum):
    FULL_REPLACE = "full_replace"
    PARTIAL_EDIT = "partial_edit"
    INSERT = "insert"
    DELETE = "delete"


DiffChangeType = ChangeType


class DiffLineType(str, Enum):
    CONTEXT = "context"
    INSERT = "insert"
    DELETE = "delete"


@dataclass
class DiffHunk:
    old_start: int
    old_lines: int
    new_start: int
    new_lines: int
    changes: List[Dict[str, Any]] = field(default_factory=list)
    
    def to_dict(self) -> Dict[str, Any]:
        return {
            "old_start": self.old_start,
            "old_lines": self.old_lines,
            "new_start": self.new_start,
            "new_lines": self.new_lines,
            "changes": self.changes
        }


@dataclass
class DiffResult:
    change_type: ChangeType
    hunks: List[DiffHunk] = field(default_factory=list)
    old_content: str = ""
    new_content: str = ""
    summary: Dict[str, int] = field(default_factory=dict)
    
    def to_dict(self) -> Dict[str, Any]:
        return {
            "change_type": self.change_type.value,
            "hunks": [h.to_dict() for h in self.hunks],
            "old_content": self.old_content,
            "new_content": self.new_content,
            "summary": self.summary
        }


class DiffEngine:
    def __init__(self, context_lines: int = 3):
        self.context_lines = context_lines
    
    def compute_diff(
        self,
        old_content: str,
        new_content: str,
        change_type: Optional[ChangeType] = None
    ) -> DiffResult:
        if change_type == ChangeType.FULL_REPLACE:
            return self._full_replace_diff(old_content, new_content)
        
        if change_type == ChangeType.INSERT:
            return self._insert_diff(old_content, new_content)
        
        if change_type == ChangeType.DELETE:
            return self._delete_diff(old_content, new_content)
        
        return self._unified_diff(old_content, new_content)
    
    def _unified_diff(self, old_content: str, new_content: str) -> DiffResult:
        old_lines = old_content.splitlines(keepends=True)
        new_lines = new_content.splitlines(keepends=True)
        
        if not old_lines:
            return self._insert_diff("", new_content)
        
        if not new_lines:
            return self._delete_diff(old_content, "")
        
        hunks = []
        matcher = difflib.SequenceMatcher(None, old_lines, new_lines)
        
        for tag, i1, i2, j1, j2 in matcher.get_opcodes():
            if tag == 'equal':
                continue
            
            old_start = i1 + 1
            old_lines_count = i2 - i1
            new_start = j1 + 1
            new_lines_count = j2 - j1
            
            changes = []
            
            if tag in ('replace', 'delete'):
                for i in range(i1, i2):
                    changes.append({
                        "type": DiffLineType.DELETE.value,
                        "content": old_lines[i].rstrip('\n\r'),
                        "line_number": i + 1
                    })
            
            if tag in ('replace', 'insert'):
                for j in range(j1, j2):
                    changes.append({
                        "type": DiffLineType.INSERT.value,
                        "content": new_lines[j].rstrip('\n\r'),
                        "line_number": j + 1
                    })
            
            if changes:
                hunks.append(DiffHunk(
                    old_start=old_start,
                    old_lines=old_lines_count,
                    new_start=new_start,
                    new_lines=new_lines_count,
                    changes=changes
                ))
        
        additions = sum(1 for h in hunks for c in h.changes if c["type"] == "insert")
        deletions = sum(1 for h in hunks for c in h.changes if c["type"] == "delete")
        
        return DiffResult(
            change_type=ChangeType.PARTIAL_EDIT,
            hunks=hunks,
            old_content=old_content,
            new_content=new_content,
            summary={
                "additions": additions,
                "deletions": deletions,
                "hunks": len(hunks)
            }
        )
    
    def _full_replace_diff(self, old_content: str, new_content: str) -> DiffResult:
        old_lines = old_content.splitlines() if old_content else []
        new_lines = new_content.splitlines() if new_content else []
        
        changes = []
        max_lines = max(len(old_lines), len(new_lines))
        
        for i in range(max_lines):
            if i < len(old_lines):
                changes.append({
                    "type": DiffLineType.DELETE.value,
                    "content": old_lines[i],
                    "line_number": i + 1
                })
            if i < len(new_lines):
                changes.append({
                    "type": DiffLineType.INSERT.value,
                    "content": new_lines[i],
                    "line_number": i + 1
                })
        
        hunks = [DiffHunk(
            old_start=1,
            old_lines=len(old_lines),
            new_start=1,
            new_lines=len(new_lines),
            changes=changes
        )]
        
        return DiffResult(
            change_type=ChangeType.FULL_REPLACE,
            hunks=hunks,
            old_content=old_content,
            new_content=new_content,
            summary={
                "additions": len(new_lines),
                "deletions": len(old_lines),
                "hunks": 1
            }
        )
    
    def _insert_diff(self, old_content: str, new_content: str) -> DiffResult:
        new_lines = new_content.splitlines() if new_content else []
        
        changes = []
        for i, line in enumerate(new_lines):
            changes.append({
                "type": DiffLineType.INSERT.value,
                "content": line,
                "line_number": i + 1
            })
        
        hunks = [DiffHunk(
            old_start=0,
            old_lines=0,
            new_start=1,
            new_lines=len(new_lines),
            changes=changes
        )]
        
        return DiffResult(
            change_type=ChangeType.INSERT,
            hunks=hunks,
            old_content=old_content,
            new_content=new_content,
            summary={
                "additions": len(new_lines),
                "deletions": 0,
                "hunks": 1
            }
        )
    
    def _delete_diff(self, old_content: str, new_content: str) -> DiffResult:
        old_lines = old_content.splitlines() if old_content else []
        
        changes = []
        for i, line in enumerate(old_lines):
            changes.append({
                "type": DiffLineType.DELETE.value,
                "content": line,
                "line_number": i + 1
            })
        
        hunks = [DiffHunk(
            old_start=1,
            old_lines=len(old_lines),
            new_start=0,
            new_lines=0,
            changes=changes
        )]
        
        return DiffResult(
            change_type=ChangeType.DELETE,
            hunks=hunks,
            old_content=old_content,
            new_content=new_content,
            summary={
                "additions": 0,
                "deletions": len(old_lines),
                "hunks": 1
            }
        )
    
    def apply_partial_edit(
        self,
        content: str,
        start_line: int,
        end_line: int,
        new_lines: List[str]
    ) -> str:
        lines = content.splitlines(keepends=True)
        
        start_idx = max(0, start_line - 1)
        end_idx = min(len(lines), end_line)
        
        new_content_with_newlines = [line if line.endswith('\n') else line + '\n' for line in new_lines]
        
        lines[start_idx:end_idx] = new_content_with_newlines
        
        return ''.join(lines)
    
    def get_line_range(
        self,
        content: str,
        start_line: int,
        end_line: int
    ) -> str:
        lines = content.splitlines()
        start_idx = max(0, start_line - 1)
        end_idx = min(len(lines), end_line)
        return '\n'.join(lines[start_idx:end_idx])


diff_engine = DiffEngine()
