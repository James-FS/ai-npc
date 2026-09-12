# MongoDB 存储改造方案（v2：以 Mongo 替换 MySQL）

> 目标：在保持 REST 契约与 `AIBot.Core`/Unity 构建零改动的前提下，用 MongoDB **替换** MySQL，
> 作为 Server 的正式在线存储；JSON 继续作为默认的开发/单机存储。通过配置在两者间切换。

---

## 0. 决策记录与结论摘要

| 问题 | 决定 |
|---|---|
| Q1 动机 | **不是横向扩展**；是「替换 MySQL + 简化写入 + 事实结构灵活 + 日志/审计增长」。多实例不在本方案内（见 §15 架构澄清） |
| Q2 存量 MySQL 数据 | **直接删除，不保留** → 不做存量迁移，删除 P4 |
| Q3 静态钩子接口化 | **做，但限定为「持久化接缝」接口化，保留静态门面，分两阶段**（见 §8） |
| Q4 迁移后 MySQL 去留 | **直接弃用** → 从代码、依赖、脚本、文档中彻底移除 MySql* |

### 核心结论

- 从「三后端并存」改为「**Json（默认，开发） / Mongo（正式）+ 彻底移除 MySql**」。
- 不需要 MongoDB 事务：事实内嵌后，记忆是单文档 CAS 写，其余全是单文档原子操作 →
  **单机 standalone 即可**，无需副本集。
- 主要收益：记忆保存从「事务 + `FOR UPDATE` + 删光重插」塌缩为一次 CAS `UpdateOne`；
  日志/审计的过期交 TTL 索引；事实/schema 演进不再需要 DDL；顺带移除 MySql/Dapper 依赖。
- 主要成本：多一个数据库组件；一次性重构五个文件的持久化接缝（Q3）；删除 MySQL 产物需同步文档与测试。

---

## 1. 目标与非目标

### 目标
1. 支持 `Storage:Provider=Mongo`（或 `AIBOT_STORAGE_PROVIDER=Mongo`）启动。
2. 持久化五类数据：玩家长期记忆 + 事实、Session、对话日志、记忆审计、摘要任务。
3. 保持**对外行为**不变：REST 契约不变；`IMemoryRepository` 接口不变；`SessionStore`/`ChatLogService`
   静态门面的**公开方法签名**（供 `ChatEndpoints`/`AdminEndpoints` 调用）不变。
   **允许变更**的是内部装配签名——`MemorySummaryQueue`/`LogMaintenanceService` 的构造函数参数、
   `ReadinessService.CheckAsync`/`StartupDiagnostics.RunAsync` 的第二个参数（由 MySQL 工厂换成 Mongo 工厂），
   以及 `MemoryAuditService` 的构造函数（改为接收 `IMemoryAuditStore`）。这些不对外暴露，但需同步更新测试（§13）。
4. `AutoMigrate` 幂等创建集合与索引，并记录版本到 `_meta`。
5. 支持 JSON→Mongo 记忆迁移（复用现有迁移器）。
6. 提供 `docker.yml` 服务、`.env` 变量与 `start-server-mongo.ps1`，体验对标原 MySQL 流程。
7. **彻底移除 MySQL**：代码、依赖、脚本、schema、文档、测试。

### 非目标
- **不把配置文件搬进 Mongo**：`data/games/*/npcs`、`world.json`、`memory-policy.json`、
  `system-settings.json`（全局 LLM Key）继续以文件为准。
- **不改 `AIBot.Core`、不让 Unity 接触 Mongo 驱动**。
- **不做多实例/横向扩展改造**（见 §15）；摘要队列仍为进程内 `Channel`，Mongo 只做崩溃恢复持久化。
- **不保留 MySQL 双轨，也不保留 MySQL 存量数据**（Q2/Q4）。

---

## 2. 现状接缝 → 目标形态

| 数据 | 现状 | 目标 |
|---|---|---|
| 长期记忆 + 事实 | `MySqlMemoryRepository` / `JsonMemoryRepository` 实现 `IMemoryRepository` | 保留接口；新增 `MongoMemoryRepository`（事实内嵌），删除 `MySqlMemoryRepository` |
| Session | `SessionStore` 静态门面 + `MySqlSessionPersistence` | 抽 `ISessionPersistence`；新增 `JsonSessionPersistence`（原文件逻辑）+ `MongoSessionPersistence`；删除 MySql 版 |
| 对话日志 | `ChatLogService` 静态门面 + 内联 `if (MySqlStorage != null)` | 抽 `IChatLogStore`；`JsonChatLogStore` + `MongoChatLogStore` |
| 记忆审计 | `MemoryAuditService` 双 ctor（`Func<string>` / `MySqlConnectionFactory`） | 抽 `IMemoryAuditStore`；`JsonMemoryAuditStore` + `MongoMemoryAuditStore`；`MemoryAuditService(IMemoryAuditStore)` 单 ctor |
| 摘要任务 | `MySqlMemorySummaryJobPersistence`（可空字段） | 抽 `IMemorySummaryJobPersistence`；仅 `MongoMemorySummaryJobPersistence`（Json 模式为 null，沿用现状） |
| 建表/索引 | `DatabaseMigrator` + `MySqlSchema` | `MongoInitializer` + `MongoSchema` |
| 就绪/诊断 | `if (storage.IsMySql)` 分支 | `if (storage.IsMongo)` 分支（Json/Mongo 二选一） |
| 配置 | `StorageOptions`（`IsMySql`...） | `IsMongo` + Mongo 连接串/库名/自动建索引；移除 MySql 字段 |
| 管理台展示 | `/api/admin/storage` 的 `mysql` 块 | 改为 `mongo` 块；前端类型同步 |
| 迁移 | `JsonToMySqlMemoryMigrator` | 重命名 `JsonToMemoryRepositoryMigrator`（已泛化，仅改名） |

**关键点**：只有记忆仓储本就是接口；本轮把其余四处也接口化（Q3），但**只抽持久化层，静态门面不动**。

---

## 3. 依赖变更

| 动作 | 项 |
|---|---|
| 新增 | `MongoDB.Driver` 2.28.x（`AIBot.Server.csproj` 与 `AIBot.Tests.csproj`） |
| 移除 | `MySqlConnector`、`Dapper`（两个 csproj 都移除） |

> **同步 / 异步签名现状（勿一概而论）**：`IMemoryRepository` 本就是 `Task` 异步接口，保持异步；
> 另外四个新抽的持久化接缝（`ISessionPersistence`/`IChatLogStore`/`IMemoryAuditStore`/
> `IMemorySummaryJobPersistence`）的调用方目前均为同步签名，不能为了 Mongo 全部改成 async
> （会波及 `ChatEndpoints`/`AdminEndpoints` 的同步调用路径）。
> 实测 `MongoDB.Driver` 2.28 **同时提供同步与异步 API**：`InsertOne`(void)、`DeleteOne`(DeleteResult)、
> `UpdateOne`(UpdateResult)、`ReplaceOne`(ReplaceOneResult)、`CountDocuments`(long) 均有同步版本。
> 因此这四个 Mongo 实现可保持同步签名、直接调用驱动的同步方法；而 `IMemoryRepository` 的 Mongo 实现
> 用 `*Async`（异步版本为 `*Async` 后缀；`Find` 返回 `IFindFluent`，其 `ToList()` 同步、`ToListAsync()` 异步）。

> 移除 Dapper 前需确认无残留引用：`LogMaintenanceService`、`MemoryAuditService`、`ChatLogService`、
> `ReadinessService`、`StartupDiagnostics`、`DatabaseMigrator`、`MySqlSchema`、`MySql*Persistence`
> 都将被改写或删除，故可安全移除。

> **测试工程用的是显式文件清单，不是通配符（易漏，会整体编译失败）**：`AIBot.Server.csproj` 用
> `**\*.cs` 通配，但 `AIBot.Tests.csproj` 逐条
> `<Compile Include="..\AIBot.Server\Xxx.cs" Link="Server\Xxx.cs" />` 链接了
> `MySqlConnectionFactory.cs`、`MySqlSessionPersistence.cs`、`MySqlMemorySummaryJobPersistence.cs`、
> `MySqlMemoryRepository.cs`、`JsonToMySqlMemoryMigrator.cs`。删除/改名这些文件时**必须同步删掉对应的
> `<Compile>` 行**，否则测试工程报“找不到源文件”，整个 `dotnet test` 编译失败（不是单个用例红）。
> 同理，新抽出的 `ISessionPersistence`/`IChatLogStore`/`IMemoryAuditStore`/`IMemorySummaryJobPersistence`
> 及其 Json 实现、`Mongo*.cs`/`MongoInitializer.cs` 若被测试引用，也**必须在测试工程显式补
> `<Compile Include>`**；因此新类型应尽量各占一个文件，便于逐条登记。此项列入 §11/§13 的独立任务。

---

## 4. 配置开关

`appsettings.json` 的 `Storage` 段（移除 MySql）：

```json
"Storage": {
  "Provider": "Json",
  "Mongo": {
    "ConnectionString": "",
    "Database": "ai_npc",
    "AutoMigrate": false
  },
  "MigrationGameId": "default"
}
```

| 配置 | 环境变量 |
|---|---|
| `Storage:Provider` | `AIBOT_STORAGE_PROVIDER` |
| `Storage:Mongo:ConnectionString` | `AIBOT_MONGO_CONNECTION_STRING` |
| `Storage:Mongo:Database` | `AIBOT_MONGO_DATABASE` |
| `Storage:Mongo:AutoMigrate` | `AIBOT_MONGO_AUTOMIGRATE` |

`StorageOptions` 变更：移除 `MySqlConnectionString`/`AutoMigrate`/`IsMySql`；新增
`MongoConnectionString`/`MongoDatabase`/`MongoAutoMigrate`/`IsMongo`。`Validate()` 在 `IsMongo` 时
要求连接串与库名非空，并**校验库名合法性**：MongoDB 库名不得包含 `/ \ . " $ * < > : | ?` 及空格、
不得为空且长度 < 64（沿用现有 MySQL 库名 `字母/数字/下划线` 校验的同等精神，
见 `DatabaseMigrator.cs:20-23`），避免拼接出非法库名。

> **`Mongo` 模式必须强制 `MongoAutoMigrate=true`（否则就绪探针恒 503）**：Mongo 集合是惰性创建的，
> 不执行 `MongoInitializer` 时 `listCollectionNames` 为空，而 §9.1 第 8 条会把 6 个必需集合列为
> 就绪条件 → `/api/ready` 永远 `not_ready`、启动诊断永远告警，但连接本身是通的，极易误判为故障。
> 因此 `Validate()` 在 `IsMongo` 且 `!MongoAutoMigrate` 时直接抛明确错误
> （“Mongo 模式需要 Storage:Mongo:AutoMigrate=true 以创建集合与索引”），不要只依赖脚本注入环境变量。
> 若未来要支持“手工建好集合、不自动建索引”的部署，再放宽为就绪判定区分 `migrated` 状态。

> **解析陷阱（照抄现状的已知坑）**：现有 `AutoMigrate` 用
> `env != null ? 解析env : (config.GetValue<bool?>... ?? ...)`。若在 `appsettings` 里写死
> `Storage:Mongo:AutoMigrate=false`，`GetValue<bool?>` 返回 `false` 而非 `null`，`??` 链会短路。
> 给 `MongoAutoMigrate` 必须沿用**「env 非空则 env 优先」**的同一写法，否则
> `AIBOT_MONGO_AUTOMIGRATE=true` 会被静默忽略。

> **appsettings 示例保留 `AutoMigrate:false` 是合规的**：`Provider` 默认 `Json`，此时 `Validate()`
> 的「Mongo 必须 AutoMigrate」检查不会触发，示例本身能启动；只有把 `Provider` 改成 `Mongo` 时才必须
> 同时置 `AutoMigrate:true`（或设 `AIBOT_MONGO_AUTOMIGRATE=true`），否则 `Validate()` 抛错提示。
> 保持 `false` 作为示例默认值是有意的（避免 JSON 模式误建 Mongo 集合）。

---

## 5. 集合与文档设计

集合命名沿用表名，`_id` 用确定性字符串键，便于按业务主键幂等 upsert。

### 5.1 `player_memories`（合并原 `player_memories` + `memory_facts`）

```jsonc
{
  "_id": "<gameId>|<npcId>|<playerId>",
  "gameId": "...", "npcId": "...", "playerId": "...",
  "schemaVersion": 2,
  "memoryVersion": 7,
  "summary": "……",
  "lastSummarizedUtc": ISODate("..."),
  "facts": [
    { "id": "...", "category": "...", "key": "...", "value": "...",
      "confidence": 0.8, "source": "...", "sourceSessionId": "...",
      "createdUtc": ISODate("..."), "updatedUtc": ISODate("..."),
      "pinned": false, "expiresUtc": null }
  ],
  "createdUtc": ISODate("..."), "updatedUtc": ISODate("...")
}
```

