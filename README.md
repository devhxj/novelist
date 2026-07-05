<p align="center">
  <img src="assets/logo-dark.svg#gh-dark-mode-only" alt="Novelist" />
  <img src="assets/logo-light.svg#gh-light-mode-only" alt="Novelist" />
</p>

<h1 align="center">桌面 AI 写作系统<br><sub>Agent 实时决策 × 结构化记忆 × 写完自检状态</sub></h1>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Photino.NET-Desktop-2E7D32?style=for-the-badge" alt="Photino.NET" />
  <img src="https://img.shields.io/badge/React-19-61DAFB?style=for-the-badge&logo=react&logoColor=white" alt="React 19" />
  <img src="https://img.shields.io/badge/SQLite-3-003B57?style=for-the-badge&logo=sqlite&logoColor=white" alt="SQLite" />
  <br />
  <img src="https://img.shields.io/badge/TypeScript-6.0-3178C6?style=for-the-badge&logo=typescript&logoColor=white" alt="TypeScript 6" />
  <img src="https://img.shields.io/badge/Tailwind-4.3-06B6D4?style=for-the-badge&logo=tailwindcss&logoColor=white" alt="Tailwind 4" />
  <img src="https://img.shields.io/badge/Agent_Framework-Microsoft-5E5CE6?style=for-the-badge" alt="Microsoft Agent Framework" />
  <img src="https://img.shields.io/badge/license-MIT-716B94?style=for-the-badge&logo=opensourceinitiative&logoColor=white" alt="MIT" />
</p>

---

<p align="center"><strong>用过通用 AI 写长篇小说的人都知道——写到第五章它就忘了主角叫什么。到第三十章还要手动翻前文找那句伏笔。写完一章还得自己提醒它"更新角色状态""检查弧线进度"。Novelist 不会。它是一个有结构化记忆的桌面 AI 写作系统——角色档案、伏笔状态、弧线进度、地点关系、读者认知，系统记着，Agent 自己查、自己改、自己维护。</strong></p>

## 最新更新

### 2026-07-05

更多更新记录见 [Release Notes](docs/releases/release-notes.md)。

