# 记忆管理与 Vue 控制台设计方案

> 文档版本：v1.5（2026-08-26）
> 状态：阶段 A、B、C、D 已实施  
> 适用范围：AIBot.Core、Unity 运行时、AIBot.Server、`AIBot.Web` Vue 管理端

## 1. 结论

本需求需要同时修改三层，而不只是增加 Vue 页面：

1. **AIBot.Core**：定义统一的记忆策略、结构化事实和最终生效配置，保证 Server 与 Unity 使用相同语义。
2. **AIBot.Server**：负责配置分层合并、安全上限、记忆持久化、迁移、管理 API 和审计。
3. **Unity 运行时**：只消费 Core 契约和一个 `ILlmBackend`，支持 Local 直连或 Server 中转，不直接连接数据库。
4. **Vue 管理端**：负责游戏级策略、NPC 覆盖、玩家记忆检查与自定义字段的可视化管理。

原单文件管理台的调试能力统一迁移到现有 Vue 工程；`wwwroot/index.html` 仅作为根路径跳转入口，正式控制台不再维护两套前端。

## 2. 设计目标

- 将一次对话的短期上下文与玩家/NPC 的长期关系记忆分开。
- 支持 Server、Game、NPC、Session 四级配置，并能解释每个最终值来自哪里。
- Server 保留不可突破的安全上限，Vue 只能在允许范围内配置。
- 策划可以配置 NPC 记忆偏好，但不能接触 API key、文件路径等敏感项。
- 测试人员可以查看、纠正、删除、重新摘要玩家记忆。
- 支持游戏自定义记忆字段，同时避免 Core DTO 无限增加游戏专属字段。
- 保持现有 JSON 存储可用，同时提供 MySQL+Dapper 替换实现；两种存储由 Server 配置切换。
- 对现有 session JSON 做兼容迁移，不要求一次性停机转换全部数据。

## 3. 非目标

本阶段不实现：

- 向量数据库和语义检索 RAG。
- 多 Server 实例共享会话。
- 完整用户账号、组织和复杂 RBAC。
- 玩家端记忆编辑功能。
- 跨游戏共享玩家身份。

## 4. 总体架构

```text
┌──────────────────────── Vue 管理端（可选后台工具） ───────┐
│ 系统边界 │ 游戏策略 │ NPC覆盖 │ 记忆检查器 │ extensions 高级字段 │ 审计 │
└──────────────────────────┬─────────────────────────────────┘
                           │ 管理 API
┌──────────────────────────▼─────────────────────────────────┐
│                       AIBot.Server                          │
│ MemoryPolicyService      配置读取、合并、校验、来源解释       │
│ PlayerMemoryService      玩家/NPC 长期记忆管理               │
│ SessionStore             单次会话短期记忆                    │
│ MemorySummaryQueue       后台摘要队列                        │
│ IMemoryRepository        JSON 存储实现，可替换数据库           │
│ MemoryAuditService       配置与人工记忆修改审计               │
└──────────────────────────┬─────────────────────────────────┘
                           │ 共享契约与算法
┌──────────────────────────▼─────────────────────────────────┐
│                        AIBot.Core                           │
│ MemoryPolicy / EffectiveMemoryPolicy / MemoryFact          │
│ MemoryPolicyResolver / MemorySummarizer / ContextBuilder    │
└────────────────────────────────────────────────────────────┘
```

### 客户端部署边界

Unity 游戏运行时不属于 Vue 管理端，也不直接连接 MySQL。Unity 只打包 `AIBot.Core`、`AIBot.Unity` 和一个 `ILlmBackend` 实现：

```text
Local 开发模式：Unity → OpenAI 兼容 LLM
Server 正式模式：Unity → AIBot.Server → LLM / MySQL
Vue 管理端：Vue → AIBot.Server → MySQL
```

Local 模式用于 Demo、单机和离线联调；Server 模式用于在线游戏，负责隐藏模型 Key、统一校验游戏状态、保存玩家长期记忆、审计和限流。Vue、ASP.NET Core、MySQL、Dapper 和管理 API 都不进入 Unity 构建包。两种模式通过同一 `NpcAgent.ChatAsync()` 和事件契约切换，游戏业务层无需改写。

当前 Unity 包已实现 `UnityWebRequestBackend`（Local 模式）和 `UnityServerBackend`（Server 模式）。在 `AgentConfigAsset` 或 NPC JSON 中设置 `runtimeMode=server`、`serverBaseUrl`，并在 `NpcAgent` 上填写可选的 `playerId/sessionId` 即可通过 Server 中转；鉴权暂不强制，后续可在传输层增加。

## 5. 配置分层

### 5.1 Server 全局边界

Server 配置通过 `appsettings.json` 与环境变量维护，不允许 Vue 修改敏感字段。

