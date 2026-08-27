# AI NPC Agent

可插拔的游戏 NPC 智能Agent平台：纯 C# 核心 + Unity 包 + 独立 Server + Web 管理台，接入 OpenAI 兼容 API（OpenCode Zen / DeepSeek / GLM）。

- 方案文档：[AI-NPC-Agent-实施方案.md](./docs/architecture/AI-NPC-Agent-实施方案.md)（v3.0，含数据契约、四阶段记忆管理、摘要队列治理、统一 Vue 调试工作台和附录A/B）
- 记忆管理与 Vue 控制台设计：[docs/记忆管理与Vue控制台设计方案.md](./docs/记忆管理与Vue控制台设计方案.md)
- 当前进度：M1、M4、M5 已完成，M2 基本完成（详见主方案 §9）

## 目录

```
Packages/com.aibot.npcagent   Unity 包（Runtime/Core = 三端共享的 AIBot.Core 源码）
src/AIBot.Server              ASP.NET Core 独立宿主 + 静态托管（根入口跳转 wwwroot/app）
src/AIBot.Web                 Vue 3 + TypeScript 统一管理/调试控制台（可选后台）
src/AIBot.Tests               xUnit 测试（76 项，Core/记忆仓储/摘要队列/运行日志与审计免网全链路）
data/games/{gameId}           NPC 配置/世界观/JSON 兼容存储（MySQL 模式下为迁移源/配置源）
database/mysql/schema.sql     MySQL + Dapper 的表结构
database/mysql/migrations     按版本维护的增量迁移 SQL（当前 001/002）
```

Unity 游戏包只包含 `AIBot.Core`、`AIBot.Unity` 和后端实现，不包含 Vue、ASP.NET Core、MySQL 或 Dapper。开发/单机可使用 `UnityWebRequestBackend` 直连模型；Server 模式使用已实现的 `UnityServerBackend`，通过 `AIBot.Server` 统一处理对话、记忆和日志。Unity 与 Vue 都不直接连接 MySQL；当前 Server 本地运行默认不强制鉴权。

Server 默认使用 JSON，启用 MySQL 时由 Dapper 访问数据库；两种存储可通过配置切换，鉴权和登录不属于当前必选项。

## 快速开始（脱离 Unity 独立运行）

项目提供一个统一入口（根路径兼容跳转）：

- `http://localhost:5000/`：自动跳转到 Vue 流式对话调试页。
- `http://localhost:5000/app/`：Vue 统一管理台，包含记忆治理六页，以及对话、NPC、世界观、Prompt、Session、日志和统计调试页。

```bash
# 1) 跑单元测试（不需要网络和 key）
cd src/AIBot.Tests && dotnet test

# 2) 填 API key（优先级：NPC配置 > 环境变量 AIBOT_LLM_KEY > appsettings）
#    编辑 data/games/default/npcs/blacksmith_wang.json 的 model 段，已验证的三种接法：
#    - OpenCode Go（国内免梯子）: baseUrl=https://opencode.ai/zen/go/v1, model=ox-alpha-free
#    - DeepSeek:                 baseUrl=https://api.deepseek.com,              model=deepseek-chat
#    - 智谱GLM 免费档:            baseUrl=https://open.bigmodel.cn/api/paas/v4, model=glm-4-flash

# 3) 启动（Windows 双击 start-server.bat 同效）
cd src/AIBot.Server && dotnet run        # → 浏览器打开 http://localhost:5000

# 可选：使用 Docker MySQL 启动 Server（自动读取根目录 .env）
Copy-Item .env.example .env       # 首次使用时执行；可修改密码和端口
.\start-server-mysql.ps1
# 如果 PowerShell 阻止脚本，可仅对当前窗口放行：
# Set-ExecutionPolicy -Scope Process Bypass

# 可选：把现有 JSON 玩家长期记忆迁移到 MySQL（幂等，目标已有记录会跳过）
dotnet run -- --migrate-json --exit-after-migrate

# 也可以只启动数据库
docker compose -f docker.yml up -d mysql
docker compose -f docker.yml ps

# 修改 Vue 控制台后重新类型检查并部署到 Server/wwwroot/app
cd ../AIBot.Web
npm install
npx vue-tsc -b --force
npm run build

# 可选（PowerShell）：部署管理台时启用管理 API 鉴权（管理台顶部填写同一 token）
$env:AIBOT_ADMIN_TOKEN="请换成长随机值"

# 4) curl 调试对话（中文请存 UTF-8 文件后 --data-binary @chat.json）
curl -N -X POST http://localhost:5000/api/games/default/chat/stream \
  -H "Content-Type: application/json" \
  -d '{"npcId":"blacksmith_wang","sessionId":"s1","message":"hello"}'
```

返回 SSE 事件流（主方案附录B）：`token` → `reasoning`（推理模型思考过程）→ `tool_call` → `reply` → `done`。
**没填 key 也能调通**：返回兜底台词（`"fallback":true`）。

摘要链路说明：短期窗口淘汰的消息会在达到 `summaryThreshold` 后进入后台队列。单个任务自动重试 3 次；失败时不会删除 Session 中的 `evictedMessages`，可在 `/app/#/memories` 的会话详情中点击“重试摘要”，或调用 `POST /api/admin/memory-summary-queue/retry`。队列状态接口会返回待处理数、当前失败数、累计失败数和失败明细。

MySQL 模式会把摘要任务状态持久化到 `memory_summary_jobs`，Server 重启后自动恢复 pending/processing 任务；数据库迁移由 `schema_migrations` 管理。`GET /api/ready` 可用于启动探针，未连接数据库、缺少表、未配置模型或没有默认 NPC 时返回 503。模型错误在 SSE `error` 事件和连接测试接口中提供稳定的 `code/status/retryable` 字段。

Docker MySQL 首次初始化会自动执行 `database/mysql/schema.sql`，数据保存在 `ai_npc_mysql_data` volume。`docker.yml` 默认映射宿主机 `3306`；如果该端口已被其他 MySQL 服务占用，可在根目录 `.env` 中设置 `AIBOT_MYSQL_PORT=3307`（本机当前示例已使用 3307），此时宿主机运行 Server 要连接 `127.0.0.1:3307`。以后若把 Server 也容器化，连接地址改为 `mysql:3306`。停止容器使用 `docker compose -f docker.yml down`，不要随意使用 `down -v`，否则会删除数据库卷。

## Unity 接入

游戏工程 `Packages/manifest.json` 添加
`"com.aibot.npcagent": "file:D:/Code/aibot/Packages/com.aibot.npcagent"`，
菜单 **AIBot → Demo → Create Demo Scene** 一键生成示例场景。

API key 永不入库（.gitignore 已隔离含 key 的真实配置；模板见 `new_npc.template.json`）。

运行模式由 NPC 配置的 `runtimeMode` 控制：

```json
{
  "runtimeMode": "server",
  "serverBaseUrl": "http://127.0.0.1:5000"
}
```

`local` 模式由 Unity 直连模型；`server` 模式由 Unity 调用
`/api/games/{gameId}/chat/stream`，长期记忆、摘要、工具和日志由 Server 处理。
`NpcAgent` 上的 `playerId` 可选，填写后启用玩家范围长期记忆；`sessionId` 用于复用短期会话。