- 参考锚定页新增默认编排入口，可填写章节目标、已知事实和禁止事实后一键启动低干预候选运行；限制到已选锚点现在只是高级选项。
- 参考锚定页现在可直接查看、继续和取消编排运行，并显示当前停止原因、审批摘要、候选 ID 与事件历史；最终正文插入仍不会自动执行。
- 创建参考锚点时可通过系统文件选择器选择本地参考源文件，仍保留手动输入路径能力。
- 选择 `.txt`、`.md` 或 `.markdown` 文件后，参考源格式会按扩展名自动匹配，减少导入时的手动切换。
- 未知授权的参考锚点现在会在材料搜索/库预览中默认截断精确原文；完整文本仍只在本地用于溯源、改写、材料绑定和审计。
- 配置 Embeddings 后，参考锚点导入和重建会为参考材料建立专用向量索引；sqlite-vec 缺失时会显示为可恢复的 embedding 失败状态，已导入材料仍可继续检索。
- 参考复用审计现在会列出 L2 轻度改写中的非槽位改动，方便判断候选是否仍在可接受范围内。
- 参考锚定流程现在会持久化接受、拒绝和人工编辑反馈记录，为后续材料排序与坏候选回归提供依据。
- 蓝图材料绑定会给已被接受过的参考材料加分，并在评分组成中显示反馈 boost。
- 配置 Embeddings 且参考锚点向量索引就绪时，参考材料搜索和蓝图材料绑定会把向量相似度纳入评分组成；向量查询失败时仍会回退到原有词面/标签排序。
- 蓝图材料匹配现在可以先返回按 beat 排序的候选而不自动选中；工作区的绑定按钮会显式选中每个 beat 的最高分候选后再进入候选草稿生成。
- Agent 参考草稿工具现在会明确提示先生成、评审、批准蓝图，再显式选中材料后生成候选并审计，减少跳步起草或直接写入正文的风险。
- Agent 现在可以启动、查看、读取事件历史和取消参考锚定编排流程；来源/事实确认、蓝图修订批准和最终正文插入仍必须由作者确认。
- 参考锚定编排的材料绑定现在会应用语料检索策略中的包含/排除锚点和授权状态过滤，默认流程不再依赖预先手动指定锚点列表。
- 参考材料检索现在可读取工作区级共享语料锚点，同时继续隔离其他小说的私有参考；使用共享材料产生的反馈仍记录在当前小说下。
- 参考锚定编排现在会记录本地运行事件历史，可查看流程为何停止、AI 提议了什么、用户确认或取消了什么，以及哪个确定性关卡产生了阻断。
- 参考锚定编排中的草稿审计失败会停在可检查的高风险决策点，保留候选和审计问题供处理，不会直接进入正文插入。
- 参考锚定编排在材料绑定缺少必需 beat 的选中材料时会停在高风险决策点，提示补充/选择参考材料或调整蓝图策略后再重跑。
- 参考锚定编排在待批准蓝图因章节计划变化而失效时会停在高风险决策点，要求重新生成/评审蓝图后再继续。
- 候选草稿生成现在允许已批准 `no_reuse_reason` 的过渡 beat 跳过材料链接，普通 beat 仍必须有当前材料链接。
- 候选草稿生成会验证每个候选都能追溯到当前选中的材料链接；未批准的 no-reuse 或材料不匹配会被拒绝。
- 候选草稿审计现在会检查 beat 的段落意图、执行模式和候选拒绝规则，纯走位候选不能冒充需要停留/迟疑的段落。
- 候选草稿审计会把 subtext、感官锚点和 source-backed detail 的必需目标纳入检查，缺少指定暗示或细节会要求修正。
- 候选草稿审计现在会检查 `delayed_reaction` / `withheld_reaction` 正文职责，缺少停顿、咽回、忍住或沉默等压住反应证据会要求修正。
- 候选草稿审计现在会拦截 close/limited 叙事距离下的镜头化或上帝视角语言，避免候选越过蓝图 POV 边界。
- 候选草稿审计现在会拦截“POV 角色没有看见/不知道某事，却把那件事告诉读者”的有限视角泄漏。
- 候选草稿审计现在会拦截 POV 角色听不见/看不见门外或窗后时，候选却直接揭示场外已发生事实的有限视角泄漏。
- 候选草稿审计现在会拦截 POV 角色背对/未回头时直接揭示门后、身后隐藏方位的有限视角泄漏。
- 候选草稿审计现在会拦截非 POV 角色“打算/准备/企图”等隐藏意图直陈，要求改成可被视角人物观察到的外部证据。
- 候选草稿审计现在会把未批准的门禁卡、通行证、工牌等准入凭证视为高风险新增事实，避免候选临时发明关键通行物。
- 候选草稿审计现在会把未批准的地址、门牌号、房间号、车牌等定位线索视为高风险新增事实，避免候选临时发明关键位置。
- 候选草稿审计现在会把未批准的银行卡号、案号、病历号等敏感编号视为高风险新增事实，避免候选临时发明关键标识。
- 候选草稿审计现在会把未批准的合同、协议、产权证明、遗嘱等权属文书视为高风险新增事实，避免候选临时发明关键文书。
- 候选草稿审计现在会把未批准的指纹、DNA 报告、毛发、纤维、脚印等法证证据视为高风险新增事实，避免候选临时发明破案线索。
- 候选草稿审计现在会把未批准的聊天记录、通话记录、转账记录、监控录像等通信或交易证据视为高风险新增事实，避免候选临时发明关键线索。
- 蓝图生成现在会先归一化章节计划、章节目标、参考锚点和已知/禁止事实组成的上下文包，再计算 `context_hash`，减少等价输入造成的误失效。
- 蓝图详情现在会暴露生成器 `build_version` 和关键哈希锚点，方便确认评审、审批和候选草稿对应的是同一份分析合同。
- 过期蓝图现在会保留为只读对比视图，并明确提示重新生成；评审、批准、修订、材料绑定和候选生成都会保持禁用。
- 蓝图生成遇到疑似最终正文段落的章节计划时，会先转成结构化蓝图职责提示，不再把正文原句塞进 beat premise。
- 重复评审未修改的蓝图会复用已有评审结果，不再产生重复记录，也不会把已批准蓝图退回待批准状态。
- 蓝图评审结果现在包含 `review_version`，审批会拒绝旧评审版本，避免评审规则升级后误用旧结果。
- 蓝图评审现在会拒绝角色状态 before/after 完全相同的 beat，避免缺少角色状态变化的蓝图继续进入材料绑定和候选草稿生成。
- 蓝图评审现在会拒绝缺少角色误信或关系压力的 beat，确保角色状态变化有明确心理盲点和关系张力支撑。
- 蓝图评审现在会检查 `character_states_before/after` 里的高风险事实，角色状态不能夹带未进入 known facts 或场景事实的关键物件、身份或证据。
- 蓝图评审现在会阻止 `character_states_before/after` 使用 forbidden facts，禁止项不能被声明成角色状态上下文。
- 蓝图评审现在会检查 `character_goals` 里的高风险事实，角色目标不能夹带未进入 known facts 或场景事实的关键物件、身份或证据。
- 蓝图评审现在会阻止 `character_goals` 使用 forbidden facts，禁止项不能被声明成角色目标。
- 蓝图评审现在会检查 `character_misbeliefs` 里的高风险事实，角色误信不能夹带未进入 known facts 或场景事实的关键物件、身份或证据。
- 蓝图评审现在会阻止 `character_misbeliefs` 使用 forbidden facts，禁止项不能被声明成角色误信。
- 蓝图评审现在会检查 `relationship_pressure` 里的高风险事实，关系压力不能夹带未进入 known facts 或场景事实的关键物件、身份或证据。
- 蓝图评审现在会阻止 `relationship_pressure` 使用 forbidden facts，禁止项不能被声明成关系压力。
- 蓝图评审现在会执行 beat 自己的 `forbidden_facts`，同一 beat 禁止揭示的事实不能再出现在场景事实、POV 知识、角色状态或执行目标里。
- 蓝图评审现在会检查 `chapter_function` 里的高风险事实并阻止 forbidden facts，章节功能不能夹带未批准的关键物件、身份或证据。
- 蓝图评审现在会检查 `logic_analysis`、`emotion_analysis`、`narration_analysis`、`character_analysis`、`reference_analysis` 和 `transition_plan` 的摘要/要点里的高风险事实并阻止 forbidden facts，分析轨道不能夹带未批准的关键物件、身份或证据。
- 蓝图评审现在会检查 `execution_contract` 的摘要、段落意图、执行模式、防剧本化职责、细节目标和候选拒绝规则里的高风险事实并阻止 forbidden facts，执行合同不能夹带未批准的关键物件、身份或证据。
- 蓝图评审现在会检查 `previous_state` 和 `final_state` 里的高风险事实并阻止 forbidden facts，章节起止状态不能夹带未批准的关键物件、身份或证据。
- 蓝图评审现在会检查 `locked_phrase_policy` 里的高风险事实并阻止 forbidden facts，锁定短语策略不能夹带未批准的关键物件、身份或证据。
- 蓝图评审现在会在材料绑定前拒绝非法 `max_rewrite_level`，改写预算必须是 L0-L4 的有效等级。
- 蓝图评审现在会在材料搜索前拒绝非法 `required_material_types` 和 `reference_query.material_types`，材料类型必须使用受支持的 chapter、paragraph、sentence 或 passage。
- 蓝图评审现在会在材料绑定前拒绝缺少 intended use 的 beat，`narrative_function` 必须写清材料链接的具体用途。
- 蓝图评审现在会在过多 beat 使用 `no_reuse_reason` 时给出软告警，提醒重新确认这些段落是否真的不需要参考材料。
- 蓝图评审现在会在所有 reference-bound beat 都请求同一种材料类型时给出软告警，提醒按段落功能区分 chapter、paragraph、sentence 或 passage。
- 蓝图评审现在会在过多 beat 复用同一种 prose duty 时给出软告警，提醒蓝图区分内心、感官、潜台词、转场、环境或来源细节职责。
- 蓝图评审现在会在相邻 beat 机械重复同一段落意图时给出软告警，提醒为每个 beat 写清不同的正文执行任务。
- 蓝图评审现在会在合法但高于默认 L1 的 `max_rewrite_level` 上给出软告警，提醒确认是否真的需要更高改写预算。
- 蓝图评审现在会在所有情绪变化都直接即时释放时给出软告警，提醒至少一个 beat 加入压抑、延迟、误导或克制反应。
- 蓝图评审现在会在压力变化的 beat 始终保持同一叙事距离时给出软告警，提醒按冲突、关系或情绪压力调整叙述远近。
- 蓝图评审现在会检查 `narrative_function` 里的高风险事实，叙事功能不能夹带未批准的关键物件、身份或证据。
- 蓝图评审现在会检查 `logic_premise` 和 `conflict_pressure` 里的高风险事实，逻辑前提和冲突压力不能夹带未批准的关键物件、身份或证据。
- 蓝图评审现在会检查 `causality_in/out` 和 `transition_in/out` 里的高风险事实，因果与转场说明不能夹带未批准的关键物件、身份或证据。
- 蓝图评审现在会检查 `paragraph_intention` 里的高风险事实，段落意图不能夹带未批准的关键物件、身份或证据。
- 蓝图评审现在会拒绝“写得更好/更有代入感”这类泛化段落意图，要求每个 beat 写清具体正文职责。
- 蓝图评审现在会拒绝“正常写/自然展开”这类泛化执行模式，要求 `execution_mode` 写成可检查的起草操作。
- 蓝图评审现在会检查 `execution_mode` 里的高风险事实，起草操作不能夹带未批准的关键物件、身份或证据。
- 蓝图评审现在会拒绝“不好的不要/质量差就拒绝”这类泛化候选拒绝规则，要求写清 action-only、POV 泄漏、缺证据等具体失败条件。
- 蓝图评审现在会检查 `candidate_rejection_rule` 里的高风险事实，候选拒绝规则不能夹带未批准的关键物件、身份或证据。
- 蓝图评审现在会拒绝“避免剧本化”这类泛化 anti-screenplay duty，要求写清具体要补足的内心、暗示、细节或非镜头化叙事工作。
- 蓝图评审现在会检查 `anti_screenplay_duty` 里的高风险事实，防剧本化职责不能夹带未批准的关键物件、身份或证据。
- 蓝图评审现在会拒绝“正常叙述/写得有画面感”这类泛化叙事策略，要求每个 beat 写清 POV 距离、感官/内心边界和叙事职责。
- 蓝图评审现在会检查 `narration_strategy` 里的高风险事实，叙事策略不能夹带未批准的关键物件、身份或证据。
- 蓝图评审现在会拒绝缺失或“节奏自然流畅/快慢结合”这类泛化节奏策略，要求 beat 写清停顿、推进、转折或释放方式。
- 蓝图评审现在会检查 `rhythm_strategy` 里的高风险事实，节奏策略不能夹带未批准的关键物件、身份或证据。
- 蓝图评审现在会拒绝“加一点细节/丰富细节”这类泛化 source-backed detail 目标，要求写清来自参考材料的具体物件、感官或环境压力。
- 蓝图评审现在会拦截 final hook 中未由已知事实、beat 场景事实或 POV 已知边界铺垫的高风险身份、证据和过去事件揭示。
- 蓝图评审现在会拦截 beat 场景事实中未由已知事实或声明槽值支持的高风险身份、证据和过去事件揭示。
- 蓝图评审现在会拦截同一 beat 中把 POV 明确禁止知道的事实放进场景事实的冲突，避免有限视角蓝图绕过 POV 边界。
- 蓝图评审现在会阻止 `viewpoint_allowed_knowledge` 使用 forbidden facts，禁止项不能被声明为 POV 已知边界。
- 蓝图评审现在会返回结构化缺陷，界面可直接显示字段路径、beat、严重度、原因和必须修复项，不需要解析整段评语。
- 蓝图审批会写入独立审批记录，冻结评审 ID、上下文/计划/分析哈希、评审版本、审批来源和审批时间，便于后续追溯。
- 候选草稿审计会继续拦截剧本化的纯对话或纯动作段落，但明确标记为 `short_exchange` 的两句短对话不会再被误判。
- 候选草稿审计现在会额外标出“他说/她转身”这类说话报幕加走位的 blocking-only 剧本化风险，要求补足内心、感官、暗示或转折。
- 候选草稿审计现在会把未批准的消音手枪、炸药包、毒剂配方、伪造处方等危险物或医疗伪造物视为 unsupported fact，避免候选段落凭空加入高风险证据。
- 有限视角审计现在会拦截“隔着门只听见雨声，却直接写门外动作”的 barrier-separated POV 泄露，要求候选段落留在视角人物可感知范围内。
- 参考材料提取现在能把“只把钥匙放回桌面”这类克制物件动作识别为情绪证据和潜台词外显证据，供 `subtext` 搜索与材料绑定使用。
- 蓝图评审现在会拦截 `slot_plan` 里的“随便替换/待定/一个东西”这类泛化槽位计划，要求改成具体的已批准对象、地点、感官线索或证据项。
- 蓝图评审现在会检查 `slot_plan` 替换值里的高风险事实，未进入 known facts、scene facts 或视角允许知识的“密室钥匙”等槽位事实不能绕过后续草稿审计。
- 蓝图评审现在会阻止 `slot_plan` 使用 forbidden facts，禁止项不能作为槽位替换值进入候选草稿。
- 蓝图评审现在也会检查 `reference_query.query` 里的高风险事实，未批准的“密室钥匙”等搜索目标不能直接进入材料绑定流程。
- 蓝图评审现在会阻止 `reference_query.query` 使用 forbidden facts，即使该事实已出现在上下文里，也不能被拿去做参考材料搜索目标。
- 蓝图评审现在会检查 `no_reuse_reason` 里的高风险事实并阻止 forbidden facts，过渡 beat 不能借“无需材料复用”绕过事实审批。
- 蓝图评审现在会检查 `source_backed_detail_target` 里的高风险事实，必写的“密室钥匙”等细节目标必须先进入已批准事实范围。
- 蓝图评审现在会阻止 `source_backed_detail_target` 使用 forbidden facts，禁止项不能成为候选草稿的必写细节。
- 蓝图评审现在也会检查 `sensory_anchor_target` 里的高风险事实，必写的感官锚点必须先进入已批准事实范围。
- 蓝图评审现在会阻止 `sensory_anchor_target` 使用 forbidden facts，禁止项不能成为候选草稿的必写感官锚点。
- 蓝图评审现在会检查 `subtext_plan` 里的高风险事实，必写的潜台词目标必须先进入已批准事实范围。
- 蓝图评审现在会阻止 `subtext_plan` 使用 forbidden facts，禁止项不能成为候选草稿的必写潜台词目标。
- 蓝图评审现在会检查 `external_evidence` 里的高风险事实，必写的外显情绪证据必须先进入已批准事实范围。
- 蓝图评审现在会阻止 `external_evidence` 使用 forbidden facts，禁止项不能成为候选草稿的必写外显情绪证据。
- 蓝图评审现在会检查 `emotion_trigger` 里的高风险事实，触发情绪变化的关键物件、身份或证据必须先进入已批准事实范围。
- 蓝图评审现在会阻止 `emotion_trigger` 使用 forbidden facts，禁止项不能作为情绪变化触发器进入候选草稿。
- 蓝图评审现在会检查 `suppressed_reaction` 里的高风险事实，被压住的反应目标必须先进入已批准事实范围。
- 蓝图评审现在会阻止 `suppressed_reaction` 使用 forbidden facts，禁止项不能成为候选草稿里的被压住反应目标。
- 候选草稿情绪审计现在会把蓝图里的情绪 after-state 也视为已批准证据，避免短段落被误判为缺少情绪机制。
- 候选草稿情绪审计现在能识别“指尖发紧”与“手指蜷紧”这类等价中文外显证据，不再只依赖逐字命中蓝图短语。
- 参考材料搜索结果现在会返回排序评分组成，让工具和后续界面能解释词面匹配、标签和置信度如何影响结果顺序。
- 参考材料搜索面板现在可直接筛选叙事职责、情绪转变、材料类型和标签，并在结果卡片上显示评分组成与人工校正状态。
- 参考材料搜索现在支持按叙事职责与情绪转变过滤，能更精确地区分外显证据、感官压力、转场等用途。
- 参考材料搜索现在会把“把杯子推远”这类物件动作识别为克制的外显潜台词证据，并可通过 `subtext` 职责命中。
- 蓝图材料绑定结果现在会显示每条材料与 beat 的匹配解释，方便判断它命中了功能、情绪、POV、正文职责还是人工反馈。
- 蓝图材料绑定现在会把 `emotion_evidence` 材料视为可满足 `external_evidence` 和 `subtext` 正文职责，外显情绪证据不会再因标签过窄被丢弃。
- 参考材料标签可由用户手动校正，校正后的 function、emotion、POV、scene 和 technique 标签会标记为用户确认并参与后续搜索和蓝图材料绑定。
- 参考材料导入现在能识别更多中文叙事标签，包括外显情绪证据、背对/未回头的有限视角、动作后拍，以及“欲言又止/指节扣紧”这类克制情绪证据。
- 重建参考锚点时会保留文本哈希未变化材料的用户校正标签，避免源文件小幅调整后丢失人工确认结果。

