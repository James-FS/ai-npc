# MySQL migrations

Server 的 `DatabaseMigrator` 按 `schema_migrations.version` 升序执行内置迁移，并在成功后记录版本。

- `001_initial`：创建 `schema.sql` 中的基础业务表（使用 `CREATE TABLE IF NOT EXISTS`，可安全作用于已有数据库）。
- `002_memory_summary_jobs`：创建可恢复的后台摘要任务表。

`001` 的完整基础结构以根目录 `database/mysql/schema.sql` 为可直接执行版本；后续增量结构应新增三位数字文件并同步 `MySqlSchema.Migrations`。
