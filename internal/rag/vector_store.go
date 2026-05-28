//go:build cgo

package rag

import (
	"context"
	"database/sql"
	"fmt"
	"log/slog"
	"strings"
	"sync"

	sqlite_vec "github.com/asg017/sqlite-vec-go-bindings/cgo"
	_ "github.com/mattn/go-sqlite3"
)

// VectorStore 使用 sqlite-vec 管理向量索引，每部小说一张 vec0 虚拟表。
type VectorStore struct {
	db          *sql.DB
	embedder    Embedder
	log         *slog.Logger
	ensuredOnce sync.Map // map[int64]bool，避免重复 CREATE TABLE IF NOT EXISTS
}

// NewVectorStore 创建向量存储。db 应为已启用 sqlite-vec 扩展的 SQLite 连接。
func NewVectorStore(db *sql.DB, embedder Embedder, log *slog.Logger) *VectorStore {
	return &VectorStore{db: db, embedder: embedder, log: log}
}

func (s *VectorStore) tableName(novelID int64) string {
	return fmt.Sprintf("vec_novel_%d", novelID)
}

// ensureTable 确保指定小说的向量表存在，不存在则创建。结果缓存在 ensuredOnce 中。
func (s *VectorStore) ensureTable(ctx context.Context, novelID int64) error {
	if _, ok := s.ensuredOnce.Load(novelID); ok {
		return nil
	}

	sql := fmt.Sprintf(`CREATE VIRTUAL TABLE IF NOT EXISTS %s USING vec0(
		embedding float[768],
		chunk_id text,
		content text,
		chunk_type text,
		chapter_id integer,
		chunk_index integer
	)`, s.tableName(novelID))

	_, err := s.db.ExecContext(ctx, sql)
	if err != nil {
		return fmt.Errorf("rag: create vec table for novel %d: %w", novelID, err)
	}
	s.ensuredOnce.Store(novelID, true)
	return nil
}

// IndexChunks 将文本块批量生成 embedding 并在事务中写入向量表。
func (s *VectorStore) IndexChunks(ctx context.Context, novelID int64, chunks []Chunk) error {
	if len(chunks) == 0 {
		return nil
	}

	if err := s.ensureTable(ctx, novelID); err != nil {
		return err
	}

	// 批量生成 embedding，一次 ONNX Run。
	texts := make([]string, len(chunks))
	for i, c := range chunks {
		texts[i] = c.Content
	}
	embs, err := s.embedder.EmbedBatch(ctx, texts)
	if err != nil {
		return fmt.Errorf("rag: batch embed: %w", err)
	}

	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return fmt.Errorf("rag: begin tx: %w", err)
	}
	defer tx.Rollback()

	tableName := s.tableName(novelID)
	for i, chunk := range chunks {
		v, err := sqlite_vec.SerializeFloat32(embs[i])
		if err != nil {
			return fmt.Errorf("rag: serialize chunk %s: %w", chunk.ID, err)
		}

		_, err = tx.ExecContext(ctx,
			fmt.Sprintf(`INSERT INTO %s (embedding, chunk_id, content, chunk_type, chapter_id, chunk_index) VALUES (?, ?, ?, ?, ?, ?)`, tableName),
			v, chunk.ID, chunk.Content, chunk.ChunkType, chunk.ChapterID, chunk.ChunkIndex,
		)
		if err != nil {
			return fmt.Errorf("rag: insert chunk %s: %w", chunk.ID, err)
		}
	}

	if err := tx.Commit(); err != nil {
		return fmt.Errorf("rag: commit tx: %w", err)
	}

	s.log.Info("向量索引完成", "novel_id", novelID, "chunks", len(chunks))
	return nil
}

