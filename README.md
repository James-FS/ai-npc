# AI NPC Agent

面向 Unity 游戏的 NPC Agent：共享 C# 核心、Unity 插件、可选 ASP.NET Core Server 与 Vue 管理台。模型端使用 OpenAI Chat Completions 兼容接口；可接入提供此接口的模型服务。

**核心特性**

- **四层记忆**：Session 短期窗口 → 玩家×NPC 长期记忆（滚动摘要 + 可独立编辑的结构化事实），后台摘要队列自动压缩、失败重试、重启恢复。
- **双运行模式**：`local`（Unity 直连模型，零依赖）与 `server`（集中配置、跨设备记忆、日志审计），游戏层 API 完全一致，切换只换配置。
- **工具回传**：模型发起工具调用时 Server 挂起对话、由游戏侧真实执行后续跑；断线重放不会导致工具双执行。
- **流式与幂等**：SSE 实时输出（token / reasoning / tool_call / reply），`requestId` 幂等去重与断线重放。
- **可选持久化**：JSON 文件适合开发与单实例部署；MongoDB 提供集中持久化；当前版本强制单活 Server，不支持多个实例同时写入。
- **管理控制台**：Vue 3 实现的记忆治理、NPC/世界观配置、对话调试、日志与用量统计。

| 文档 | 内容 |
| --- | --- |
| [Unity 插件接入](./Packages/com.aibot.npcagent/README.md) | 安装、NpcAgent 配置、两种模式接入与工具注册 |

## 目录

```
Packages/com.aibot.npcagent   Unity 2022.3 插件与 Server 共用的 AIBot.Core 源码
src/AIBot.Server              .NET 8 ASP.NET Core 宿主、API 与 Vue 静态文件
src/AIBot.Web                 Vue 3 + TypeScript 管理与调试控制台
src/AIBot.Tests               xUnit 自动化测试
data/games/{gameId}           NPC、世界观和记忆策略配置；JSON 模式下也保存运行数据
docker.yml                    MongoDB 容器配置（只启动数据库）
start-server-mongo.ps1        Windows PowerShell：启动 MongoDB 并以 Mongo 模式运行 Server
deploy/                       发布目录和 Dockerfile（本地发布产物目录）
```

Server 的管理 API 按职责放在 `AdminEndpoints.cs`（入口与配置）、`AdminMemoryEndpoints.cs`（记忆策略、事实、迁移、审计）和 `AdminOperationsEndpoints.cs`（存储、队列、会话、日志、统计）中。聊天由 `ChatEndpoints.cs` 注册路由，`ChatRequestHandler.cs` 编排校验、幂等、工具续跑和记忆持久化，`ChatSseWriter.cs` 负责流式输出与终态重放。各文件直接使用现有服务，接口地址与请求契约不变。

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
                │ Mongo Driver           │ OpenAI 兼容 HTTP
        ┌───────▼────────┐       ┌─────▼────────────────┐
        │ MongoDB（正式） │       │ OpenAI 兼容模型服务   │
        │ 记忆/Session/日志│       │ 主模型与摘要模型      │
        └────────────────┘       └──────────────────────┘