## 跟通用 AI 聊天有什么不同

| | 通用 AI 聊天 | Novelist |
|---|---|---|
| 创作信息 | 每次对话重新交代 | 角色/关系/伏笔/弧线/地点/读者认知 全链路结构化追踪 |
| 改正文 | 直接输出文本，改了什么不知道 | Diff 预览 + 逐行对比 + 点确认才写入 |
| 翻前文 | 手动搜索、逐章翻 | 语义搜索 + 本地索引，一句"那个吊坠"找到所有段落 |
| 写完维护 | 不管，除非你再提醒 | 写完自动触发角色更新、伏笔回收、弧线推进、读者认知刷新 |
| 写作风格 | 靠 prompt 硬写 | 8 个内置方法论 + 自定义 Skill 热重载，三层覆盖 |
| 版本历史 | 无 | 内置 Git，每次对话自动 commit，随时回退 |
| 环境依赖 | 往往要 Python/GPU | 一个安装包，打开即用 |

## AI 自己查、自己改、自己维护——不是流水线，是 Agent

31 个结构化工具，LLM 自主决策调用哪个、传什么参数、下一步干什么。不是"写完一章传给下一棒"的 pipeline——Agent 在当前对话中调工具查角色、查伏笔、读写正文、更新状态，直到任务完成。

