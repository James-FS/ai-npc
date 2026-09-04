# AI NPC Agent

可插拔的游戏 NPC 智能Agent平台：纯 C# 核心 + Unity 包 + 独立 Server + Web 管理台，接入 OpenAI 兼容 API（OpenCode Zen / DeepSeek / GLM）。

- 方案文档：[AI-NPC-Agent-实施方案.md](./docs/architecture/AI-NPC-Agent-实施方案.md)（v3.1，含数据契约、四阶段记忆管理、摘要队列治理、运行加固、统一 Vue 调试工作台和附录A/B）
- 记忆管理与 Vue 控制台设计：[docs/记忆管理与Vue控制台设计方案.md](./docs/记忆管理与Vue控制台设计方案.md)
- Unity 插件快速接入：[Packages/com.aibot.npcagent/README.md](./Packages/com.aibot.npcagent/README.md)
- 当前进度：M1、M4、M5 已完成，M2 基本完成，M6 基础链路已完成（详见主方案 §9）

## 目录

```
Packages/com.aibot.npcagent   Unity 包（Runtime/Core = 三端共享的 AIBot.Core 源码）
src/AIBot.Server              ASP.NET Core 独立宿主 + 静态托管（根入口跳转 wwwroot/app）
src/AIBot.Web                 Vue 3 + TypeScript 统一管理/调试控制台（可选后台）
src/AIBot.Tests               xUnit 测试（107 项，Core/协议/请求幂等/工具边界/记忆仓储/摘要队列/运行日志与审计免网全链路）
data/games/{gameId}           NPC 配置/世界观/JSON 兼容存储（MySQL 模式下为迁移源/配置源）
database/mysql/schema.sql     MySQL + Dapper 的表结构
database/mysql/migrations     按版本维护的增量迁移 SQL（当前 001/002）
```

## 架构概览

项目采用“共享 Core + 可选 Server + Vue 管理台”的分层设计。Unity 游戏和 Server 共用同一套 `AIBot.Core` 代码，避免不同运行端出现 Prompt、记忆和工具行为不一致；Vue 只负责管理和调试，不直接接触数据库或模型密钥。

```text
┌──────────────────── Unity 游戏 ────────────────────┐
│ AIBot.Unity / NpcAgent                             │
│  local：UnityWebRequestBackend → LLM                │
│  server：UnityServerBackend ───────────────┐        │
└─────────────────────────────────────────────┼────────┘
                                              │ HTTP/SSE
┌──────────────────────── AIBot.Server ───────▼────────┐
│ ASP.NET Core 宿主                                    │
│  Chat API → AgentLoop → Prompt/工具/结构化输出        │
│      ├→ ShortTermMemory（Session 短期窗口）           │
│      ├→ PlayerMemoryService（玩家/NPC 长期摘要+事实） │
│      ├→ MemorySummaryQueue（后台摘要、重试、恢复）    │
│      └→ ChatLog / RuntimeLog / Audit                 │
│  Admin API / Health / Ready / 静态托管 Vue            │
└───────────────┬───────────────────────┬──────────────┘
                │ Dapper                │ OpenAI 兼容 HTTP
        ┌───────▼────────┐       ┌─────▼────────────────┐
        │ MySQL（可选）   │       │ OpenCode/DeepSeek/GLM │
        │ 记忆/Session/日志│       │ 主模型与摘要模型      │
        └────────────────┘       └──────────────────────┘

┌────────────── Vue 3 控制台 ──────────────┐
│ 对话、NPC、世界观、Prompt、Session、记忆、日志、统计 │
│ 通过 Server API 管理配置和查看运行状态              │
└─────────────────────────────────────────┘
```

### 各层职责

