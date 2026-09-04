# 现有功能实现情况 / 可用性 / 易用性 全面评审（2026-09-03）

## 0. 本文范围、方法与定位

本文是同日第三份评审，与既有两份分工不同，**不重复其内容**：

| 文档 | 视角 | 产出 |
|---|---|---|
| `docs/user-perspective-review-2026-09-03.md` | 真实用户走查界面 | 17 项 UX 缺陷（A1-A7 / B1-B5 / C1-C2 / D） |
| `docs/user-perspective-review-2026-09-03-fixes.md` | 落地记录 | 上述 17 项 + 二轮 13 项的修复结果 |
| **本文** | 代码与产物为证据的工程评审 | 三条轴：实现情况、可用性、易用性 |

三条轴的定义：

- **实现情况**：功能是否真正接线、前后端契约是否闭合、文档与代码是否一致、有多少表面是死的。
- **可用性**：是否真的能跑通、失败与恢复路径是否闭合、数据是否安全、性能与测试证据是否可复现。
- **易用性**：发现性、词表与文案一致性、残余交互摩擦。

方法：桥接方法三方账本核对（后端注册 ↔ `frontend/src/lib/novelist/api.ts` ↔ UI 调用点）、构建与测试实跑、关键失败路径读码、`git show --stat` 核对历史。所有结论标注 `文件:行`，验证命令与原始结果见 §7。

**一句话结论：产品不是"到处是桩"，而是"接线不平衡"。** 后端可用能力显著多于前端出口，而这是 `ccb6d2c` 一次**有意的范围收缩**造成的，符合 AGENTS.md 的"不扩张专家控制面"。真正的缺陷因此不是"缺 65 个界面"，而是**计划文档没有跟着收缩**——它仍把已删除的代码标记为完成。

## 1. 结论摘要

| 轴 | 评价 | 核心依据 |
|---|---|---|
| 实现情况 | 主干闭合，侧翼悬空，文档失真 | 191 个后端注册方法中 123 个有 UI 调用点，65 个无出口，3 个未暴露；1 个 1954 行服务连 DI 都没注册 |
| 可用性 | 默认路径可用，集成测试不稳定，两处诊断黑洞 | 单元 216/216 通过；集成 710/713，3 项失败，隔离重跑 13/13 全通过 |
| 易用性 | 视觉与主流程已收口，词表漂移与静默失败仍拖后腿 | 前端有 2 个幻影 family、缺 2 个真实 family；13 处 `.catch(() => {})`，含 1 处安全设置 |

### 必须优先处理（P0，共 4 项）

- **U1** 章节保存无并发令牌，后端为最后写入者胜 —— `src/Novelist.Contracts/App/ChapterContentPayloads.cs:20-23`
- **U2** 桥接兜底 `catch` 不记日志，且 Release 构建下唯一错误出口被编译掉 —— `src/Novelist.Core/Bridge/BridgeDispatcher.cs:~141`、`src/Novelist.App/Desktop/PhotinoWebMessageBridge.cs:25-33`
- **I5** 计划文档把 `ccb6d2c` 已删除的代码标为完成 —— `docs/corpus-driven-writing/tasks.md:264-265`
- **U5** 全量集成测试不是稳定绿的（3 项 flaky），而落地记录只承认 1 项 —— 见 §3.1

## 2. 实现情况

### 2.1 桥接账本：191 / 188 / 123 / 65 / 3

这是评估"实现到哪一步"最硬的一把尺子。全量比对结果：

| 口径 | 数量 | 含义 |
|---|---|---|
| 后端 `Register(...)` 实际注册 | **191** | `BridgeDispatcher` 上真实可调用的方法 |
| `api.ts` 暴露给前端 | **188** | `NovelistAppApi` 接口成员（`frontend/src/lib/novelist/api.ts:269` 导出 `appApi`） |
| UI 里有真实调用点 | **123** | 在 `src/components`、`src/views`、`src/hooks` 中被引用 |
| 暴露但无任何 UI 调用点 | **65** | 死表面 |
| 已注册但未暴露 | **3** | `InspectReferenceCorpusTechniqueVectorIndexes`、`PumpReferenceCorpusTechniqueVectorMaintenance`、`ScheduleReferenceCorpusTechniqueVectorMaintenance` |

两个值得记录的结构性事实：

1. `api.ts` 的 188 个名字与 `BridgeCompatibilityAppMethods` 的 188 名占位白名单**逐字节相同**（`diff` 无差异）。说明前端表面是按兼容白名单机械对齐的，不是按实际需求裁剪的——这正是死表面的成因。
2. 统计需同时匹配两种注册写法：名字在前的 `Register("Name", …)` 与泛型式 `Register<TIn,TOut>(dispatcher, "Name", service.Method)`。只匹配前者会漏掉 11 个（集中在 `src/Novelist.Core/Bridge/ReferenceCorpusGovernanceBridgeHandlers.cs`），得到错误的 180。后续任何审计脚本都必须覆盖两种形态。