┌────────────── Vue 3 控制台 ──────────────┐
│ 对话、NPC、世界观、Prompt、Session、记忆、日志、统计 │
│ 通过 Server API 管理配置和查看运行状态              │
└─────────────────────────────────────────┘
```

### 各层职责

- **AIBot.Core**：跨 Unity/Server 共享的 Agent 引擎，包括 Prompt 组装、上下文、SSE 聚合、工具调用、结构化回复、短期记忆和摘要契约。它不依赖 ASP.NET Core、Vue 或 MongoDB。
- **AIBot.Unity**：Unity 适配层，提供 `NpcAgent`、`UnityWebRequestBackend` 和 `UnityServerBackend`。单机可直连模型；多人或需要集中管理时调用 Server。
- **AIBot.Server**：唯一的业务编排和数据边界。负责 NPC/世界观配置、对话流式 API、记忆读写、后台摘要、日志、审计、限流和可选管理 API 鉴权。
- **AIBot.Web**：Vue 3 + TypeScript 管理台，通过 HTTP/SSE 调用 Server。生产构建产物由 Server 的 `wwwroot/app` 静态托管。
- **MongoDB（正式）**：Server 的正式/在线持久化实现，由官方 MongoDB.Driver 访问。保存玩家长期记忆、结构化事实、Session、对话日志、审计和摘要任务；开发/单机默认使用 JSON 文件，便于零依赖启动。

### 一次对话的处理链路

1. Unity 或 Vue 向 `POST /api/games/{gameId}/chat/stream` 发送 NPC、玩家、Session 和消息。
2. Server 读取 NPC/世界观配置，解析最终记忆策略，并加载 Session 短期窗口。
3. 如果启用玩家范围记忆，Server 合并长期摘要和结构化事实，交给 Core 的 `AgentLoop` 生成 Prompt。
4. `AgentLoop` 调用 OpenAI 兼容模型，实时输出 `token`、`reasoning`、`tool_call`、`reply`、`done` SSE 事件。
5. Server 保存 Session、对话日志和用量；达到阈值的淘汰消息进入 `MemorySummaryQueue`。
6. 后台摘要任务将消息压缩为一段滚动摘要和多条事实，成功写入长期记忆后才确认消费；失败任务保留并支持重试。

### 记忆与数据边界

短期记忆属于具体 Session，保存最近对话和待摘要消息；长期记忆属于 `gameId + npcId + playerId`，保存滚动摘要和可独立编辑的结构化事实。Unity 与 Vue 都不直接连接数据库，API Key、数据库连接串和审计数据只由 Server 管理。Mongo 模式下摘要任务存放在 `memory_summary_jobs` 集合，初始化与 TTL 索引状态记录在 `_meta` 集合。

### 两种运行模式

| 模式 | 调用链路 | 适用场景 |
| --- | --- | --- |
| `local` | Unity → LLM | 单机 Demo、无需后台的原型、无需集中记忆 |
| `server` | Unity → AIBot.Server → LLM/MongoDB | 集中配置、玩家长期记忆、日志审计、多客户端共享 |

#### Local：轻量直连模式

Local 模式不需要部署 `AIBot.Server`、Vue 管理台或 MongoDB，Unity 插件可以直接运行。

```text
NpcAgent → AIBot.Core/AgentLoop → UnityWebRequestBackend → OpenAI 兼容模型
```

推荐配置方式：

1. 创建 `AI NPC → Agent Config`（`AgentConfigAsset`）。
2. 填写 NPC 人设、模型地址、模型名称和开发期 API Key。
3. 可选创建 `AI NPC → World Config`（`WorldConfigAsset`），拖到 `NpcAgent.worldConfigAsset`。
4. 将 `NpcAgent` 挂到 NPC GameObject，调用 `Chat()` 或 `ChatAsync()`。

`NpcAgent` 的 Local 运行配置使用 `AgentConfigAsset`，Server 运行配置使用 `AIBotConnectionProfile`。旧版 `data/games/{gameId}/npcs/*.json` 可在 Unity 菜单 **AIBot → Import Legacy NPC JSON as Local Asset...** 导入为本地 Asset；相邻 `world.json` 与 `memory-policy.json` 会一并导入，完整保留 Local 世界观和记忆策略。已有 AgentConfigAsset 的记忆字段也会随导入保留。旧 Server JSON 可用 **Import Legacy NPC JSON as Server Profile...** 导入地址、Game/NPC 标识和工具 ID。导入不会删除源文件，也不会将模型 Key 复制到 Server Profile。已有声明 Server 模式的旧 AgentConfigAsset，可在 Hierarchy 选中其 NpcAgent 后执行 **AIBot → Convert Selected Legacy Server NpcAgent to Connection Profile...**。

Local 模式的特点：

- 短期记忆保存在当前 `NpcAgent` 实例中，适合 Demo 和小型单机项目。
- 游戏可以通过 `NpcAgent.Tools` 注册并执行真实 Unity 工具。
- Unity 直接连接模型，API Key 会进入客户端运行环境，仅建议用于开发、Demo 或可信的本地场景。
- 不依赖后台和数据库，部署结构最简单；但仍需要能够访问配置的模型地址，也不提供跨设备的统一记忆、日志和 NPC 运营管理。

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

1. 启动 `AIBot.Server`，需要时再启用 MongoDB 和 Vue 管理台。
2. 在 Unity 中创建 `AI NPC → Server Connection Profile`（`AIBotConnectionProfile`）。
3. 填写 Server 地址、`gameId`、`npcId`，以及可选的 `playerId`、`sessionId`。
4. 将 Profile 拖到 `NpcAgent.connectionProfile`。
5. 可选调用 `await agent.CheckServerAsync()`，检查网络、Server 就绪状态和目标 NPC 是否存在。

Server 模式下 Unity 每轮可以上传可选的游戏状态快照，Server 会把它作为当前上下文注入 Prompt。玩家长期记忆、Session、摘要队列、日志和审计均由 Server 管理，存储可以选择 JSON 或 Mongo。

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

两种模式对游戏层保持相同的 `NpcAgent.ChatAsync()`、流式事件和结构化回复接口。切换模式时将 `AgentConfigAsset` 或 `AIBotConnectionProfile` 指派给 NpcAgent；Unity 侧模型故障但仍交付兜底台词时会触发 `onFallback`，请求取消时触发 `onCancelled`；两者都不替代正常的 `onReply`/终止错误语义。

Unity 游戏包只包含 `AIBot.Core`、`AIBot.Unity` 和后端实现，不包含 Vue、ASP.NET Core 或 MongoDB 驱动。开发/单机使用 `UnityWebRequestBackend` 直连模型；Server 使用 `UnityServerBackend` 连接 `AIBot.Server`。Unity 与 Vue 都不直接连接数据库；Server 默认使用 JSON，启用 Mongo 时通过 MongoDB.Driver 访问数据库，两种存储可通过配置切换。Server 配置 `AIBOT_CLIENT_TOKEN` 后会强制聊天客户端携带令牌；未配置时仅适合本机开发，正式部署还应配合 HTTPS、网关认证和访问控制。

## 快速开始（脱离 Unity 独立运行）

项目提供一个统一入口（根路径兼容跳转）：

- `http://localhost:5000/`：自动跳转到 Vue 流式对话调试页。
- `http://localhost:5000/app/`：Vue 统一管理台，侧栏提供调试、内容、记忆、模型、运维五个入口；每个入口内以子导航切换已有功能页。

运行 Server 需要 .NET 8 SDK。Server 默认使用 JSON 存储，无需 MongoDB。开发 Vue 管理台需要 Node.js/npm；MongoDB 模式需要 Docker Compose 或可访问的 MongoDB 实例。

```powershell
# Windows 也可双击 start-server.bat。启用鉴权时先设置令牌，再运行 Server。
# $env:AIBOT_ADMIN_TOKEN = "请换成长随机值"
# $env:AIBOT_CLIENT_TOKEN = "请换成另一组长随机值"
dotnet run --project .\src\AIBot.Server\AIBot.Server.csproj
```

打开 `http://localhost:5000/` 进入管理台。在「模型」入口添加兼容 OpenAI Chat Completions 的连接，填写 Base URL、模型 ID 和 API Key 并分配给默认模型、Game 或 NPC，再到「调试」发送消息。Server 首次启动会将旧 NPC/Game 内嵌连接中带 Key 的配置迁移到 `data/model-connections.json`，保存连接和绑定后才清除旧字段；重复启动可安全续跑。新文件由 Git 忽略，API Key 不会写入仓库。

可选：运行自动化测试。

```powershell
dotnet test .\src\AIBot.Tests\AIBot.Tests.csproj
```

可选：使用 Docker MongoDB 启动 Server（Windows PowerShell）。首次运行时复制并编辑 `.env`，设置数据库用户、强密码、端口和库名。该文件由 Docker Compose 和启动脚本读取；Server 鉴权令牌需设在进程环境中。

```powershell
if (-not (Test-Path .env)) { Copy-Item .env.example .env }
# 编辑 .env 后，在启动 Server 前设置令牌：
$env:AIBOT_ADMIN_TOKEN = "请换成长随机值"
$env:AIBOT_CLIENT_TOKEN = "请换成另一组长随机值"
.\start-server-mongo.ps1
```

脚本会启动 MongoDB、等待健康检查、创建集合与索引，再启动 Server。JSON → Mongo 迁移仅导入玩家长期记忆，不包含 Session、对话日志或审计记录；Mongo 模式启动后可在管理台「系统记忆边界」页面执行迁移。

`docker.yml` 只启动 MongoDB。若只需启动数据库，可运行：

```powershell
docker compose --env-file .env -f docker.yml up -d mongo
docker compose --env-file .env -f docker.yml ps
```

开发 Vue 管理台时，在 `src/AIBot.Web` 运行 `npm ci` 和 `npm run dev`；Vite 默认监听 `http://localhost:5173`，并将 `/api` 代理到本机 5000 端口。运行 `npm run build` 会先执行类型检查，再把生产文件写入 `src/AIBot.Server/wwwroot/app`。

调试 SSE 对话时，每次请求使用唯一的 `requestId`。若已设置 `AIBOT_CLIENT_TOKEN`，需在请求中添加 `Authorization: Bearer <token>`。

```powershell
$body = @{ requestId = "req-demo-001"; npcId = "blacksmith_wang"; sessionId = "s1"; message = "hello" } | ConvertTo-Json -Compress
curl.exe -N -X POST http://localhost:5000/api/games/default/chat/stream `
  -H "Content-Type: application/json" `
  -H "Authorization: Bearer $env:AIBOT_CLIENT_TOKEN" `
  -H "X-Request-Id: req-demo-001" `
  --data-binary $body
```

返回 SSE 事件流：`token` → `reasoning`（推理模型思考过程）→ `tool_call` → `reply` → `done`。
上游 OpenAI 兼容流必须正常收到 `[DONE]` 或带有 `finish_reason` 的结束 chunk；如果连接中途截断，Server/Unity 会将其视为模型传输失败并进入既定的重试或兜底流程。
**没填 key 也能调通**：返回兜底台词（`"fallback":true`）。

聊天参数、鉴权、限流和内部异常使用统一 JSON 错误契约（`error`、`code`、`status`、`requestId`，可选 `details`）；`GET /api/health` 只返回最小健康信息，`GET /api/ready` 供部署探针使用且不会回传数据库异常原文。

每轮对话应携带稳定且唯一的 `requestId`。客户端断线后使用完全相同的请求体和 `requestId` 重试，Server 会等待原请求完成或从 Session 重放已持久化的终态 SSE（`reply`/`done`/`error`，不保存逐 token 事件），并返回 `X-AIBot-Replayed: true`；相同 ID 携带不同内容返回 `409 request_id_conflict`。若 Server 在处理过程中异常退出，重启后该 ID 返回 `409 request_in_doubt`，避免不确定副作用被自动执行第二次。

摘要链路说明：短期窗口淘汰的消息会在达到 `summaryThreshold` 后进入后台队列。单个任务自动重试 3 次；失败时不会删除 Session 中的 `evictedMessages`，可在 `/app/#/memories` 的会话详情中点击“重试摘要”，或调用 `POST /api/admin/memory-summary-queue/retry`。队列状态接口会返回待处理数、当前失败数、累计失败数和失败明细。

JSON 存储会由维护任务清理长期不活跃的 Session 文件；`Sessions:MemoryIdleHours` 同时控制内存会话和文件的闲置清理。JSON 模式适合单 Server 实例，多个进程不要同时写入同一 data 目录。聊天请求中的模型覆盖参数仅允许管理端调试请求使用，并会被 Server 限制在安全范围内。

Mongo 模式会把摘要任务状态持久化到 `memory_summary_jobs` 集合，Server 重启后自动恢复 pending/processing 任务；集合与索引由 `MongoInitializer` 初始化，初始化与 TTL 状态记录在 `_meta` 集合。本地开发可设置 `AIBOT_MONGO_AUTOMIGRATE=true`（`.env` 配置后由启动脚本透传，Mongo 模式必需）让 Server 启动时自动创建集合与索引。`GET /api/ready` 可用于启动探针，未连接数据库、缺少集合、所有 NPC 均未配置可用模型连接/Key 或所有 Game 均没有可用 NPC 时返回 503。模型故障若已由 AgentLoop 降级为兜底回复，会在 SSE `reply.diagnostic` 中提供稳定的 `code/status/retryable` 字段；只有无法返回任何有效回复的终止故障才使用 `error` 事件。连接测试接口继续返回同一套错误码契约。

注意存储模式跟随启动方式：纯 `dotnet run` 回到 JSON 模式（数据仍保留在 MongoDB，但该进程按 JSON 读写而不可见），固定使用 Mongo 请始终通过 `start-server-mongo.ps1` 启动。两种模式切换时控制台「01 系统边界」页会显示提醒横幅，避免"数据丢失"的误解。

Docker MongoDB 首次启动即由 Server 的 `MongoInitializer` 创建所需集合与索引（无外部 schema 脚本），数据保存在 `ai_npc_mongo_data` volume。`docker.yml` 默认映射宿主机 `27017`；如果该端口已被其他 MongoDB 服务占用，可在根目录 `.env` 中设置 `AIBOT_MONGO_PORT=27018`，此时宿主机运行 Server 要连接 `127.0.0.1:27018`。停止容器使用 `docker compose -f docker.yml down`；`down -v` 会删除数据库卷。

**Mongo 数据备份**：`ai_npc_mongo_data` volume 是 Mongo 模式下全部业务数据（长期记忆、Session、日志、审计、摘要任务）的唯一载体，请定期备份。容器在运行时可直接导出：

```bash
# 导出（写入宿主机当前目录的 dump/ 目录）
docker exec ai-npc-mongo mongodump --username aibot --password <密码> --authenticationDatabase admin --db ai_npc --archive > ai_npc_backup.archive
# 恢复
docker exec -i ai-npc-mongo mongorestore --username aibot --password <密码> --authenticationDatabase admin --archive < ai_npc_backup.archive
```

也可以直接快照 Docker volume（`docker run --rm -v aibot_ai_npc_mongo_data:/data -v <宿主机目录>:/backup alpine tar czf /backup/mongo-data.tgz /data`）。注意 `docker compose -f docker.yml down -v` 会删除该卷，等同于删除全部业务数据。

### 发布 Server

`publish-server.ps1` 会安装前端锁定依赖、运行前端回归测试、重建管理台，再发布 Server，避免把过期的静态页面打入发布包。`deploy/publish/` 和镜像 tar 是本地产物，不再纳入 Git；CI 会运行包含 MongoDB 的后端测试并上传发布包。

仓库提供 `deploy/Dockerfile`，用于将 .NET 发布目录打包为 Linux 容器镜像。构建命令：

```powershell
.\publish-server.ps1
Push-Location .\deploy
docker build -t aibot-server:latest .
Pop-Location
```

镜像在容器内监听 8080 端口。部署时需把持久化的数据目录挂载到 `/app/data`，并设置 `AIBOT_DATA_ROOT=/app/data`。挂载目录至少要有 `games/` 子目录；在管理台创建或导入 NPC 后才能通过就绪探针。模型连接和相关配置也保存在该目录中。Server 容器连接同一 Docker 网络里的 MongoDB 时，连接串中的主机名应使用 Mongo 服务名，不能使用 `127.0.0.1`。

下面的例子将宿主机 `D:\aibot-data` 挂载为 Server 数据目录。部署前先准备该目录并放入 Game/NPC 配置；首次启动后也可通过管理台创建配置。

```powershell
docker run -d --name aibot-server -p 8080:8080 `
  -e AIBOT_DATA_ROOT=/app/data `
  -e AIBOT_ADMIN_TOKEN="替换为长随机管理令牌" `
  -e AIBOT_CLIENT_TOKEN="替换为另一组长随机客户端令牌" `
  -v D:\aibot-data:/app/data `
  aibot-server:latest
```

对外提供服务前设置 `AIBOT_ADMIN_TOKEN` 和 `AIBOT_CLIENT_TOKEN` 两个不同的长随机值，并在 Unity Connection Profile 中设置对应客户端令牌。Mongo 模式还需设置 `AIBOT_STORAGE_PROVIDER=Mongo`、`AIBOT_MONGO_CONNECTION_STRING`、`AIBOT_MONGO_DATABASE` 和 `AIBOT_MONGO_AUTOMIGRATE=true`。当前 `docker.yml` 只启动 MongoDB；Server 镜像需单独运行或由部署平台编排。正式环境还应通过 HTTPS 提供访问，并限制管理 API 和管理台的访问范围。

### 管理控制台

管理台采用五个主入口：调试（对话、Prompt、会话）、内容（NPC、世界观）、记忆（记忆检查、Game 策略、NPC 覆盖、系统边界、审计）、模型（模型连接）和运维（日志、统计、高级操作）。旧地址继续可用；低频记忆迁移位于运维的高级操作子页。顶部可切换 Game/NPC，底部显示存储模式与 Server 状态。

**模型连接**页统一创建 Server 模型连接并分配给控制台默认、NPC 主模型、Game 摘要或 NPC 摘要。NPC 主模型使用专用连接或控制台默认连接；未分配时 Server 返回明确的配置错误，不从 NPC 文件回退。连接地址、模型 ID 和 Key 保存在不入 Git 的 `data/model-connections.json`，Key 仅脱敏回显。旧 NPC/Game 内嵌连接在 Server 启动时幂等迁入；NPC 配置页和记忆策略页只编辑生成参数。Unity `local` 模式仍由本地 Asset 配置。

**Game / NPC 管理**：Game 入口可应用记忆策略预设并创建 Game；内容入口可创建、编辑 NPC 与世界观。**记忆**入口提供策略、检查、审计和系统边界功能；Mongo 模式的 JSON→Mongo 迁移位于运维入口的高级操作中（幂等，与 `--migrate-json` 等效）。公共部署务必设置 `AIBOT_ADMIN_TOKEN` 保护管理 API。
- **会话按 NPC 隔离**：调试对话页为每个 NPC 记住独立会话，切换 NPC 自动切换会话，不再串扰。
- **时间显示**：会话与日志页的时间统一转换为本地时区显示，原始 UTC 值可在日志行展开或悬浮提示中查看。

## Unity 接入

Unity 2022.3 游戏工程可在 `Packages/manifest.json` 中添加本地包依赖；将路径替换为仓库所在位置：

```json
{
  "dependencies": {
    "com.aibot.npcagent": "file:<仓库路径>/Packages/com.aibot.npcagent"
  }
}
```

菜单 **AIBot → Demo → Create Demo Scene** 可生成示例场景。包依赖和代码调用详见 [Unity 插件说明](./Packages/com.aibot.npcagent/README.md)。

API key 不应提交到仓库：`.gitignore` 已忽略全部 NPC 真实配置（`data/games/*/npcs/*.json`，模板 `new_npc.template.json` 除外）与模型连接配置（`data/model-connections.json`）。Server 模式在管理台「模型」入口统一配置。

Unity 运行模式由 NpcAgent 上指定的配置资源决定：`AgentConfigAsset` 表示 Local，`AIBotConnectionProfile` 表示 Server；旧 JSON 可使用 Unity Editor 导入菜单迁移，不再由 NpcAgent 在运行时搜索目录。`local` 模式由 Unity 直连模型；`server` 模式由 Unity 调用
`/api/games/{gameId}/chat/stream`，长期记忆、摘要、工具和日志由 Server 处理。
`NpcAgent` 上的 `playerId` 可选，填写后启用玩家范围长期记忆；`sessionId` 用于复用短期会话。

如果使用 Server Connection Profile，则不需要在 Unity 中保存完整 NPC 配置或模型 API Key；Profile 会直接以 Server 模式初始化连接。正式部署时还应在 Server 设置 `AIBOT_CLIENT_TOKEN`，并在 Profile 的 `serverAuthToken` 填入同一令牌；聊天 API 会拒绝未携带该令牌的请求。Local 模式仍建议使用 `AgentConfigAsset`，这样不依赖后台即可运行。

Local 模式还可以在 `NpcAgent.worldConfigAsset` 指定 `World Config` 资源，并在 `AgentConfigAsset` 中填写开发期模型 Key；这样整个 Demo 不需要复制仓库的 `data/` 目录。真实项目请勿把含 Key 的 Asset 提交到公共仓库。

工具能力按运行模式隔离：Local 模式由 Unity 通过 `NpcAgent.Tools` 注册并执行；Server 模式默认由 `AIBot.Server` 注册和执行（simulated 调试），Unity 本地注册的工具不会自动上传。需要让 Server 编排、Unity 真实执行时，使用 Profile 的 `enableGameTools`（game 模式回传，见上文）。插件会在检测到模式与工具配置不匹配时给出运行时警告。

Server 模式的工具边界按 `toolMode` 分为三种：

- `none`（默认）：不注册任何工具，纯对话。
- `simulated`（仅调试）：Vue 调试台或 Unity Profile 显式选择后启用 `SimulatedToolHost`（背包、好感度和剧情阶段只写入会话模拟状态，不触碰正式游戏）。
- `game`（工具回传）：Server 遇到模型工具调用时不执行，挂起对话并下发 `tool_pending` 终态事件；Unity 在本地用 `NpcAgent.Tools` 真实执行（QuestSystem/背包等游戏代码负责"能不能做"），随后携带 `roundToken` 与结果发起续跑请求，Server 从挂起态恢复对话。使用条件：Connection Profile 勾选 `enableGameTools`（与 `enableSimulatedTools` 互斥）、NpcAgent 已注册本地工具、NPC 配置的 `enabledToolIds` 包含对应工具。工具执行结果都会通过 Unity `onToolExecuted` 统一通知；工具调用意图与结果写入 Unity 持久化执行记录，重放复用已保存结果；执行中崩溃会阻止自动重做，游戏工具仍应以业务幂等键保护副作用。

Server 模式可调用 `await agent.CheckServerAsync()` 主动检查后台连接、就绪状态和当前 NPC 是否存在；结果会通过 `onServerStatus` 事件通知，不会给每次聊天额外增加检查请求。
`UnityServerBackend` 会自动为每轮生成 `requestId` 并最多恢复重试两次，同时消除重放 token、reasoning 和工具事件。需要显式恢复时可读取 `agent.LastServerRequestId`，再调用 `RetryServerRequestAsync(message, requestId)`。

## 可靠性与运行边界

- 管理台使用 `AIBOT_ADMIN_TOKEN`，它可访问聊天接口以及模拟工具/模型覆盖；游戏客户端使用独立的 `AIBOT_CLIENT_TOKEN`，不能访问管理写入和调试覆盖。
- JSON 模式通过 data 目录文件锁阻止并发 Server。Mongo 模式还通过 `_meta` 中的数据库时间租约阻止同库多写者；续期失败时停止服务。正常退出释放租约，异常退出后替代实例最多等待两分钟。当前不支持多个活跃 Server 并行服务同一数据库，CLI 迁移也应在原 Server 停止后执行。
- 每个 Session 保留最近 20 个请求的完整终态重放。更旧的 ID 保留去重标记，再次提交返回 `409 request_replay_expired`，不会重新执行。删除或过期清理 Session 后该范围失效；不要复用历史请求 ID。会话读取失败时拒绝执行，不会从空白状态覆盖历史。
- Unity game 模式把工具执行记录保存在 `Application.persistentDataPath/AIBot/tool-rounds`。应随游戏存档一起保留；删除这些记录会丢失客户端防重保证。重启或续跑失败后调用 `ResumeServerToolsAsync()`，它会复用已记录的结果和续跑 requestId。若工具执行中崩溃，必须先查询游戏权威数据，再调用 `ConfirmServerToolResult(roundToken, callId, success, message)` 确认结果后恢复。`PendingServerToolRoundToken` 可读取待恢复令牌。
- 用量统计从当前存储保留期内的日志重建，包含工具挂起轮、同步和后台摘要，不再重启清零；只计入模型返回的 usage，不等同供应商账单。

回归测试：`dotnet test src/AIBot.Tests/AIBot.Tests.csproj`；前端在 `src/AIBot.Web` 运行 `npm test`。设置 `AIBOT_MONGO_TEST_CONNECTION` 后启用 Mongo 集成测试；CI 默认运行这些测试。HTTP 测试会启动独立的临时 Server 和本地模拟模型，不使用真实模型 Key。

