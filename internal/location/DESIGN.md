# Location 设计文档

## 概述

Location 管理小说中的地点/场景系统，由两张表组成：`locations`（地点节点）和 `location_relations`（空间关系边）。形成两种边构成的图结构：包含树（parent_location_id）+ 空间图（LocationRelation），AI 可通过此图查询"当前地点周围有哪些地方"来辅助空间推理写作。

## 与 Python 版本的差异

### locations 表（Python 14 字段 → Go 9 字段）

**保留的优化：**

1. **location_type 自由文本**：Python 使用 16 值枚举（city/town/forest/mountain/building/room/sea/river/road/castle/temple/village/dungeon/palace/market/inn/other），Go 改为自由文本。系统不需要根据类型执行不同行为（不像 timeline.category 驱动不同查询/注入策略），纯粹是给 AI 分类和过滤用的标签。自由文本让 AI 不受限制，可以填写"洞穴""战场""营地""学院"等。

2. **detail_json 替代 geo_info**：Python 的 geo_info 语义太局限（只表达地理信息），且 MCP create 工具参数未暴露该字段，LLM 永远写不进去——"设计了但没接上线"。Go 改名为 detail_json（语义更宽泛），与 TimelineEntry.detail_json 命名一致，并在 create/update 工具中暴露，AI 可填写：气候、建筑风格、氛围、历史事件、常驻 NPC 等任意结构化信息。

3. **parent_location_id 自引用树**：构建树形包含关系（王国→王宫→大殿），get_locations(mode="detail") 返回子地点列表，前端可渲染地点树。

**砍掉的字段及理由：**

| 字段 | 理由 |
|------|------|
| `related_characters` | Python 模型中定义但 MCP 工具从未读写、无任何查询使用，死字段 |
| `related_chapters` | 同上，从未使用 |
| `extra_metadata` | detail_json + description + tags 已覆盖扩展需求 |
| `first_appearance_chapter_id` | AI 在 description 中记录"首次出现：第N章"即可，不需要独立字段 |
| `geo_info` | 改名为 detail_json（语义更宽泛），并真正接入 MCP 工具 |

### 新增 location_relations 表

Python 没有独立的表，地点之间的空间关系只能写在 description 自然语言里，AI 无法结构化查询"主角在 A 点，周围有哪些地方可以去"。

Go 新增 LocationRelation 表，形成完整的空间图。与 CharacterRelation 的关键差异：

| | CharacterRelation | LocationRelation |
|---|---|---|
| 关系含义 | 复杂人际（亦师亦敌，子亦友亦对手） | 空间关系（相邻、连通、可望见） |
| 演进频率 | 高，每章都可能变 | 低，空间关系相对静态 |
| 历史追踪 | append-only + is_current | 不需要，直接 UPDATE/DELETE |
| 写入模式 | INSERT 新行，旧行 is_current=false | (source, target) 联合唯一约束，UPSERT 覆盖 |
| relation 字段 | 自由文本描述人际 | 自由文本描述空间（"相邻""由山路连通""可望见""骑马半天路程"） |

## 图结构

```
包含树（Location.parent_location_id）:
  王国首都
  ├── 王宫
  │   ├── 大殿
  │   └── 密室
  └── 集市
      ├── 铁匠铺
      └── 药铺

空间图（LocationRelation 表）:
  迷雾森林 ←──相邻──→ 黑铁城堡
  迷雾森林 ──小路通往──→ 女巫小屋
  黑铁城堡 ──可望见───→ 巨龙山脉
  王都     ──官道连通──→ 港口城
  王都     ──骑程半日──→ 黑铁城堡
```

查询某地点时同时返回两种边：子地点列表（parent=X）+ 邻接关系列表（source=X）。

## MCP 工具（4 个）

| 工具 | 功能 | 内部路由 |
|------|------|---------|
| `get_locations` | list（类型/搜索过滤）/ detail（含子地点+邻接关系）/ network（完整图） | 查 locations ± location_relations |
| `create_location` | 新建地点节点，name 必填 | INSERT locations |
| `update_location` | 更新节点字段（name/type/description/detail_json/tags/parent） | UPDATE locations |
| `update_location_relation` | (source, target) 联合唯一约束 UPSERT 空间边 | INSERT ON CONFLICT DO UPDATE / DELETE（无 relation_type 时删除边） |

### get_locations 三种模式

- **list**：返回地点列表概览（id, name, type, description 前 100 字, tags），支持 location_type 和 search 过滤
- **detail**：返回地点完整信息（所有字段 + 父地点名称 + 子地点列表 + 该地点出发的所有空间邻接关系）
- **network**：返回完整图结构（nodes 列表 + edges 列表），前端可直接渲染。edges 包括：包含关系（parent→child）+ 空间关系（source→target）

### create_location 与 update_location 保持独立

不合并的理由：
- create 时 name 必填，update 时所有字段可选——合并后同一工具的必填约束取决于是否传 ID，行为不一致
- 与 timeline（add_timeline_entry + update_timeline_entry）和其他领域包保持一致
- LLM 对 CRUD 动词更直观，create 语义清晰
- 错误路径清楚：create 报"名称必填"，update 报"地点不存在"

### update_location_relation 使用 UPSERT

同一对 (source_location_id, target_location_id) 永远只有一条记录。AI 第一次调用是 INSERT，后续调用自动覆盖旧值。

## 排序规则

- **locations 列表**：`name ASC`
- **location_relations**：`relation_type ASC`（同一起点的不同边按关系类型排列）

## 与其他模块的关系

| 模块 | 关系 |
|------|------|
| Novel | `novel_id` FK，级联删除 |
| Chapter | 无直接引用（地点不绑定章节，通过时间线/伏笔间接关联） |
| Character | 无 FK（角色和地点的关联通过 TimelineEntry 表达"某角色在某地点做了什么"） |
| TimelineEntry | 地点可出现在伏笔的 content/detail_json 中，通过名称或 ID 引用 |