### 2.2 65 个死表面几乎全属参考语料子系统

65 个中约 63 个属于参考语料/风格/治理链路，按能力簇归类：

- **风格画像与风格样本**：画像 CRUD、样本增删改查与检索、技能抽取及其取消。
- **分析任务调度**：入队、暂停、恢复、取消、重新定优先级——后端有完整的 10 态状态机、租约、心跳与回收。
- **治理**：聚合视图、复核队列、授权闸门、库成员管理。
- **语料维护**：级联影响分析、素材详情与标签、来源处理与分段详情、锚点提升/重建/构建状态、去重重建、对账、导入恢复状态。

这些**不是空实现**。后端有服务、有契约、有定向测试；缺的只是前端入口。

### 2.3 一个完全未接线的子系统

`src/Novelist.Infrastructure/App/FileSystemNarrativePatternExtractionService.cs` 共 **1954 行**，`NarrativePatternExtraction` 全仓仅出现在契约、接口、实现和两个测试文件中：

- `src/Novelist.Contracts/App/NarrativePatternPayloads.cs`
- `src/Novelist.Core/App/INarrativePatternExtractionService.cs`
- 实现本身
- `tests/Novelist.IntegrationTests/NarrativePatternServiceTests.cs`、`tests/Novelist.Tests/Phase15ContractTests.cs`

**既没有 `Novelist.App` 的 DI 注册，也没有桥接处理器。** 它比 §2.2 的死表面更彻底：那 65 个至少能从后端调到，这 1954 行在运行时根本不存在于对象图里。其前端出口 `NarrativePatternView.tsx`（723 行）已在 `ccb6d2c` 删除。

另有 `src/Novelist.App/Desktop/DesktopBridgeComposition.cs:106` 漏掉 `modelPreflight`，属同类接线遗漏但影响面小。

### 2.4 这是有意收缩，不是烂尾

`git show --stat ccb6d2c`（"feat: rebuild frontend around corpus writing loop"）：**37 个文件，1167 行新增，18421 行删除**。删除的是整条专家装配线：

`ReferenceAnchorView.tsx`（4555）、`ChapterReferencePanel.tsx`（3191）、`StyleProfileLibraryPanel.tsx`（947）、`CorpusAnalysisLibraryTab.tsx`（742）、`NarrativePatternView.tsx`（723）、`StyleSampleLibraryView.tsx`（721）、`BlueprintDetail.tsx`（706）、`OrchestrationPanel.tsx`（688）、`StyleExtractionPanel.tsx`（470）、`blueprintRevision.ts`（371）、`BlueprintPreviewPanel.tsx`（278）、`CorpusAnalysisJobsPanel.tsx`（218）、`CorpusGovernancePanel.tsx`（150）、`MaterialCoveragePanel.tsx`（143）、`referenceAnchorStyles.ts`（10）。新增仅 `CorpusAreaView.tsx`（+493）、`ChoiceBlock.tsx`（+31）、`CorpusUsageCard.tsx`（+33）、`choices.ts`（+27）。

这个删/增比例（约 15.8:1）配合 AGENTS.md 的"Do not expand the expert control surface"，只有一种解释：**主动砍掉专家控制面，把产品收敛到章节写作主循环。** 因此 §2.2 的 65 个死方法**不应被当作待建 UI 的 backlog**。

### 2.5 真正的缺陷：计划文档仍在为已删代码背书

最锋利的证据是两个已勾选的复选框引用了不存在的文件：

```text
tasks.md:264  - [x] 后台任务面板：…代码证据：`CorpusAnalysisJobsPanel` 与 `ListReferenceCorpusAnalysisJobs` adapter
tasks.md:265  - [x] 后台控制交互：…代码证据：`CorpusAnalysisJobsPanel.runAction`
```

`CorpusAnalysisJobsPanel` 已在 `ccb6d2c` 删除。**一个以已删除文件为"代码证据"的已完成任务，等于没有证据。**

同类漂移：

- `docs/corpus-driven-writing/development-plan.md:123` 仍把 `OrchestrationPanel`/`BlueprintDetail` 列为前端重建目标，两者均已删除。
- `tasks.md` 共 205 个 `[x]`、仅 2 个 `[ ]`（`:431` 素材库工作台收口、`:432` 易用性真实走查）。97% 的完成率与 §2.1 的 65 个死表面、§2.3 的未接线服务无法同时为真。
- `development-plan.md:712-726` 的成熟度表把 M0-M9 全部标为 `S`。该表自身的定义写得很克制（`S` 明确"不等于用户可持续使用"），但把已删能力也计入 `S`，会让读者以为这些能力仍在薄切片状态待深化，而实际上它们已被移出产品。

