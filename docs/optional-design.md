# 可选设计：累计审批（文件 Copy 方案）

## 场景

当前主设计使用逐条审批（每次 AI 编辑即时 accept/reject）。本方案描述另一种思路——累计审批：AI 连续多轮修改都不打断，用户最后一次性决定整批接受还是拒绝。

## 核心思路

用户点"开始审批"，系统对即将被 AI 编辑的章节文件各 copy 一份 `.backup`。之后 AI 无论改多少轮、改多少个文件，全部直接作用到原件。用户随时点确认或拒绝——确认删备份，拒绝用备份恢复文件。

## 与逐条审批的区别

| | 逐条审批（主方案） | 累计审批（本方案） |
|---|---|---|
| 审批粒度 | 每次 edit_chapter | 从"开始审批"到"确认"为止的整批 |
| 跨 turn | 不跨，当场决定 | 可跨多个 turn |
| 文件恢复方式 | 反向执行 search_replace | .backup 文件覆盖恢复 |
| DB 数据 | 直接生效，不审批 | 直接生效，不审批（同左） |
| 自动模式 | 每次自动 accept | 不适用，累计就是让 AI 自由改 |

## 实现

每次 AI 调用 edit_chapter 之前，检查目标文件是否已有 `.backup`——有就用现有的（同一个审批批次），没有就创建新的。

用户 accept → 删掉所有 `.backup`，操作日志标记该批次为 accepted。用户 reject → `.backup` 文件逐个覆盖恢复原件，操作日志标记该批次为 rejected。

.backup 文件跨 turn 存活，不受 git commit 影响（加入 .gitignore 或在 commit 时排除）。

## 当前选择

主设计走逐条审批，原因：
1. 实现更简单：不需要跨 turn 管理备份文件的生命周期
2. 全自动模式等同 AI 可以直接写，与累计审批的用户体验差异不大
3. 逐条审批覆盖了"精准修改时需要确认"的场景
4. 这种设计无法做到db状态的回退，只能回退正文，因为中间可能经历了很多改动 几乎无法追踪

累计审批方案保留在此文档中，后续如果逐条审批不够用，可以在此基础上实现。

---

# 可选设计：Git 作为审核基线（全自动 + 不自动 commit）

## 场景

全自动模式下 AI 连续多轮自由编辑，但不自动 commit。用户 review 之后手动 commit 或拒绝。Git HEAD 天然作为回退基线。

## 核心思路

全自动模式下 git 不自动 commit。AI 每次 edit_chapter 直接改文件，DB 变更走操作日志。用户随时执行：

- `git diff` 查看所有未提交的变更
- **accept** → `git commit`，操作日志标记该批次 confirmed
- **reject** → `git checkout -- .`（恢复所有文件），操作日志逆向执行 pending DB 变更

HEAD 就是备份点，不需要手动 cp 文件。

## 与主方案的区别

| | 主方案（逐条审批） | Git 基线方案 |
|---|---|---|
| 审批粒度 | 每次 edit_chapter | 用户 review 时整批 |
| commit 时机 | 每次 turn 结束自动 commit | 用户 accept 时手动 commit |
| 回退机制 | 单次 edit 反向 search_replace | git checkout 整批恢复 |
| DB 回退 | 不处理（无审批状态） | 操作日志按 pending/confirmed 区分 |
| 适用模式 | 逐个审批 + 全自动 | 全自动模式 |

## 实现要点

edit_chapter 工具写完后不 commit。agent loop 在每个 turn 结束时也不 commit。只有用户点了 accept，app 层才触发 git add + git commit 和操作日志批量确认。

用户拒绝时，`git checkout -- .` 恢复所有被改的文件。操作日志查出所有 pending 记录，倒序逆向执行。

## 与逐条审批共存

用户可以切换模式：
- **逐个模式**：每次 edit 当场确认/拒绝
- **全自动模式**：AI 自由写，等用户 review 后 commit 或回退

两个模式共用同一套 git 仓库和操作日志基础设施。

## 利弊

优点：不需要 .backup 文件管理，不需要跨 turn 追踪审批状态，git 天然是基线。
缺点：git checkout 回退是仓库级的——如果用户在全自动期间手动改了文件，也会被一起回退。