- **AIBot.Core**：跨 Unity/Server 共享的 Agent 引擎，包括 Prompt 组装、上下文、SSE 聚合、工具调用、结构化回复、短期记忆和摘要契约。它不依赖 ASP.NET Core、Vue 或 MySQL。
- **AIBot.Unity**：Unity 适配层，提供 `NpcAgent`、`UnityWebRequestBackend` 和 `UnityServerBackend`。单机可直连模型；多人或需要集中管理时调用 Server。
- **AIBot.Server**：唯一的业务编排和数据边界。负责 NPC/世界观配置、对话流式 API、记忆读写、后台摘要、日志、审计、限流和可选管理 API 鉴权。
- **AIBot.Web**：Vue 3 + TypeScript 管理台，通过 HTTP/SSE 调用 Server。生产构建产物由 Server 的 `wwwroot/app` 静态托管。
- **MySQL + Dapper**：Server 的可选持久化实现。保存玩家长期记忆、结构化事实、Session、对话日志、审计和摘要任务；默认仍可使用 JSON 文件，便于单机开发。

### 一次对话的处理链路

1. Unity 或 Vue 向 `POST /api/games/{gameId}/chat/stream` 发送 NPC、玩家、Session 和消息。
2. Server 读取 NPC/世界观配置，解析最终记忆策略，并加载 Session 短期窗口。
3. 如果启用玩家范围记忆，Server 合并长期摘要和结构化事实，交给 Core 的 `AgentLoop` 生成 Prompt。
4. `AgentLoop` 调用 OpenAI 兼容模型，实时输出 `token`、`reasoning`、`tool_call`、`reply`、`done` SSE 事件。
5. Server 保存 Session、对话日志和用量；达到阈值的淘汰消息进入 `MemorySummaryQueue`。
6. 后台摘要任务将消息压缩为一段滚动摘要和多条事实，成功写入长期记忆后才确认消费；失败任务保留并支持重试。

### 记忆与数据边界

短期记忆属于具体 Session，保存最近对话和待摘要消息；长期记忆属于 `gameId + npcId + playerId`，保存滚动摘要和可独立编辑的结构化事实。Unity 与 Vue 都不直接连接 MySQL，API Key、数据库连接串和审计数据只由 Server 管理。MySQL 模式下摘要任务存放在 `memory_summary_jobs`，迁移版本记录在 `schema_migrations`。

### 两种运行模式

| 模式 | 调用链路 | 适用场景 |
| --- | --- | --- |
| `local` | Unity → LLM | 单机 Demo、无需后台的原型、无需集中记忆 |
| `server` | Unity → AIBot.Server → LLM/MySQL | 集中配置、玩家长期记忆、日志审计、多客户端共享 |

#### Local：轻量直连模式

Local 模式不需要部署 `AIBot.Server`、Vue 管理台或 MySQL，Unity 插件可以直接运行。

```text
NpcAgent → AIBot.Core/AgentLoop → UnityWebRequestBackend → OpenAI 兼容模型
```

推荐配置方式：

1. 创建 `AI NPC → Agent Config`（`AgentConfigAsset`）。
2. 填写 NPC 人设、模型地址、模型名称和开发期 API Key。
3. 可选创建 `AI NPC → World Config`（`WorldConfigAsset`），拖到 `NpcAgent.worldConfigAsset`。
4. 将 `NpcAgent` 挂到 NPC GameObject，调用 `Chat()` 或 `ChatAsync()`。

Local 模式的配置也可以继续从 `data/games/{gameId}` 下的 JSON 加载；使用 Asset 时不需要复制 `data/` 目录。

Local 模式的特点：

- 短期记忆保存在当前 `NpcAgent` 实例中，适合 Demo 和小型单机项目。
- 游戏可以通过 `NpcAgent.Tools` 注册并执行真实 Unity 工具。
- Unity 直接连接模型，API Key 会进入客户端运行环境，仅建议用于开发、Demo 或可信的本地场景。
- 不依赖后台和 MySQL，部署结构最简单；但仍需要能够访问配置的模型地址，也不提供跨设备的统一记忆、日志和 NPC 运营管理。

#### Server：集中管理模式

Server 模式下，Unity 只作为客户端，NPC 配置、模型调用、记忆和管理功能由 `AIBot.Server` 统一处理。