// Search 在指定小说的向量索引中执行语义检索。
func (s *VectorStore) Search(ctx context.Context, novelID int64, query string, topK int, filter *SearchFilter) ([]SearchResult, error) {
	if err := s.ensureTable(ctx, novelID); err != nil {
		return nil, err
	}

	queryEmb, err := s.embedder.Embed(ctx, query)
	if err != nil {
		return nil, fmt.Errorf("rag: embed query: %w", err)
	}

	q, err := sqlite_vec.SerializeFloat32(queryEmb)
	if err != nil {
		return nil, fmt.Errorf("rag: serialize query: %w", err)
	}

	tableName := s.tableName(novelID)
	whereClauses := []string{}
	args := []any{q}

	if filter != nil {
		if len(filter.ChapterIDs) > 0 {
			placeholders := make([]string, len(filter.ChapterIDs))
			for i, id := range filter.ChapterIDs {
				placeholders[i] = "?"
				args = append(args, id)
			}
			whereClauses = append(whereClauses,
				fmt.Sprintf("chapter_id IN (%s)", strings.Join(placeholders, ",")))
		}
		if len(filter.ChunkTypes) > 0 {
			placeholders := make([]string, len(filter.ChunkTypes))
			for i, t := range filter.ChunkTypes {
				placeholders[i] = "?"
				args = append(args, t)
			}
			whereClauses = append(whereClauses,
				fmt.Sprintf("chunk_type IN (%s)", strings.Join(placeholders, ",")))
		}
	}

	whereSQL := ""
	if len(whereClauses) > 0 {
		whereSQL = " AND " + strings.Join(whereClauses, " AND ")
	}

	querySQL := fmt.Sprintf(
		`SELECT chunk_id, content, chunk_type, chapter_id, distance FROM %s WHERE embedding MATCH ?%s ORDER BY distance LIMIT ?`,
		tableName, whereSQL,
	)
	args = append(args, topK)

	rows, err := s.db.QueryContext(ctx, querySQL, args...)
	if err != nil {
		return nil, fmt.Errorf("rag: search query: %w", err)
	}
	defer rows.Close()

	var results []SearchResult
	for rows.Next() {
		var chunkID, content, chunkType string
		var chapterID int64
		var distance float64
		if err := rows.Scan(&chunkID, &content, &chunkType, &chapterID, &distance); err != nil {
			return nil, fmt.Errorf("rag: scan result: %w", err)
		}
		relevance := 1.0 - distance
		if relevance < 0 {
			relevance = 0
		}
		results = append(results, SearchResult{
			ChunkID:    chunkID,
			Content:    content,
			SourceType: chunkType,
			SourceID:   chapterID,
			Distance:   distance,
			Relevance:  relevance,
		})
	}

	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("rag: iterate results: %w", err)
	}

	s.log.Info("向量检索完成", "novel_id", novelID, "query_len", len([]rune(query)), "results", len(results))
	return results, nil
}

// DeleteChapterChunks 删除指定章节的所有向量块。
func (s *VectorStore) DeleteChapterChunks(ctx context.Context, novelID, chapterID int64) error {
	tableName := s.tableName(novelID)
	_, err := s.db.ExecContext(ctx,
		fmt.Sprintf(`DELETE FROM %s WHERE chapter_id = ?`, tableName),
		chapterID,
	)
	if err != nil {
		return fmt.Errorf("rag: delete chapter %d chunks: %w", chapterID, err)
	}
	s.log.Info("已删除章节向量", "novel_id", novelID, "chapter_id", chapterID)
	return nil
}

// DeleteNovel 删除整部小说的向量表。
func (s *VectorStore) DeleteNovel(ctx context.Context, novelID int64) error {
	tableName := s.tableName(novelID)
	_, err := s.db.ExecContext(ctx,
		fmt.Sprintf("DROP TABLE IF EXISTS %s", tableName),
	)
	if err != nil {
		return fmt.Errorf("rag: drop table for novel %d: %w", novelID, err)
	}
	s.ensuredOnce.Delete(novelID)
	s.log.Info("已删除小说向量表", "novel_id", novelID)
	return nil
}