写完一章正文后，系统自动注入维护提醒，告诉 Agent 具体检查什么：角色有没有变化、该回收的伏笔回收了没有、弧线节点需要推进吗、读者认知需要更新吗。Agent 不会"忘了维护"——它被迫逐项自查。

如果还不放心，可以启动审稿子 Agent——一个独立 Agent 从头审读章节内容与系统状态的一致性，发现问题直接写进对话，主 Agent 当场修正。

## 几十万字里找一句话：本地语义搜索

写到第五十章，要找"主角第一次见到那个吊坠是在哪一章来着？"——不用逐章翻。告诉 AI 一句话，它能在整本书里找到相关段落。

不是关键词匹配，是按意思搜索。你问"关于吊坠的线索"，它能找到那些没写"吊坠"两个字但确实在暗示吊坠存在的段落。Agent 写新章节时也可主动搜索前文，确保持续一致。

索引和检索状态保存在本机 sqlite-vec 中，向量可由标准 Embeddings API 或本地 ONNX 模型生成。在线 Embeddings API 不限制供应商和模型；本地 ONNX 使用内置固定的 `bge-small-zh-v1.5` int8 模型，在硬件足够的设备上可完全本机生成 embedding，不需要把正文发到在线向量服务；ONNX 模式严格本地执行，失败时不会悄悄回退到线上 API。写完章节会标记索引过期，重建后 Agent 可主动搜索前文，确保持续一致。