```json
{
  "Memory": {
    "MaxShortTermTurns": 50,
    "MaxSummaryThreshold": 200,
    "MaxFacts": 20,
    "AllowBackgroundSummarization": true,
    "SummaryQueueCapacity": 256,
    "RetentionDays": 90,
    "SupportedSummaryTriggers": ["message_count"],
    "SupportedMemoryScopes": ["session", "player_npc"]
  }
}
```

当前实现使用以上配置键；保留期清理通过管理 API 显式触发，不是独立的定时后台任务。

Vue 可以只读展示这些边界，但以下内容不得通过 API 返回或修改：

- API key。
- 数据根目录和数据库连接字符串。
- 加密密钥。
- 管理鉴权 token。

### 5.2 Game 默认策略

新增文件：

```text
data/games/{gameId}/memory-policy.json
```

示例：

```json
{
  "policyVersion": 1,
  "shortTermTurns": 12,
  "summaryThreshold": 20,
  "summaryTrigger": "message_count",
  "memoryScope": "player_npc",
  "maxFacts": 8,
  "rememberPlayerProfile": true,
  "rememberPromises": true,
  "rememberQuestEvents": true,
  "rememberCasualChat": false,
  "backgroundSummarization": true,
  "summaryModel": null,
  "extensions": {}
}
```

### 5.3 NPC 覆盖配置

NPC 的 `memory` 改为“可空覆盖值”，避免每个 NPC JSON 都复制完整默认配置。

```json
{
  "memory": {
    "inheritGameDefaults": true,
    "shortTermTurns": null,
    "summaryThreshold": 10,
    "rememberCasualChat": false,
    "extensions": {
      "rememberTradeHistory": true
    }
  }
}
```

`null` 表示继承 Game 默认值；显式值表示 NPC 覆盖。

### 5.4 Session 临时覆盖

仅用于测试和调试，不写回 Game/NPC 配置：

```json
{
  "memoryOverride": {
    "disabled": false,
    "shortTermTurns": 4,
    "forceSummarizeAfterReply": true
  }
}
```

生产聊天接口默认不允许普通玩家提交任意记忆覆盖；该字段只对管理端调试请求或具备管理权限的调用开放。

### 5.5 合并优先级

```text
Session 临时覆盖
    ↓
NPC 显式覆盖
    ↓
Game 默认策略
    ↓
Core 默认值
    ↓
Server 安全上限最终裁剪
```

Server 返回最终配置时，同时返回来源：

```json
{
  "effective": {
    "shortTermTurns": 16,
    "summaryThreshold": 10,
    "maxFacts": 8
  },
  "sources": {
    "shortTermTurns": "npc",
    "summaryThreshold": "npc",
    "maxFacts": "game"
  },
  "limits": {
    "maxShortTermTurns": 50,
    "maxFacts": 20
  }
}
```

## 6. 记忆归属模型

### 6.1 当前问题

当前长期摘要与 `sessionId` 绑定。创建新 session 后，NPC 无法继续识别同一个玩家。

### 6.2 新模型

引入必填或可逐步启用的 `playerId`：

```text
短期记忆键：gameId + npcId + playerId + sessionId
长期记忆键：gameId + npcId + playerId
```

数据目录建议调整为：

```text
data/games/{gameId}/
├── sessions/{npcId}/{playerId}/{sessionId}.json
└── memories/{npcId}/{playerId}.json
```

短期 session 文件只保存：

- 最近对话消息。
- 待摘要消息。
- 本次谈话状态。
- 最后活跃时间。

长期 memory 文件保存：

- 滚动摘要。
- 结构化事实。
- 最近摘要时间。
- 记忆版本。
- 来源 session 列表或最近来源。

`playerId` 必须经过与 `npcId` 相同级别的长度和字符校验。正式游戏应使用内部稳定 ID，不使用玩家昵称作为目录名。

## 7. 长期事实模型

将当前 `List<string> facts` 升级为结构化对象：

```csharp
public sealed class MemoryFact
{
    public string Id;
    public string Category;
    public string Key;
    public string Value;
    public float Confidence;
    public string Source;
    public string SourceSessionId;
    public DateTime CreatedUtc;
    public DateTime UpdatedUtc;
    public bool Pinned;
    public DateTime? ExpiresUtc;
}
```

示例：

```json
{
  "id": "fact-01",
  "category": "player_profile",
  "key": "player.name",
  "value": "小明",
  "confidence": 0.95,
  "source": "player_statement",
  "sourceSessionId": "s-123",
  "pinned": false,
  "createdUtc": "2026-08-25T08:00:00Z",
  "updatedUtc": "2026-08-25T08:00:00Z",
  "expiresUtc": null
}
```

事实合并规则：

1. 相同 `key` 默认更新原事实，不无限追加。
2. 新事实可信度较低且与旧事实冲突时，保留旧事实并标记冲突。
3. `pinned=true` 的事实不能被模型摘要自动删除或覆盖。
4. 游戏权威状态不写成普通事实；剧情阶段、背包、货币、任务状态仍以 `IGameContext` 为准。
5. 过期事实由 `ExpiresUtc` 清理，适合临时话题和短期偏好。

