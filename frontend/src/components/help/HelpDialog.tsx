import { useState } from 'react'
import { BookOpen, Wrench, Bot, Wand2, Cpu, Zap, ShieldCheck } from 'lucide-react'

type Tab = 'quickstart' | 'tools' | 'subagents' | 'skills' | 'llm' | 'context' | 'approval'

interface Props {
  open: boolean
  onClose: () => void
}

// ── 工具参考（硬编码用户描述） ──────────────────────────

interface ToolEntry {
  name: string
  desc: string
}

const toolGroups: { label: string; tools: ToolEntry[] }[] = [
  {
    label: '小说管理',
    tools: [
      { name: 'get_chapter_list', desc: '浏览小说的章节列表，按章节号排序，支持翻页。' },
      { name: 'read', desc: '读取小说相关文件的内容，包括章节正文、大纲、故事状态和技能文件。' },
      { name: 'get_characters', desc: '查看小说中所有角色的列表，支持按名称搜索。' },
      { name: 'create_character', desc: '在小说中创建新角色，设定姓名、外貌、性格等属性。' },
      { name: 'update_character', desc: '修改已有角色的信息，如更新状态、补充背景故事。' },
      { name: 'get_locations', desc: '查看小说中所有地点的列表，支持列表、详情和关系网络三种模式。' },
      { name: 'create_location', desc: '在小说中创建新地点，填写名称、描述、类型等信息。' },
      { name: 'update_location', desc: '修改已有地点的信息。' },
      { name: 'delete_record', desc: '删除指定记录（角色、地点、时间线条目等），删除前自动检查关联数据。' },
    ],
  },
  {
    label: '记忆检索',
    tools: [
      { name: 'get_preferences', desc: '查看当前的创作偏好设置，包括写作风格、叙事规则等。' },
      { name: 'get_character_relations', desc: '查看角色之间的关系图谱，了解角色间的互动和联系。' },
      { name: 'get_timeline', desc: '查看小说的时间线，包括伏笔、事件条目和章节计划。' },
      { name: 'get_story_arcs', desc: '查看故事弧线的整体结构，了解各情节线的进展状态。' },
      { name: 'get_reader_perspective', desc: '查看读者认知状态，追踪读者在不同阶段知道什么、疑惑什么。' },
      { name: 'search_story_memory', desc: '使用语义搜索在小说内容中查找相关信息，支持自然语言描述查询。' },
    ],
  },
  {
    label: '写作辅助',
    tools: [
      { name: 'create_preference', desc: '添加新的创作偏好或写作规则。' },
      { name: 'update_preference', desc: '修改已有的创作偏好。' },
      { name: 'update_character_relationship', desc: '编辑或更新角色之间的关系（朋友、敌人、恋人之类）。' },
      { name: 'create_location_relation', desc: '创建地点之间的空间关系（相邻、包含等）。' },
      { name: 'update_location_relation', desc: '修改已有的地点关系。' },
      { name: 'create_timeline_entry', desc: '创建新的伏笔或时间线条目，用于规划情节发展。' },
      { name: 'update_timeline_entry', desc: '修改已有的伏笔或时间线条目。' },
      { name: 'update_chapter_plan', desc: '更新章节的三层计划槽位（next / near / far），规划后续写作方向。' },
      { name: 'create_story_arc', desc: '创建新的故事弧线，用于追踪一条完整的情节线。' },
      { name: 'update_story_arc', desc: '修改故事弧线的信息和状态。' },
      { name: 'create_arc_node', desc: '向故事弧线中添加节点，标记关键剧情转折点。' },
      { name: 'update_arc_node', desc: '修改故事弧线中某个节点的信息。' },
      { name: 'create_reader_perspective_entry', desc: '创建新的读者认知条目，定义读者在特定时刻的所知所感。' },
      { name: 'update_reader_perspective_entry', desc: '修改已有的读者认知条目。' },
      { name: 'edit', desc: '编辑小说文件（章节、大纲、故事状态、技能），支持全文替换、查找替换和行范围替换三种模式。' },
      { name: 'run_subagent', desc: '启动专项子代理执行复杂任务（记忆检索或章节审稿），子代理独立运行后返回报告。' },
      { name: 'web_search', desc: '联网搜索真实信息，获取实时数据、新闻或参考资料用于写作。' },
      { name: 'web_fetch', desc: '抓取指定网页的正文内容，返回清洗后的纯净文本供参考。' },
    ],
  },
]