## 不只是记忆——是结构化创作状态

### 角色：关系有历史

角色档案包含性格、能力、背景。角色关系是有向图——"张三对李四是师徒但暗中提防"，"李四对张三是敬重但有所隐瞒"，两条独立记录。关系变化时旧记录保留，可回顾演变过程。

### 伏笔：不会石沉大海

每条伏笔记录目标回收章节和重要程度。快到回收点系统提醒，超时未回收标记异常。章节计划分三档——下一章、近期、远期——管理创作节奏。

### 弧线：跨章节叙事线索

弧线由节点链组成，每个节点关联目标章节。写完一章自动推进节点。一个故事通常 3–5 条并行弧线同时追踪。

### 世界观：地点是图，不是列表

追踪层级包含（王国 → 王宫 → 大殿）和空间连通（A 和 B 由山路连通）。AI 可查详情、子地点、连通关系或完整地图。

### 读者认知：控制信息释放

追踪读者已知什么、在等什么答案、误认了什么。精确控制悬念和反转时机。

### 创作偏好：说一次就够

全局偏好和单书偏好两层管理。写到第三十七章，"对话保持冷峻风格"依然生效。

## 前端可视化状态
<p align="center">
  <img src="assets/arc-demo.png" alt="故事弧线" />
</p>
<p align="center">
  <img src="assets/location-demo.png" alt="地点图谱" />