- 事实内嵌：删除 `memory_facts` 集合与 `ON DELETE CASCADE`；删除记忆即级联消失。`MaxFacts` 默认 20。
- **文档大小并非严格由 `MaxFacts` 界定（需知悉，但风险比初稿小）**：生效的 `policy.maxFacts`
  其实**已被 `MemoryPolicyResolver` 夹到 `Memory:MaxFacts`（默认 20）以内**
  （`MemoryPolicy.cs:184` 的 `Clamp(result, "maxFacts", ref p.maxFacts, 1, Math.Max(1, limits.maxFacts))`），
  所以正常配置下事实条数有界。真正的不确定性只有三条：
  1. **配置上限本身无绝对上界**：`MemoryPolicyService.LoadLimits` 读取 `Memory:MaxFacts` 时不做 clamp
     （`MemoryPolicyService.cs:28`），运维若把它配成很大的值，内嵌文档就会随之膨胀；
  2. **pinned 事实完全绕过条数上限**：`AddFactAsync` 只对非 pinned 计数
     （`PlayerMemoryService.cs:102`），`MemoryFactMerger.Merge` 更是
     `Take(Math.Max(limit, pinnedCount))`，pinned 全保留（`MemoryFactMerger.cs:45-49`）；
  3. **事实 `value` 无长度截断**：`Normalize` 不裁剪 value（`MemoryFactMerger.cs:67-78`）；
     仅管理后台手工新增路径限制 ≤1000 字符（`AdminEndpoints.cs:978`），LLM 抽取路径无此限制。
  实践上事实来自 LLM 抽取、量小且值短，多数部署远达不到 16MB；唯一现实风险路径是**运维把
  `Memory:MaxFacts` 配得很大，且管理员大量 pin 长事实**。缓解（**建议在 P1 一并做，二选一**）：
  - **推荐**：在 `MemoryPolicyService.LoadLimits` 对**配置项 `Memory:MaxFacts`** 加绝对上限（如 500），
    使内嵌策略有明确上界，同时不改变正常行为（默认 20 不受影响）；
  - 或在 `MongoMemoryRepository.Save` 前估算文档字节数，超阈值（如 8MB）抛显式异常而非让驱动报错。
  若将来事实确实可能很大，再改为「事实独立集合 + 按需 `$lookup`/二次查询」——
  但那会退回多文档模型，本方案的前提就不再成立。
- 映射层 `MemoryDocument` + `ToMemory/ToDocument`，不把 Core DTO 直接当 BSON 实体。
- **`updatedUtc` 的语义必须明确（影响保留/清理）**：JSON 版的 `List` 把 `updatedUtc` 算成
  「文件时间、`lastSummarizedUtc`、所有事实 `updatedUtc` 的最大值」（`MemoryRepository.cs:155-161`），
  而 MySQL 版直接存写入时刻的列值——**两者本就不一致**。Mongo 采用**与 JSON 一致**的定义：
  写入时 `updatedUtc = max(now, lastSummarizedUtc, facts.max(updatedUtc))`。
  `PlayerMemoryService.FindRetentionCandidatesAsync` 依赖 `updatedUtc` 排序决定淘汰对象，
  定义不一致会导致淘汰集合不同；该字段须纳入 P1 单测对齐。
- **写路径的 `updatedUtc` 必须由 `MongoMemoryRepository` 自己算出来**：Core DTO
  `PlayerLongTermMemory` **根本没有 `updatedUtc` 字段**（只有 `lastSummarizedUtc`；`MemoryFact.cs:22-32`）——
  JSON 版靠“文件最后写入时间”隐式得到，MySQL 用 `UTC_TIMESTAMP(6)`。因此 Mongo 的 `Save` 不能从入参读，
  必须在写入时按 §7.1 的 `MaxLatest(now, memory.lastSummarizedUtc, memory.facts)` 重算并 `Set` 到
  文档顶层 `updatedUtc`（该字段只存在于 `MemoryDocument`，不回灌 DTO）。若漏算或直接透传默认值，
  时间会被写成 0001 年，`FindRetentionCandidatesAsync` 会把活跃记忆全部判为过期。
- **`List` 的 `updatedUtc` 直接读持久化字段即可与 JSON 对齐**：JSON 的 `List.updatedUtc` 是
  「文件时间 / `lastSummarizedUtc` / 事实最大值」三者取大；Mongo 写入时已把等价值持久化到文档
  `updatedUtc`（含当时的写入时刻），`List` projection 取该字段即可，不必再次动态计算
  （否则 `List` 与 `Save` 两条路径可能给出不同的时间）。
- **事实顺序差异（已知且接受）**：MySQL 版 `Load` 为 `ORDER BY updated_utc DESC`，JSON 与 Mongo 均为
  写入顺序；`ToPromptFacts` 按列表顺序拼提示词，故从 MySQL 迁移过来顺序会变。因 MySQL 数据本就删除，
  无迁移影响；单测固定为「与 JSON 一致的写入顺序」。

### 5.2 `sessions`

```jsonc
{
  "_id": "<gameId>|<npcId>|<playerKey 或空串>|<sessionId>",
  "gameId": "...", "npcId": "...", "playerKey": "...", "sessionId": "...",
  "payload": { /* SessionFileDto */ },
  "hasPendingMemory": true,
  "lastActiveUtc": ISODate("..."), "createdUtc": ISODate("..."), "updatedUtc": ISODate("...")
}
```

`payload` 用「Newtonsoft JSON ↔ BsonDocument」桥接，规避 BSON 类映射与 Core DTO 字段命名的摩擦：

```csharp
BsonDocument payload = BsonDocument.Parse(JsonConvert.SerializeObject(dto, Formatting.None));
SessionFileDto dto    = JsonConvert.DeserializeObject<SessionFileDto>(payload.ToJson());
```

`hasPendingMemory` = `dto.evictedMessages.Count > 0`。

> **JSON 桥接会把 `payload` 里的 `DateTime` 落成 BSON 字符串，不是日期**：`BsonDocument.Parse` 只能看到
> JSON 的字符串（Newtonsoft 把 `DateTime` 序列化成 ISO 串），故 `payload` 内的
> `lastActiveUtc`/`recentRequests[].createdUtc`/`pendingToolRound.createdUtc` 都是 **BSON string**；
> 回读时 `JsonConvert.DeserializeObject` 再把 ISO 串解回 `DateTime`，round-trip 正常。**真正用于查询/排序的
> 顶层 `lastActiveUtc` 才是 BSON Date**。不要试图在 Mongo 侧按 `payload.lastActiveUtc` 做时间范围查询
> （那会变成字符串比较），`DeleteExpired` 的条件里也只查顶层 `lastActiveUtc` 和 `payload` 的对象/数组结构。

> **`payload`/审计里的键可能含 `.` 或 `$`，来自客户端与 LLM，需在边界校验**：
> `SimGameState.extras` 的键由请求体直接合并（`ChatEndpoints.cs:366-370`，键完全由客户端决定）、
> `SimGameState.items` 的键来自工具调用的 `item` 参数（`SimulatedToolHost.cs:36`）、
> `MemoryPolicy.extensions` 是管理员自定义 `JObject` 键。这些都会经 JSON 桥接进入 `payload`/审计文档。
> BSON 对字段名（`.` 与 `$` 前缀）有额外限制、且这类字段无法用点号路径查询；JSON 后端则无此约束。
> 建议在 API 边界就拒绝/规范化这类键（或写入前对键做转义、回读时还原），否则会出现
> 「JSON 正常、Mongo 报错或该字段查不到」的后端差异。至少纳入 §13 一个「extras 含非法键」的用例。

> **顶层字段与 `payload` 是同一份数据的两种视图，写入时必须以 DTO 为准**：`gameId`/`npcId`/
> `playerKey`/`sessionId`/`hasPendingMemory`/`lastActiveUtc` 是为了查询与索引而冗余到顶层的，
> 真正的数据在 `payload`。`MongoSessionPersistence.Save` 必须全部从同一个 `dto` 派生
> （`lastActiveUtc` 沿用 MySQL 现状：`dto.lastActiveUtc` 为 `default` 时写 `UtcNow`），
> 不允许从门面另传一套，否则顶层与 payload 会漂移、`List`/`ScanPending` 的过滤结果与回读 DTO 不一致。

> **必须与现状对齐的过滤语义**：原 `ScanPending` 是 `has_pending_memory=1 AND player_key<>''`，
> Mongo 版过滤必须是 `{ hasPendingMemory: true, playerKey: { $ne: "" } }`，否则会多捞 `playerKey=""`
> 的 legacy session。`List` 的 `(@PlayerKey IS NULL OR player_key=@PlayerKey)` 对应
> 「`playerId` 为 null 则不加条件，否则 `playerKey == playerId`」，注意区分空串与 null。

> **DTO 侧必须把空串 `playerKey` 还原成 `null`（否则门面去重会重复/漏项）**：Mongo `_id` 的
> playerKey 空位是空串，但 `SessionFileDto.playerId` 在 JSON 语义下是 `null`（legacy）。门面
> `Key(gid,npcId,playerId,sid)` 对 `null` 用占位符 `"<legacy>"`（`SessionStore.cs:104-107`），
> `ListByGame` 用它把内存会话与持久化会话去重。因此 Mongo `Load`/`List` 读回时**必须把
> `playerKey == ""` 映射为 DTO 的 `playerId = null`（而非 `""`）**，否则同一 legacy 会话会算出两个不同
> 的 key，出现重复条目或覆盖错位；`ScanPending` 同理返回 `PlayerId = null`。

> **入站 `playerId` 应把空串规范成 `null`（消除「空串 vs 缺省」的存储键歧义）**：聊天入口只校验
> `!IsNullOrEmpty && !IsValidPlayerId`（`ChatEndpoints.cs:143`），**空串 `""` 既不被拒绝也不会归一化**。
> 于是 `playerId=""` 与「不传 playerId」在门面 `Key` 里是两个不同会话（`""` vs `"<legacy>"`，
> `SessionStore.cs:104-107`），但落到存储层会撞成同一个键：JSON 的 `FilePath` 用
> `string.IsNullOrEmpty(playerId)` 判断，`""` 与 `null` 都走 legacy 路径；MySQL/Mongo 的 `playerKey`
> 用 `playerId ?? string.Empty`，也都归到 `""`。**这是三个后端都存在的既有歧义**（JSON 表现为两个内存
> 会话抢写同一文件，Mongo 表现为同一个 `_id`），不是 Mongo 新引入，但方案既然要求「Mongo 对齐 JSON 的
> 会话身份」，就应把入站空串统一 `?? null` 归一化，让「空 = 未绑定玩家」在入站、门面 `Key`、存储键三处
> 语义一致。该归一化对 JSON 也成立、不改变其磁盘行为（`""`/`null` 本就同路径），可作为统一修复。

### 5.3 `chat_logs`
`{ _id: ObjectId, gameId, ts: ISODate(...), npcId, playerId, sessionId, legacyMemoryScope, userMessage, say, emotion, action, fallback, promptTokens, completionTokens, elapsedMs, tools: [...], injection }`
（`tools` 用原生数组；现有查询/REST 不暴露 `id`，无契约影响。）
- **`ts` 必须落成 BSON `Date`，不能沿用 `ChatLogEntry.ts` 的 ISO 字符串**：一是 §6 的 TTL `{ts:1}`
  只对 Date 生效，写成字符串会让 TTL **静默不删**（不报错，最易踩）；二是查询按 `ts >= day && ts < day+1`
  做时间范围过滤与 `ORDER BY ts DESC`，字符串比较在跨月/时区时会错。实现用现有 `ParseUtc(entry.ts)`
  的同一规则（`RoundtripKind` → `ToUniversalTime()`）转成 `DateTime` 写入。`chat_logs` 只有 `ts`
  一个时间字段（TTL 只挂它），不要照搬 `sessions` 的多时间字段。

### 5.4 `memory_audits`
`{ _id: "audit-<guid>", ts: ISODate(...), gameId, npcId, playerId, actor, action, before, after, metadata }`
（`_id` 沿用应用侧 id，天然幂等，替代现状「先 `COUNT(*)` 再插」。）
- 与 `chat_logs.ts` 同理：**`ts` 必须落成 BSON `Date`**（`MemoryAuditEntry.ts` 是字符串，用现有
  `ParseUtc` 规则转换），否则 §6 的审计 TTL 静默失效、按日查询范围也会失准。