// ── 子代理介绍 ──────────────────────────────────────────

const subAgentCards = [
  {
    type: 'memory',
    name: '记忆检索分析员',
    desc: '只读子代理，能在大量故事数据中并行搜索，将分散的角色、地点、时间线、伏笔等信息整合为连贯的报告。适合需要回溯大量设定、查找隐藏约束或跨章节信息关联的场景。',
    example: '例如：让 AI "查一下第三至五章中所有与某某角色相关的伏笔是否都已回收"。',
  },
  {
    type: 'review',
    name: '章节审稿人',
    desc: '只读子代理，对指定章节进行多维度质量审查，逐项检查角色一致性、情节逻辑、伏笔管理、读者认知和故事弧线推进，输出结构化的审稿报告。',
    example: '例如：让 AI "审一下第八章，看看主角的性格表现是否与前几章一致"。',
  },
]

// ── Tab 定义 ─────────────────────────────────────────────

const tabs: { id: Tab; label: string; icon: React.ReactNode }[] = [
  { id: 'quickstart', label: '快速入门', icon: <BookOpen className="w-4 h-4" /> },
  { id: 'tools', label: '工具参考', icon: <Wrench className="w-4 h-4" /> },
  { id: 'subagents', label: '子代理', icon: <Bot className="w-4 h-4" /> },
  { id: 'skills', label: '技能系统', icon: <Wand2 className="w-4 h-4" /> },
  { id: 'llm', label: '模型配置', icon: <Cpu className="w-4 h-4" /> },
  { id: 'context', label: '上下文与缓存', icon: <Zap className="w-4 h-4" /> },
  { id: 'approval', label: '审批模式', icon: <ShieldCheck className="w-4 h-4" /> },
]

export default function HelpDialog({ open, onClose }: Props) {
  const [activeTab, setActiveTab] = useState<Tab>('quickstart')

  if (!open) return null

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="absolute inset-0 bg-black/40" onClick={onClose} />

      <div className="relative bg-background rounded-xl shadow-2xl border flex w-[960px] h-[680px] max-w-[95vw] max-h-[90vh]">
        {/* 左侧导航 */}
        <nav className="w-[160px] border-r py-4 px-2 flex flex-col gap-1 shrink-0">
          <div className="text-sm font-medium px-3 pb-3 text-foreground">帮助</div>
          {tabs.map(tab => (
            <button
              key={tab.id}
              onClick={() => setActiveTab(tab.id)}
              className={`flex items-center gap-2 px-3 py-2 rounded-lg text-sm transition-colors w-full text-left ${
                activeTab === tab.id
                  ? 'bg-primary/10 text-primary font-medium'
                  : 'text-muted-foreground hover:text-foreground hover:bg-muted/50'
              }`}
            >
              {tab.icon}
              {tab.label}
            </button>
          ))}
        </nav>

        {/* 右侧内容区 */}
        <div className="flex-1 flex flex-col min-w-0 overflow-hidden">
          {/* 关闭按钮 */}
          <button
            onClick={onClose}
            className="absolute top-3 right-3 w-7 h-7 flex items-center justify-center rounded-md text-muted-foreground hover:text-foreground hover:bg-muted transition-colors z-10"
          >
            ✕
          </button>

          <div className="flex-1 overflow-y-auto p-6">
            {activeTab === 'quickstart' && <QuickStartTab />}
            {activeTab === 'tools' && <ToolsTab />}
            {activeTab === 'subagents' && <SubAgentsTab />}
            {activeTab === 'skills' && <SkillsTab />}
            {activeTab === 'llm' && <LLMConfigTab />}
            {activeTab === 'context' && <ContextCacheTab />}
            {activeTab === 'approval' && <ApprovalTab />}
          </div>
        </div>
      </div>
    </div>
  )
}

// ── 快速入门 ─────────────────────────────────────────────