</p>
<p align="center">
  <img src="assets/preferences-demo.png" alt="创作偏好" />
</p>

## Skill 系统：3 层覆盖 × 3 种模式

Skill 是 Novelist 的创作方法论模块。每个 Skill 由一个 `.md` 文件定义，包含 YAML frontmatter 元数据和 markdown 正文。**三层覆盖 + 三种模式 = 9 种策略维度**，精确控制"什么内容、在什么范围、以什么方式生效"。

### 三层覆盖

同名 Skill 按 **小说 > 用户 > 内置** 优先级覆盖。修改即时热重载，无需重启。

| 层级 | 存储路径 | 可见范围 | 可编辑 |
|---|---|---|---|
| 内置 Builtin | 打包只读 | 所有小说 | 否 |
| 用户 User | 数据目录 `skills/`（工具路径兼容 `~/.goink/skills/`） | 所有小说 | 是 |
| 小说 Novel | `{novel}/skills/` | 当前小说 | 是 |

### 三种触发模式

| 模式 | AI 自主调用 | 用户 `/` 触发 | 会话开头注入 | 出现在目录 |
|---|---|---|---|---|
| 智能 `auto` | 是 | 是 | — | 是 |
| 指令 `manual` | — | 是 | — | — |
| 常驻 `always` | 是 | 是 | 是（注入全文） | — |

