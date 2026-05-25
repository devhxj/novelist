package git

import (
	"bytes"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"
)

// Repo 管理单部小说的 Git 仓库，提供文件读写和版本控制。
type Repo struct {
	dir    string
	gitBin string
}

// CommitInfo 是 git log 的单条记录。
type CommitInfo struct {
	Hash    string
	Message string
	Time    time.Time
}

// New 打开已有仓库，不存在则 git init + 首次空 commit。
// gitBin 为 git 可执行文件路径，为空时从 PATH 查找。
func New(novelDir, gitBin string) (*Repo, error) {
	if gitBin == "" {
		gitBin = "git"
	}

	r := &Repo{dir: novelDir, gitBin: gitBin}

	if _, err := os.Stat(filepath.Join(novelDir, ".git")); err != nil {
		if !os.IsNotExist(err) {
			return nil, fmt.Errorf("git: stat .git: %w", err)
		}
		if err := os.MkdirAll(novelDir, 0755); err != nil {
			return nil, fmt.Errorf("git: create novel dir: %w", err)
		}
		if out, err := r.run("init", novelDir); err != nil {
			return nil, fmt.Errorf("git: init: %s: %w", out, err)
		}
		gitkeep := filepath.Join(novelDir, "chapters", ".gitkeep")
		if err := os.MkdirAll(filepath.Dir(gitkeep), 0755); err != nil {
			return nil, fmt.Errorf("git: create chapters dir: %w", err)
		}
		if err := os.WriteFile(gitkeep, nil, 0644); err != nil {
			return nil, fmt.Errorf("git: write .gitkeep: %w", err)
		}
		if _, err := r.runInDir("add", "chapters/.gitkeep"); err != nil {
			return nil, fmt.Errorf("git: stage .gitkeep: %w", err)
		}
		if _, err := r.runInDir("commit", "-m", "initial commit"); err != nil {
			return nil, fmt.Errorf("git: initial commit: %w", err)
		}
	}

	return r, nil
}

// ── 文件路径 ──────────────────────────────────────────────

func (r *Repo) ChapterPath(num int) string {
	return fmt.Sprintf("chapters/%03d.md", num)
}

func (r *Repo) GolinkPath() string {
	return "golink.md"
}

// ── 文件读写 ──────────────────────────────────────────────

func (r *Repo) ReadChapter(num int) (string, error) {
	return r.readFile(r.ChapterPath(num))
}

func (r *Repo) WriteChapter(num int, content string) error {
	return r.writeFile(r.ChapterPath(num), content)
}

func (r *Repo) ReadGolink() (string, error) {
	return r.readFile(r.GolinkPath())
}

func (r *Repo) WriteGolink(content string) error {
	return r.writeFile(r.GolinkPath(), content)
}

func (r *Repo) readFile(relPath string) (string, error) {
	data, err := os.ReadFile(filepath.Join(r.dir, relPath))
	if err != nil {
		if os.IsNotExist(err) {
			return "", fmt.Errorf("%w: %s", os.ErrNotExist, relPath)
		}
		return "", fmt.Errorf("git: read %s: %w", relPath, err)
	}
	return string(data), nil
}

func (r *Repo) writeFile(relPath, content string) error {
	fullPath := filepath.Join(r.dir, relPath)
	if err := os.MkdirAll(filepath.Dir(fullPath), 0755); err != nil {
		return fmt.Errorf("git: mkdir for %s: %w", relPath, err)
	}
	if err := os.WriteFile(fullPath, []byte(content), 0644); err != nil {
		return fmt.Errorf("git: write %s: %w", relPath, err)
	}
	return nil
}

// ── Diff ──────────────────────────────────────────────────