`development-plan.md` 的口径纪律本身是好的（"不得再使用'语料驱动写作系统已完成'…等超出证据的表述"），问题纯粹在于**收缩发生后没有回写**。

## 3. 可用性

### 3.1 测试实况：单元干净，集成不稳定

本次实跑 `dotnet test Novelist.slnx`（命令与汇总见 §7）：

| 套件 | 结果 | 耗时 |
|---|---|---|
| `Novelist.Tests` | **216 / 216 通过** | 661 ms |
| `Novelist.IntegrationTests` | **710 通过 / 3 失败 / 713 总数**，退出码 1 | 2m54s |

三项失败：

1. `ReferenceCorpusAnalysisWorkerTests.RealWorkerLoopReclaimsExpiredLeaseAndFencesLostWorkerCommit` —— `TimeoutException: Analysis job 'analysis-job:9f955de2…' did not reach the expected state within 00:00:10`（`ReferenceCorpusAnalysisWorkerTests.cs:420`）
2. `ReferenceCorpusRetrievalProductionTests.WarmRetrievalAcrossOneThousandNodesStaysWithinControlledBudget` —— `Warm retrieval took 00:00:15.2267496`，超出 1000 节点热检索预算（`ReferenceCorpusRetrievalProductionTests.cs:135`）
3. `ReferenceCorpusAnalysisWorkerTests.LifecycleStartIsIdempotentAndIdleLoopProcessesQueuedJob` —— `IOException: The process cannot access the file 'index.sqlite' because it is being used by another process`，发生在 `DisposeAsync()`（`ReferenceCorpusAnalysisWorkerTests.cs:457`）

隔离重跑（`--no-build --filter "FullyQualifiedName~ReferenceCorpusAnalysisWorkerTests|FullyQualifiedName~WarmRetrievalAcrossOneThousandNodes"`）：**13 / 13 通过，8 秒，退出码 0**。

结论：三项都是时序与文件锁的 flakiness，不是确定性破损。但**这是趋势恶化**——`docs/user-perspective-review-2026-09-03-fixes.md` 记录的基线是"216 + 713 通过，含 1 个已知 flaky 性能测试"，现在扩大为 3 项，且新增了并行执行下的 `index.sqlite` 句柄争用（这类失败会随机命中任意用例，比性能抖动更需要处理）。**在这个状态下，CI 的绿灯不能作为发布依据。**

同时必须记住 `development-plan.md` 的 P0 定义："全套构建与测试必须可编译、可通过。该项是继续开发的入口条件，不得以局部定向测试替代。" 按此标准，当前**未满足自己设定的入口条件**——用隔离重跑证明"其实是好的"正是该条禁止的做法。

### 3.2 构建健康

- `dotnet build`：**0 错误 / 9 警告**，49.55s。警告为 8 处 CS8604 可空性（`tests/Novelist.Tests/Bridge/ReferenceBridgeHandlerRoutingTests.cs:1085,1093,1094,1095`、`tests/Novelist.Tests/MafToolRegistryTests.cs:879,885,886,887`）+ 1 处 xUnit2000 实参顺序颠倒（`tests/Novelist.IntegrationTests/ChapterContentServiceTests.cs:270`，`GetMaxChapterNumberIncludesDeletedHighWaterMark`）。均在测试项目内，不影响产品代码，但 xUnit2000 会让失败信息里的 expected/actual 反向，值得顺手修。
- `npm --prefix frontend run verify`：build、lint、`test:node` 全绿；node 单元测试 **23/23**，834 ms。
- **产物体积是真实风险**：`dist/assets/WorkspaceView-*.js` 达 **5,294 kB（gzip 1,434 kB）**，远超 650 kB 警告线；另有 mermaid-parser 604 kB、cytoscape 434 kB、index 265 kB、katex 259 kB。桌面应用没有网络下载成本，但单块 5.3 MB 的 JS 直接转化为解析/执行时间，影响冷启动——而 `development-plan.md` P2 明确要求"建立默认章节路径的冷打开和首个主操作性能基线"。该基线目前不存在。

### 3.3 数据安全：章节保存是最后写入者胜

`src/Novelist.Contracts/App/ChapterContentPayloads.cs:20-23`：

```csharp
public sealed record SaveContentPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string Content);
```

没有版本号、没有 ETag、没有 hash、没有基线时间戳。后端无法判断"我要覆盖的内容是不是调用方读到的那份"，因此**服务端一侧是无条件覆盖**。