## 8. Core 改造

### 8.1 新增类型

建议新增：

```text
Runtime/Core/Memory/
├── MemoryPolicy.cs
├── EffectiveMemoryPolicy.cs
├── MemoryPolicyResolver.cs
├── MemoryFact.cs
├── PlayerLongTermMemory.cs
└── FactMerger.cs
```

### 8.2 AgentRunInput 调整

```csharp
public sealed class AgentRunInput
{
    public AgentConfigDto Config;
    public WorldConfigDto World;
    public IGameContext Game;
    public string UserMessage;
    public ShortTermMemory Memory;
    public string MemorySummary;
    public List<string> MemoryFacts;
    public MemoryPolicy ResolvedMemoryPolicy;
    public bool DeferMemorySummarizationToHost;
}
```

AgentLoop 不负责读取配置文件，只消费 Server/Unity 已经解析好的最终策略。

### 8.3 ContextBuilder 调整

“关于玩家的记忆”层按类别组织：

```text
# 关于玩家的记忆
关系摘要：……

玩家档案：
- 姓名：小明
- 职业：冒险者

承诺与剧情：
- 答应调查矿洞
```

如果记忆与 `IGameContext` 冲突，Prompt 明确声明当前游戏状态优先。

### 8.4 摘要输出契约

摘要模型输出升级为：

```json
{
  "summary": "玩家小明曾帮助铁匠调查矿洞。",
  "facts": [
    {
      "category": "player_profile",
      "key": "player.name",
      "value": "小明",
      "confidence": 0.95,
      "source": "player_statement"
    }
  ],
}
```

解析失败时保留旧长期记忆和待摘要消息，不影响主回复。

## 9. Server 改造

### 9.1 服务划分

新增服务：

```text
MemoryPolicyService
  - 读取 Server/Game/NPC/Session 配置
  - 合并并应用安全上限
  - 返回 EffectiveMemoryPolicy 与来源

PlayerMemoryService
  - 加载与保存 playerId + npcId 长期记忆
  - 人工编辑、删除、固定事实
  - 触发重新摘要

MemorySummaryQueue
  - 主回复完成后排队摘要
  - 控制并发与容量
  - 失败重试并保留待摘要消息
  - 玩家级 generation 与互斥，防止删除后旧任务复活记忆

IMemoryRepository
  - 存储抽象
  - 第一版 JsonMemoryRepository

MemoryAuditService
  - 记录谁修改了什么记忆或策略
  - 关键写入必写审计，失败重试并显式返回错误
```

### 9.2 后台摘要

Server 玩家长期记忆默认采用后台摘要；Unity 或没有后台宿主队列的调用方仍在 AgentLoop 内同步摘要。Server 路径改为：

```text
主回复完成
→ 保存短期消息与待摘要队列
→ 立即返回 reply/done
→ 投递 MemorySummaryJob
→ 后台加载最新记忆并摘要
→ 使用版本号或 ETag 防止覆盖更新
→ 写入必写审计
→ 审计成功后确认消费待摘要消息
```

后台任务需要携带：

- gameId、npcId、playerId。
- 触发时的 memoryVersion。
- 待摘要消息 ID 或快照。
- 最终生效的摘要模型设置标识。

如果版本发生变化，任务重新加载并合并，不直接覆盖新数据。

实现约束：

- `AgentRunInput.DeferMemorySummarizationToHost` 只有 Server 玩家后台队列路径设置为 `true`；Unity 默认 `false`。
- `summaryThreshold=0` 表示关闭自动摘要：Session 范围直接丢弃窗口外消息；玩家范围保留最近一个短期窗口，供管理端手动摘要，但不会无限积累。
- 摘要任务带玩家级 generation。删除长期记忆或保留期清理时，旧 generation 立即失效，并在同一玩家互斥区间完成长期文件删除和全部关联 Session 清理。
- 后台摘要只有在长期记忆、必写审计和 Session 确认落盘均成功后，才会移除待摘要消息；任一步失败都会保留批次等待重试。
- 单个后台任务自动重试 3 次；耗尽后记录游戏/NPC/玩家/Session、错误和时间，状态为 `failed`。失败批次仍保存在 `evictedMessages` 中，可通过管理 API 或 Vue 重新排队。
- 会话摘要状态统一为 `idle`、`waiting`、`pending`、`failed`，用于管理台展示和人工判断。

### 9.3 存储接口

```csharp
public interface IMemoryRepository
{
    Task<PlayerLongTermMemory> LoadPlayerMemoryAsync(string gameId, string npcId, string playerId, CancellationToken ct);
    Task<PlayerLongTermMemory> SavePlayerMemoryAsync(PlayerLongTermMemory memory, int expectedVersion, CancellationToken ct);
    Task<MemoryListPage> ListPlayerMemoriesAsync(string gameId, string npcId, string playerId, int limit, int offset, CancellationToken ct);
    Task DeletePlayerMemoryAsync(string gameId, string npcId, string playerId, int? expectedVersion, CancellationToken ct);
}
```