// DiffContent 对比当前工作区文件与 proposed 内容，返回 unified diff。
// 文件不存在时以空内容为基准。用临时文件 + git diff --no-index 实现。
func (r *Repo) DiffContent(relPath, proposed string) (string, error) {
	fromPath := relPath
	fullPath := filepath.Join(r.dir, relPath)

	if _, err := os.Stat(fullPath); os.IsNotExist(err) {
		empty, err := os.CreateTemp("", "git-diff-empty-*")
		if err != nil {
			return "", fmt.Errorf("git: diff: create empty temp: %w", err)
		}
		empty.Close()
		defer os.Remove(empty.Name())
		fromPath = empty.Name()
	}

	tmp, err := os.CreateTemp("", "git-diff-*"+filepath.Ext(relPath))
	if err != nil {
		return "", fmt.Errorf("git: diff: create temp: %w", err)
	}
	defer os.Remove(tmp.Name())

	if _, err := tmp.WriteString(proposed); err != nil {
		tmp.Close()
		return "", fmt.Errorf("git: diff: write temp: %w", err)
	}
	tmp.Close()

	out, err := r.runInDir("diff", "--no-index", "--", fromPath, tmp.Name())
	if err != nil && out == "" {
		return "", fmt.Errorf("git: diff: %w", err)
	}
	out = strings.ReplaceAll(out, filepath.ToSlash(tmp.Name()), "/"+relPath)
	if fromPath != relPath {
		out = strings.ReplaceAll(out, filepath.ToSlash(fromPath), "/dev/null")
	}
	return out, nil
}

// ── Git 操作 ──────────────────────────────────────────────

func (r *Repo) StageAll() error {
	out, err := r.runInDir("add", "-A")
	if err != nil {
		return fmt.Errorf("git: stage all: %s: %w", out, err)
	}
	return nil
}

func (r *Repo) Commit(msg string) (string, error) {
	out, err := r.runInDir("commit", "-m", msg)
	if err != nil {
		return "", fmt.Errorf("git: commit: %s: %w", out, err)
	}
	hash, err := r.runInDir("rev-parse", "HEAD")
	if err != nil {
		return "", fmt.Errorf("git: rev-parse after commit: %s: %w", hash, err)
	}
	return strings.TrimSpace(hash), nil
}

func (r *Repo) HasUncommitted() bool {
	out, err := r.runInDir("status", "--porcelain")
	if err != nil {
		return false
	}
	return strings.TrimSpace(out) != ""
}

func (r *Repo) Revert(hashes []string) error {
	// TODO: revert 过程中合并冲突会导致仓库处于冲突状态，暂无恢复机制。
	// 未来需支持 --abort 或交互式冲突解决。
	//
	// 逆序处理：从最新到最旧
	for i := len(hashes) - 1; i >= 0; i-- {
		out, err := r.runInDir("revert", "--no-edit", hashes[i])
		if err != nil {
			return fmt.Errorf("git: revert %s: %s: %w", hashes[i], out, err)
		}
	}
	return nil
}

func (r *Repo) Log(relPath string, n int) ([]CommitInfo, error) {
	args := []string{"log", "--format=%H%x00%s%x00%ct"}
	if n > 0 {
		args = append(args, "-n", strconv.Itoa(n))
	}
	if relPath != "" {
		args = append(args, "--", relPath)
	}

	out, err := r.runInDir(args...)
	if err != nil {
		return nil, fmt.Errorf("git: log: %s: %w", out, err)
	}
	return parseLog(out), nil
}

func parseLog(out string) []CommitInfo {
	if strings.TrimSpace(out) == "" {
		return nil
	}
	lines := strings.Split(strings.TrimSpace(out), "\n")
	var commits []CommitInfo
	for _, line := range lines {
		parts := strings.SplitN(line, "\x00", 3)
		if len(parts) < 3 {
			continue
		}
		ts, _ := strconv.ParseInt(parts[2], 10, 64)
		commits = append(commits, CommitInfo{
			Hash:    parts[0],
			Message: strings.SplitN(parts[1], "\n", 2)[0],
			Time:    time.Unix(ts, 0),
		})
	}
	sort.Slice(commits, func(i, j int) bool {
		return commits[i].Time.Before(commits[j].Time)
	})
	return commits
}

// ── CLI ───────────────────────────────────────────────────

func (r *Repo) run(args ...string) (string, error) {
	return runCmd(r.gitBin, "", args...)
}

func (r *Repo) runInDir(args ...string) (string, error) {
	return runCmd(r.gitBin, r.dir, args...)
}

func runCmd(gitBin, dir string, args ...string) (string, error) {
	cmd := exec.Command(gitBin, args...)
	cmd.Dir = dir
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	err := cmd.Run()
	// 优先返回 stdout（git diff 退出码 1 表示有差异，并非错误）
	if stdout.Len() > 0 {
		return stdout.String(), nil
	}
	if err != nil {
		return stderr.String(), err
	}
	return "", nil
}