前端确实有冲突处理，而且做得细致（node 测试断言："冲突补丁从不携带 content 或 isDirty"、"选择传入版本后落地并把标签页恢复干净态"、"冲突 diff 的标签页 id 由路径派生，所以重复点击复用同一个标签"）。但这套机制是**客户端事件驱动**的——它依赖前端先感知到外部变更。任何绕过该事件流的写入（第二个窗口、外部编辑器、Git 操作、Agent 工具直写）都不会被拦住，正文会静默丢失。

考虑到这是一个长篇写作工具，**丢正文是最高代价的故障**。建议在 payload 上加基线令牌（内容 hash 或版本号），后端不匹配时返回冲突错误码，让现有前端冲突 UI 接管——前端已经有这个 UI，成本主要在契约与服务端校验。

### 3.4 诊断黑洞：Release 构建下桥接错误无声消失

两处叠加，后果是"用户报错但日志里什么都没有"。

`src/Novelist.Core/Bridge/BridgeDispatcher.cs` 的兜底分支：

```csharp
catch (InvalidContentPathException ex) { return Error(id, BridgeErrorCodes.InvalidPath, ex.Message, ex.Details); }
catch (ArgumentException ex) { return Error(id, BridgeErrorCodes.ValidationError, ex.Message); }
catch (OperationCanceledException) { return Error(id, BridgeErrorCodes.Cancelled, "Bridge request was cancelled."); }
catch { return Error(id, BridgeErrorCodes.InternalError, "Internal bridge error."); }
```

最后一个 `catch` **既不记录异常也不保留堆栈**，前端只拿到 `InternalError` + 固定英文串。异常对象就地丢弃。

`src/Novelist.App/Desktop/PhotinoWebMessageBridge.cs:25-33`：

```csharp
public void Post(string message)
{
    _ = ReceiveAsync(message).AsTask()
        .ContinueWith(task => Debug.WriteLine(task.Exception), CancellationToken.None,
                      TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
```

`Debug.WriteLine` 带 `[Conditional("DEBUG")]`，**在 Release 构建里整句被编译掉**。这是消息泵层唯一的错误出口，意味着发布版本中桥接层的未处理异常没有任何落地渠道。而 `scripts/novelist-publish.sh` 产出的正是 Release 自包含包——**用户手上的版本恰好是唯一没有错误日志的版本。**

修复成本很低（注入 `ILogger`，在兜底 `catch` 里 `LogError(ex, …)`，`Post` 改为写日志而非 `Debug.WriteLine`），收益是让所有线上问题从"无法复现"变成"可查"。这应当排在任何新功能之前。

### 3.5 其他静默失败路径

- `src/Novelist.Infrastructure/App/ReferenceCorpusTechniqueVectorMaintenanceLoop.cs:~88-93`：`catch (OperationCanceledException) when (…) { break; }` 之后是裸 `catch { await Task.Delay(_idleDelay, …); }`。任何持久性错误（磁盘满、schema 不匹配、sqlite-vec 缺失）都会变成**无声无限重试**，既不上报也不退避升级。向量索引可能长期处于陈旧状态而无人知晓。
- `src/Novelist.Infrastructure/App/FileSystemWorkspaceSearchService.cs:163-175`：`catch (OperationCanceledException) { throw; } catch { return []; }` 包住 `_semanticSearch.SearchAsync`，随后 `.Where(hit => hit.Relevance >= 0.3)`。**语义检索故障与"没有结果"在返回值上完全不可区分**，用户会以为语料里没有相关内容，而实际是嵌入服务挂了。至少应把降级状态回传给前端。
- 前端 **13 处 `.catch(() => {})`**，分布在 3 个文件：`frontend/src/components/chat/ChatPanel.tsx:297,431,889,900,905,911,1013,1017,1636`、`frontend/src/components/settings/GeneralConfigTab.tsx:60,63,66`、`frontend/src/views/WorkspaceView.tsx:104`。其中两处特别不该沉默：
  - `ChatPanel.tsx:911` 是 `app.SetApprovalMode(next)` —— **审批模式是安全设置**。若该调用失败，UI 会显示新模式而后端仍在旧模式下执行工具，用户以为自己收紧了权限而实际没有。这是安全语义与显示状态的静默背离。
  - `ChatPanel.tsx:1017` 是 `app.CancelChat` —— 用户点了取消却没生效时，界面不会告诉他。

### 3.6 阈值与证据的可复现性