> **`before`/`after` 必须是 `BsonValue` 而非 `BsonDocument`（会直接抛异常）**：这两个字段在代码里是
> `JToken`，且多处传 **`JValue.CreateNull()`**（`AdminEndpoints.cs:562/570/628/1013-1014` 的
> `session.delete`、`memory.delete`、retention 删除等）。`BsonDocument.Parse("null")` 不是合法 BSON
> 文档，会抛 `FormatException`。因此字段类型定为 `BsonValue`（只有 `metadata` 恒为对象，可用
> `BsonDocument`）；写入用 `BsonValue.Create(...)` 或统一包一层 `{"v": ...}`，查询回读时保证
> null / scalar / object 三种形态都能还原成原 JSON 形状（管理台审计列表直接展示 `before`/`after`，
> 形状变了会破坏前端渲染）。

> **幂等必须「吞掉重复键」而非报错**：现状 `RecordRequired` 捕获**任何**异常后重试 3 次，最终仍失败才抛
> `MemoryAuditWriteException`（`MemoryAuditService.cs:71-89`）。若 Mongo 实现对已存在 `_id` 直接抛
> `DuplicateKey`，重试 3 次都会失败，最终把一个**合法幂等重试**误报为致命写入失败。
> 因此 `MongoMemoryAuditStore.Write` 必须捕获 `ServerErrorCategory.DuplicateKey` 并**视为成功返回**；
> 其余异常照常抛出以触发重试。这与现状「第二次 `COUNT` 命中即返回」等价且更直接。

### 5.5 `memory_summary_jobs`
`{ _id: "<job_key>", gameId, npcId, playerId, sessionId, force, actor, generation, status, attempts, lastError, availableUtc, createdUtc, updatedUtc }`
（`job_key` 形如 `<playerKey>|<sessionId>|g<gen>`，已含 `|`，与 `_id` 分隔符一致。）

### 5.6 `_meta`
`{ _id: "mongo-initial", name: "mongo-initial", appliedUtc: ISODate(...), indexes: { chatLogsTtlSeconds: <n>, auditsTtlSeconds: <n> } }`，
替代 `schema_migrations` 表。
- **`_id` 不要用 `1`**：§15 风险表已为 BSON `DateTime`/字段命名注册自定义 serializer，`BsonSerializer`
  全局注册可能影响 `BsonValue` 推断，`_id: 1`（Int32）在“字符串 _id 集合”与“数值 _id 集合”混用时
  容易踩 `_id` 类型不一致的坑。用固定字符串 `"mongo-initial"` 最稳，也便于人工识别。
- **TTL 秒数记在这里**（`indexes` 子文档），供 §6「`expireAfterSeconds` 变更时重建 TTL 索引」读取；
  应用签名为「当前配置秒数 + `_meta` 记录秒数」，不一致则 drop 旧 TTL 索引重建。
  `appliedUtc` 为首次初始化时间，重复 `ApplyAsync` 不应覆盖它（用 `$setOnInsert`）。

---

## 6. 索引与 TTL

由 `MongoInitializer` 幂等创建。

| 集合 | 索引 |
|---|---|
| `player_memories` | `{gameId:1, updatedUtc:-1}`；`{gameId:1, npcId:1, playerId:1}` |
| `sessions` | `{gameId:1, npcId:1, playerKey:1, lastActiveUtc:-1}`；`{hasPendingMemory:1, playerKey:1}`；`{lastActiveUtc:1}`（`DeleteExpired` 按 `lastActiveUtc < cutoff` 范围删除且不带 gameId，单列索引避免每日清理全表扫描）。**不建 TTL**：TTL 无法表达「有待摘要/挂起工具轮就保留」，会误删有业务副作用的会话；过期清理走 `ISessionPersistence.DeleteExpired`（§8.1） |
| `chat_logs` | `{gameId:1, ts:-1}`；`{gameId:1, npcId:1, ts:-1}`；`{gameId:1, playerId:1, ts:-1}`；**TTL** `{ts:1}`, `expireAfterSeconds = ChatRetentionDays*86400` |
| `memory_audits` | `{gameId:1, ts:-1}`；`{gameId:1, npcId:1, playerId:1, action:1, ts:-1}`；**TTL** `{ts:1}`, `expireAfterSeconds = AuditRetentionDays*86400` |
| `memory_summary_jobs` | `{status:1, updatedUtc:-1}`；`{gameId:1, npcId:1, playerId:1, sessionId:1}` |

- TTL 后台约每 60s 扫描，删除有延迟；查询侧仍按时间范围过滤，不依赖 TTL 保证正确性。
- **`_meta` 必须建（不能省）**：§9.1 第 8 条把 `_meta` 列为必需集合，但**没有任何业务写入路径会创建它**
  ——它只由 `MongoInitializer` 在 AutoMigrate 时写入。若实现为先建业务集合、漏建 `_meta`，Mongo 模式下
  `/api/ready` 会永远缺一个集合而恒 `not_ready`。因此 `MongoInitializer.ApplyAsync` 必须显式
  `CreateCollection("_meta")` 或 upsert 一条 `_meta` 文档。
- **每个业务集合也要在初始化时显式创建**：`listCollectionNames` 看不到「因无数据而未创建」的集合；
  好在 `CreateIndex` 在集合不存在时会顺带创建集合，所以只要为 5 个业务集合各建至少一个索引（本表已列），
  集合就会出现。仍建议初始化末尾用 `listCollectionNames` **自检 6 个必需集合齐全**，缺失则抛出明确错误，
  避免就绪探针在启动后才暴露问题。
- **TTL 的 `ts` 字段类型决定成败**：`chat_logs`/`memory_audits` 的 `ts` 必须是 BSON `Date`
  （§5.3/§5.4）；写成字符串时 MongoDB **不会报错，但也永不删除**，属最易漏的静默失效。
- `expireAfterSeconds` 不能原地改：`MongoInitializer` 把两个秒数记入 `_meta.indexes`（§5.6），
  与当前配置不一致时 drop 旧 TTL 索引并重建。
- **`MongoInitializer` 必须接收 `IConfiguration`**（或显式传入两个保留天数），因为 TTL 秒数来自
  `Logging:ChatRetentionDays` / `Logging:AuditRetentionDays`。签名应为
  `ApplyAsync(MongoConnectionFactory factory, IConfiguration config, CancellationToken ct)`，
  而不是只有 factory。

---

## 7. 写路径

### 7.1 记忆保存：CAS 乐观锁
把 `memoryVersion` 作为过滤条件，一次原子 `UpdateOne` 完成「校验 + 写入 + 版本递增」：

```csharp
var id = Key(gameId, npcId, playerId);
// 与 JSON 对齐：updatedUtc 取 写入时刻 / lastSummarizedUtc / 事实最大 updatedUtc 之最大值（§5.1）
DateTime updatedUtc = MaxLatest(now, memory.lastSummarizedUtc, memory.facts);

FilterDefinition<MemoryDocument> filter = Builders<MemoryDocument>.Filter.And(
    Builders<MemoryDocument>.Filter.Eq(d => d.Id, id),
    Builders<MemoryDocument>.Filter.Eq(d => d.MemoryVersion, expectedVersion));   // memoryVersion 必写出，无需 Exists

UpdateDefinition<MemoryDocument> update = Builders<MemoryDocument>.Update
    .Set(d => d.SchemaVersion, 2).Set(d => d.MemoryVersion, expectedVersion + 1)
    .Set(d => d.Summary, memory.summary).Set(d => d.LastSummarizedUtc, memory.lastSummarizedUtc)
    .Set(d => d.Facts, ToFactDocuments(memory.facts)).Set(d => d.UpdatedUtc, updatedUtc)
    .SetOnInsert(d => d.GameId, gameId).SetOnInsert(d => d.NpcId, npcId)
    .SetOnInsert(d => d.PlayerId, playerId).SetOnInsert(d => d.CreatedUtc, now);

try
{
    var r = await _memories.UpdateOneAsync(filter, update,
        new UpdateOptions { IsUpsert = expectedVersion == 0 }, ct);
    if (r.MatchedCount == 0 && r.UpsertedId == null)
        throw new MemoryVersionConflictException(expectedVersion, await CurrentVersionAsync(id, ct));
}
catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
{
    throw new MemoryVersionConflictException(expectedVersion, await CurrentVersionAsync(id, ct));
}
return ToMemory(await LoadDocumentAsync(id, ct));
```

语义同 MySQL：`expectedVersion==0` 表示新建（已存在则冲突）；否则版本不匹配即冲突。

> **入参校验必须保留**：现有 `JsonMemoryRepository`/`MySqlMemoryRepository` 在每个方法入口都用
> `DataStore.IsValidId`/`IsValidPlayerId` 校验 `gameId`/`npcId`/`playerId`
> （`MemoryRepository.cs:136-138`、`MySqlMemoryRepository.cs:173-177`），非法时抛 `ArgumentException`。
> `MongoMemoryRepository` 必须逐一保留，否则 REST 层对非法 id 的错误语义会变（从 400 变成查不到/500）。

### 7.2 删除
`DeleteOne(_id [+ memoryVersion])`，无级联问题；带版本且未删到则读取实际版本：存在→冲突，不存在→幂等。

### 7.3 列表
`CountDocumentsAsync` 出 `total`，`Sort(updatedUtc desc, npcId asc, playerId asc)` + `Skip/Limit`。
`factCount` 用聚合 `$project: { factCount: { $size: { $ifNull: ["$facts", []] } } }`；
`Projection.Expression` 里的三元表达式未必能被 LINQ provider 翻译，鉴于 `MaxFacts` 仅 20，
退路是取回文档内存投影。对外字段语义须与 `JsonMemoryRepository.ListPlayerMemoriesAsync` 对齐（单测直接对比）。
- **分页参数必须与 JSON 同样 clamp**：JSON 是 `safeOffset = Max(0, offset)`、
  `safeLimit = Max(1, Min(200, limit))`（`MemoryRepository.cs:180-181`），返回体里的 `limit`/`offset`
  是**夹取后的值**（不是入参）。Mongo 直接 `Skip(offset).Limit(limit)` 会把负数/超大值透传给驱动
  （负 `Skip` 会抛异常），且返回体字段与 JSON 不一致。必须照抄同样的夹取并回填
  `MemoryListPage.limit/offset`。
- **`MemoryListItem.gameId` 要显式填**：Mongo 文档里有 `gameId`，但别依赖它在 projection 里被自动带出；
  `Find` 的 projection 若只选部分字段，需把 `gameId/npcId/playerId/memoryVersion` 一并投影，
  否则 `MemoryListItem` 的必填字段会缺失。`factCount`/`hasSummary` 由 `facts`/`summary` 派生。

### 7.4 摘要任务
均为单文档操作：`MarkProcessing` = `$set status + $inc attempts`；`MarkSucceeded` = `deleteOne`；
`MarkFailed`/`MarkPending` = `$set`；`UpsertPending` = `$setOnInsert createdAt + $set ... upsert`，
保留「`succeeded` 重置为 `pending`」语义。

> 注：该「重置」条件在当前代码里实际是**死分支**——`MarkSucceeded` 直接 `DELETE`，
> 表中不会出现 `status='succeeded'` 的行（`MySqlMemorySummaryJobPersistence.cs:70-74`）。
> 照搬无害，但不必为它设计聚合管道：直接 `$set` 即可，或顺手清掉这段无效条件。
> 另注 `availableUtc` 字段在现有代码中**从未被读取**（仅 upsert 时写入），Mongo 可保留以便将来
> 做延迟重试，也可直接省去。

---

## 8. 持久化接缝接口化（Q3 设计）

**原则**：抽接口，**不**把静态门面改成 DI（避免波及 `ChatEndpoints`/`AdminEndpoints` 的海量调用点）。

### 8.1 接口

```csharp
public interface ISessionPersistence {
    // 返回 DTO；JSON 实现内部负责 legacy 路径回退与「文件时间兜底 lastActiveUtc」
    SessionStore.SessionFileDto Load(string gameId, string npcId, string playerId, string sessionId);
    // 返回结果而非 bool：需回报「legacy v1 文件是否已归档」，供门面清除 SessionState.LegacySourcePath
    SessionSaveResult Save(string gameId, SessionStore.SessionFileDto dto);
    List<SessionStore.SessionFileDto> List(string gameId, string npcId, string playerId);
    List<PendingMemorySession> ScanPending();
    bool Delete(string gameId, string npcId, string playerId, string sessionId);   // 只删持久层，不碰 Map
    // 清理长期不活跃的会话；activeKeys 为规范身份键集合，protectedPaths 为 legacy 来源路径集合（见约束 1）
    int DeleteExpired(TimeSpan idle, ISet<string> activeKeys, ISet<string> protectedPaths);
}
public sealed class SessionSaveResult
{
    public bool Persisted;        // 写失败时为 false（JSON 无 data root、Mongo 写入异常都算失败）；门面返回给调用方
    public bool LegacyArchived;   // 本次是否把 v1 legacy 文件归档为 .migrated.bak；Mongo 恒 false
}
// SessionFileDto 增补一个仅进程内使用的字段（不落盘、不进 BSON）：
//   [JsonIgnore] public string legacySourcePath;
public interface IChatLogStore {
    void Write(string gameId, ChatLogService.ChatLogEntry entry);   // JSON 实现在写入时顺带清理超期文件（保持现状）
    JObject Query(string gameId, string date, string npcId, int limit, int offset);
    // 无 DeleteExpired：JSON 靠「写入时清理」，Mongo 靠 TTL 索引
}
public interface IMemoryAuditStore {
    void Write(MemoryAuditEntry entry);               // 幂等；失败时抛异常，由 MemoryAuditService 重试并包成 MemoryAuditWriteException
    JObject Query(string gameId, string npcId, string playerId, string action, string date, int limit, int offset);
    int DeleteExpired(DateTime cutoffUtc);            // Mongo 返回 0（TTL 接管）；JSON 返回实际删除数
}
public interface IMemorySummaryJobPersistence { /* 沿用现有方法集 */ }
```