JSON 仍是默认开发存储，并采用逐文件信号量、乐观版本检查、临时文件 + 原子替换。MySQL 模式使用 Dapper、InnoDB 事务和 `memoryVersion` 乐观锁；长期记忆、Session、聊天日志和审计分别落到数据库表，Server API 契约保持不变。Session 由 `SessionStore` 独立管理，不与长期记忆仓储耦合。

当前 MySQL 表结构位于 `database/mysql/schema.sql`，Server 也支持 `Storage:MySql:AutoMigrate=true` 自动建表。迁移由 `schema_migrations` 版本表管理，当前版本 001 为基础表、002 为 `memory_summary_jobs` 摘要任务表；人工迁移 SQL 参考位于 `database/mysql/migrations/`。现有 JSON 长期记忆可通过 `dotnet run -- --migrate-json --exit-after-migrate` 幂等迁移；鉴权/登录不作为本阶段前置条件。

开发环境可直接执行项目根目录的 `docker compose -f docker.yml up -d mysql` 启动 MySQL。该 Compose 文件只包含数据库服务，不会把 Unity、Vue 或 Server 打包进容器。默认使用宿主机 `3306`；若被其他 MySQL 占用，可在 `.env` 设置 `AIBOT_MYSQL_PORT`（本机示例为 `3307`），Server 连接宿主机映射端口，容器内部仍使用 `mysql:3306`。

本机联调示例账号为 `aibot` / `123456`。宿主机 Server 的连接串需要包含 `SslMode=None;AllowPublicKeyRetrieval=True` 以兼容本地 Docker MySQL 的 `caching_sha2_password`；线上部署应使用 TLS 与独立强密码。

Windows 本地开发可从项目根目录执行 `.\start-server-mysql.ps1`。该脚本读取 `.env` 中的数据库名、账号、密码和映射端口，确保 MySQL 容器健康后为当前 AIBot.Server 进程生成连接串；不需要永久写入 Windows 用户环境变量。

### 9.4 管理 API

#### 系统边界

```text
GET /api/admin/memory-limits
```

只读返回非敏感限制。

#### Game 策略

```text
GET /api/games/{gid}/memory-policy
PUT /api/games/{gid}/memory-policy
```

#### NPC 最终策略

```text
GET /api/games/{gid}/npcs/{npcId}/memory-policy
PUT /api/games/{gid}/npcs/{npcId}/memory-policy
POST /api/games/{gid}/npcs/{npcId}/memory-policy/preview-effective
```

#### 玩家长期记忆

```text
GET    /api/games/{gid}/memories?npcId=&playerId=&limit=&offset=
GET    /api/games/{gid}/memories/{npcId}/{playerId}
PUT    /api/games/{gid}/memories/{npcId}/{playerId}/summary
POST   /api/games/{gid}/memories/{npcId}/{playerId}/facts
PUT    /api/games/{gid}/memories/{npcId}/{playerId}/facts/{factId}
DELETE /api/games/{gid}/memories/{npcId}/{playerId}/facts/{factId}
POST   /api/games/{gid}/memories/{npcId}/{playerId}/summarize
DELETE /api/games/{gid}/memories/{npcId}/{playerId}
GET    /api/games/{gid}/memories/{npcId}/{playerId}/export
POST   /api/games/{gid}/memories/cleanup
```

摘要、事实和整份记忆写操作必须携带 `expectedVersion`。如果后台摘要或其他管理员已经更新记忆，Server 返回 HTTP 409 及当前版本，客户端刷新后再提交。

旧会话显式迁移：

```text
GET  /api/games/{gid}/memory-migrations?npcId=
POST /api/games/{gid}/sessions/{sid}/migrate-memory?npcId=&playerId=
```

#### 审计

```text
GET /api/games/{gid}/memory-audit?npcId=&playerId=&date=
```

所有写接口记录操作前后差异。管理 Bearer 鉴权保持可选，当前本地默认关闭；API key 始终不回传。
管理端可通过 `X-AIBot-Actor` 标记操作人；未提供时使用管理端 IP 作为兼容身份。

人工修改、删除、迁移、策略变更和后台摘要使用 `RecordRequired`：短暂 I/O 故障最多重试三次，最终失败时管理 API 返回 HTTP 503；后台任务不确认消费待摘要批次。

#### 摘要队列

```text
GET  /api/admin/memory-summary-queue
POST /api/admin/memory-summary-queue/retry
```

查询接口返回 `pending`、`failedCurrent`、`failedTotal` 和 `failures` 明细。重试接口可传入 `gameId`、`npcId`、`playerId`、`sessionId` 进行过滤；空对象表示重试全部当前失败任务。会话列表额外返回 `summaryStatus`、`summaryError` 与 `summaryFailedUtc`。

#### 保留期清理

