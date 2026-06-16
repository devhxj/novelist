package storage

import (
	"context"
	"database/sql"
	"fmt"
	"log/slog"
	"time"

	sqlite_vec "github.com/asg017/sqlite-vec-go-bindings/cgo"
	"gorm.io/driver/sqlite"
	"gorm.io/gorm"
	gormlogger "gorm.io/gorm/logger"
)

// Open 打开 SQLite 数据库，启用 WAL 模式和 sqlite-vec 向量扩展。
//
//	dsn: SQLite 文件路径，如 "/data/my-novel/novel.db"
func Open(dsn string, log *slog.Logger) (*gorm.DB, error) {
	// 启用 sqlite-vec 扩展（全局注册，所有 mattn/go-sqlite3 连接自动可用）
	sqlite_vec.Auto()

	db, err := gorm.Open(sqlite.Open(dsn), &gorm.Config{
		Logger: &gormSlogAdapter{log: log},
	})
	if err != nil {
		return nil, fmt.Errorf("打开 SQLite 失败: %w", err)
	}

	// 配置连接池
	sqlDB, err := db.DB()
	if err != nil {
		return nil, fmt.Errorf("storage: 获取 sql.DB 失败: %w", err)
	}
	sqlDB.SetMaxOpenConns(1)            // SQLite 写串行，单连接最安全
	sqlDB.SetMaxIdleConns(1)            // 至少保留一个空闲连接
	sqlDB.SetConnMaxLifetime(time.Hour) // 连接最长存活 1 小时

	if err := enableWAL(sqlDB); err != nil {
		return nil, fmt.Errorf("storage: 启用 WAL 失败: %w", err)
	}

	log.Info("SQLite 已打开", "dsn", dsn)
	return db, nil
}

// enableWAL 开启 WAL 模式，支持一写多读并发。
func enableWAL(db *sql.DB) error {
	var journal string
	if err := db.QueryRow("PRAGMA journal_mode=WAL").Scan(&journal); err != nil {
		return err
	}
	if journal != "wal" {
		return fmt.Errorf("期望 journal_mode=wal，实际为 %s", journal)
	}
	return nil
}

// Close 关闭数据库并清理 sqlite-vec 全局状态。
func Close(db *gorm.DB) error {
	sqlDB, err := db.DB()
	if err != nil {
		return fmt.Errorf("storage: 获取 sql.DB 失败: %w", err)
	}

	sqlite_vec.Cancel()

	if err := sqlDB.Close(); err != nil {
		return fmt.Errorf("storage: 关闭数据库失败: %w", err)
	}
	return nil
}

// Ping 检查数据库连接是否正常。
func Ping(ctx context.Context, db *gorm.DB) error {
	sqlDB, err := db.DB()
	if err != nil {
		return fmt.Errorf("storage: 获取 sql.DB 失败: %w", err)
	}
	if err := sqlDB.PingContext(ctx); err != nil {
		return fmt.Errorf("storage: ping 失败: %w", err)
	}
	return nil
}

// gormSlogAdapter 将 slog 适配为 GORM 的 logger 接口。
type gormSlogAdapter struct {
	log *slog.Logger
}

func (a *gormSlogAdapter) LogMode(level gormlogger.LogLevel) gormlogger.Interface {
	// 日志级别由 slog 的 HandlerOptions.Level 控制，此处不做二次过滤
	return a
}

func (a *gormSlogAdapter) Info(_ context.Context, msg string, args ...any) {
	a.log.Info(msg, args...)
}

func (a *gormSlogAdapter) Warn(_ context.Context, msg string, args ...any) {
	a.log.Warn(msg, args...)
}

func (a *gormSlogAdapter) Error(_ context.Context, msg string, args ...any) {
	a.log.Error(msg, args...)
}

func (a *gormSlogAdapter) Trace(_ context.Context, begin time.Time, fc func() (sql string, rowsAffected int64), err error) {
	elapsed := time.Since(begin)
	sql, rows := fc()
	attrs := []any{
		"耗时", elapsed,
		"行数", rows,
		"sql", sql,
	}
	if err != nil {
		attrs = append(attrs, "错误", err)
		a.log.Warn("GORM 查询异常", attrs...)
		return
	}
	a.log.Debug("GORM 查询", attrs...)
}
