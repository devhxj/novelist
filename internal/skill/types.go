package skill

// Skill 是一个完整的 skill 定义，包含元数据和正文内容。
type Skill struct {
	Name        string   `json:"name" yaml:"name"`
	Description string   `json:"description" yaml:"description"`
	Tags        []string `json:"tags" yaml:"tags"`
	Mode        string   `json:"mode" yaml:"mode"`
	Author      string   `json:"author" yaml:"author"`
	Version     int      `json:"version" yaml:"version"`
	Content     string   `json:"content" yaml:"-"`
}

// SkillMeta 是去除了正文的 skill 元数据，用于列表展示。
type SkillMeta struct {
	Name        string   `json:"name"`
	Description string   `json:"description"`
	Tags        []string `json:"tags"`
	Mode        string   `json:"mode"`
	Author      string   `json:"author"`
	Version     int      `json:"version"`
	Source      string   `json:"source"` // "builtin" | "user" | "novel"
}

// Meta 返回去除正文的元数据视图。
func (s *Skill) Meta(source string) SkillMeta {
	return SkillMeta{
		Name:        s.Name,
		Description: s.Description,
		Tags:        s.Tags,
		Mode:        s.Mode,
		Author:      s.Author,
		Version:     s.Version,
		Source:      source,
	}
}