```text
NpcAgent → UnityServerBackend → HTTP/SSE → AIBot.Server
                                      ├─ AgentLoop / Prompt
                                      ├─ Session / 玩家长期记忆
                                      ├─ 日志 / 审计 / 统计
                                      └─ OpenAI 兼容模型
```

推荐配置方式：

1. 启动 `AIBot.Server`，需要时再启用 MySQL 和 Vue 管理台。
2. 在 Unity 中创建 `AI NPC → Server Connection Profile`（`AIBotConnectionProfile`）。
3. 填写 Server 地址、`gameId`、`npcId`，以及可选的 `playerId`、`sessionId`。
4. 将 Profile 拖到 `NpcAgent.connectionProfile`。
5. 可选调用 `await agent.CheckServerAsync()`，检查网络、Server 就绪状态和目标 NPC 是否存在。

Server 模式下 Unity 每轮可以上传可选的游戏状态快照，Server 会把它作为当前上下文注入 Prompt。玩家长期记忆、Session、摘要队列、日志和审计均由 Server 管理，存储可以选择 JSON 或 MySQL。

Server 模式的当前工具边界需要特别注意：

- Server 默认不启用工具；调试时可显式启用 `SimulatedToolHost`。
- 模拟工具只修改会话内的 `SimGameState`，用于验证背包、好感度和剧情阶段闭环。
- 当前 Server 模拟工具不能直接修改正式游戏的背包、任务、好感度或其他业务数据。
- Unity 本地通过 `NpcAgent.Tools` 注册的工具不会自动上传到 Server。

#### 两种模式如何选择

```text
只想快速做 Demo、单机原型、无需后台       → Local
多个 NPC、多人游戏、长期记忆、统一配置管理 → Server
```

两种模式对游戏层保持相同的 `NpcAgent.ChatAsync()`、流式事件和结构化回复接口。切换模式主要是更换配置来源：Local 使用 `AgentConfigAsset`，Server 使用 `AIBotConnectionProfile`；旧的 `runtimeMode=local/server` 配置方式仍然兼容。Unity 侧模型故障但仍交付兜底台词时会触发 `onFallback`，请求取消时触发 `onCancelled`；两者都不替代正常的 `onReply`/终止错误语义。

Unity 游戏包只包含 `AIBot.Core`、`AIBot.Unity` 和后端实现，不包含 Vue、ASP.NET Core、MySQL 或 Dapper。开发/单机使用 `UnityWebRequestBackend` 直连模型；Server 使用 `UnityServerBackend` 连接 `AIBot.Server`。Unity 与 Vue 都不直接连接 MySQL；Server 默认使用 JSON，启用 MySQL 时由 Dapper 访问数据库，两种存储可通过配置切换。Server 配置 `AIBOT_CLIENT_TOKEN` 后会强制聊天客户端携带令牌；未配置时仅适合本机开发，正式部署还应配合 HTTPS、网关认证和访问控制。

## 快速开始（脱离 Unity 独立运行）

项目提供一个统一入口（根路径兼容跳转）：

- `http://localhost:5000/`：自动跳转到 Vue 流式对话调试页。
- `http://localhost:5000/app/`：Vue 统一管理台，包含记忆治理六页，以及对话、NPC、世界观、Prompt、Session、日志和统计调试页（能力速览见下文「管理控制台」）。

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
.\start-server-mysql.ps1          # 自动起 MySQL 容器、按 AIBOT_MYSQL_AUTOMIGRATE 补齐缺失表、以 MySql 模式运行
# 如果 PowerShell 阻止脚本，可仅对当前窗口放行：
# Set-ExecutionPolicy -Scope Process Bypass

# 可选：把现有 JSON 玩家长期记忆迁移到 MySQL（幂等，目标已有记录会跳过）；
# MySQL 模式下也可以在控制台「01 系统边界」页点击「从 JSON 迁移到 MySQL」按钮完成同样的事
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
$env:AIBOT_CLIENT_TOKEN="请换成另一组长随机值"