- `src/Novelist.Infrastructure/App/ChapterCorpusCoverageService.cs:17` 硬编码 `private const double SufficientRatio = 0.5;`（判定见 `:156`、`:177`），`:187` 注释写明"v1 阈值：检索返回任意命中即视为该 beat 已覆盖"，并有两处注释把标定推给"开放问题 3"。也就是说，**"覆盖度充足"这个直接影响用户是否继续写的判断，目前建立在未标定的常量和最宽松的命中规则上**。
- 更新检查在默认构建下永久关闭：`src/Novelist.App/Novelist.App.csproj:8` 的 `NovelistUpdateCheckEndpointUrl` 默认空串，`:16` 作为 `_Parameter2` 注入，全仓无任何位置为其赋值，于是 `GitHubUpdateCheckService.cs:48,58` 恒定返回 `disabled` / `update.endpoint_missing`。功能实现完整但**默认不可达**，用户永远不会收到更新提示。
- **性能门禁的证据不在仓库里**：50K 全管线门禁的结果文件位于被 gitignore 的 `build/tmp/corpus-driven-writing/scale-50k-metrics.json`（characters 50015、work_items 13385、duplicate_outputs 0、throughput 29.157、P95 claim/list/progress 10.1213/27.0401/3.5105 ms、generated_at 2026-07-10T14:25:13Z）。AGENTS.md 要求"formal 50K gate has passed; future changes must preserve it"，但**仅凭仓库无法复现或校验这个已通过状态**——门禁事实上依赖某台机器上的临时产物。2M 长跑档位唯一留存的记录是一次历史失败。建议把指标产物（非源语料）纳入版本控制或落到可追溯的构建工件，否则"必须保持"的约束无法执行。

## 4. 易用性

同日两份文档已处理绝大部分界面级摩擦（主题不统一、结构层级、中文化、焦点可见性等 30 项）。本节只补它们没覆盖的、由代码结构导致的易用性问题。

### 4.1 分类词表前后端漂移，且两侧都无守卫

后端真值（`src/Novelist.Core/App/ReferenceCorpusFeatureFamilySchemas.cs:12-68`）共 12 个 family：句子级 `syntax/rhythm/sensory/emotion/rhetoric`，段落级 `narrative/pov/action/character/commercial`，场景级 `scene/trope`。

前端（`frontend/src/lib/novelist/corpusFamilies.ts:1-24`）：

```ts
export const OBSERVATION_FAMILIES = ['emotion','sensory','rhythm','syntax','action','interaction','pov','rhetoric','hook','narrative','scene','trope'] as const
export const SPECIMEN_FAMILIES = ['emotion','rhetoric','rhythm','action','structure'] as const
```

差异：

- **幻影 family**：`interaction`、`hook` 后端不存在；技法侧 `structure` 同样不存在。用户在界面上按这些维度筛选，只会得到空结果，而这不是数据问题。
- **缺失 family**：`character`、`commercial` 后端真实存在但前端没有出口，这两个维度的语料无法被筛选到。

讽刺的是该文件自己的头注释写着"须与后端 `ReferenceCorpusFeatureFamilies` / 技法标本 family 词表保持一致"。三层都没有守住：

1. 没有代码生成或共享词表；
2. 后端不校验取值 —— `src/Novelist.Infrastructure/App/ReferenceCorpusTechniqueSpecimenPersistence.cs:105-115` 的 `Validate` 只检查 `TechniqueFamily` 非空，任何字符串都能落库；
3. 前端不暴露未知值 —— `frontend/src/lib/novelist/corpusTaxonomy.ts:7,13,21,29,38,54` 的 `taxonomyLabel` 在查不到映射时**回退显示原始英文键**，于是漂移在界面上表现为"某个英文单词"而不是报错，谁都不会去查。

建议：把 family 词表作为唯一真值从后端导出（生成 TS 常量或经桥接下发），`Validate` 增加白名单校验，`taxonomyLabel` 对未知键在开发构建下抛出或告警。这是一次性投入，能消除整类"筛选没结果"的用户困惑。

### 4.2 不可达的 UI 状态

`frontend/src/components/shell/ActivityBar.tsx:8,40,42,45,53` 定义并消费了 `disabled?: boolean`，`:42` 据此渲染 `（即将推出）` 标题。但在 `src/components/shell/` 与 `src/views/` 中检索 `disabled: true` **无任何结果**——没有任何入口传入该属性。这段代码及其中文文案永远不会被用户看到。属于 `ccb6d2c` 收缩后的遗留：曾经用来标记未上线的专家面板，面板删了，标记留下了。应删除以免误导后续维护者以为存在"即将推出"的路线。

同类死代码：`frontend/src/components/content/types.ts:32,40,48,85` 的导出类型无消费者；`frontend/src/components/reference-anchor/CorpusAreaView.tsx:109` 的关键词输入框不参与检索请求（输入无效果，比禁用更糟）。

### 4.3 残余文案与交互债

以下项已在前两份文档的范围之外，仍待处理：