`POST /api/games/{gid}/memories/cleanup` 使用 `inactiveDays`、`dryRun` 和 `limit`。仓储列表按更新时间倒序返回，Server 从分页末尾读取最旧批次，避免只扫描最新数据而漏掉应清理记录。响应包含：

- `totalMemoryCount`：当前 Game 的长期记忆总数。
- `batchLimit`：本次最多处理数量。
- `candidateCount`、`candidates`：本次候选。
- `hasMoreCandidates`：仍有更旧候选时为 `true`，前端应重新预演后继续下一批。

执行删除会逐条检查 `memoryVersion`，失效旧摘要任务，删除长期文件，清空该玩家与 NPC 的全部关联 Session 消息及待摘要队列，并写入审计。预演结果只对原 `gameId + inactiveDays` 有效，修改条件后必须重新预演。

### 9.5 Chat 请求调整

```json
{
  "npcId": "blacksmith_wang",
  "playerId": "player-001",
  "sessionId": "s-123",
  "message": "你还记得我吗？"
}
```

兼容规则：

- 未提供 `playerId` 时继续使用旧的 session 范围记忆，不创建伪玩家长期文件，并在日志标记 `legacyMemoryScope=true`。
- 正式环境可通过配置逐步将 `playerId` 设为必填。

## 10. Vue 管理端设计

### 10.1 工程位置

新增正式工程：

```text
src/AIBot.Web/
├── src/api/
├── src/types/
├── src/stores/
├── src/components/memory/
├── src/views/settings/
├── src/views/memory/
└── src/views/npc/
```

技术栈沿用现有 Vue 方案：Vue 3、TypeScript、Vite、Pinia、Element Plus。

### 10.2 页面结构

#### 系统记忆边界

路由：`/settings/memory`

展示 Server 只读限制、摘要队列状态和存储状态：

- 最大短期轮数。
- 最大事实数。
- 保留天数。
- 摘要队列长度、并发数和失败数。
- 当前失败任务明细、错误时间和一键重试。
- 记忆检查器显示每个关联 Session 的摘要状态；失败时可针对单个 Session 重试。
- 原始消息持久化是否允许。

#### Game 记忆策略

路由：`/game/memory-policy`

- 基础策略表单。
- 低成本/标准/剧情/无记忆预设。
- 自定义字段 Schema。
- 最终值与 Server 上限提示。

#### NPC 记忆覆盖

整合进 `/npc/:id/edit` 的“记忆”Tab：

- 每个字段提供“继承/覆盖”开关。
- 显示继承来源。
- 实时调用 `preview-effective`。
- 显示修改对现有会话的影响。

#### 记忆检查器

路由：`/memories`

列表支持按 NPC、playerId、最近更新时间、冲突状态过滤。

详情页包含：

- 长期摘要编辑器。
- 结构化事实表格。
- 事实来源、可信度、时间和是否固定。
- 最近短期消息和待摘要消息。
- 手动新增、修改、删除、固定事实。
- 立即摘要、清空记忆、导出 JSON。

#### 审计记录

路由：`/memory-audit`

展示配置和记忆人工修改记录，支持查看修改前后差异。

### 10.3 自定义字段

Game 级定义自定义字段 Schema：

```json
{
  "key": "relationshipDecayDays",
  "label": "关系记忆衰减天数",
  "type": "number",
  "min": 0,
  "max": 365,
  "default": 30,
  "description": "0 表示不衰减"
}
```

第一版支持字段类型：

- boolean。
- number。
- string。
- enum。
- string array。

Vue 根据 Schema 动态生成表单；Server 使用同一 Schema 校验 `extensions`。未知字段拒绝保存，避免拼写错误形成脏配置。

### 10.4 Pinia Store

```text
useMemoryLimitsStore       Server 只读边界与队列状态
useGameMemoryPolicyStore   Game 策略编辑与预设
useNpcMemoryPolicyStore    NPC 覆盖与最终值预览
useMemoryInspectorStore    玩家记忆列表和详情
useMemoryAuditStore        审计查询
```

### 10.5 权限

第一版继续使用现有管理 token，但前端按功能预留权限标识：

```text
memory.policy.read
memory.policy.write
memory.inspect
memory.edit
memory.delete
memory.audit
```

后续接入账号系统时不需要重写页面逻辑。

## 11. 配置变更行为

不同配置的生效方式必须明确：

| 配置 | 生效时间 | 对旧数据的处理 |
|---|---|---|
| 短期轮数 | 下一轮对话 | 超出新窗口的消息进入待摘要队列 |
| 摘要阈值 | 下一轮检查 | 达到新阈值立即排队摘要 |
| 摘要模型 | 下一个摘要任务 | 已完成摘要不重跑 |
| 最大事实数 | 下次事实合并 | pinned 事实不自动删除 |
| 记忆类别开关 | 下次摘要 | 新生成的摘要/Facts 不再写入禁用类别；已有事实可在记忆检查器中人工清理 |
| `summaryThreshold=0` | 下一轮检查 | Session 丢弃窗口外消息；玩家范围仅保留最近短期窗口供手动摘要 |
| memoryScope | 需要迁移确认 | 不自动合并不同玩家或会话 |
| Server 保留期 | 管理端显式清理 | 删除前记录审计统计，并清空关联 Session |

