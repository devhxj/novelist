package location

import "time"

// Location 是小说中的地点/场景节点。
//
// 与 Python 版本的差异（14 字段 → 9 字段）：
//
//  1. location_type 自由文本 — Python 使用 16 值枚举（city/town/forest/mountain/building/room/
//     sea/river/road/castle/temple/village/dungeon/palace/market/inn/other），
//     Go 改为自由文本。理由：系统不需要根据 location_type 执行不同行为（不像 timeline.category
//     驱动不同注入策略），纯粹是给 AI 分类和过滤用的标签。自由文本让 AI 不受 16 种类型限制，
//     可以填写"洞穴""战场""营地"等。与 preferences.category 的自由文本设计一致。
//
//  2. detail_json 替代 geo_info — Python 的 geo_info 语义太局限（只表达地理信息），
//     Python 中该字段定义了但 MCP create 工具参数未暴露，LLM 永远写不进去，属于"设计了但没接上线"。
//     Go 改名为 detail_json，并在 MCP create/update 工具中暴露，AI 可自由填写任意结构化信息：
//     {"气候":"常年阴雨","建筑风格":"哥特式","氛围":"压抑诡异","历史事件":"三百年前古战场遗址",...}
//
//  3. parent_location_id 保留 — 构建树形包含关系（王国→王宫→大殿），get_location(mode="network")
//     返回的图结构中包含此层级。前端可渲染地点树/图。
//
//  4. tags 保留 — JSON 数组自由标签，如 ["危险","神秘","主角出生地"]，AI 自行维护。
//
// 砍掉的字段及理由：
//   - related_characters       → Python 模型中定义但 MCP 工具从未读写、无任何查询使用，死字段
//   - related_chapters         → 同上，从未使用
//   - extra_metadata           → detail_json + description + tags 已覆盖扩展需求
//   - first_appearance_chapter_id → AI 在 description 中记录"首次出现：第N章"即可，不需要独立字段
//   - geo_info                 → 改名为 detail_json（语义更宽泛），并真正接入 MCP 工具
//
// MCP 工具（4 个）：
//   - get_locations：list（类型/搜索过滤）/ detail（含子地点+邻接空间关系）/ network（完整图结构）
//   - create_location：新建地点节点（name 必填）
//   - update_location：更新节点字段（name/type/description/detail_json/tags/parent_location_id）
//   - update_location_relation：(source, target) 联合唯一约束 UPSERT 空间边，旧值直接覆盖，无演进历史
//
// 图结构说明：
//   地点系统形成两种边：
//     - 包含树（Location.parent_location_id）：父→子层级，如 王宫→大殿→密室
//     - 空间图（LocationRelation 表）：有向空间关系，如 迷雾森林→相邻→黑铁城堡
//   get_location(mode="detail", location_id=X) 返回子地点列表和邻接关系列表，
//   前端可渲染为完整地点网络图，AI 可查询"当前地点周围有哪些地方"来辅助空间推理。
type Location struct {
	ID               int64     `gorm:"column:id;primaryKey;autoIncrement" json:"id"`
	NovelID          int64     `gorm:"column:novel_id;not null;index"    json:"novel_id"`
	Name             string    `gorm:"column:name;not null;index"        json:"name"`          // 地点名称，如"迷雾森林""黑铁城堡"
	LocationType     string    `gorm:"column:location_type;index"        json:"location_type"`  // 自由文本，LLM 自行填写，如"森林""洞穴""城市""战场"
	Description      string    `gorm:"column:description"                json:"description"`    // 自然语言描述，环境氛围、特色等
	DetailJSON       string    `gorm:"column:detail_json"                json:"detail_json"`    // JSON 自由格式，AI 填写结构化信息：气候、氛围、历史事件、常驻NPC 等
	ParentLocationID *int64    `gorm:"column:parent_location_id;index"   json:"parent_location_id"` // 父级地点 ID，构建树形包含关系。NULL=根节点，自引用 FK
	Tags             string    `gorm:"column:tags"                       json:"tags"`           // JSON 数组自由标签，如 ["危险","神秘","主角出生地"]
	CreatedAt        time.Time `gorm:"column:created_at;autoCreateTime"  json:"created_at"`
	UpdatedAt        time.Time `gorm:"column:updated_at;autoUpdateTime"  json:"updated_at"`
}

// TableName 指定 GORM 表名。
func (Location) TableName() string { return "locations" }

// LocationRelation 是地点之间空间关系的有向边。
//
// 设计原则（与 CharacterRelation 的关键差异）：
//  1. 不需要历史追踪 — 地点空间关系相对静态（山不会搬走、路不会改道），
//     变更时直接 UPDATE 旧行或 DELETE 即可，不需要 append-only + is_current 模式。
//     与 CharacterRelation 不同：人物关系每章都可能演变，需要保留完整历史；
//     地点关系只是空间事实，错了就改，不存在"前一个关系是什么"的查询需求。
//  2. 联合唯一约束 — (source_location_id, target_location_id) 唯一，
//     MCP 工具 update_location_relation 做 UPSERT（ON CONFLICT DO UPDATE），
//     同一对有向边永远只有一条当前记录。与 ChapterPlan 的 (novel_id, scope) 设计一致。
//  3. relation_type 自由文本 — LLM 自行描述空间关系："相邻""由山路连通""可望见"
//     "骑马半天路程""途经一片沼泽""同城不同区"。系统不根据类型做不同行为，自由文本更灵活。
//  4. 有向边 — A→B 表示 A 可以通过某种关系到达 B。如果需要表达双向相邻，
//     AI 可创建两条 (A→B 相邻, B→A 相邻)。
//
// 图查询支持：
//   - 某地点的邻接关系：WHERE source_location_id = ? ORDER BY relation_type
//   - 两点之间的边：WHERE (source_location_id = A AND target_location_id = B)，单条
//   - 与包含树的区别：parent_location_id 表达"属于"，LocationRelation 表达"相邻/连通/可望见"
//
// MCP 工具实现参考：
//   - get_locations(mode="detail", location_id=X)：同时返回子地点列表（parent=X）和邻接关系列表（source=X）
//   - update_location_relation：INSERT ... ON CONFLICT (source_location_id, target_location_id) DO UPDATE
type LocationRelation struct {
	ID               int64     `gorm:"column:id;primaryKey;autoIncrement" json:"id"`
	NovelID          int64     `gorm:"column:novel_id;not null;index"              json:"novel_id"`
	SourceLocationID int64     `gorm:"column:source_location_id;uniqueIndex:uk_location_pair;not null" json:"source_location_id"`
	TargetLocationID int64     `gorm:"column:target_location_id;uniqueIndex:uk_location_pair;not null" json:"target_location_id"`
	RelationType     string    `gorm:"column:relation_type;not null"              json:"relation_type"`     // 自由文本："相邻""由山路连通""可望见""骑马半天路程"，LLM 自行描述
	Description      string    `gorm:"column:description"                          json:"description"`       // 补充细节，如"途经一片沼泽""需穿越迷雾森林"
	CreatedAt        time.Time `gorm:"column:created_at;autoCreateTime"            json:"created_at"`
	UpdatedAt        time.Time `gorm:"column:updated_at;autoUpdateTime"            json:"updated_at"`
}

// TableName 指定 GORM 表名。
func (LocationRelation) TableName() string { return "location_relations" }