- `frontend/src/components/reference-anchor/CorpusAreaView.tsx` 中 `已拒绝` 与 `已驳回` 混用指同一状态；同文件有 4 项后端返回的诊断信息被丢弃不显示。
- `frontend/src/components/novel/NovelImportDialog.tsx:136,175` 直接把 C# 枚举原文（`reason`、`diagnostic.code`）渲染给用户。导入失败是新用户最可能遇到的第一个错误，此处暴露内部标识符的代价最高。
- `frontend/src/views/ProfileView.tsx:68,78,198` 保存失败无反馈；`frontend/src/components/profile/ContributionGrid.tsx:20,32-35,108-113` 年份标签错误，且贡献格子不可键盘访问（与已修复的 C1/C2 焦点工作同源，属漏网）。
- `frontend/src/components/chat/ToolCallCard.tsx:13,65,79,97,110` 有一个必填但从未读取的 prop，以及多处英文兜底文案。

### 4.4 启动期数据健壮性

`frontend/src/hooks/useEditorTabs.ts:33-52` 在启动时从 `novelist_tabs_all` 恢复标签页，`catch` 只覆盖 `JSON.parse` 抛错，随后 `saved.map(t => ({ ...t, id: nextId(...) }))` **原样展开存储中的任意结构**。若历史版本写入的形状与当前不兼容（字段更名、类型变化），恢复出的标签会带着坏字段进入渲染，表现为白屏或局部崩溃，而用户没有任何自救手段（他不知道要去清 localStorage）。应加入形状校验：逐条校验必需字段，丢弃不合法项而非整体失败，并在丢弃时给一次提示。

### 4.5 发现性总体判断

正面：`ccb6d2c` 之后主导航收敛到章节写作循环，配合 `-fixes.md` 落地的主题统一与结构调整，**默认路径的发现性明显优于收缩前**（收缩前是 13 个专家面板并列）。

尚未验证：AGENTS.md 与 `tasks.md:431-432` 都记录着两件未完成的事——素材库参考书管理与蓝图预演的定向浏览器验收、以及 5 名目标用户的真实走查（要求至少 4 人无指导完成主流程）。**本次评审不能替代这两项**：代码审查能证明词表漂移会导致空结果，但不能证明用户是否找得到入口、看不看得懂"六维语料覆盖"。易用性的最终判定仍然缺一手用户数据。

## 5. 问题清单

编号与既有文档的 A/B/C/D 体系不冲突：`I` = 实现情况，`U` = 可用性，`E` = 易用性。