function QuickStartTab() {
  return (
    <div className="space-y-6 max-w-none">
      <section>
        <h2 className="text-lg font-semibold mb-2">欢迎使用 Goink</h2>
        <p className="text-muted-foreground leading-relaxed">
          Goink 是一款桌面端 AI 小说写作助手。它不只是聊天机器人——它理解你的小说世界，
          能管理角色、地点、时间线、故事弧线等创作要素，并通过 AI 对话辅助你写作、审稿和构思。
        </p>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">界面概览</h3>
        <div className="space-y-3 text-sm text-muted-foreground leading-relaxed">
          <div>
            <span className="text-foreground font-medium">左侧活动栏</span>
            —— 切换不同功能面板：搜索、小说、章节、偏好、角色、地点、故事弧线、时间线、读者视角、技能。
            点击图标即可进入对应面板。
          </div>
          <div>
            <span className="text-foreground font-medium">中间内容区</span>
            —— 显示当前面板的主要内容，如章节列表、角色关系图、地点网络图等。
          </div>
          <div>
            <span className="text-foreground font-medium">右侧聊天面板</span>
            —— 与 AI 对话的核心区域。你可以在这里让 AI 帮你写作、查资料、审稿、管理创作要素。
            AI 会自动调用合适的工具来完成你的需求。
          </div>
          <div>
            <span className="text-foreground font-medium">底部状态栏</span>
            —— 显示当前操作状态和文件编辑信息。
          </div>
        </div>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">基本工作流</h3>
        <div className="space-y-2 text-sm text-muted-foreground leading-relaxed">
          <p>1. <span className="text-foreground font-medium">创建小说</span> —— 在小说面板中创建你的第一部小说。</p>
          <p>2. <span className="text-foreground font-medium">告诉 AI 你想写什么</span> —— 在右侧聊天面板中描述你的想法，AI 会根据需要自动创建角色、地点、章节等内容。</p>
          <p>3. <span className="text-foreground font-medium">享受与 AI 协作的时刻</span> —— AI 帮你写作、审稿、管理伏笔和故事弧线，你在左侧面板中随时查看和调整一切。</p>
        </div>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">核心概念</h3>
        <div className="space-y-3 text-sm text-muted-foreground leading-relaxed">
          <p><span className="text-foreground font-medium">工具（Tools）</span> —— AI 可以调用的一系列操作能力，如读取章节、创建角色、搜索记忆等。你可以在「工具参考」标签页查看完整列表。</p>
          <p><span className="text-foreground font-medium">子代理（Sub-agents）</span> —— 专门执行特定任务的独立 AI，如记忆检索和章节审稿。它们拥有独立的上下文窗口，不会干扰主对话。</p>
          <p><span className="text-foreground font-medium">技能（Skills）</span> —— 预定义的写作技法和流程模板，AI 可以根据需要调用。你也可以创建自己的技能。</p>
          <p><span className="text-foreground font-medium">审批模式</span> —— 聊天面板底部可切换自动/手动审批。手动模式下 AI 的编辑和删除操作需要你确认后才会执行。</p>
        </div>
      </section>
    </div>
  )
}

// ── 工具参考 ─────────────────────────────────────────────

function ToolsTab() {
  return (
    <div className="space-y-6">
      <p className="text-sm text-muted-foreground">
        以下是 AI 可调用的全部工具，按功能领域分组。当你在聊天中向 AI 提出需求时，AI 会自动选择合适的工具来完成任务。
      </p>
      {toolGroups.map(group => (
        <section key={group.label}>
          <h3 className="text-base font-semibold mb-3">{group.label}</h3>
          <div className="space-y-2">
            {group.tools.map(tool => (
              <div key={tool.name} className="rounded-lg border bg-card px-4 py-3">
                <code className="text-sm font-medium text-primary">{tool.name}</code>
                <p className="text-sm text-muted-foreground mt-1">{tool.desc}</p>
              </div>
            ))}
          </div>
        </section>
      ))}
    </div>
  )
}

// ── 子代理 ───────────────────────────────────────────────