**接口边界的四条约束（否则无法落地）**：

1. **`PruneInactiveFiles` 的「活跃 key 过滤」留在门面，删除动作下沉到 `DeleteExpired`**。
   现有实现的删除条件有二层：其一是「不在当前进程 `Map` 中」（`SessionStore.cs:418-427` 用
   `Map.Values` 构造 `tracked`），其二是「无待摘要批次 / 无挂起工具轮 / 无处理中请求」
   （`SessionStore.cs:445-451`）。前者依赖门面内存，后者可从 DTO 判断。
   因此：门面 `PruneInactiveFiles(idle)` 从 `Map` 构造活跃身份键集合后调用
   `_persistence.DeleteExpired(idle, activeKeys)`；两个实现各自按 DTO 判断第二层条件。
   **这修正了原 v2 的缺口——JSON 会删过期会话文件，Mongo 若什么都不删，会话文档会无限增长。**
   - **`activeKeys` 的身份约定（关键，易错）**：门面传入的必须是**用同一个共享函数构造的规范键**，
     不能复用现有的 `SessionStore.Key`——后者对 `playerId == null` 用占位符 `"<legacy>"`
     （`SessionStore.cs:104-107`），而 Mongo `_id` 的空位是**空串**，两者对不上会导致 Mongo
     误删活跃会话。做法：新增单一规范函数
     `SessionStore.IdentityKey(gid, npcId, playerId, sid) => gid + "|" + npcId + "|" + (playerId ?? "") + "|" + sid`，
     门面用它构造 `activeKeys`，Mongo 实现用它构造/比对 `_id`；JSON 实现把该键映射回文件路径
     （或直接按四元组比对）。
   - JSON 实现：遍历会话文件，跳过规范键在 `activeKeys` 中的记录，跳过含 `evictedMessages` /
     `pendingToolRound` / `recentRequests` 中 `status == "processing"`（值见 `SessionStore.cs:55-60`）
     的记录，删除超期文件。
   - **`LegacySourcePath` 的路径级豁免不能丢（否则会删掉活跃的 legacy 文件）**：现状 `tracked` 集合
     除了规范路径，还加入了 `session.LegacySourcePath`（`SessionStore.cs:426`）。原因：请求带
     `playerId`、但磁盘上只有 v1 legacy 文件时，`Map` 中会话的 `PlayerId=playerId`、
     `LegacySourcePath=legacy路径`，而该 legacy 文件自身的 DTO `playerId` 是空串。若只下传
     `activeKeys = gid|npc|playerId|sid`，JSON `DeleteExpired` 从文件算出的是 `gid|npc||sid`，
     两者对不上，**一个仍被内存引用、尚未落盘到新路径的 legacy 文件会被误删**。
     做法：`DeleteExpired` 除 `activeKeys` 外再接收一组“legacy 来源路径”（门面从 `Map` 里各会话的
     `LegacySourcePath` 收集），或者让 JSON 实现按 DTO 记录的文件自身路径做二次豁免。
     接口相应改为 `DeleteExpired(TimeSpan idle, ISet<string> activeKeys, ISet<string> protectedPaths)`；
     Mongo 实现忽略 `protectedPaths`（传空集），JSON 实现两者都跳过。
   - Mongo 实现：`deleteMany`，过滤
     `{ lastActiveUtc: { $lt: cutoff }, hasPendingMemory: false, "payload.pendingToolRound": null,
        _id: { $nin: activeIds }, "payload.recentRequests": { $not: { $elemMatch: { status: "processing" } } } }`。
     `payload` 是子文档，可按字段查询，故保护条件可下推到数据库。
   - **不能对 `sessions` 建 TTL 索引**：TTL 无法表达「有待摘要/挂起轮就保留」的例外，
     会误删有业务副作用的会话。
2. **legacy v1 归档逻辑必须随 DTO 传递**（原 v2 遗漏，会丢功能）。归档触发在
   `SessionStore.Save`（`SessionStore.cs:216-226`），依赖 `session.LegacySourcePath`；而该值来自
   `Load`（`FromDto` 参数，`SessionStore.cs:487/506`）。若 `Load`/`Save` 只交换 `SessionFileDto`，
   这个信息就断了。做法：
   - `SessionFileDto` 增补 `[JsonIgnore] public string legacySourcePath;`（仅进程内，不落盘/不进 BSON）；
   - `JsonSessionPersistence.Load` 解析出 legacy 路径后写入 `dto.legacySourcePath`，门面 `FromDto`
     改读该字段设置 `SessionState.LegacySourcePath`；
   - `JsonSessionPersistence.Save` 用 `dto.legacySourcePath` 做归档，通过
     `SessionSaveResult.LegacyArchived` 回报，门面据此把 `session.LegacySourcePath = null`
     （保持现状语义）；Mongo 实现恒返回 `{Persisted=true, LegacyArchived=false}`。
3. **`Load`/`List` 只返回 DTO，转换留在门面**。现有 `LoadFromDisk` 返回的是 `SessionState`（经 `FromDto`），
   且 JSON 分支包含 legacy 路径回退（`FilePath(...null...)`）与 `LastActiveUtc = fileTime` 兜底；
   `ListByGame` 的 JSON 分支同样有「文件时间兜底 lastActiveUtc」（`SessionStore.cs:289`）。
   这些 JSON 专有信息由 `JsonSessionPersistence` 在 `Load`/`List` 内部消化后写进 DTO，门面统一做 `FromDto`。
   - **`ListByGame` 的内存容量公式必须先钉死（否则 JSON 列表行为会变）**：现状两个分支不一致——
     JSON 分支直接 `new ShortTermMemory(Math.Max(2, dto.messages.Count + 2))`
     （`SessionStore.cs:274`），MySQL 分支却是 `FromDto(..., Math.Max(1, count / 2 + 1), ...)`，
     经 `FromDto` 后容量为 `Max(2, maxTurns * 2)`（`SessionStore.cs:259`）。统一走 `FromDto` 后
     **必须明确采用哪一个公式**（建议统一为 JSON 现有语义，即按消息条数，避免列表加载出来的会话
     容量变小、后续淘汰时机改变），并补一条「列表加载与磁盘加载容量一致」的回归用例。
   - **加载失败必须沿用“从空白会话继续”**：现状 `LoadFromDisk` 在 MySQL 分支捕获异常、
     记 warning 后 `return null`（`SessionStore.cs:155-159`），不会把一次瞬时存储抖动升级为聊天请求 500。
     接口化后该 `try/catch` 应留在**门面**统一包裹 `_persistence.Load`（对 JSON/Mongo 一视同仁），
     而不是让 Mongo 实现把异常直接冒泡。
4. **`Delete` 只负责持久层删除，`Map.TryRemove` 留在门面**。现有语义是
   `persisted || removedFromMemory`（`SessionStore.cs:359-385`），且 JSON 删除失败会抛 `IOException`
   并保留内存状态；这条契约必须在门面层维持。

**注意 `Save` 的异常语义（易自相矛盾，必须与 §5.4 保持一致）**：§5.4 已确立「Mongo 审计 store 对
非重复键异常照常抛出、由服务层重试并包成 `MemoryAuditWriteException`」——即**写失败是抛异常，不是静默返回**。
`SessionStore.Save` 也是同一模式：现有门面捕获异常返回 `false`，而调用方（`ChatEndpoints.cs:313/471/591`）
依赖这个 `false` 来撤销幂等状态/记警告并回滚。因此接口化后应统一为：
- **`ISessionPersistence.Save` 返回 `SessionSaveResult`（含 `Persisted`），不抛异常**（内部捕获，与原
  `SessionStore.Save` 的「吞异常返 false」等价）；门面 `SessionStore.Save` 继续返回 `bool`。
  这与 §5.4 审计 store 的「抛异常」是**两类不同契约**：审计要触发重试，会话保存失败由聊天路径就地降级。
- 关键是**不要出现第三种语义**（如实现抛异常、门面只 catch 一部分），否则 `ChatEndpoints` 里的
  `if (!SessionStore.Save(...))` 分支要么永不触发（异常直接冒泡 500）、要么触发逻辑错位。
- 同理，`Load` 的「失败 → 空会话」、`Delete` 的「失败 → 抛 `IOException` 且保留内存状态」三条语义
  必须成文并各自有回归用例（§13）。

### 8.2 实现与门面
- `JsonSessionPersistence`：把 `SessionStore` 的文件 I/O（`LoadFromDisk` 的 JSON 分支、`Save` 的写盘与
  `.migrated.bak` 归档、`List`/`ScanPending`/`Delete` 的枚举与删除、`EnumerateSessionFiles`/`FilePath`/
  `WriteAtomic`）搬入；**JSON 专有的 legacy 路径回退、文件时间兜底与归档也在实现内完成，对接口只暴露 DTO**
  （legacy 路径经 `SessionFileDto.legacySourcePath` 传递，见 §8.1 约束 2）。
  `DeleteExpired` 实现原 `PruneInactiveFiles` 的文件删除与保护条件判断。
  **`PruneInactiveFiles` 门面方法保留**（它负责从 `Map` 构造 `activeKeys` 与 `protectedPaths`），
  内部委托给 `DeleteExpired`：`activeKeys` 用 `IdentityKey` 从 `Map` 各会话构造，
  `protectedPaths` 收集 `Map` 中各会话的 `LegacySourcePath`（见 §8.1 约束 1），
  Mongo 实现收到空 `protectedPaths` 即可。
  门面 `Save(SessionState)` 改为：构造 `dto` 时**带上 `dto.legacySourcePath = session.LegacySourcePath`**，
  再 `var r = _persistence.Save(gid, dto); if (r.LegacyArchived) session.LegacySourcePath = null; return r.Persisted;`。
  归档的前置条件（`summary` 与 `facts` 均已清空）由 `JsonSessionPersistence.Save` 用 DTO 上的
  `summary`/`facts` 判断，与现状一致（`SessionStore.cs:216-219`）。
- `JsonChatLogStore`：把 `ChatLogService` 的 jsonl 写入/查询/`Cleanup`（写入时清理超期文件）搬入。
  **仅搬「落盘」逻辑**：`ChatLogService.Record` 里的内存聚合统计（`Aggregate`/`Stats`/`Snapshot`）
  仍留在门面。`Cleanup` 需要的保留天数由构造参数注入
  （`new JsonChatLogStore(configChatRetentionDays)`，来源同现有 `ChatLogService.Configure`），
  不进入共享接口。
  > **`Record` 必须保留「写失败不冒泡、但仍继续聚合统计」的语义**：现状 `Record` 用 try/catch 包住
  > `WriteFile`，失败只记 warning + RuntimeLogs，然后**照常执行 `Aggregate`**（`ChatLogService.cs:65-78`）。
  > 接口化后 `_store.Write` 仍可能抛（Mongo 不可达/磁盘故障），门面 `Record` 的 try/catch 要原样保留，
  > 顺序也不能变（先写后聚合、写失败不影响聚合）。否则一次存储故障会打断聊天请求或丢掉用量统计。
  > 这与会话 store「不抛异常」、审计 store「抛异常触发重试」是第三类契约，注意区分。
  > **附带**：`Snapshot` 的 `note` 文案硬编码「明细见 data/logs/{gameId}/ 按日 jsonl」
  > （`ChatLogService.cs:292`），Mongo 模式下该路径不存在，文案应随 provider 调整（或改为中性表述），
  > 否则控制台用量页会误导。
- `JsonMemoryAuditStore`：把 `MemoryAuditService` 的文件分支搬入，并承接
  `LogMaintenanceService.CleanupAuditFiles` 的删除逻辑为 `DeleteExpired`。
  > **`id`/`ts`/`actor` 的默认值必须留在服务层、且在重试循环之外只赋一次**：现状
  > `MemoryAuditService.Write` 里给 `entry.id = "audit-"+Guid` 等赋值，而 `RecordRequired` 的重试复用
  > **同一个 `entry` 对象**，所以重试时 id 稳定、重复写被幂等吸收（`MemoryAuditService.cs:98-100,116`）。
  > 接口化后这段必须在调用 store 之前完成（留在 `MemoryAuditService`，或让 store 只认已赋值的 entry）；
  > 若把默认值下沉到 store 内每次重试都重新生成 id，幂等就会失效、产生重复审计记录。
  > **同时把静态 `FileLock` 一起搬进 store**：JSON 写路径的 `File.ReadLines` + `File.AppendAllText`
  > 靠它串行化（`MemoryAuditService.cs:37,112`），移入 store 时锁必须跟着走，否则并发 `RecordRequired`
  > 会交错读写同一 jsonl。
