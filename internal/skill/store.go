package skill

import (
	"fmt"
	"log/slog"
	"sync"
)

// Store 在内存中管理三层 skill（内置、用户、小说），支持热重载。
type Store struct {
	mu      sync.RWMutex
	builtin []Skill
	user    []Skill
	novel   map[int64][]Skill
}

// NewStore 创建 Store 并初始加载内置和用户级 skill。
func NewStore(logger *slog.Logger, userSkillsDir string) (*Store, error) {
	builtin, err := LoadBuiltinSkills()
	if err != nil {
		logger.Warn("加载内置 skill 失败，内置列表为空", "err", err)
		builtin = nil
	}

	s := &Store{
		builtin: builtin,
		novel:   make(map[int64][]Skill),
	}
	if err := s.loadUser(userSkillsDir); err != nil {
		return nil, fmt.Errorf("skill: 初始加载用户 skill 失败: %w", err)
	}
	return s, nil
}

// Get 按名称返回 skill（含 Content），同名时 novel > user > builtin。
func (s *Store) Get(novelID int64, name string) (*Skill, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()

	if skills, ok := s.novel[novelID]; ok {
		if sk := findByName(skills, name); sk != nil {
			return sk, true
		}
	}
	if sk := findByName(s.user, name); sk != nil {
		return sk, true
	}
	if sk := findByName(s.builtin, name); sk != nil {
		return sk, true
	}
	return nil, false
}

// ListMeta 返回所有可用 skill 的元数据，按 name 去重（novel > user > builtin）。
func (s *Store) ListMeta(novelID int64) []SkillMeta {
	s.mu.RLock()
	defer s.mu.RUnlock()

	seen := make(map[string]bool)
	var result []SkillMeta

	// 优先展示 novel 级
	if skills, ok := s.novel[novelID]; ok {
		for i := range skills {
			seen[skills[i].Name] = true
			result = append(result, skills[i].Meta("novel"))
		}
	}
	// user 级，跳过已出现的
	for i := range s.user {
		if seen[s.user[i].Name] {
			continue
		}
		seen[s.user[i].Name] = true
		result = append(result, s.user[i].Meta("user"))
	}
	// builtin，跳过已出现的
	for i := range s.builtin {
		if seen[s.builtin[i].Name] {
			continue
		}
		seen[s.builtin[i].Name] = true
		result = append(result, s.builtin[i].Meta("builtin"))
	}

	return result
}

// ReloadUser 重新扫描用户级 skill 目录。
func (s *Store) ReloadUser(userSkillsDir string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.loadUser(userSkillsDir)
}

// ReloadNovel 重新扫描指定小说的 skill 目录。
func (s *Store) ReloadNovel(novelID int64, novelSkillsDir string) error {
	skills, err := scanDir(novelSkillsDir)
	if err != nil {
		return fmt.Errorf("skill: reload novel %d: %w", novelID, err)
	}
	s.mu.Lock()
	if len(skills) == 0 {
		delete(s.novel, novelID)
	} else {
		s.novel[novelID] = skills
	}
	s.mu.Unlock()
	return nil
}

// loadUser 需在持有锁时调用。
func (s *Store) loadUser(dir string) error {
	skills, err := scanDir(dir)
	if err != nil {
		return err
	}
	s.user = skills
	return nil
}

// findByName 在切片中按名称查找 skill。
func findByName(skills []Skill, name string) *Skill {
	for i := range skills {
		if skills[i].Name == name {
			return &skills[i]
		}
	}
	return nil
}