function SubAgentsTab() {
  return (
    <div className="space-y-6 max-w-none">
      <section>
        <h2 className="text-lg font-semibold mb-2">什么是子代理</h2>
        <p className="text-muted-foreground leading-relaxed">
          子代理是拥有独立上下文窗口和专属工具集的 AI 分身。它们专注于执行特定类型的复杂任务，
          运行结束后将结果报告返回给主对话。主对话上下文不会被子代理的中间过程撑满，
          因此子代理特别适合需要大量检索和多轮分析的任务。
        </p>
      </section>

      <section>
        <h3 className="text-base font-medium mb-3">可用子代理类型</h3>
        <div className="space-y-4">
          {subAgentCards.map(sa => (
            <div key={sa.type} className="rounded-lg border bg-card p-5">
              <div className="flex items-center gap-2 mb-2">
                <Bot className="w-4 h-4 text-primary" />
                <h4 className="font-semibold">{sa.name}</h4>
                <code className="text-xs bg-muted px-1.5 py-0.5 rounded text-muted-foreground">{sa.type}</code>
              </div>
              <p className="text-sm text-muted-foreground leading-relaxed mb-2">{sa.desc}</p>
              <div className="text-sm text-muted-foreground bg-muted/50 rounded px-3 py-2">
                {sa.example}
              </div>
            </div>
          ))}
        </div>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">如何使用</h3>
        <p className="text-sm text-muted-foreground leading-relaxed">
          在聊天中直接告诉 AI 你的需求即可，AI 会自动判断是否需要启动子代理。
          例如：「帮我检查第四章有没有与前面设定的矛盾之处」「帮我整理所有关于王婆婆的伏笔」。
          你也可以明确要求 AI 用特定子代理来处理任务。
        </p>
      </section>
    </div>
  )
}

// ── 技能系统 ─────────────────────────────────────────────

function SkillsTab() {
  return (
    <div className="space-y-6 max-w-none">
      <section>
        <h2 className="text-lg font-semibold mb-2">什么是技能</h2>
        <p className="text-muted-foreground leading-relaxed">
          技能（Skill）是预定义的写作技法或工作流程模板。每个技能包含一段专门的提示词，
          当 AI 调用某个技能时，这段提示词会临时加入对话上下文，引导 AI 按照特定的方法论来完成任务。
        </p>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">技能的三层结构</h3>
        <div className="space-y-2 text-sm text-muted-foreground leading-relaxed">
          <p><span className="text-foreground font-medium">内置技能</span> —— 应用自带的基础写作技能，如头脑风暴、角色设计、对话潜台词、节奏控制等，所有小说通用。</p>
          <p><span className="text-foreground font-medium">用户级技能</span> —— 你在用户目录下创建的技能，适用于你的所有小说。命名相同时会覆盖同名的内置技能。</p>
          <p><span className="text-foreground font-medium">小说级技能</span> —— 仅在当前小说中生效的技能，优先级最高。适合为特定项目定制的专属技法。</p>
        </div>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">如何使用技能</h3>
        <p className="text-sm text-muted-foreground leading-relaxed">
          在聊天中直接描述你的需求，AI 会自动判断是否需要调用技能。
          你也可以明确让 AI 使用某个技能，例如「用头脑风暴技能帮我想几个开场方式」。
          当前可用的技能列表可以在左侧活动栏的「技能」面板中查看。
        </p>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">如何创建自定义技能</h3>
        <div className="text-sm text-muted-foreground leading-relaxed space-y-2">
          <p>技能是 Markdown 格式的文件，包含 YAML 头部和正文。创建一个技能只需要：</p>
          <div className="bg-muted/50 rounded-lg p-4 font-mono text-xs leading-relaxed">
            <p className="text-foreground/60">---</p>
            <p>name: <span className="text-foreground">我的写作技法</span></p>
            <p>description: <span className="text-foreground">简要描述这个技能的用途，以及何时适合调用</span></p>
            <p>category: <span className="text-foreground">写作技法</span></p>
            <p className="text-foreground/60">---</p>
            <p className="mt-2 text-foreground/60"># 正文（给 AI 的提示词）</p>
            <p className="mt-1 text-foreground/80">这里是详细的指导内容，告诉 AI 在执行这个技能时应该遵循什么步骤、采用什么风格、注意什么要点……</p>
          </div>
          <p className="mt-3">
            创建好后，将文件放入用户技能目录（在左侧「技能」面板中可以看到路径），或通过 AI 的 edit 工具直接创建。
            技能面板会自动检测新文件并加载。
          </p>
        </div>
      </section>
    </div>
  )
}