- `MongoSessionPersistence` / `MongoChatLogStore` / `MongoMemoryAuditStore` / `MongoMemorySummaryJobPersistence`。
- `SessionStore` / `ChatLogService` **保留静态类**，把 `UseMySql(factory)` 换成 `UsePersistence(ISessionPersistence)` / `UseStore(IChatLogStore)`；`MemoryAuditService`、`MemorySummaryQueue` 改为接口 ctor 注入。
- 摘要任务 Json 模式无实现（`null`），与原行为一致。

### 8.3 收益
静态类不再引用任何数据库类型；测试可用假实现，**无需连库**（直接化解「引入首个依赖数据库的测试」风险）；
未来再换/加后端只加实现类。

### 8.4 门面默认实现（否则现有测试立刻空引用）

现有测试**直接调用静态门面而不经过 `Program.cs`**，例如
`PlayerMemoryTests.cs:442` 的 `SessionStore.Save(session)`、`:436` 的 `SessionStore.GetOrCreate(...)`、
`GameToolBridgeTests` 等。原实现里 `MySqlPersistence == null` 时门面就走文件分支，天然可用。

改成注入后，**静态字段必须默认指向 JSON 实现**，不能默认 null：

```csharp
private static ISessionPersistence _persistence = new JsonSessionPersistence(DataStore.FindDataRoot);
public static void UsePersistence(ISessionPersistence persistence) { _persistence = persistence; }
```

`ChatLogService` 同理（默认 `new JsonChatLogStore(configChatRetentionDays)`）。这样：
- 测试与未启动 `Program.cs` 的场景继续走 JSON；
- `Program.cs` 里 Json 模式可**不显式注入**，或注入等价实现，行为一致；
- 只有 Mongo 模式才 `UsePersistence(new MongoSessionPersistence(...))`。

> **默认实现需要参数，不能写 `new JsonSessionPersistence()` 空构造**：`JsonSessionPersistence` 依赖
> 数据根工厂（§9），`JsonChatLogStore` 依赖保留天数（§8.2），字段初始化器里必须把这两个值补上
> （数据根用 `DataStore.FindDataRoot`；保留天数在静态字段初始化时不可从 `IConfiguration` 拿到——
> 可先给一个默认值 `30`，等 `ChatLogService.Configure` 时再按配置重置，保持现有测试行为）。
> §5.4「审计 store 抛异常」与本节「会话 store 不抛异常」是**两套契约**，实现时不要混。

> **`UsePersistence` 必须同时 `Map.Clear()`**：现有 `UseMySql` 就是这样做的
> （`SessionStore.cs:85`），目的是丢弃上一个后端的进程内缓存，避免模式切换后读到陈旧会话。
> 新方法沿用，否则 `Program.cs` 调用后 `Map` 里可能残留 JSON 分支加载的会话。

> 若忽略这点，Json 模式下 `SessionStore` 会在测试里抛 `NullReferenceException`，
> 属必踩的坑。

---

## 9. 接线（Program.cs 目标形态）

```csharp
StorageOptions storageOptions = StorageOptions.From(builder.Configuration);
storageOptions.Validate();
MongoConnectionFactory mongoFactory = storageOptions.IsMongo
    ? new MongoConnectionFactory(storageOptions.MongoConnectionString, storageOptions.MongoDatabase) : null;

if (storageOptions.IsMongo)
{
    builder.Services.AddSingleton(mongoFactory);
    builder.Services.AddSingleton<IMemoryRepository, MongoMemoryRepository>();
    builder.Services.AddSingleton<IMemoryAuditStore, MongoMemoryAuditStore>();
    builder.Services.AddSingleton<ISessionPersistence, MongoSessionPersistence>();
    builder.Services.AddSingleton<IChatLogStore, MongoChatLogStore>();
    builder.Services.AddSingleton<IMemorySummaryJobPersistence, MongoMemorySummaryJobPersistence>();
}
else
{
    builder.Services.AddSingleton<IMemoryRepository, JsonMemoryRepository>();
    // Json 实现有构造参数（数据根工厂 / 保留天数），DI 无法自行提供 Func<string> 与 int，
    // 必须用显式实例注册；JsonChatLogStore 的保留天数与 ChatLogService.Configure 同源。
    int chatRetentionDays = Math.Max(1, builder.Configuration.GetValue<int?>("Logging:ChatRetentionDays") ?? 30);
    builder.Services.AddSingleton<IMemoryAuditStore>(new JsonMemoryAuditStore(DataStore.FindDataRoot));
    builder.Services.AddSingleton<ISessionPersistence>(new JsonSessionPersistence(DataStore.FindDataRoot));
    builder.Services.AddSingleton<IChatLogStore>(new JsonChatLogStore(chatRetentionDays));
    // 摘要任务 Json 模式无持久化实现（MemorySummaryQueue 的 IMemorySummaryJobPersistence 参数保持 null）
}
// MemoryAuditService 由 IMemoryAuditStore 构造，两种模式都要注册
builder.Services.AddSingleton<MemoryAuditService>();
```

> **`JsonSessionPersistence`/`JsonChatLogStore` 的构造依赖不要漏**：`JsonSessionPersistence` 需要数据根
> 定位（沿用 `DataStore.FindDataRoot`），`JsonChatLogStore` 需要 `Logging:ChatRetentionDays`
> （§8.2 已明确它由构造参数注入）。用 `AddSingleton<ISessionPersistence, JsonSessionPersistence>()`
> 这种「按类型注册」的写法会因 DI 解析不出 `Func<string>`/`int` 而启动即失败——必须像上面那样
> `new` 实例注册。`JsonMemoryAuditStore` 同理（方案原文已用 `new`，保持一致）。

`MemorySummaryQueue` 的 ctor 参数由 `MySqlConnectionFactory mysqlFactory = null` 改为
`IMemorySummaryJobPersistence jobPersistence = null`；`LogMaintenanceService` 的
`MySqlConnectionFactory mysql = null` 改为 `IMemoryAuditStore auditStore = null`（
Session 清理仍走 `SessionStore` 静态方法，chat 无共享清理方法）。

启动阶段（`SessionStore`/`ChatLogService` 从容器取实现注入静态门面）：

```csharp
SessionStore.UsePersistence(app.Services.GetRequiredService<ISessionPersistence>());
ChatLogService.UseStore(app.Services.GetRequiredService<IChatLogStore>());
// 现有 ChatLogService.Configure 调用必须保留：改造后它只负责注入 RuntimeLogs
// （写入失败的运行时日志依赖它）；保留天数已在 JsonChatLogStore 构造时注入。
ChatLogService.Configure(builder.Configuration, app.Services.GetRequiredService<RuntimeLogService>());
```

> **别把 `ChatLogService.Configure` 一起删了**：它的 `RetentionDays` 职责虽已移到 store（§8.2），
> 但 `RuntimeLogs` 赋值仍是 `ChatLogService.Record` 失败时写运行时日志的唯一来源
> （`ChatLogService.cs:26-30,74-75`）。只保留 RuntimeLogs 那一半、调用点不能漏。

`AutoMigrate` 与 `--migrate-json` 门禁由 `IsMySql` 改为 `IsMongo`；迁移器泛化后：
`new JsonMemoryRepository()` → `new MongoMemoryRepository(mongoFactory)`。

> **`MongoInitializer` 必须排在 `--migrate-json` 之前执行**：现有 `Program.cs` 里 AutoMigrate 块
> （`Program.cs:74`）本就在迁移块（`:79`）之前，改造后要保持这个顺序——迁移会向
> `player_memories` 写入并依赖 CAS upsert；虽然 Mongo 会自动建集合，但先跑 `MongoInitializer`
> 才能保证索引/TTL 就绪，且 `--exit-after-migrate` 会在 `StartupDiagnostics` 之前 return，
> 不能指望后面再补建。同时 Mongo 模式 `Validate()` 已强制 `MongoAutoMigrate=true`（§4），
> 二者叠加即「迁移前必定初始化」。

### 9.1 必须同步的分支/调用点（原 `IsMySql` 散点，逐条）
1. `Program.cs` 单实例告警：`if (!storageOptions.IsMySql)` → `if (!storageOptions.IsMongo)`（JSON 才提示）。
2. `Program.cs` `AutoMigrate` / `--migrate-json` 门禁 → `IsMongo` / `MongoAutoMigrate`。
3. `AdminEndpoints` provider 值 → `storage.IsMongo ? "Mongo" : "Json"`；
   `AdminEndpoints.cs:136` 的 `GetService<MySqlConnectionFactory>()` 改为取
   `IMemoryRepository`/`MongoConnectionFactory`，迁移入口的 `if (!IsMySql)` 拒绝逻辑改写为 `IsMongo`。
   - 相关用户可见文案要一起改：`AdminEndpoints.cs:135`「请以 MySQL 模式启动」、`:138`「MySQL 连接未初始化」
     改为 Mongo；
   - `MemoryError` 里审计失败的 `detail` 硬编码「请检查磁盘与 data/logs 权限」（`AdminEndpoints.cs:1049`），
     Mongo 模式下该提示会误导，应改为中性表述（如「请检查存储可用性」）或按 provider 分支。
4. `StartupDiagnostics` 的 `.last-storage-mode` → `storage.IsMongo ? "Mongo" : "Json"`，否则控制台「上次运行模式」横幅失效。
   **同时要归一化历史值**：存量 marker 里可能是旧值 `"MySql"`，而前端 `previousProvider` 联合类型已改为
   `'Mongo' | 'Json' | null`，直接透出会渲染出一个类型外的字符串。读 marker 时把非 `Mongo`/`Json`
   的历史值视为 `null`（或按 `Json` 处理），并写回新值。
5. `StartupDiagnostics` / `ReadinessService`：Json/Mongo 二选一，Mongo 分支必须显式探测（ping + `listCollections`），否则 Mongo 不可达时 `/api/ready` 误报 ready。**注意签名变更会影响测试**：
   - `StartupDiagnostics.RunAsync(storage, mySqlFactory, config, ct)` / `ReadinessService.CheckAsync(storage, mysql: ..., queue, config, ct)` 的第二个参数由 `MySqlConnectionFactory` 改为 `MongoConnectionFactory`；
   - `ApiKeyManagementTests.cs:239-240` 用具名参数 `mysql: null` 调用，改造时必须同步（否则编译失败）。
   - `MemorySummaryQueue`、`LogMaintenanceService` 的 ctor 注入也随之从工厂换成接口（见 §9 末尾）。
6. `LogMaintenanceService`：清理职责按后端重排（现状用 `IsMySql` 二选一，需改为三分支）。
   - JSON：chat 由 `JsonChatLogStore.Write` 写入时自清理（保持现状）；audit 走
     `IMemoryAuditStore.DeleteExpired`；Session 走 `SessionStore.PruneInactiveFiles`（→ `DeleteExpired`）。
   - Mongo：chat/audit 交 TTL（**跳过**），Session 仍走 `PruneInactiveFiles`（→ Mongo `deleteMany`）。
   - 注意 `IChatLogStore` **没有** `DeleteExpired`（见 §8.1），故 `LogMaintenanceService` 不应再调用
     chat 的共享清理方法。
7. 迁移器改名：`AdminEndpoints`、`Program.cs`（及测试）引用同步。
8. **必需集合清单**：`ReadinessService.cs:12` 与 `StartupDiagnostics.cs:22` 各有一份硬编码
   `RequiredTables`（含 `schema_migrations`、`memory_facts`）。Mongo 下这两张表不存在（分别被 `_meta`
   与内嵌 `facts` 取代）。Mongo 分支应校验的集合为：
   `player_memories`、`memory_audits`、`chat_logs`、`sessions`、`memory_summary_jobs`、`_meta`。
   探测方式改为 `db.listCollectionNames()` 后做集合名比对（错误信息保持脱敏，不回传连接串）。
   **该清单成立的前提是 `MongoAutoMigrate=true`**（§4）：Mongo 集合惰性创建，未建索引时
   `listCollectionNames` 为空，故 `Validate()` 已强制 Mongo 模式必须开启 AutoMigrate，
   避免出现「连接健康但 `/api/ready` 恒 503」的误判。
9. `MemorySummaryQueue` 的可选 ctor 参数（`IMemorySummaryJobPersistence x = null`）与现有 `RuntimeLogService ... = null` 用法一致，MS DI 可用默认值，无需额外 `TryAdd`。
10. 前端控制台（见 §9.2）。