### 3×3 能力矩阵

|  | 智能 auto | 指令 manual | 常驻 always |
|---|---|---|---|
| **内置** | 场景节拍、对白潜台词、节奏控制、悬念钩子、角色设计、修改打磨、去AI味、共创构思 | review / memory / collect / next | — |
| **用户** | 跨小说可复用的创作工作流 | 个人快捷命令 | 全局生效的风格规则 |
| **小说** | 单书专属工作流 | 单书快捷命令 | 单书常驻规则 |

新建一个 `.md` 文件就是新 Skill：

```markdown
---
name: 我的写作流程
description: 个人定制创作流程
category: 自定义
mode: auto
---
# 正文 markdown 内容
```

零代码扩展。修改即时生效。删除同理。

<p align="center">
  <img src="assets/skill-demo.png" width="80%" alt="Skill 技能系统" />
</p>

## 风格蒸馏：一段文字 → 一个仿写 Skill

想写出某个作家的笔法？贴一段样文，AI 从六个维度拆解——**句式结构、用词习惯、修辞手法、节奏控制、叙事距离、氛围语调**——自动生成一个完整的仿写 Skill。不是关键词替换，是提炼风格模式。

生成的 Skill 立刻出现在列表中，`/风格名` 一键加载，后续所有对话都按此风格输出。也可以打开编辑继续微调。