// ── 模型配置 ─────────────────────────────────────────────

function LLMConfigTab() {
  return (
    <div className="space-y-6 max-w-none">
      <section>
        <h2 className="text-lg font-semibold mb-2">为什么需要配置模型</h2>
        <p className="text-muted-foreground leading-relaxed">
          Goink 本身不提供 AI 模型，你需要接入第三方大模型服务才能使用 AI 功能。
          配置过程就是告诉 Goink：用哪个服务商的哪个模型、API Key 是什么。
          配置入口在 <span className="text-foreground font-medium">设置 → 模型配置</span>。
        </p>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">两种服务商类型</h3>
        <div className="space-y-3 text-sm text-muted-foreground leading-relaxed">
          <div className="rounded-lg border bg-card px-4 py-3">
            <h4 className="text-foreground font-medium mb-1">内置服务商</h4>
            <p>
              应用预置了 5 个主流服务商：<span className="text-foreground">DeepSeek</span>、<span className="text-foreground">GLM（智谱）</span>、<span className="text-foreground">MiniMax</span>、<span className="text-foreground">MiMo（小米）</span>、<span className="text-foreground">Kimi（月之暗面）</span>。
              每个内置服务商自带官方模型列表，你只需要填入 API Key 即可使用。API Key 需要在对应服务商的官网注册获取。
            </p>
          </div>
          <div className="rounded-lg border bg-card px-4 py-3">
            <h4 className="text-foreground font-medium mb-1">自定义服务商</h4>
            <p>
              如果你的模型服务兼容 OpenAI API 格式（例如本地部署的 Ollama、vLLM，或其他第三方代理），
              可以添加自定义服务商。你需要提供名称、Chat API 地址和 API Key，然后手动添加或自动发现模型。
            </p>
          </div>
        </div>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">模型管理</h3>
        <div className="space-y-3 text-sm text-muted-foreground leading-relaxed">
          <p>每个服务商可以配置多个模型，有两种方式添加：</p>
          <div className="space-y-2">
            <p><span className="text-foreground font-medium">自动发现</span> —— 点击「自动发现」按钮，Goink 会调用服务商的 /models 接口，列出所有可用模型。勾选需要的模型导入即可。并非所有服务商都支持此功能。</p>
            <p><span className="text-foreground font-medium">手动添加</span> —— 点击「+ 添加」按钮，手动填写模型 ID、名称、上下文窗口大小、最大输出长度等信息。</p>
          </div>
          <p className="mt-3">模型参数说明：</p>
          <div className="space-y-1">
            <p><span className="text-foreground">上下文窗口</span> —— 模型一次能处理的最大 token 数，决定 AI 能「记住」多长的对话和资料。</p>
            <p><span className="text-foreground">最大输出</span> —— 模型单次回复的最大 token 数。</p>
            <p><span className="text-foreground">支持深度思考</span> —— 开启后模型会在回复前进行内部推理，适合复杂创作任务。部分模型还支持选择推理程度（low / high / max）。</p>
            <p><span className="text-foreground">支持视觉</span> —— 开启后模型可以理解和分析图片。</p>
          </div>
        </div>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">创意度（Temperature）</h3>
        <p className="text-sm text-muted-foreground leading-relaxed">
          控制模型输出的随机性和创造性。范围 0 ~ 2，值越高输出越有创意和不可预测，
          值越低输出越确定和保守。写作创作场景通常建议 0.7 ~ 1.0，需要严格遵循设定时可适当降低。
          注意：Kimi 服务商的 temperature 由模型内部固定，配置值不生效。
        </p>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">测试与保存</h3>
        <div className="space-y-2 text-sm text-muted-foreground leading-relaxed">
          <p>1. 填写 API Key 和模型后，点击「测试」按钮验证连接是否正常。测试会发送一个最小请求，不会消耗额度。</p>
          <p>2. 测试通过后点击「保存配置」，所有配置（含 API Key）会被加密存储到本地磁盘。</p>
          <p>3. 保存后即可在聊天面板顶部的模型选择器中看到已配置的模型，选择即可使用。</p>
        </div>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">常见问题</h3>
        <div className="space-y-3 text-sm text-muted-foreground leading-relaxed">
          <div>
            <p className="text-foreground font-medium">测试连接失败？</p>
            <p>检查 API Key 是否正确、网络是否可达。如果服务商不支持自动发现，请尝试手动添加模型。</p>
          </div>
          <div>
            <p className="text-foreground font-medium">API Key 安全吗？</p>
            <p>API Key 以 AES-256 加密存储在本地磁盘（<code className="text-xs bg-muted px-1 rounded">~/Goink/llm_config.enc</code>）。注意这只防磁盘文件扫描，不防设备被物理破解。</p>
          </div>
          <div>
            <p className="text-foreground font-medium">可以配置多个服务商吗？</p>
            <p>可以。所有填了 API Key 的服务商都会保存。在聊天时通过模型选择器切换即可。</p>
          </div>
        </div>
      </section>
    </div>
  )
}