> **`IsMongo` 必须与 `IsMySql` 一样忽略大小写**：现有 `IsMySql` 用
> `string.Equals(Provider, "mysql", StringComparison.OrdinalIgnoreCase)`（`StorageOptions.cs:15`）。
> 环境变量里写 `Mongo`/`mongo`/`MONGO` 都应生效，新属性照抄同一比较，别只比 `"Mongo"` 字面量。

> **查询响应里的时间字段要还原成字符串/ISO，不能把 BSON 类型直出**：`MemoryAuditEntry.ts` 在前端是
> `string`、`ChatLogEntry.ts` 同样；MySQL 路径把 `DateTime` 放进 `JObject` 由 Newtonsoft 序列化成 ISO，
> JSON 路径直接透传原字符串。Mongo 查询返回 `JObject` 时须把 `BsonDateTime`/`DateTime` 转成同样的
> ISO 字符串（`ts`），否则管理台日志/审计列表的日期渲染会变。这是 §5.3/§5.4「写入转 Date」的另一半，
> 别只做写入侧。

> **`ChatLogService.Configure` 的职责要拆清**：它现在同时设置 `RetentionDays` 与 `RuntimeLogs`
> （`ChatLogService.cs:26-30`）。改造后 `RetentionDays` 由 `JsonChatLogStore` 构造参数持有
> （§8.2），`Configure` 只应保留 `RuntimeLogs`；否则保留天数出现两个真源（静态字段 + store），
> Mongo 模式下还会残留一份无用的静态值。Json store 的默认实例若在静态字段初始化时构造，
> 先给默认 30，由 `Configure` 或 DI 覆盖为配置值（§8.4）。

### 9.2 前端控制台同步（原方案遗漏）

| 文件 | 改动 |
|---|---|
| `src/AIBot.Web/src/types/memory.ts` | provider 联合类型 `'MySql' \| 'Json'` → `'Mongo' \| 'Json'`；`mysql` 块换 `mongo: { host, database, autoMigrate } \| null`（**不返回完整连接串**）；`previousProvider` 同步 |
| `MemorySettingsView.vue` | 标签配色、目标行、迁移按钮文案按 Mongo 调整；第 68/72 行的 `provider === 'MySql'` 判断改 `'Mongo'`；第 54 行提示语里的「从 JSON 迁移到 MySQL」改 Mongo |
| **`src/AIBot.Web/src/api/memory.ts`（原方案遗漏）** | `migrateJsonToMysql` 方法名与错误文案含 MySQL（`api/memory.ts:15`）。端点路径 `/api/admin/storage/migrate-json` 保持不变即可（服务端保留该路径），但**方法名/文案应同步改名**，否则源码里仍到处是 MySQL；若重命名，`MemorySettingsView.vue` 的调用处一并改 |
| `stores/app.ts` | `storageLabel` 只插值 `s.provider`（通用字符串），**无需改动**；仅确认展示正常 |
| **`src/AIBot.Server/wwwroot/app/**`（构建产物）** | **必须重新构建并提交**：见下方说明 |

> **关键：Server 提供的是已构建产物，不是 Vue 源码。** `src/AIBot.Web/vite.config.ts:26` 的 `outDir`
> 指向 `../AIBot.Server/wwwroot/app`，且该目录已被 git 跟踪（`wwwroot/app/index.html` 及带哈希的
> `assets/*.js`）。`Program.cs` 用 `app.UseDefaultFiles(); app.UseStaticFiles();` 直接对外提供这些文件。
> 因此改完 Vue 源码后必须执行 `npm run build`（`package.json:8`，含 `vue-tsc -b`）重新生成并提交
> `wwwroot/app/**`，否则控制台页面仍显示旧的「MySql」界面——这是只改源码看不出来的坑。
> `types/memory.ts` 的联合类型若不同步，`vue-tsc` 会直接让构建失败。

---

## 10. 迁移（仅 JSON→Mongo）

- 复用 `JsonToMemoryRepositoryMigrator`（原 `JsonToMySqlMemoryMigrator`，只依赖 `IMemoryRepository`）：
  ```bash
  # Windows PowerShell（AIBOT_* 是自定义键；Storage:MigrationGameId 用标准的双下划线环境变量形式）
  $env:AIBOT_STORAGE_PROVIDER="Mongo"
  $env:AIBOT_MONGO_CONNECTION_STRING="mongodb://..."
  $env:Storage__MigrationGameId="fogharbor"
  dotnet run --project src/AIBot.Server -- --migrate-json --exit-after-migrate
  ```
  > 注意迁移 game 的键是 `Storage:MigrationGameId`，**没有** `AIBOT_` 前缀的自定义变量；
  > 它在 `Program.cs` 里通过 `builder.Configuration` 读取，环境变量只能用 `Storage__MigrationGameId`
  > 双下划线形式，或直接写在 `appsettings.json`。
- 管理 API `POST /api/admin/storage/migrate-json` 保留，target 换成 `MongoMemoryRepository`。
- 幂等：目标 `memoryVersion > 0` 跳过。
- **不做 MySQL→Mongo**（Q2：MySQL 数据直接删除；且 Session/日志/审计本无 JSON 回退，删除即作废）。

> **迁移只处理单个 game**：`JsonToMemoryRepositoryMigrator.RunAsync(gameId, ct)` 每次只扫一个 game，
> game 取自 `Storage:MigrationGameId`（默认 `"default"`，见 `Program.cs:85`）。
> 而当前唯一的记忆文件实际位于 **`fogharbor`**（`data/games/fogharbor/memories/herbalist_lin/player-001.json`），
> 不是 `default`。因此必须显式设置 `Storage:MigrationGameId`（或环境变量）为 `fogharbor`，
> 否则命令跑完 `scanned=0` 却以为迁移成功。当前只有一个 game 有待迁数据，无需扩展为多 game 循环；
> 若将来多 game 都有数据，再迭代调用或改为遍历 `DataStore.ListGameIds()`。

### 10.1 迁移范围与数据丢失（必须知悉）

现有迁移器**只迁长期记忆**。切到 Mongo 后，JSON 目录下这些数据将不再被读取：

| 数据 | JSON 位置 | 切换后 |
|---|---|---|
| 玩家长期记忆 + 事实 | `data/games/*/memories/*.json` | **迁移** |
| Session（对话窗口、待摘要批次、挂起工具轮、幂等请求记录） | `data/games/*/sessions/**/*.json` | **不迁 → 丢失** |
| 对话日志 | `data/logs/*/*.jsonl` | **不迁 → 丢失** |
| 记忆审计 | `data/logs/*/memory-audit/*.jsonl` | **不迁 → 丢失** |
| NPC/世界/记忆策略/系统设置 | `data/games/*/*.json`、`data/system-settings.json` | 仍读文件，不受影响 |

原方案（JSON→MySQL）也**只迁记忆**，所以这是既有行为；但那时 JSON 是「开发」，MySQL 是「可选」，
影响有限。现在 Mongo 是唯一正式后端、这条迁移成为主路径，必须显式决策：

- **采纳方案 A：仅迁长期记忆，其余不迁、也不做「首次运行」提示。**
  - 现有 `data/` 实际数据（已核查）：长期记忆 1 个文件（≈2.5KB）、**Session 0 个**、
    **对话日志 7 个 jsonl**（`default` 5 + `fogharbor` 2）、**审计 3 个**（`default` 2 + `fogharbor` 1）、
    另有 runtime 日志 5 个（共 15 个 jsonl，均个体很小，最大 ≈16KB），基本是开发联调残留。
    Session 为零意味着「切换后对话上下文清空」这一最大风险在当前数据下不存在。
  - 迁移器改名保留 `--migrate-json` 能力，可把那 1 条长期记忆搬入 Mongo。
  - **不新增任何「首次运行检测/提示」逻辑**：启动脚本不猜测是否首次，避免为一次性风险引入
    标记文件等额外状态。将来若真有生产数据需迁 Session/日志/审计，再按方案 B 补一次性工具，
    由该工具自行打印迁移统计。
- **方案 B（不采用，备查）**：扩展迁移器，把 Session/日志/审计也搬入 Mongo。需新增
  `JsonSessionPersistence → MongoSessionPersistence`、`JsonChatLogStore → MongoChatLogStore`、
  `JsonMemoryAuditStore → MongoMemoryAuditStore` 的搬运逻辑，工期 +1 天。仅在确有历史需保留时启用。

> 无论 A/B，**记忆迁移必须与 Session 迁移同时考虑**：`ClearPlayerMemoryAsync`、待摘要批次
> （`evictedMessages`）都挂在 Session 上。只迁记忆不迁 Session，会出现「旧记忆在、但产生它的
> 对话窗口没了」，后续摘要不会重复触发，属可接受。
> 当前实测 Session 为 0，故 A 方案下不存在「上下文被清空」的实际影响，无需任何提示（与 §12.3 一致）。

---

## 11. MySQL 下线清单

| 类别 | 删除/改动 |
|---|---|
| Server 代码 | 删 `MySqlConnectionFactory.cs`、`MySqlSchema.cs`、`MySqlMemoryRepository.cs`、`MySqlSessionPersistence.cs`、`MySqlMemorySummaryJobPersistence.cs`、`DatabaseMigrator.cs`；`JsonToMySqlMemoryMigrator.cs` 改名 |
| **测试工程编译清单** | `AIBot.Tests.csproj` 是显式 `<Compile Include>` 清单（非通配符）：删掉上述 5 个文件对应的 5 条 `<Compile>`，并为新抽出的接口/Json/Mongo 实现逐条补链（见 §3） |
| 依赖 | 两个 csproj 移除 `MySqlConnector`、`Dapper` |
| 数据库资产 | 删 `database/mysql/`（`schema.sql`、`migrations/`） |
| 容器/脚本 | `docker.yml` 移除 `mysql` 服务与卷；删 `start-server-mysql.ps1` |
| 配置 | `appsettings.json` 移除 `Storage:MySql`；`.env` / `.env.example` 移除 `AIBOT_MYSQL_*`；`StorageOptions.From` 中一并移除通用的 `Storage:ConnectionString` 与 `Storage:AutoMigrate` 回退键（它们原本是 MySql 的别名） |
| **`.env.example` 要补 Mongo 键（不只删）** | `.env.example` 是「复制即用」的模板（`start-server-mongo.ps1` 读 `.env`）：删掉 `AIBOT_MYSQL_*` 后必须补上 `AIBOT_MONGO_USER/PASSWORD/PORT/DATABASE/AUTOMIGRATE`（内容见 §12.2），否则新用户 `Copy-Item .env.example .env` 后脚本取不到必填项。`README.md` 中的启动示例、`.env` 键说明、端口占用提示也要同步 |
| 文档 | `README.md`、`docs/记忆管理与Vue控制台设计方案.md`、**`docs/architecture/AI-NPC-Agent-实施方案.md`** 的 MySQL 段落改写为 Mongo；另清理代码内注释/摘要里的 MySQL 表述（如 `ChatEndpoints.cs:926`「Session 文件或 MySQL payload」、`LogMaintenanceService` 类注释「MySQL 与 JSON 模式共用」） |
| 前端产物 | `src/AIBot.Server/wwwroot/app/**` 用 `npm run build` 重新生成并提交（§9.2） |
| 测试 | `StorageAndGameTests` 中 MySQL 相关用例改为 Mongo |

> 全仓库 MySQL/Dapper 引用扫描结果（已执行）：涉及的代码/配置/文档共 30+ 处，全部落在上表
> 与 §2 的改造范围内，无遗漏文件。特别提醒 `docs/architecture/AI-NPC-Agent-实施方案.md` 含
> 版本历史表、**§6.2.1「JSON / MySQL 双存储」整节**、架构图（`AIBot.Server ─── JSON 或 MySQL`）、
> M6 里程碑等多处 MySQL 表述，是除 README 与设计文档外的**第三份需同步的文档**。

---

## 12. 运维

### 12.1 `docker.yml`
移除 `mysql` 服务，新增：

```yaml
  mongo:
    image: mongo:7
    container_name: ai-npc-mongo
    restart: unless-stopped
    environment:
      MONGO_INITDB_ROOT_USERNAME: ${AIBOT_MONGO_USER:-aibot}
      MONGO_INITDB_ROOT_PASSWORD: ${AIBOT_MONGO_PASSWORD:-change-this-password}
      TZ: Asia/Shanghai
    ports:
      - "${AIBOT_MONGO_PORT:-27017}:27017"
    volumes:
      - ai_npc_mongo_data:/data/db
    healthcheck:
      # 带凭据 ping：避免在启用认证的实例上依赖「未认证也能 ping」的行为差异
      test: ["CMD-SHELL", "mongosh --quiet --username \"$$MONGO_INITDB_ROOT_USERNAME\" --password \"$$MONGO_INITDB_ROOT_PASSWORD\" --authenticationDatabase admin --eval \"db.runCommand({ping:1}).ok\" | grep -q 1"]
      interval: 5s
      timeout: 5s
      retries: 20
      start_period: 20s
volumes:
  ai_npc_mongo_data:
```
> standalone 无副本集；本方案不需要事务。若将来需要事务，改 `--replSet rs0` + `rs.initiate()`。