危险变更如 `memoryScope` 必须在 Vue 弹出迁移确认，不允许静默切换。

## 12. 数据迁移

### 12.1 版本字段

新文件统一增加：

```json
{
  "schemaVersion": 2,
  "memoryVersion": 1
}
```

### 12.2 懒迁移策略

读取旧 session 时：

1. 识别没有 `schemaVersion` 的 v1 文件。
2. 将 `summary` 和字符串 `facts` 转为兼容长期记忆。
3. 如果请求提供 `playerId`，将长期部分迁移到 `memories/{npcId}/{playerId}.json`。
4. 原 session 文件保留短期消息与迁移标记。
5. 写入时使用 v2 格式。

旧字符串事实迁移为：

```json
{
  "category": "legacy",
  "key": "legacy.{hash}",
  "value": "原事实文本",
  "confidence": 0.5,
  "source": "migration"
}
```

迁移必须幂等，重复读取不能重复生成事实。

## 13. 测试方案

### Core 单元测试

- 四级策略合并与来源解释。
- Server 上限裁剪。
- 结构化事实去重、冲突、固定和过期。
- 新摘要 JSON 的容错解析。
- 游戏状态优先于语言记忆。

### Server 集成测试

- Game/NPC 策略 CRUD。
- API key 和敏感配置不回传。
- playerId/sessionId 路径校验。
- 旧 session 懒迁移。
- 后台摘要失败重试且不丢消息。
- 并发摘要的版本冲突处理。
- 手动修改和删除产生审计记录。
- 保留期清理从最旧分页开始，并在有更多候选时返回 `hasMoreCandidates`。
- Session 持久化文件删除失败时不移除缓存状态，接口返回明确错误。

### Vue 测试

- 继承/覆盖切换。
- 最终配置来源展示。
- 自定义 Schema 表单和校验。
- 记忆检查器的增删改和固定操作。
- 危险操作确认。
- 401、409 版本冲突和 422 校验错误展示。
- 清理预演在 `gameId` 或未活跃天数变化后失效，不能直接执行旧候选。
- 清理、整份删除和 Session 删除的确认文案与实际影响范围一致。

## 14. 实施阶段

### 阶段 A：契约与配置解析（✅ 2026-08-25 已完成）

- 新增 Core 记忆策略与结构化事实 DTO。
- 实现 `MemoryPolicyResolver`。
- 增加 Game 策略文件和 NPC nullable 覆盖。
- 增加最终配置预览 API。

验收：同一 NPC 的每个最终值都能说明来源，并受 Server 上限约束。

### 阶段 B：玩家长期记忆与后台摘要（✅ 2026-08-25 已完成）

- 引入 playerId。
- 拆分 session 短期记忆与 player/NPC 长期记忆。
- 实现 `IMemoryRepository`、JSON 版本和 MySQL/Dapper 版本。
- 实现后台摘要队列、版本控制和失败重试。
- 实现旧数据懒迁移。

验收：更换 session 后同一玩家仍被 NPC 记住；摘要不阻塞 reply/done；重启和失败不丢记忆。

实现补充：

- Chat 契约已加入可选 `playerId`，短期会话按 `gameId+npcId+playerId+sessionId` 隔离。
- 长期记忆采用 `memories/{npcId}/{playerId}.json`，带 `schemaVersion` 与乐观 `memoryVersion`。
- `MemorySummaryQueue` 在 `reply/done` 刷新后投递，使用有界 Channel、任务去重、三次失败重试和启动恢复扫描。
- 只有长期记忆提交和审计写入都成功后才消费 session 的待摘要消息；失败与进程重启均保留原始批次。
- 失败任务在队列中保留可查询明细；管理端可按范围重新排队，成功后清除失败记录。会话列表返回 `idle/waiting/pending/failed` 状态，Vue 记忆检查器提供全局和会话级重试入口。
- 后台处理使用玩家级任务代数和互斥锁；清空记忆时旧任务先失效，再在同一独占区间删除长期文件并清理全部 Session，避免并发摘要复活数据。
- `summaryThreshold=0` 统一表示关闭自动摘要：Session 范围丢弃窗口外消息，玩家范围仅保留最近一个短期窗口供手动摘要。
- v1 session 的摘要和字符串事实按需幂等迁移，成功后写 v2 玩家会话并归档旧文件。
- 阶段 B 额外提供长期记忆只读调试 API 与摘要队列状态 API；人工编辑和审计仍属于阶段 C。

### 阶段 C：管理 API、迁移与审计（✅ 2026-08-25 已完成）

- 实现策略、记忆检查、事实编辑、重新摘要和审计 API。
- 增加导出、清空与数据保留接口。
- 完成参数校验和敏感字段过滤；管理 API 鉴权保持可选，当前本地默认关闭。