// ── 上下文与缓存 ─────────────────────────────────────────

function ContextCacheTab() {
  return (
    <div className="space-y-6 max-w-none">
      <section>
        <h2 className="text-lg font-semibold mb-2">什么是上下文和缓存</h2>
        <p className="text-muted-foreground leading-relaxed">
          每次与 AI 对话时，Goink 会把对话历史、系统提示词（System1/2/3）打包成「上下文」发送给模型。
          主流大模型服务商会缓存对话前缀——如果连续多轮对话的前缀不变，模型可以直接复用之前的计算结果（KV 缓存），
          跳过重复处理，从而降低费用和延迟。这就是「缓存命中」。
        </p>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">缓存命中率</h3>
        <p className="text-sm text-muted-foreground leading-relaxed">
          聊天面板底部的状态环会显示<span className="text-foreground font-medium">缓存命中率</span>。
          高命中率（接近 100%）意味着大部分上下文被缓存，费用更低、响应更快。
          低命中率意味着模型需要重新计算大量内容，费用会上升。
        </p>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">什么会导致缓存命中率下降</h3>
        <div className="space-y-3 text-sm text-muted-foreground leading-relaxed">
          <div className="rounded-lg border bg-card px-4 py-3">
            <h4 className="text-foreground font-medium mb-1">切换模型</h4>
            <p>
              在对话中途切换到不同服务商的模型，缓存会从头开始计算。因为缓存是服务商侧的，
              换到新服务商意味着全新的缓存周期，命中率会暂时降至 0%。
              即使是同一服务商的不同模型，某些服务商也可能清空缓存。
            </p>
          </div>
          <div className="rounded-lg border bg-card px-4 py-3">
            <h4 className="text-foreground font-medium mb-1">上下文压缩</h4>
            <p>
              压缩会重建对话前缀（生成新摘要、重建 System1/2/3），开始新的缓存周期。
              虽然短期内命中率下降，但能控制 token 消耗，是必要的取舍。
            </p>
          </div>
          <div className="rounded-lg border bg-card px-4 py-3">
            <h4 className="text-foreground font-medium mb-1">新建会话</h4>
            <p>
              新会话从零开始构建上下文，没有任何缓存可以复用，命中率为 0% 是正常现象。
              随着对话轮次增加，命中率会逐渐上升。
            </p>
          </div>
          <div className="rounded-lg border bg-card px-4 py-3">
            <h4 className="text-foreground font-medium mb-1">缓存时效</h4>
            <p>
              服务商的缓存不是永久的——如果一段时间不使用该会话，服务商可能会清空缓存。
              下次回来继续对话时，命中率会重新开始计算。这也是建议完成阶段性工作后
              及时压缩的原因之一（见下方实用建议）。
            </p>
          </div>
        </div>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">上下文压缩</h3>
        <p className="text-sm text-muted-foreground leading-relaxed mb-3">
          随着对话进行，历史消息越积越多，最终会触及模型的上下文窗口上限（每个模型有不同的窗口大小）。
          压缩机制会保留最近的消息，将旧消息交给 AI 生成一份摘要，然后用摘要替代旧消息，释放空间。
        </p>

        <div className="space-y-3 text-sm text-muted-foreground leading-relaxed">
          <div className="rounded-lg border bg-card px-4 py-3">
            <h4 className="text-foreground font-medium mb-1">自动压缩</h4>
            <p>
              当 token 使用量达到模型上下文窗口的 <span className="text-foreground">80%</span> 时自动触发。
              无需手动操作，系统会在下一轮对话前自动完成压缩。你会看到「正在压缩上下文」的提示。
            </p>
          </div>
          <div className="rounded-lg border bg-card px-4 py-3">
            <h4 className="text-foreground font-medium mb-1">手动压缩</h4>
            <p>
              聊天面板底部状态环上有一个压缩按钮。当你完成一个阶段的工作、
              或者上下文过于冗长时，可以手动触发压缩，让 AI 在精简后的摘要基础上继续。
              手动压缩会立即生效。
            </p>
          </div>
        </div>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">实用建议</h3>
        <div className="space-y-2 text-sm text-muted-foreground leading-relaxed">
          <p>• <span className="text-foreground font-medium">避免在对话中途频繁切换模型</span>。如果确实需要换模型，考虑开启新会话，让缓存从新服务商开始累积。</p>
          <p>• <span className="text-foreground font-medium">完成阶段性工作后，及时压缩或新开会话</span>。当你写完一章、完成一轮审稿等节点，可以手动压缩或新建会话，避免上下文越来越臃肿。</p>
          <p>• <span className="text-foreground font-medium">如果要暂离一段时间，走之前手动压缩</span>。服务商的缓存有时效性，隔段时间回来缓存可能已清空。如果当前 token 已经很多，与其回来面对高 token + 零缓存的局面，不如离开前压缩好，回来直接在新摘要基础上继续。</p>
          <p>• <span className="text-foreground font-medium">长对话定期压缩是正常的</span>，短暂的命中率下降换来的是更低的 token 消耗和更快的响应。</p>
        </div>
      </section>
    </div>
  )
}