# 4) curl 调试对话（中文请存 UTF-8 文件后 --data-binary @chat.json）
curl -N -X POST http://localhost:5000/api/games/default/chat/stream \
  -H "Content-Type: application/json" \
  -H "X-Request-Id: req-demo-001" \
  -d '{"requestId":"req-demo-001","npcId":"blacksmith_wang","sessionId":"s1","message":"hello"}'
```

返回 SSE 事件流（主方案附录B）：`token` → `reasoning`（推理模型思考过程）→ `tool_call` → `reply` → `done`。
上游 OpenAI 兼容流必须正常收到 `[DONE]` 或带有 `finish_reason` 的结束 chunk；如果连接中途截断，Server/Unity 会将其视为模型传输失败并进入既定的重试或兜底流程。
**没填 key 也能调通**：返回兜底台词（`"fallback":true`）。

聊天参数、鉴权、限流和内部异常使用统一 JSON 错误契约（`error`、`code`、`status`、`requestId`，可选 `details`）；`GET /api/health` 只返回最小健康信息，`GET /api/ready` 供部署探针使用且不会回传数据库异常原文。

每轮对话应携带稳定且唯一的 `requestId`。客户端断线后使用完全相同的请求体和 `requestId` 重试，Server 会等待原请求完成或从 Session 重放已持久化的终态 SSE（`reply`/`done`/`error`，不保存逐 token 事件），并返回 `X-AIBot-Replayed: true`；相同 ID 携带不同内容返回 `409 request_id_conflict`。若 Server 在处理过程中异常退出，重启后该 ID 返回 `409 request_in_doubt`，避免不确定副作用被自动执行第二次。

摘要链路说明：短期窗口淘汰的消息会在达到 `summaryThreshold` 后进入后台队列。单个任务自动重试 3 次；失败时不会删除 Session 中的 `evictedMessages`，可在 `/app/#/memories` 的会话详情中点击“重试摘要”，或调用 `POST /api/admin/memory-summary-queue/retry`。队列状态接口会返回待处理数、当前失败数、累计失败数和失败明细。

JSON 存储会由维护任务清理长期不活跃的 Session 文件；`Sessions:MemoryIdleHours` 同时控制内存会话和文件的闲置清理。JSON 模式适合单 Server 实例，多个进程不要同时写入同一 data 目录。聊天请求中的模型覆盖参数仅允许管理端调试请求使用，并会被 Server 限制在安全范围内。

MySQL 模式会把摘要任务状态持久化到 `memory_summary_jobs`，Server 重启后自动恢复 pending/processing 任务；数据库迁移由 `schema_migrations` 管理。本地开发可设置 `AIBOT_MYSQL_AUTOMIGRATE=true`（`.env` 配置后由启动脚本透传）让 Server 启动时自动补齐缺失表。`GET /api/ready` 可用于启动探针，未连接数据库、缺少表、未配置模型或没有默认 NPC 时返回 503。模型故障若已由 AgentLoop 降级为兜底回复，会在 SSE `reply.diagnostic` 中提供稳定的 `code/status/retryable` 字段；只有无法返回任何有效回复的终止故障才使用 `error` 事件。连接测试接口继续返回同一套错误码契约。

注意存储模式跟随启动方式：纯 `dotnet run` 回到 JSON 模式（数据保留在 MySQL 但本进程不可见），固定使用 MySQL 请始终通过 `start-server-mysql.ps1` 启动。两种模式切换时控制台「01 系统边界」页会显示提醒横幅，避免"数据丢失"的误解。

Docker MySQL 首次初始化会自动执行 `database/mysql/schema.sql`，数据保存在 `ai_npc_mysql_data` volume。`docker.yml` 默认映射宿主机 `3306`；如果该端口已被其他 MySQL 服务占用，可在根目录 `.env` 中设置 `AIBOT_MYSQL_PORT=3307`（本机当前示例已使用 3307），此时宿主机运行 Server 要连接 `127.0.0.1:3307`。以后若把 Server 也容器化，连接地址改为 `mysql:3306`。停止容器使用 `docker compose -f docker.yml down`，不要随意使用 `down -v`，否则会删除数据库卷。