实现补充：

- 玩家长期记忆支持分页筛选、摘要编辑、事实新增/编辑/删除/固定、导出和整份清空。
- 所有人工写操作使用 `expectedVersion` 乐观并发控制，过期版本统一返回 HTTP 409。
- 增加旧 session 迁移候选查询与显式迁移接口；迁移保持幂等并归档 v1 文件。
- 手动摘要通过原后台队列执行，不阻塞管理请求，完成后记录前后快照。
- 审计按 `data/logs/{gameId}/memory-audit/yyyy-MM-dd.jsonl` 保存，支持日期、NPC、玩家和动作过滤。
- 人工写操作与后台摘要使用必写审计，短暂 I/O 故障重试三次；最终失败时管理 API 返回 HTTP 503，后台任务保留待摘要批次，不再静默返回成功。
- 数据保留提供默认 90 天边界、清理预演和显式执行；实际删除逐条进行版本检查并写审计。
- 阶段 C API 由统一 Vue 控制台的记忆治理与调试页面共同使用，保留 API 兼容性但不再维护第二套前端。

验收：所有记忆变更可追踪、可恢复来源、不会泄露密钥。

### 阶段 D：Vue 控制台（✅ 2026-08-26 已完成）

- 创建 `AIBot.Web` 工程。
- 实现系统边界、Game 策略、NPC 覆盖、记忆检查器、迁移、审计，以及原调试台的对话、NPC/世界观、Prompt、Session、日志和统计页面。
- 增加高级 `extensions` JSON 对象编辑与格式校验；动态 Schema 生成器保留为后续增强。
- 构建产物部署到 Server `wwwroot/app/`。

实现补充：

- 正式工程采用 Vue 3、TypeScript、Vite、Pinia、Vue Router 与 Element Plus，使用 Hash 路由部署在 `/app/`；根路径 `/` 自动跳转 `/app/#/debug/chat`，不再保留独立原生调试入口。
- 13 个路由覆盖系统边界、Game 策略预设、NPC 按字段继承/覆盖、玩家摘要与事实 CRUD、409 版本冲突刷新、旧 Session 显式迁移、审计差异和保留期清理，以及流式对话、注入/A-B 测试、NPC/世界观编辑、Prompt 预览、Session 回放、日志和统计。
- NPC 表单修改后调用 `preview-effective` 实时显示最终值与 `game/npc/server-limit` 来源；预览 API 支持未保存的 `npcOverride`，不会误标为 Session 来源。
- 控制台连接设置支持 Server Base URL、管理 Bearer Token 与 `X-AIBot-Actor`，敏感值仅保存在浏览器 localStorage。
- Element Plus 按需加载且页面路由懒加载；`npx vue-tsc -b --force` 与生产构建均通过，产物直接部署到 `src/AIBot.Server/wwwroot/app/`。
- 表单 Switch 和表格行使用显式类型收窄，生成 `components.d.ts` 后再次执行全量 `vue-tsc -b --force` 和生产构建仍可通过。

验收：策划无需编辑 JSON 即可完成全部非敏感记忆配置；测试人员可以解释和纠正 NPC 记忆。

### 四阶段 P1 加固（✅ 2026-08-25 已完成）

- 运行中的 Session 会立即应用新的 `shortTermTurns`，缩小窗口时旧消息进入待摘要队列。
- Unity 未提供后台宿主队列时，即使策略启用 `backgroundSummarization` 也执行同步摘要；Server 仅在玩家范围且确有后台队列时延迟给宿主。
- 类别开关同时进入摘要模型的 `summary`/`facts` 契约，结构化事实保存前再次过滤；Prompt 明确当前游戏状态优先于记忆。
- 删除与保留期清理会使旧摘要任务失效、清空该玩家全部 Session，并通过玩家级互斥消除保存竞态。
- 审计写入不可用不再伪装成功；管理端获得明确 503，后台批次保持未确认状态。
- 回归结果：xUnit 66/66、Server 0 warning/0 error、Vue 强制全量类型检查和生产构建通过。

### 四阶段 P2 加固（✅ 2026-08-25 已完成）

- 保留期清理从仓储倒序分页的末尾读取最旧批次，不再误扫最新一页；返回批次上限与 `hasMoreCandidates`，便于分批继续处理。
- Vue 清理预演绑定当前 `gameId + inactiveDays`；切换 Game 或修改天数会立即作废旧预演，执行按钮只接受新鲜预演结果。
- 统一 Vue 控制台的危险操作提示与实际行为对齐，明确整份删除和保留期清理会同时清空关联 Session 消息与待摘要队列。
- Session 持久化文件删除失败时保留内存状态并返回明确错误，避免 API 误报成功后重启复活。
- 前端预设和清理确认取消均被正常吞掉，不再产生未处理 Promise rejection。
- 回归结果：xUnit 76/76、Server 0 warning/0 error、Vue 强制全量类型检查和生产构建通过。