| 编号 | 严重度 | 问题 | 位置 |
|---|---|---|---|
| **U1** | P0 | 章节保存无并发令牌，服务端最后写入者胜，非事件流写入会静默丢正文 | `src/Novelist.Contracts/App/ChapterContentPayloads.cs:20-23` |
| **U2** | P0 | 桥接兜底 `catch` 丢弃异常不记日志；消息泵唯一错误出口 `Debug.WriteLine` 在 Release 被编译掉 | `src/Novelist.Core/Bridge/BridgeDispatcher.cs:~141`、`src/Novelist.App/Desktop/PhotinoWebMessageBridge.cs:25-33` |
| **I5** | P0 | 已勾选任务以 `ccb6d2c` 删除的 `CorpusAnalysisJobsPanel` 为"代码证据" | `docs/corpus-driven-writing/tasks.md:264,265` |
| **U5** | P0 | 全量集成测试 3 项 flaky（含并行下 `index.sqlite` 句柄争用），未满足计划自设的 P0 入口条件 | 见 §3.1 |
| **I1** | P1 | 65 个 `api.ts` 方法无 UI 调用点；188 名前端表面与兼容白名单逐字节相同，说明按白名单而非需求对齐 | `frontend/src/lib/novelist/api.ts` |
| **I2** | P1 | 1954 行叙事模式抽取服务既无 DI 注册也无桥接处理器 | `src/Novelist.Infrastructure/App/FileSystemNarrativePatternExtractionService.cs` |
| **I3** | P1 | `development-plan.md:123` 仍以已删除的 `OrchestrationPanel`/`BlueprintDetail` 为重建目标；M0-M9 全标 `S` 含已移出产品的能力 | `docs/corpus-driven-writing/development-plan.md:123,712-726` |
| **E1** | P1 | family 词表三层无守卫：前端 3 个幻影值、缺 2 个真实值，后端只校验非空，`taxonomyLabel` 回退原始英文键掩盖漂移 | `corpusFamilies.ts:1-24`、`ReferenceCorpusTechniqueSpecimenPersistence.cs:105-115`、`corpusTaxonomy.ts:7,13,21,29,38,54` |
| **U3** | P1 | `SetApprovalMode` 失败被静默吞掉，UI 显示的审批模式可能与后端实际执行模式背离 | `frontend/src/components/chat/ChatPanel.tsx:911` |
| **U4** | P1 | 语义检索故障返回 `[]`，与"无结果"不可区分 | `src/Novelist.Infrastructure/App/FileSystemWorkspaceSearchService.cs:163-175` |
| **U6** | P1 | 50K 门禁证据只存在于 gitignore 的 `build/tmp/`，"必须保持"的约束无法从仓库校验 | `build/tmp/corpus-driven-writing/scale-50k-metrics.json` |
| **U7** | P2 | 向量维护循环裸 `catch` + 固定延迟 = 无声无限重试，索引可长期陈旧 | `ReferenceCorpusTechniqueVectorMaintenanceLoop.cs:~88-93` |
| **U8** | P2 | 更新检查端点默认空串，全仓无赋值，默认构建恒为 `disabled` | `src/Novelist.App/Novelist.App.csproj:8,16`、`GitHubUpdateCheckService.cs:48,58` |
| **U9** | P2 | 覆盖度阈值 `SufficientRatio = 0.5` 未标定，且"任意命中即覆盖"，直接影响用户是否继续写 | `src/Novelist.Infrastructure/App/ChapterCorpusCoverageService.cs:17,156,177,187` |
| **U10** | P2 | `WorkspaceView` 单块产物 5,294 kB（gzip 1,434 kB），冷启动无性能基线 | `frontend/dist/assets/WorkspaceView-*.js` |
| **E2** | P2 | 启动期 `novelist_tabs_all` 无形状校验，坏数据可致白屏且用户无自救手段 | `frontend/src/hooks/useEditorTabs.ts:33-52` |
| **E3** | P2 | 导入失败对用户暴露 C# 枚举原文，是新用户最可能遇到的第一个错误 | `frontend/src/components/novel/NovelImportDialog.tsx:136,175` |
| **E4** | P2 | `已拒绝`/`已驳回` 混用；4 项后端诊断被丢弃；关键词输入框不参与检索 | `frontend/src/components/reference-anchor/CorpusAreaView.tsx:109` 等 |
| **E5** | P2 | 保存失败无反馈、年份标签错误、贡献格子不可键盘访问 | `ProfileView.tsx:68,78,198`、`ContributionGrid.tsx:20,32-35,108-113` |
| **I4** | P3 | `ActivityBar` 的 `disabled` prop 与 `（即将推出）` 文案不可达 | `frontend/src/components/shell/ActivityBar.tsx:8,40,42,45,53` |
| **I6** | P3 | 3 个已注册方法未在 `api.ts` 暴露（技法向量索引检视/泵送/调度） | 见 §2.1 |
| **I7** | P3 | `DesktopBridgeComposition` 漏注册 `modelPreflight`；`content/types.ts` 死导出；`ToolCallCard` 未读必填 prop 与英文兜底 | `DesktopBridgeComposition.cs:106`、`content/types.ts:32,40,48,85`、`ToolCallCard.tsx:13,65,79,97,110` |
| **U11** | P3 | 测试项目 9 条编译警告，含 1 处 xUnit2000 实参颠倒会导致失败信息反向 | `ChapterContentServiceTests.cs:270` 等 |
| **I8** | P3 | 优先级老化 SQL 无上限，长期排队任务的加权可无界增长 | `SqliteReferenceCorpusAnalysisJobStore.Leases.cs:37-41` |

## 6. 建议的收口顺序

刻意不包含"补齐 65 个界面"。按 §2.4，收缩是有意决策，AGENTS.md 明确禁止扩张专家控制面。

**第一步：让故障可查、让数据不丢（U2、U1）。** 先注入日志（兜底 `catch` 记录异常、`Post` 改写日志出口），再给 `SaveContentPayload` 加基线令牌并让后端在不匹配时返回冲突码。顺序如此是因为 U2 的修复会让后续所有排查成本下降，且成本远低于收益；U1 是唯一会造成不可恢复用户损失的问题。

**第二步：让文档停止说谎（I5、I3）。** 把 `tasks.md:264,265` 的 `[x]` 改回未完成或明确标注"随 `ccb6d2c` 移出产品范围"，把 `development-plan.md:123` 的前端目标改为现存组件，在成熟度表旁注明哪些里程碑的能力已被移出产品。这一步不写代码但优先级很高：当前状态下任何人（包括后续的 AI 协作）读计划都会得到错误的产品认知，并可能"好心地"去恢复已被主动删除的面板。