### 管理控制台

侧栏按「记忆治理（01-06）/ 调试工作台（07-13）」分组。管理台顶部可切换 Game（支持下拉选择与输入新 ID）与 NPC，底部常驻存储模式与 Server 启动时间徽标。

- **Game / NPC 管理**：「02 Game 策略」页右上角可应用策略预设；Game 旁的「＋」按钮可直接创建新 Game（生成 world 与 memory-policy 骨架）；「08 NPC 配置」页弹窗式新建 NPC（内置模板兜底，无需先准备模板文件）。
- **存储模式指示**：「01 系统边界」页显示当前存储模式（Json/MySql）、MySQL 目标与自动建表迁移状态；检测到本次与上次运行模式不同时，会显示提醒横幅（两种模式数据互不可见）。
- **JSON→MySQL 迁移按钮**：MySQL 模式下「01 系统边界」页可一键把 JSON 侧的玩家长期记忆迁入 MySQL（幂等，与 `--migrate-json` 等效）。
- **会话按 NPC 隔离**：调试对话页为每个 NPC 记住独立会话，切换 NPC 自动切换会话，不再串扰。
- **时间显示**：会话与日志页的时间统一转换为本地时区显示，原始 UTC 值可在日志行展开或悬浮提示中查看。

## Unity 接入

游戏工程 `Packages/manifest.json` 添加
`"com.aibot.npcagent": "file:D:/Code/aibot/Packages/com.aibot.npcagent"`，
菜单 **AIBot → Demo → Create Demo Scene** 一键生成示例场景。

API key 永不入库：`.gitignore` 已忽略全部 NPC 真实配置（`data/games/*/npcs/*.json`，模板 `new_npc.template.json` 除外）。也可以删掉 NPC JSON 里的 `apiKey`、统一改用 `.env` 的 `AIBOT_LLM_KEY`（Server 模式），这样配置本身就能安全入库。

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

如果使用 Server Connection Profile，则不需要在 Unity 中保存完整 NPC 配置或模型 API Key；Profile 会直接以 Server 模式初始化连接。正式部署时还应在 Server 设置 `AIBOT_CLIENT_TOKEN`，并在 Profile 的 `serverAuthToken` 填入同一令牌；聊天 API 会拒绝未携带该令牌的请求。Local 模式仍建议使用 `AgentConfigAsset`，这样不依赖后台即可运行。

Local 模式还可以在 `NpcAgent.worldConfigAsset` 指定 `World Config` 资源，并在 `AgentConfigAsset` 中填写开发期模型 Key；这样整个 Demo 不需要复制仓库的 `data/` 目录。真实项目请勿把含 Key 的 Asset 提交到公共仓库。

工具能力按运行模式隔离：Local 模式由 Unity 通过 `NpcAgent.Tools` 注册并执行；Server 模式由 `AIBot.Server` 注册和执行，Unity 本地注册的工具不会自动上传。插件会在检测到模式与工具配置不匹配时给出运行时警告。

Server 聊天默认 `toolMode=none`，不会注册模拟工具。Vue 调试台或 Unity Profile 显式选择 `toolMode=simulated` 后，才会启用 `SimulatedToolHost`（背包、好感度和剧情阶段只写入会话模拟状态）。Local/Server 的工具执行结果都会通过 Unity `onToolExecuted` 统一通知，但该事件只报告执行结果，不会把 Server 模拟结果写入正式游戏。生产交易、任务和背包仍应接入真实业务工具。

Server 模式可调用 `await agent.CheckServerAsync()` 主动检查后台连接、就绪状态和当前 NPC 是否存在；结果会通过 `onServerStatus` 事件通知，不会给每次聊天额外增加检查请求。
`UnityServerBackend` 会自动为每轮生成 `requestId` 并最多恢复重试两次，同时消除重放 token、reasoning 和工具事件。需要显式恢复时可读取 `agent.LastServerRequestId`，再调用 `RetryServerRequestAsync(message, requestId)`。