<p align="center">
  <img src="assets/extract-demo.png" width="80%" alt="风格蒸馏" />
</p>

## 三重保障，维护不会遗漏

**第一层—系统提示词** • Agent 核心指令写死维护流程。"创作完成后立即进行状态维护。不是可选步骤。"

**第二层—动态注入** • AI 写完长文后系统自动注入检查项——角色变化、伏笔状态、弧线节点、读者认知。

**第三层—审稿 Agent** • 独立子 Agent 对比章节与系统状态，发现问题立即反馈。

## 你的每一次确认

AI 不会直接改正文。每次编辑系统先生成 Diff，等你批准再写入。可以当场批准、拒绝，或者给反馈让 AI 修正。也可以切换到自动模式，连续多轮自由写作。

所有修改都有 Git 历史，任何时候都可以回退到任意状态。
<p align="center">
  <img src="assets/write-demo.png" alt="写作与 Diff 审批" />
</p>
<p align="center">
  <img src="assets/outline-demo.png" alt="大纲与章节计划" />
</p>
## AI 碰不到不该碰的文件

双层沙箱安全隔离——正则白名单只允许 `chapters/`、`outlines/`、`goink.md` 等合法路径，SafePath 杜绝路径穿越。文件编辑写入前重读对比，防止覆盖你的手动修改。

## 安装

从 [Releases](https://github.com/devhxj/goink/releases) 下载对应平台安装包：

- **Windows** — 运行安装程序
- **macOS** — 打开 DMG，拖入 Applications
- **Linux** — 运行 AppImage

需要 LLM API Key（内置 DeepSeek、GLM、MiMo 模板，兼容 OpenAI 格式）。语义检索可使用任意兼容的在线 Embeddings API，也可在设置中切换到内置 ONNX；本地模式固定使用随包 `bge-small-zh-v1.5` int8 模型。安装包自带桌面宿主、前端资源和 Git 运行时，不需要 Python、Node.js 或外部数据库。Windows SmartScreen 可能弹出提示（未签名），点击"更多信息"→"仍要运行"即可。

### 从源码构建

```bash
sudo apt install libgtk-3-0 libwebkit2gtk-4.1-0 curl file unzip
git clone https://github.com/devhxj/goink
cd goink
dotnet restore Novelist.slnx
npm --prefix frontend ci
make deps
make build   # 生产构建
make dev     # Photino 桌面开发模式
```

`make dev` 不会自动构建前端；本地桌面模式需要先运行 `npm --prefix frontend run build` 生成 `frontend/dist`。调试纯前端时可用 `make frontend-dev` 启动 Vite，再用 `--start-url=http://localhost:5173/` 的桌面启动配置加载 Vite 页面。

## 技术栈

| 层 | 选型 |
|---|---|
| Agent 引擎 | Microsoft Agent Framework + OpenAI-compatible streaming + 结构化工具 + 子 Agent 嵌套 |
| 桌面框架 | Photino.NET + .NET 10 |
| 编辑器 | Monaco Editor |
| 数据库 | 文件系统 JSON 存储 + SQLite/sqlite-vec RAG 索引 |
| 向量搜索 | 标准 Embeddings API / 本地 ONNX + sqlite-vec |
| 版本控制 | 内置 Git（自动 commit / Diff / Revert） |
| 安全 | 正则白名单 + SafePath 双层沙箱 + 审批流 |
| 前端 | React 19 + TypeScript + Tailwind CSS 4 + shadcn/ui |

## License

MIT
