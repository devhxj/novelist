import {
  BookOutlined, UnorderedListOutlined,
  ReadOutlined, BarChartOutlined, SettingOutlined, EditOutlined,
  TeamOutlined, UserOutlined, PlusCircleOutlined, ThunderboltOutlined,
  SearchOutlined, HistoryOutlined, CompassOutlined,
  CheckSquareOutlined, BugOutlined, AlertOutlined, ExperimentOutlined,
  RobotOutlined,
  PushpinOutlined, AimOutlined, BulbOutlined,
  ClockCircleOutlined, ApartmentOutlined, NodeIndexOutlined,
  ClusterOutlined, ApiOutlined,
} from '@ant-design/icons'

export interface ToolDisplayInfo {
  displayName: string
  displayDescription: string
  icon: React.ReactNode
  color: string
  userAction: string
}

const toolDisplayMap: Record<string, ToolDisplayInfo> = {

  get_novel_summary: {
    displayName: '查看小说概况',
    displayDescription: '获取小说的基本信息，包括标题、类型、简介、章节数、总字数、角色数量等整体数据',
    icon: <BookOutlined />,
    color: '#1890ff',
    userAction: '正在读取小说的基本档案信息…',
  },

  get_chapter_list: {
    displayName: '查看章节目录',
    displayDescription: '浏览小说的所有章节列表，查看每章的标题、字数、完成状态和摘要',
    icon: <UnorderedListOutlined />,
    color: '#1890ff',
    userAction: '正在翻阅章节目录…',
  },

  get_chapter_content: {
    displayName: '读取章节正文',
    displayDescription: '读取指定章节的完整内容，用于了解前文情节或引用已有内容',
    icon: <ReadOutlined />,
    color: '#1890ff',
    userAction: '正在阅读某章的完整内容…',
  },

  get_novel_progress: {
    displayName: '查看写作进度',
    displayDescription: '查看小说的整体写作进度，包括已完成章节数、总字数、平均每章字数、最新章节等统计',
    icon: <BarChartOutlined />,
    color: '#52c41a',
    userAction: '正在统计写作进度…',
  },

  get_creative_profile: {
    displayName: '查看创作规则',
    displayDescription: '查看作者设定的创作偏好和规则，包括全局写作习惯、本书专属风格、必须保留/避免的内容等',
    icon: <SettingOutlined />,
    color: '#722ed1',
    userAction: '正在查阅创作规则手册…',
  },

  update_creative_profile: {
    displayName: '设置创作规则',
    displayDescription: '更新或添加创作偏好设置，如写作风格、文风要求、长期目标、禁忌事项等',
    icon: <EditOutlined />,
    color: '#722ed1',
    userAction: '正在更新创作规则…',
  },

  get_character_list: {
    displayName: '查看角色列表',
    displayDescription: '查看小说中所有角色的基本信息，包括姓名、性格特点、能力、关系概要',
    icon: <TeamOutlined />,
    color: '#13c2c2',
    userAction: '正在查阅角色阵容…',
  },

  get_character_detail: {
    displayName: '查看角色档案',
    displayDescription: '深入查看某个角色的详细档案，包括完整的性格设定、能力列表、人物关系网络',
    icon: <UserOutlined />,
    color: '#13c2c2',
    userAction: '正在翻阅某位角色的详细档案…',
  },

  create_character: {
    displayName: '创建新角色',
    displayDescription: '为小说添加一个新角色，设定姓名、性格、能力等基础信息',
    icon: <PlusCircleOutlined />,
    color: '#13c2c2',
    userAction: '正在创建新角色…',
  },

  update_character: {
    displayName: '更新角色设定',
    displayDescription: '修改已有角色的姓名、性格、能力等属性信息',
    icon: <EditOutlined />,
    color: '#13c2c2',
    userAction: '正在更新角色设定…',
  },

  get_writing_characters: {
    displayName: '查看写作角色概览',
    displayDescription: '一步获取所有关键角色信息：名单+性格标签+关系网络+最近动态，写作前最推荐的工具',
    icon: <ClusterOutlined />,
    color: '#13c2c2',
    userAction: '正在整理角色阵容和关系…',
  },

  get_character_network: {
    displayName: '查看人物关系图',
    displayDescription: '获取整本小说的人物关系全景图（网络结构），了解所有角色间的关系格局',
    icon: <ApartmentOutlined />,
    color: '#13c2c2',
    userAction: '正在绘制人物关系网络图…',
  },

  get_character_relationships: {
    displayName: '查看角色关系详情',
    displayDescription: '查看某个特定角色与所有人的详细关系（盟友/敌人/恋人/家人等），含强度和状态',
    icon: <NodeIndexOutlined />,
    color: '#13c2c2',
    userAction: '正在查阅某位角色的关系网…',
  },

  update_character_relationship: {
    displayName: '更新人物关系',
    displayDescription: '创建新的人物关系或更新/演变已有关系（如敌变友、建立联盟、解除婚约等）',
    icon: <ApiOutlined />,
    color: '#13c2c2',
    userAction: '正在更新人物关系记录…',
  },

  create_new_chapter: {
    displayName: '创建新章节',
    displayDescription: '在小说中新建一个空白章节，系统会自动分配下一章的序号',
    icon: <PlusCircleOutlined />,
    color: '#1890ff',
    userAction: '正在创建新章节…',
  },

  edit_chapter: {
    displayName: '编辑章节',
    displayDescription: '编辑指定章节的内容，支持全量替换、局部修改、搜索替换等多种模式',
    icon: <ThunderboltOutlined />,
    color: '#fa8c16',
    userAction: '正在编辑章节内容…',
  },

  search_plot_memory: {
    displayName: '搜索情节内容',
    displayDescription: '用自然语言语义搜索小说中已写过的相关情节片段，找到与查询最匹配的内容',
    icon: <SearchOutlined />,
    color: '#eb2f96',
    userAction: '正在搜索相关情节片段…',
  },

  get_character_memory: {
    displayName: '回顾角色经历',
    displayDescription: '查看某个角色在故事中的所有出场记录和参与的情节事件，了解他/她经历了什么',
    icon: <HistoryOutlined />,
    color: '#eb2f96',
    userAction: '正在回忆这位角色的经历…',
  },

  get_recent_context: {
    displayName: '获取写作上下文',
    displayDescription: '获取当前章节附近的写作参考信息，包括前几章的摘要、出场角色、待处理的伏笔等',
    icon: <CompassOutlined />,
    color: '#eb2f96',
    userAction: '正在准备写作所需的背景信息…',
  },

  check_character_consistency: {
    displayName: '检查角色一致性',
    displayDescription: '检查角色的性格、能力、言行是否前后一致，发现人设崩塌或矛盾的地方',
    icon: <CheckSquareOutlined />,
    color: '#ff4d4f',
    userAction: '正在核查角色是否前后一致…',
  },

  check_plot_consistency: {
    displayName: '检查情节逻辑',
    displayDescription: '检查情节发展的逻辑性和因果关系是否合理，发现剧情漏洞或矛盾',
    icon: <BugOutlined />,
    color: '#ff4d4f',
    userAction: '正在排查情节逻辑问题…',
  },

  list_unresolved_plots: {
    displayName: '查看未回收伏笔',
    displayDescription: '列出所有埋下但尚未回收的伏笔（挖坑未填），按重要程度排序',
    icon: <AlertOutlined />,
    color: '#faad14',
    userAction: '正在清点还没填的坑…',
  },

  run_full_consistency_check: {
    displayName: '全面体检',
    displayDescription: '执行全方位的一致性检查，包括角色、情节、时间线、伏笔状态等多个维度',
    icon: <ExperimentOutlined />,
    color: '#ff4d4f',
    userAction: '正在进行全面体检（角色+情节+时间线+伏笔）…',
  },

  get_story_timeline: {
    displayName: '查看故事追踪板',
    displayDescription: '查看统一的故事时间线，包含所有伏笔、章节规划、用户指令等条目，可按分类/状态筛选',
    icon: <ClockCircleOutlined />,
    color: '#fa8c16',
    userAction: '正在打开故事追踪板…',
  },

  add_timeline_entry: {
    displayName: '记录追踪条目',
    displayDescription: '在故事时间线中新增一条记录，可以是伏笔、章节规划、用户指令或情节节点',
    icon: <PushpinOutlined />,
    color: '#fa8c16',
    userAction: '正在记录一条新的伏笔或规划…',
  },

  update_timeline_entry: {
    displayName: '更新追踪条目',
    displayDescription: '修改已有追踪条目的内容，如更新描述、调整目标章节、修改优先级等',
    icon: <EditOutlined />,
    color: '#fa8c16',
    userAction: '正在更新某条记录的内容…',
  },


  get_timeline_context: {
    displayName: '获取AI写作参考',
    displayDescription: '为AI生成章节准备精简的故事上下文，自动筛选当前最相关的伏笔、规划和指令',
    icon: <AimOutlined />,
    color: '#fa8c16',
    userAction: '正在为本次写作准备参考资料…',
  },


  run_subagent: {
    displayName: '调度AI子任务',
    displayDescription: '由主AI调度专门的子AI来执行特定任务，如写作、审核、一致性检查、记忆更新等',
    icon: <RobotOutlined />,
    color: '#722ed1',
    userAction: '正在调度专业AI助手处理任务…',
  },

}

export function getToolDisplayName(toolName: string): string {
  return toolDisplayMap[toolName]?.displayName || '处理创作任务'
}

export function getToolDisplayDescription(toolName: string): string {
  return toolDisplayMap[toolName]?.displayDescription || '执行创作相关操作'
}

export function getToolIcon(toolName: string): React.ReactNode {
  return toolDisplayMap[toolName]?.icon || <BulbOutlined />
}

export function getToolColor(toolName: string): string {
  return toolDisplayMap[toolName]?.color || '#999'
}

export function getToolUserAction(toolName: string): string {
  return toolDisplayMap[toolName]?.userAction || '正在处理创作任务…'
}

export { toolDisplayMap }