### 12.2 `.env`
```
AIBOT_MONGO_USER=aibot
AIBOT_MONGO_PASSWORD=change-me
AIBOT_MONGO_PORT=27017
AIBOT_MONGO_DATABASE=ai_npc
AIBOT_MONGO_AUTOMIGRATE=true
```
连接串：`mongodb://<user>:<pass>@127.0.0.1:<port>/?authSource=admin`。
> `start-server-mongo.ps1` 读 `.env` 的这几个键拼连接串（对标原 `start-server-mysql.ps1` 的
> `AIBOT_MYSQL_*`）；`.env.example` 必须同步提供同名示例键（§11）。注意 Mongo 的密码出现在 URI 的
> userinfo 段，**若密码含 `@ : / ? # [ ]` 等必须 URL 编码**，否则连接串解析会错——脚本拼接时应
> `[Uri]::EscapeDataString($password)`（或改用 `MongoUrlBuilder`），别直接字符串插值。

> **`AdminEndpoints` 的 `mongo` 展示块不要回传连接串**：`config` 字段只暴露脱敏的 host/database/
> autoMigrate。解析用 `MongoDB.Driver.MongoUrl`（而非手写字符串拆分），并只取 `Hosts`/`DatabaseName`；
> 凭据永不进入响应（与现有 MySQL 分支同样的脱敏标准，§9.1 第 3 条）。

### 12.3 `start-server-mongo.ps1`
对标原 `start-server-mysql.ps1`：`docker compose up -d mongo` → 等 healthcheck →
注入 `AIBOT_STORAGE_PROVIDER=Mongo` 等 → `dotnet run`。
**不做「首次运行检测」或迁移提示**（见 §10.1：当前数据无需保留，且启动脚本不应猜测首次）。

### 12.4 备份
`mongodump`/`mongorestore` 或卷快照，README 补充。

---

## 13. 测试计划

> 项目当前**无任何依赖真实数据库的测试**，`dotnet test` 全绿。接口化后 Json 路径与门面逻辑可用假实现
> 覆盖；真 Mongo 集成用例必须**按环境变量 opt-in**（`AIBOT_MONGO_TEST_CONNECTION` 存在才跑），
> 否则默认会红。
>
> **opt-in 的实现方式（已实测验证，勿凭记忆）**：xUnit 版本为 **2.7.0**（`AIBot.Tests.csproj:41`）。
> - `Assert.Skip(...)` 在 2.7.0 **和 2.9.3** 均不存在（实测编译报 `CS0117: Assert 未包含 Skip 的定义`）；
>   它是 **xunit v3** 才提供的 API。照抄会编译失败。
> - 运行时 `throw SkipException.ForSkip("...")` 会被 v2 runner 记为 **FAIL**（实测），**不可用**。
> - **推荐做法**：派生子类 `FactAttribute`，在构造函数里按环境变量设置 `Skip` 属性（v2 的正规动态跳过）。
>   实测结果：未设环境变量时用例显示为「已跳过」、不计失败；设置后正常执行。
>
>   ```csharp
>   public sealed class MongoFactAttribute : FactAttribute
>   {
>       public MongoFactAttribute()
>       {
>           if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AIBOT_MONGO_TEST_CONNECTION")))
>               Skip = "未配置 AIBOT_MONGO_TEST_CONNECTION，跳过 Mongo 集成用例";
>       }
>   }
>   // 用法：[MongoFact] public async Task ... { ... }
>   ```
>
> - 备选：环境变量缺失时早退返回（测试记为**通过**，语义不如「已跳过」清晰）；或整体升级到 xunit v3
>   （改动面大，不建议仅为跳过能力升级）。

- `StorageOptions`：`Mongo` 解析、env 覆盖、缺连接串/库名 `Validate` 抛错、`IsMongo` 正确；
  **`IsMongo && !MongoAutoMigrate` 时 `Validate` 抛明确错误**（§4）。
- `MongoMemoryRepository`（opt-in）：首存 `expectedVersion=0` 成功、版本递增；陈旧版本抛
  `MemoryVersionConflictException` 且 `ActualVersion` 正确；并发两写 `expectedVersion=0` 恰一成功；
  删除级联清空事实；`List` 的 `factCount`/`hasSummary`/排序/分页/`total` 与 `JsonMemoryRepository` 对齐；
  **分页 `limit`/`offset` 与 JSON 同样 clamp 并回填**（§7.3）；**写入后 `updatedUtc == MaxLatest(...)`
  且回读 DTO 不含该字段**（§5.1）；`Load` 对非法 id 抛 `ArgumentException`（与 JSON/MySQL 一致）。
- `MongoSessionPersistence`（opt-in）：round-trip `SessionFileDto`（含 `simState`/`messages`/`evictedMessages`）；
  `ScanPending` 只返回 `hasPendingMemory=true` 且 `playerKey<>''`；**`playerKey==''` 的文档读回时
  `dto.playerId == null`（不是 `""`）**（§5.2）；
  **`payload` 里的 `DateTime`（`lastActiveUtc`/`recentRequests[].createdUtc`）round-trip 后值不变**
  （以 BSON 字符串形态存储，§5.2）；**`SimGameState.extras` 含 `.`/`$` 键时的行为明确且不炸**（§5.2）。
- `SessionStore` 入站归一化：`playerId=""` 与不传 `playerId` 归一到同一会话、不再产生两个 `_id`（§5.2）。
- `MongoMemoryAuditStore`（opt-in）：同 id 重复写只留一条；**`before`/`after` 为 null（`JValue.CreateNull`）与
  scalar/object 三种形态 round-trip 后形状不变**（§5.4，防 `BsonDocument.Parse("null")` 抛异常）；
  **`ts` 落库为 BSON Date、查询回读为 ISO 字符串**（§5.3/§5.4）。
- `MongoMemorySummaryJobPersistence`（opt-in）：状态流转与 `LoadRecoverable`/`LoadFailed` 行为正确。
- `ISessionPersistence.DeleteExpired`：JSON 实现删过期文件、保留含 `evictedMessages`/`pendingToolRound`/
  处理中请求的记录、跳过 `activeKeys`；Mongo 实现（opt-in）`deleteMany` 具备同等保护条件。
  **这是修复「Mongo 会话无限增长」缺口的关键回归用例。**
  额外覆盖：**未落盘的 v1 legacy 文件（DTO `playerId` 为空串、但被 `Map` 以带 playerId 的会话引用）
  经 `protectedPaths` 得到保护、不被 `DeleteExpired` 删除**（§8.1 约束 1）。
- `SessionStore.ListByGame` 容量：磁盘加载与会话列表加载同一会话时内存容量一致（§8.1 约束 3）。
- `SessionStore` 加载失败回退：假实现 `Load` 抛异常时门面返回空会话、不抛到调用方（§8.1 约束 3）。
- `ChatLogService.Query`：按日范围、可选 npc 过滤、分页、`total`（通过假 `IChatLogStore` 或 Json 路径）。
- `MongoInitializer`：连续两次 `ApplyAsync(mongo, config, ct)` 幂等（含配置签名），索引/TTL 正确；
  **初始化后 6 个必需集合齐全（含 `_meta`）**（§6）；**改小/改大保留天数后 TTL 索引被重建、
  `_meta.indexes` 同步**（§5.6/§6）。
- `ReadinessService`：Mongo 可达→`ok`；缺集合→`ok=false` + 列表；不可达→`error="database_unavailable"` 且不泄漏连接串。
- 门面回归：Json 模式下 `SessionStore`/`ChatLogService`/`MemoryAuditService` 行为不变。
  > **静态门面的假实现注入必须做测试隔离**：`UsePersistence`/`UseStore` 改的是进程级静态字段，
  > 一旦某个用例注入了假实现，后续用例会继承它（现有测试因 `MySqlPersistence` 始终为 null 而不受影响）。
  > 用假实现的用例必须在 `finally` 里 `SessionStore.UsePersistence(new JsonSessionPersistence(DataStore.FindDataRoot))`
  > 还原（`ChatLogService` 同理），或统一在 fixture 的 `Dispose` 里归一化，避免“单独跑绿、全量跑红”的顺序依赖。
- 现有 14 个测试文件**更新后**保持通过。需实际修改的用例：
  - **`AIBot.Tests.csproj` 的 `<Compile Include>` 清单**：删 5 条（MySQL 实现 + 旧迁移器），
    按新接口/Json/Mongo 实现补链（§3、§11）；这是最容易漏、且漏了会整体编译失败的一项；
  - `StorageAndGameTests` **四个**用例引用被删成员，需一并改（原文只列了三个，漏了第一个）：
    - `StorageOptions_DefaultsToJson`（`Assert.False(options.IsMySql)` / `Assert.False(options.AutoMigrate)`
      → `IsMongo` / `MongoAutoMigrate`）；
    - `StorageOptions_EnvVarsWinOverAppsettingsFalse`、`StorageOptions_AutomigrateRequiresExplicitEnable`
      （`Storage:MySql:AutoMigrate` 键与 `AIBOT_MYSQL_AUTOMIGRATE` → MongoDB 键/变量）；
    - `StorageOptions_MissingMySqlConnectionString_FailsValidate` → `MissingMongoConnectionString_FailsValidate`；
    - 并在 ctor/`Dispose` 中把 `AIBOT_MYSQL_*` 清理换成 `AIBOT_MONGO_*`（否则 env 跨用例泄漏）；
  - 4 处 `new MemoryAuditService(() => root)` / `(() => null)`（`PlayerMemoryTests` 2 处、
    `MemorySummaryQueueTests:83`、`ApiKeyManagementTests:238`）改为
    `new MemoryAuditService(new JsonMemoryAuditStore(() => root))`；
  - `ApiKeyManagementTests.cs:239-240` 的 `ReadinessService.CheckAsync(..., mysql: null, ...)`：
    具名参数随签名变更同步；
  - `MemorySummaryQueueTests:82`、`ApiKeyManagementTests:237` 构造 `MemorySummaryQueue` 时传入的
    `MemoryAuditService` 需按上面同步；第 4 个可选参数（jobPersistence）可省略。
  - 其中 `RequiredAudit_ThrowsWhenDataRootIsUnavailable` 断言 `RecordRequired` 抛
    `MemoryAuditWriteException`——接口化后必须保证该异常仍由服务层（重试 3 次后）抛出，而非被 store 吞掉。

---

## 14. 分阶段与工期

| 阶段 | 内容 | 估时 |
|---|---|---|
| P0 | `StorageOptions`(Mongo) + `MongoConnectionFactory` + `docker.yml` + `start-server-mongo.ps1` + 启动/就绪/诊断/管理台后端接线 | 0.5–1 天 |
| P1 | 抽 `IChatLogStore`/`IMemoryAuditStore`/`IMemorySummaryJobPersistence` + Json 实现搬运 + `MongoInitializer` + `MongoMemoryRepository` + 迁移器改名 + JSON→Mongo | 1.5–2 天 |
| P2 | 抽 `ISessionPersistence` + `JsonSessionPersistence`（搬 606 行中的文件逻辑，含 legacy 回退/归档）+ `MongoSessionPersistence` + `MongoChatLogStore`/`MongoMemoryAuditStore`/`MongoMemorySummaryJobPersistence` | 2–2.5 天 |
| P3 | MySQL 下线（§11 清单，含测试工程 `<Compile>` 清单同步）+ TTL 适配 + 前端同步（`npm run build`）+ 文档 + 测试补齐 | 1–1.5 天 |

**合计约 5–7 天**（比 v1 的 4–5 天多，增量来自 Q3 的会话持久化抽取与 MySQL 全面下线；无 P4）。
方案 B 已决定不采用（§10.1），不再计入。

### 14.1 实施进度（截至 2026-09-11）