**第三步：把测试恢复成稳定绿（U5、U11）。** `index.sqlite` 句柄争用需在测试基础设施层解决（数据库文件按用例隔离目录、`DisposeAsync` 前确保连接池释放）；租约回收超时与热检索预算需要么放宽为有依据的阈值、要么串行化执行。同时修掉 xUnit2000 与 8 处 CS8604。修完才谈得上"满足自设 P0 入口条件"。

**第四步：消除整类词表漂移（E1）。** 单一真值 + 后端白名单校验 + 未知键在开发期报错。这一项能一次性关闭"筛选没结果"类困惑，且改动范围可控。

**第五步：把证据变成资产（U6、U10、U9）。** 50K 指标产物纳入可追溯位置；建立冷打开与首个主操作的性能基线（顺带评估 `WorkspaceView` 拆分）；给 `SufficientRatio` 一个有标注数据支撑的取值。这三项都是 `development-plan.md` P2 已列但尚未落地的内容。

**第六步：完成尚缺的一手证据。** 素材库参考书管理与蓝图预演的 `test:reference-workspace` 定向验收，以及 5 人真实走查（`tasks.md:431,432`）。P2 及以下的易用性细项（E2-E5、U7-U8）可与之并行。

## 7. 验证记录

本次评审实跑的命令与结果：

| 命令 | 结果 |
|---|---|
| `dotnet build Novelist.slnx` | 退出码 0，成功，**0 错误 / 9 警告**，49.55s |
| `dotnet test Novelist.slnx --no-restore -v minimal` | 退出码 1；`Novelist.Tests` **216/216** 通过（661 ms）；`Novelist.IntegrationTests` **710 通过 / 3 失败 / 713**（2m54s） |
| `dotnet test --no-build --filter "…ReferenceCorpusAnalysisWorkerTests\|…WarmRetrievalAcrossOneThousandNodes"` | 退出码 0，**13/13 通过**，8s → 三项失败为 flaky |
| `npm --prefix frontend run verify`（build） | 退出码 0，`✓ built in 28.81s`；`WorkspaceView` 5,294.12 kB / gzip 1,434.46 kB；触发 >650 kB 警告 |
| `npm --prefix frontend run verify`（lint） | 退出码 0 |
| `npm --prefix frontend run verify`（test:node） | **23/23 通过**，0 失败，834 ms |
| 桥接账本三方比对 | 后端注册 **191**、`api.ts` 暴露 **188**、UI 有调用点 **123**、死表面 **65**、已注册未暴露 **3** |
| `diff` 前端表面 vs `BridgeCompatibilityAppMethods` | 无差异（188 名逐字节相同） |
| `git show --stat ccb6d2c` | 37 文件，**+1167 / -18421** |

统计口径说明，供后续复核者复用：

- 后端方法数必须同时匹配 `Register("Name", …)` 与 `Register<TIn,TOut>(dispatcher, "Name", …)` 两种写法，只匹配前者会漏 11 个（`ReferenceCorpusGovernanceBridgeHandlers.cs`）而得到 180。
- `api.ts` 不用 `export function`，而是 `export interface NovelistAppApi` + `export const appApi`（`:269`），因此按接口成员统计。
- "有 UI 调用点"限定在 `frontend/src/components`、`src/views`、`src/hooks` 内的固定串匹配；`src/pages` 不存在，若误传该路径会让 grep 退出码 2 并污染判定。
- 9 个名字（如 `CreateStyleSample`、`SearchStyleSamples`、`GetNovelImportRun`）仅作为**类型名子串**出现在 `frontend/src/lib/novelist/types.ts`（`CreateStyleSampleInput` 等），不是调用点，故计入死表面；`IsInitialized` 全仓零引用。
- `mock-bridge.mjs` 的方法条数不可靠（6617 行文件内的粗匹配会把嵌套对象键计入），本文只引用其行数。

## 8. 本文未覆盖范围

明确声明边界，避免被当作比实际更强的结论：

- **未跑浏览器套件**：`test:app`、`test:app:usability`、`test:phase16`、`test:reference-workspace` 本次未执行。§4.5 的易用性判断基于代码结构，不含运行时交互证据。
- **未做真实用户走查**：`tasks.md:432` 要求的 5 人研究仍然开放，本文无法替代。
- **未验证真实长篇质量**：检索相关性、蓝图区分度、拼装自然度等效果指标需要人工标注集，`development-plan.md` P2/P3 已列，尚未建立。
- **未复跑 50K 与 2M 门禁**：50K 状态引自 gitignore 的历史产物（generated_at 2026-07-10），2M 唯一记录是历史失败。
- **部分细项引自前序审计未本轮复核**：I8（老化 SQL）、E4/E5 中的部分行号、`content/types.ts` 与 `ToolCallCard.tsx` 的死代码为前序子代理审计结论，本轮未逐条重读，已在 §5 标为 P2/P3。




