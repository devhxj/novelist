package skill

import (
	"log/slog"
	"os"
	"testing"
)

func TestLoadBuiltinSkills(t *testing.T) {
	logger := slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: slog.LevelWarn}))
	skills, err := LoadBuiltinSkills(logger)
	if err != nil {
		t.Fatalf("LoadBuiltinSkills failed: %v", err)
	}
	if len(skills) == 0 {
		t.Fatal("expected at least 1 builtin skill, got 0")
	}
	t.Logf("Loaded %d builtin skills:", len(skills))
	for _, s := range skills {
		t.Logf("  - %s (mode=%s)", s.Name, s.Mode)
	}
}