### 摘要稳定性收尾（✅ 2026-08-27 已完成）

- 摘要任务耗尽 3 次自动重试后记录失败明细，原始 `evictedMessages` 保留不删除。
- 新增摘要队列查询与失败重试 API，支持按 Game/NPC/Player/Session 过滤。

### 后续 P1 运行加固（✅ 2026-08-27 已完成）

- Server 对 `SupportedSummaryTriggers`、`SupportedMemoryScopes` 做大小写不敏感去重和空值清理，策略预览与控制台不会再出现重复能力标签。
- 摘要队列增加幂等入队、玩家级失效后重新入队、非法标识拒绝和失败重试空队列等回归测试。
- MySQL 模式下摘要任务持久化保存 pending/processing/failed 状态，Server 重启后恢复未完成任务；成功后删除任务记录，失败记录保留供后台重试。
- 新增 `/api/ready` 就绪探针，检查 MySQL/表结构、LLM 配置、默认 NPC 和摘要队列，未就绪返回 503。
- 模型连接错误统一返回稳定错误码和 `retryable` 标志，便于 Vue、Unity 端按错误类型提示和重试。
- Server 启动时输出存储提供方、JSON data 根目录或 MySQL 目标、MySQL 必需表存在性、记忆边界/能力、全局 LLM Key 是否配置及 default NPC 发现结果；不会输出任何密钥或完整连接字符串。

### 统一 API 错误处理（✅ 2026-08-27 已完成）

- Server 错误契约统一包含 `error`、`code`、`status`、`requestId` 和可选 `details` 字段；全局异常、管理 API 鉴权失败和聊天限流均使用该契约。
- Server 通过 `X-Request-Id` 贯穿请求和响应，内部错误只记录服务端日志，不向客户端暴露堆栈或敏感配置。
- Vue API 层统一兼容结构化错误、RFC 7807 ProblemDetails、纯文本和网络断开，并将 400/401/403/404/409/429/5xx 映射为一致的中文提示；流式对话和文件下载也使用同一解析逻辑。

### 小规模日志优化（✅ 2026-08-27 已完成）

- Server 运行日志由 `RuntimeLogService` 同时输出标准控制台和按日 JSONL，默认位于 `data/logs/runtime/`，保留 14 天；可通过 `Logging:RuntimeFileEnabled`、`Logging:RuntimeDirectory` 和 `Logging:RuntimeRetentionDays` 调整。
- API 请求完成、未处理异常、限流和日志写入失败均带 `requestId`、HTTP 状态和耗时；运行日志默认只保存脱敏后的消息，不记录 API Key、Bearer Token 或完整敏感连接信息。
- 新增 `GET /api/admin/runtime-logs`，支持按日期、级别、类别和 requestId 查询，Vue“请求日志”页可切换查看对话日志或 Server 运行日志。
- `LogMaintenanceService` 每 24 小时清理过期数据：JSON 运行/审计日志按保留期删除；MySQL 模式清理 `chat_logs`（默认 30 天）和 `memory_audits`（默认 365 天）。
- 会话列表返回 `idle`、`waiting`、`pending`、`failed` 状态及最近失败原因。
- Vue 记忆检查器增加队列概览、当前/累计失败统计、失败提示和全局/会话级重试入口。
- 验收重点：摘要写入、审计写入、Session 确认落盘三者全部成功后才消费待摘要消息；服务重启和重试均保持幂等。

验证命令：

```powershell
cd src/AIBot.Tests; dotnet test --no-restore -p:NuGetAudit=false
cd ../AIBot.Server; dotnet build --no-restore -p:UseAppHost=false
cd ../AIBot.Web; npx vue-tsc -b --force; npm run build
```

## 15. 建议工期

按单人开发估算：

| 阶段 | 预计时间 |
|---|---:|
| A 契约与配置解析 | 2～3 天 |
| B 长期记忆与后台摘要 | 4～6 天 |
| C 管理 API 与审计 | 3～4 天 |
| D Vue 控制台 | 5～7 天 |
| 测试、迁移与联调 | 3～4 天 |
| 合计 | 17～24 天 |

如果第一版不做结构化事实冲突处理、审计页面和动态 Schema，可压缩为约 10～14 天。

## 16. 关键决策

实施前建议确认以下决策：

1. 正式聊天请求是否要求 `playerId` 必填，还是保留一段兼容期。
2. Vue 第一版是否包含人工修改长期记忆，还是仅查看和删除。
3. 是否立即实现后台摘要，还是先保留同步摘要。
4. 自定义字段第一版是否需要动态 Schema，还是仅提供 `extensions` JSON 编辑器。
5. JSON 存储预计使用多久；如果很快需要多人或多实例运行，应直接选择 SQLite/PostgreSQL 实现仓储接口。

推荐默认选择：`playerId` 兼容期可空、允许人工修改并审计、立即实现后台摘要、第一版使用固定字段加高级 JSON 扩展、存储继续使用 JSON 但先抽象仓储接口。