// ── 审批模式 ─────────────────────────────────────────────

function ApprovalTab() {
  return (
    <div className="space-y-6 max-w-none">
      <section>
        <h2 className="text-lg font-semibold mb-2">两种创作姿态，自由切换</h2>
        <p className="text-muted-foreground leading-relaxed">
          Goink 同时支持「AI 自主创作」和「精细化协同审批」两种模式。你不需要二选一，
          随时在聊天面板底部切换，适应不同阶段的创作需求。
        </p>
      </section>

      <section>
        <h3 className="text-base font-medium mb-3">自动模式</h3>
        <div className="rounded-lg border bg-card px-4 py-3 text-sm text-muted-foreground leading-relaxed">
          <p>
            AI 直接执行所有操作——创建角色、编辑章节、删除记录……一切自动完成，你只需要看着。
            适合快速推进、灵感爆发的场景，或者你更习惯「事后检查」而非「事前审批」的工作节奏。
          </p>
        </div>
      </section>

      <section>
        <h3 className="text-base font-medium mb-3">手动审批模式</h3>
        <div className="rounded-lg border bg-card px-4 py-3 text-sm text-muted-foreground leading-relaxed space-y-3">
          <p>
            AI 提出的每一个写操作——编辑章节、删除记录等——
            都会先暂停，以审批卡片的形式嵌入聊天流中，等待你的决定。
          </p>
          <div className="space-y-2">
            <p><span className="text-foreground font-medium">批准</span> —— 确认执行，AI 继续工作。</p>
            <p><span className="text-foreground font-medium">拒绝</span> —— 不执行此次操作，AI 按你的反馈调整方向。</p>
            <p><span className="text-foreground font-medium">修改后批准</span> —— 在 AI 的编辑基础上手动调整内容，再确认执行。这个能力对于章节写作尤其关键——AI 出初稿，你精修后直接提交。</p>
          </div>
          <p className="text-muted-foreground/80">
            适合对设定一致性要求高、不希望 AI 擅自改动的场景，如关键章节写作、角色设定修改、删除操作等。
            这种模式下，你始终掌握最终决定权。
          </p>
        </div>
      </section>

      <section>
        <h3 className="text-base font-medium mb-2">如何切换</h3>
        <p className="text-sm text-muted-foreground leading-relaxed">
          聊天面板底部工具栏中有一个<span className="text-foreground font-medium">「自动」</span>按钮。
          点击可在手动审批和自动模式之间切换，切换立即生效——对话进行中也可以随时改变模式。
        </p>
      </section>
    </div>
  )
}
