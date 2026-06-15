package skill

// Skill 是一个完整的 skill 定义，包含元数据和正文内容。
type Skill struct {
	Name        string `json:"name" yaml:"name"`
	Description string `json:"description" yaml:"description"`
	Category    string `json:"category" yaml:"category"` // 分类，未来可基于不同分类组合实现 skill 矩阵
	Mode        string `json:"mode" yaml:"mode"`
	Author      string `json:"author" yaml:"author"`
	Version     int    `json:"version" yaml:"version"`
	Content     string `json:"content" yaml:"-"`
	RawContent  string `json:"raw_content,omitempty" yaml:"-"` // 原始 markdown 全文（含 YAML frontmatter）
}

// SkillMeta 是去除了正文的 skill 元数据，用于列表展示。
type SkillMeta struct {
	Name        string `json:"name"` //name和description给llm其余的保留作为元数据
	Description string `json:"description"`
	Category    string `json:"category"` // 分类，未来可基于不同分类组合实现 skill 矩阵
	Mode        string `json:"mode"`
	Author      string `json:"author"`
	Version     int    `json:"version"`
	Source      string `json:"source"` // "builtin" | "user" | "novel"
}

// Meta 返回去除正文的元数据视图。
func (s *Skill) Meta(source string) SkillMeta {
	return SkillMeta{
		Name:        s.Name,
		Description: s.Description,
		Category:    s.Category,
		Mode:        s.Mode,
		Author:      s.Author,
		Version:     s.Version,
		Source:      source,
	}
}