| 阶段 | 状态 | 已落地 | 验证 |
|---|---|---|---|
| P0 | **完成** | `StorageOptions`(Mongo) + `MongoConnectionFactory`(UTC serializer) + `MongoInitializer`(6 集合/索引/TTL/`_meta`/自检) + `MongoMemoryRepository` + `docker.yml` mongo 服务 + `start-server-mongo.ps1` + 启动/就绪/诊断/管理台 Mongo 分支 | Mongo 模式实跑 `/api/ready=200`、`/api/admin/storage` mongo 块无凭据；容器内实查 TTL/`_meta` |
| P1 | **完成** | `IChatLogStore`/`IMemoryAuditStore`/`IMemorySummaryJobPersistence` + Json/MySql/Mongo 实现；`MemoryAuditService` 单 ctor；`LogMaintenanceService` 走 store；迁移器改名 `JsonToMemoryRepositoryMigrator` | HTTP PUT 策略→审计写 Mongo→查询回读；JSON→Mongo 迁移 `fogharbor` 实测（幂等重跑 skipped=1） |
| P2 | **完成** | `ISessionPersistence` + `JsonSessionPersistence`（legacy 回退/归档、`DeleteExpired` 的 `activeKeys`+`protectedPaths`）+ `MongoSessionPersistence` + `MySqlSessionPersistence` 适配；`SessionStore.UsePersistence`/`IdentityKey`/`PruneInactiveFiles` 接线；DTO 增 `[JsonIgnore] legacySourcePath` | HTTP 保存/列出会话落 Mongo（`_id=<gid>\|<npc>\|<playerKey>\|<sid>`、payload 子文档）；`DeleteExpired` 保护 legacy/待摘要/挂起轮/processing 单测 + opt-in 集成 |
| P3 | **完成** | 删除全部 `MySql*`/`DatabaseMigrator`/`MySqlSchema`/`database/mysql/`/`start-server-mysql.ps1`；两个 csproj 移除 `MySqlConnector`+`Dapper`；`StorageOptions`/`Program`/`Readiness`/`Diagnostics`/`Admin` 收敛为 Json/Mongo 二选一；`docker.yml` 移除 mysql 服务；appsettings/`.env(.example)` Mongo 化；前端 `types/memory.ts`+`api/memory.ts`+`MemorySettingsView.vue` 改 Mongo 并 `npm run build` 提交 `wwwroot/app/**`；README + 两份设计/架构文档同步 | 见下 |

> 测试：默认 `dotnet test` **145 通过 / 18 跳过**；设 `AIBOT_MONGO_TEST_CONNECTION` 后 **163 全通过**。
> 第八~十轮复查（围绕「迁移后还需优化什么」）补齐三项差距：① README 补 Mongo 备份说明（mongodump/mongorestore + 卷快照，落实 §12.4）；② 新增 `MongoInitializerTests` 行为级用例（两次 `ApplyAsync` 幂等、保留天数变更后 TTL 重建、必需索引齐全——此前只有调用无断言）；③ `sessions` 补 `{lastActiveUtc:1}` 索引并在真实 `ai_npc` 库验证存量升级时幂等补建。
> Mongo 模式实跑：`/api/ready=200`、`/api/admin/storage` 仅返回 `mongo` 块（无 `mysql`）且不含连接串、内置控制台 `index.html` 200、`POST /api/admin/storage/migrate-json` 200。
> **已无 MySQL/Dapper 引用**（源码、csproj、配置、脚本、三份文档；`docs/MongoDB存储改造方案.md` 内的历史对照除外）。

> **P3 额外发现（保留为行为约束）**：`Provider=MySql` 等未知值**不能静默回退 JSON**——否则设置过 `AIBOT_STORAGE_PROVIDER=MySql` 的部署会「数据凭空消失」。`StorageOptions.Validate()` 现在对非 `Json`/`Mongo` 的 Provider 直接抛错。

> **第四轮复查发现（真实故障，已修）**：`start-server-mongo.ps1` 与 `.env`/`.env.example` 均为「无 BOM 的 UTF-8 + 中文注释」，而 Windows PowerShell 5.1 默认按 ANSI(GBK) 读取 → 中文注释行末吞换行，**紧随其后的 `AIBOT_MONGO_USER`/`AIBOT_MONGO_AUTOMIGRATE` 被并入注释而丢失**，脚本报 `Missing required setting 'AIBOT_MONGO_USER'` 直接失败。修复：脚本显式 `Get-Content -Encoding UTF8` 读 `.env`，并给 `.ps1` 加 UTF-8 BOM（PS 5.1/7 均兼容）。**教训：仓库内任何会被 PowerShell 5.1 执行的含中文脚本都必须带 BOM 或显式指定编码。**
> 另修一处测试隔离：新增 `AIBot.Tests/AssemblyInfo.cs` 关闭 xUnit 类级并行（静态门面/环境变量在类间共享，会偶发「单跑绿、全量红」）。

> **P0/P1/P2 实测新增的坑（已写入 §15）**：BSON `Date` 毫秒精度；`BsonDocument` 索引器不接受 null 字符串（可空字段用 `BsonNull.Value`）；按日查询的范围必须与 `ts` 的存储时区一致；同毫秒 `ts` 排序不稳定。另：`SessionStore.Delete` 依赖实现抛 `IOException` 表示失败并保留内存状态，Mongo 实现已相应包装。

> **第五轮复查发现（真实故障，已修）**：`BsonValue.ToJson()` 默认 Shell 模式是**扩展 JSON**——Int64 写成 `NumberLong(...)`、DateTime 写成 `ISODate(...)`，Newtonsoft `JToken.Parse` 直接抛 `JsonReaderException`（实测 NumberLong 被误读为 NaN）。审计条目的 `before/after/metadata` 承载任意 JToken，含大整数（如管理员自定义 extensions、LLM 数值）时该条审计**永远查不出来且端点 500**。修复：新增 `BsonJson.ToJToken/ToJsonString`（BsonValue→JToken 直接映射，覆盖 Document/Array/标量/DateTime/ObjectId/Binary/Decimal/Timestamp），替换审计与 Session payload 全部三处 `ToJson()+Parse` 回读，并加 Int64 往返用例钉住。**教训：凡把 BsonValue 还原成 Newtonsoft JSON 的地方一律走显式映射，禁止 `JToken.Parse(bson.ToJson())`。**
> 同轮加固：`MongoMemoryRepository.Save` 在 CAS 提交后回读为 null（文档被并发删除）时抛 `MemoryVersionConflictException(expected, 0)`，不再返回 null 让上层 NRE；`MongoSessionPersistence.Save` 失败补 warning 日志（对齐 JSON 实现的可观测性）。
> 流程注记：新增 `BsonJson.cs` 时再次踩到「测试工程显式 `<Compile>` 清单」——服务器（通配符）能编译而测试工程报 CS0103，印证 §3 的提醒。

---

## 15. 风险与架构澄清

| 风险 | 影响 | 缓解 |
|---|---|---|
| BSON `DateTime` Kind 丢失 | 时间偏移，影响按日查询/排序 | 注册 UTC `DateTimeSerializer` + `[BsonDateTimeOptions(Kind=Utc)]`；单测断言 round-trip `.Kind==Utc` |
| **`BsonValue.ToJson()` 是扩展 JSON（NumberLong/ISODate）** | Newtonsoft 解析必炸（实测 NumberLong 被误读为 NaN）；含大整数的审计 `before/after/metadata` 该条永久不可查且端点 500 | 读取侧一律走 `BsonJson.ToJToken/ToJsonString` 显式映射，**禁止 `JToken.Parse(bson.ToJson())`**（§14.1，含 Int64 往返用例） |
| **BSON `Date` 只有毫秒精度** | 亚毫秒 tick 会被截断（JSON 保留完整 tick，MySQL `DATETIME(6)` 为微秒） | 时间字段比较/断言统一按毫秒截断；`updatedUtc`/`ts` 不受实际影响（业务粒度远大于毫秒），但单测不要拿未截断的 `DateTime.UtcNow` 做精确相等 |
| **`BsonDocument` 索引器不接受 null 字符串** | 写入含可空字段（`npcId`/`playerId`/`actor`/`action`）时抛 `ArgumentNullException: value` | 置空字段统一用 `BsonNull.Value`（或跳过该字段），不能直接赋 `null`；P1 实测已踩 |
| **按日查询的范围必须与 ts 的存储时区一致** | 用本地日界/`ToUniversalTime` 顺序写错会漏查当天记录 | 对齐 MySQL 版语义：`date` 视作本地日历日，起始/结束都走 `.Date.ToUniversalTime()`，再与 UTC 存储的 `ts` 比较 |
| **同一毫秒的 ts 排序不稳定** | 按日列表相邻条目顺序可能波动（UPSERT/断言易误判） | 排序无法保证相等值的相对顺序；查询对同毫秒多条不要依赖顺序，如需要再补次级排序键 |
| Core DTO 为 Newtonsoft 字段命名 | Session payload 反序列化错位 | payload 走 JSON↔BsonDocument 桥接（§5.2） |
| `_id` 分隔符冲突 | 键碰撞 | 业务 id 仅 `[A-Za-z0-9_.:-]`，不含 `\|`，安全 |
| TTL 索引不可原地改 / 删除有延迟 | 过期策略失效或短暂可见旧数据 | `MongoInitializer` 记录秒数（`_meta.indexes`）并在变更时重建；查询侧仍按时间过滤 |
| **`ts` 写成字符串 → TTL 静默失效** | chat/audit 永不清理，集合无限增长 | 写入时把 `ts` 转 BSON `Date`（§5.3/§5.4）；查询回读转 ISO 字符串（§9.1 注） |
| **`_meta` 集合没有写入路径** | Mongo 就绪探针永远缺集合、恒 `not_ready` | `MongoInitializer` 显式创建/upsert `_meta`，初始化末尾自检 6 集合（§6） |
| **审计 id/ts 默认值下沉到 store** | 重试生成新 id，幂等失效、重复审计 | 默认值留在 `MemoryAuditService`，重试复用同一 `entry`（§8.2） |
| 接口化引入回归 | Json 路径行为变化 | 假实现 + Json 门面回归测试；分阶段小步提交 |
| **测试工程是显式 `<Compile>` 清单** | 删/改名 MySQL 文件后测试工程整体编译失败 | 同步删 5 条 `<Compile>`、为新文件补链（§3、§11、§13） |
| **Mongo 模式下就绪探针恒 503** | 连接正常却永远 `not_ready`，误判为故障 | `Validate()` 强制 `MongoAutoMigrate=true`（§4、§9.1） |
| **审计 `before/after` 用错 BSON 类型** | `JValue.CreateNull()` 走 `BsonDocument.Parse` 抛 `FormatException` | 字段类型用 `BsonValue`，null/scalar/object 三形态 round-trip 单测（§5.4、§13） |
| 误以为换 Mongo 即可多实例 | 目标达不到 | 见下 |
| 引入首个依赖数据库的测试 | 无 Mongo 时 `dotnet test` 变红 | §13 opt-in |
| **内嵌 facts 使记忆文档可能超过 16MB** | 保存失败（驱动报文档过大） | `Memory:MaxFacts` 配置无绝对上界、pinned 绕过、value 不截断（§5.1）；建议 P1 给配置项加绝对上限或保存前做大小预检 |
| **`DeleteExpired` 误删未落盘的 legacy 会话** | 活跃会话被清理 | 接口增传 `protectedPaths`（§8.1 约束 1） |
| **payload 含客户端/LLM 自定义键（`.`/`$`）** | BSON 字段名限制 → 写入报错或字段无法查询，JSON/Mongo 行为不一致 | API 边界校验/转义 `extras`/`items`/`extensions` 的键（§5.2、§13） |
| **`ChatLogService.Record` 丢了「写失败仍聚合」语义** | 存储故障打断聊天或丢失用量统计 | 门面保留 try/catch 且顺序不变（§8.2） |
| **`Snapshot.note` 硬编码 JSON 路径** | Mongo 模式控制台误导 | 文案随 provider 调整（§8.2） |
| **静态门面互测污染** | 假实现注入后泄漏到后续用例，出现顺序依赖的偶发失败 | 用例在 `finally`/fixture `Dispose` 还原为 JSON 实现（§13） |
| **`ChatLogService.Configure` 被整体删除** | 写失败时不再记 RuntimeLog（可观测性回退） | 保留该调用，只留 RuntimeLogs 职责（§9） |
| 控制台只改源码未见效 | 页面仍显示旧界面 | `wwwroot/app/**` 是构建产物，必须 `npm run build` 并提交（§9.2） |

### 架构澄清：换 Mongo 不会带来横向扩展

若要扩展动机，须先明确：**光换数据库达不到**。仍有三处进程内状态挡在前面——
`SessionStore.Map` 是单进程字典、`MemorySummaryQueue` 是进程内 `Channel`、
`npc/world/policy/system-settings` **仍是本地文件**。多实例需要另做：配置共享（或配置中心）、
摘要任务改多实例可抢占的分布式队列、Session 缓存失效/一致性策略。**这些都不在本方案内**，
是另一个独立项目。本方案的定位是「替换存储 + 简化写入」，不承诺扩展性。

---

## 16. 回退

- 保留 JSON 后端与 `.last-storage-mode` 提醒横幅；回退只需改 `AIBOT_STORAGE_PROVIDER`。
- Mongo 数据在独立卷 `ai_npc_mongo_data`；MySQL 已彻底下线，回退目标只有 JSON。
- 因 Q2 明确 MySQL 数据可弃，回退到 MySQL 不在支持范围。
