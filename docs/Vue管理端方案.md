# Vue 管理端详细方案

> 主方案（`AI-NPC-Agent-实施方案.md` §7.5）的展开。对应里程碑 M5，启动条件：**存在不碰 Unity 的人设维护者，或上线运营需求**；单人开发可无限期后置（M4 的单文件测试页先顶上）。
>
> 文档版本：v1.0（2026-08-23）

---

## 1. 定位与原则

- **内部工具，不是产品**：服务两类人——策划（维护人设/剧情）、开发（调试排障）。不做账号体系、不做花哨视觉。
- **编辑能力优先于聊天能力**：M4 测试页已能聊天，Vue 第一步补的是"编辑"这个唯一没有替代物的功能。
- **schema 稳定前不动工**：M1~M3 期间 `AgentConfigDto` 会持续变动，编辑器必须等它沉淀（M3 验收后）。
- **与 C# DTO 一一对应**：TS 接口手工同步自 `AgentConfigDto`，字段名/结构完全一致，JSON 直接透传，不做转换层。

## 2. 技术栈

| 项 | 选择 | 说明 |
|---|---|---|
| 框架 | Vue 3 + Composition API（`<script setup>`）+ TypeScript | |
| 构建 | Vite | dev 代理 `/api` → `localhost:5000` |
| 路由 | Vue Router 4 | 见 §3 |
| 状态 | Pinia | 见 §5 |
| 组件库 | Element Plus | 表单/表格/弹窗/抽屉/标签，后台标配 |
| SSE | fetch + ReadableStream 自封装（~60 行） | 不用 EventSource（不支持 POST/自定义头）；解析逻辑与 Unity/Server 的 SseLineParser 同构 |
| 不用 | 富文本编辑器、状态机库、UI 动画库 | textarea + Element 够用，内部工具不加复杂度 |

## 3. 信息架构（路由与页面）

```
/                  NpcList          NPC 列表（默认页）
/npc/:id/edit      NpcEditor        编辑（核心页面）
/npc/:id/chat      ChatPlayground   测试对话
/npc/:id/prompt    PromptPreview    Prompt 预览        (5c 后置)
/logs              LogsView         日志查询/回放      (5c 后置)
/settings          Settings         连接与偏好
```

布局：左侧窄导航（NPC / 日志 / 设置）＋ 顶部 **gameId 切换器**（切换即清空全部 store 缓存）＋ 内容区。

### 3.1 NpcList（列表页）

- 表格/卡片：displayName、npcId、模型、剧情块数、工具数、最近编辑时间
- 操作：新建（空白 / **复制现有** / 从模板）、编辑、去测试、删除（二次确认）
- 顶部：搜索框、gameId 切换

### 3.2 NpcEditor（编辑页，最重要）

分 Tab 的分组表单，右侧常驻**迷你 Prompt 预览**（调 preview-prompt 接口实时渲染）：

| Tab | 内容与控件 |
|---|---|
| 基本 | npcId（创建后只读，格式校验 `[a-z0-9_]+`）、displayName |
| 人设 | persona / backstory 大 textarea + 字数提示（这是策划写文案的主战场） |
| 剧情知识 | `LoreBlockEditor`：可排序卡片列表，每块 = title + content + unlockStage(数字) + isSecret(开关) + 启用开关；支持"整段粘贴按空行拆分"快速录入 |
| 工具 | `GET /api/tools` 复选列表，显示 id + 给模型看的描述 |
| 模型 | provider 预设下拉（DeepSeek / GLM / 自定义）联动填充 baseUrl 与 model 下拉；temperature 滑条、maxTokens、超时 |
| 记忆 | shortTermTurns、summaryThreshold、summaryModel（内嵌同一份 `ModelSettingsForm`） |
| 输出 | 情绪枚举、动作枚举：`EnumTagEditor`（tag 增删），提示"需与游戏 Animator 参数对齐" |
| 兜底 | fallbackReplies 列表编辑 |

行为：保存（PUT，写回 JSON）；"保存并测试"→ 跳 ChatPlayground；**localStorage 自动暂存草稿**（防浏览器崩溃丢稿）；脏状态离开提示；summaryModel 为空时提示"将使用主模型"。

### 3.3 ChatPlayground（测试页）

三栏：

```
┌──────────┬───────────────────────────┬─────────────┐
│ NPC 信息   │  消息流                     │ 本次请求信息  │
│ 模拟状态:  │  [玩家] 右 / [NPC] 左        │ 耗时         │
│  好感度 ▁▁▁ │  流式打字中… ▌              │ token usage  │
│  剧情阶段 3 │  情绪标签 😠 angry           │ (in/out)     │
│  背包 tags │  工具卡片 give_item(铁矿×3)  │ 模型/温度覆盖  │
│           │                           │  (仅本会话)   │
│ [重置会话]  │  [输入框……        ] [停止][发送] │             │
│ [清空记忆]  │                           │             │
└──────────┴───────────────────────────┴─────────────┘
```

- **模拟状态**（左栏）即 `IGameContext` 替身：好感度滑条、剧情阶段数字、背包/自定义 KV——随每条消息发给 Server
- 工具卡片：模型调了什么工具、参数 JSON、SimulatedToolHost 的模拟结果
- 停止 = AbortController 中断 fetch
- 「模型/温度临时覆盖」不写回配置，只影响本会话——对比不同模型说话风格的关键功能
- 重置会话（新 sessionId）/ 清空记忆（连摘要一起删）

### 3.4 PromptPreview（后置）

选 NPC + 模拟状态 → `POST preview-prompt` → `PromptLayerView` 分层着色（世界观/身份/剧情/状态/记忆/规则/输出格式各一色）＋ 每层 token 估算 ＋ 总量 vs 预算条。

### 3.5 LogsView（后置）

过滤（NPC / 日期 / 关键词）→ 表格（时间、npc、会话、耗时、token）→ 点行开抽屉：完整 messages JSON 查看 + **"重放"按钮**（用当时的输入在当前配置下再跑一遍，对比配置改动的影响）。

### 3.6 Settings

Server 地址、管理 token、默认 gameId；全部存 localStorage。

## 4. SSE 封装（`src/api/sse.ts`）

事件载荷字段契约见主方案**附录B**（单行 `data:` JSON，`type` 字段区分事件），下方 `SseCallbacks` 与之逐字段对应：

```ts
export interface SseCallbacks {
  token?: (t: string) => void;        // {type:'token', delta}
  tool_call?: (c: ToolCallInfo) => void;
  reply?: (r: StructuredReply) => void;
  error?: (msg: string) => void;
}

export async function streamChat(
  gid: string, body: ChatRequestBody, on: SseCallbacks, signal: AbortSignal
): Promise<void> {
  const res = await fetch(`${API_BASE}/api/games/${gid}/chat/stream`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token()}` },
    body: JSON.stringify(body), signal,
  });
  if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`);

  const reader = res.body.pipeThrough(new TextDecoderStream()).getReader();
  let buf = '';
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buf += value;
    const lines = buf.split('\n');
    buf = lines.pop() ?? '';                    // 残行回缓冲（半行处理，同 Unity 侧）
    for (const line of lines) {
      if (!line.startsWith('data:')) continue;
      const payload = line.slice(5).trim();
      if (payload === '[DONE]') return;
      const ev = JSON.parse(payload);
      on[ev.type]?.(ev);
    }
  }
}
```

管理工具不做自动重连：出错显示错误条 + 手动重试按钮即可。

## 5. 状态与 API 层

**Pinia stores**：

| store | 职责 |
|---|---|
| `useAppStore` | Server 地址 / token / 当前 gameId；切换 gameId 时调用全局缓存清理 |
| `useNpcListStore` | 列表缓存、新建/复制/删除操作 |
| `useNpcEditStore` | 当前编辑态、脏标记、草稿暂存/恢复 |
| `useChatStore` | 消息流、流式状态、模拟状态、会话 id、临时模型覆盖 |

**API 层**（`src/api/`）：`http.ts`（fetch 封装：baseURL、Bearer、统一 ElMessage 错误）、`sse.ts`（上节）、`npc.ts` / `logs.ts`（类型化端点函数）。

**类型定义**（`src/types/dto.ts`，与 C# 逐字段同步）：

```ts
export interface AgentConfigDto {
  npcId: string; displayName: string;
  persona: string; backstory: string;
  worldId: string;
  loreBlocks: LoreBlock[];
  enabledToolIds: string[];
  fallbackReplies: string[];
  model: ModelSettings;
  memory: MemorySettings;
  output: OutputSettings;
  configVersion: number;
}
export interface LoreBlock { title: string; content: string; unlockStage: number; isSecret: boolean; enabled: boolean; }
```

## 6. 可复用组件

| 组件 | 用途 |
|---|---|
| `LoreBlockEditor` | 剧情块列表编辑（Editor 主力控件） |
| `ModelSettingsForm` | 模型参数表单（主模型与 summaryModel 复用） |
| `EnumTagEditor` | 枚举 tag 增删（情绪/动作） |
| `SimStatePanel` | 模拟状态编辑（好感度/阶段/背包） |
| `MessageBubble` / `ToolCallCard` / `EmotionTag` | 聊天流展示 |
| `PromptLayerView` | 分层着色渲染（Preview 页与 Editor 迷你预览共用） |

## 7. 工程与部署

```
src/AIBot.Web/
├── src/
│   ├── api/ (http.ts, sse.ts, npc.ts, logs.ts)
│   ├── types/ (dto.ts)          # 与 C# AgentConfigDto 同步
│   ├── stores/ (app, npcList, npcEdit, chat)
│   ├── components/ (§6 六个可复用组件)
│   ├── views/ (六个页面)
│   ├── router/index.ts
│   └── App.vue
└── vite.config.ts               # dev: /api → localhost:5000
```

- **部署形态（最简，推荐）**：`npm run build` 产物拷入 Server `wwwroot/app/`——与测试页同源，免 CORS、免独立部署
- 备选：独立 nginx 静态托管 + Server 开 CORS 白名单
- ESLint + Prettier 标配；环境变量仅 `VITE_API_BASE`

## 8. 开发分期（M5 内部）

| 期 | 内容 | 工期 |
|---|---|---|
| **5a 编辑闭环** | 脚手架、SSE/http 封装、Settings、NpcList、**NpcEditor 全部 Tab**、草稿暂存、保存流程 | 3~4 天 |
| **5b 测试升级** | ChatPlayground（模拟状态、流式、工具卡片、停止/重置、临时模型覆盖） | 2~3 天 |
| **5c 按需后置** | PromptPreview（独立页）、LogsView（含重放） | 2~3 天 |

顺序依据：编辑器没有任何替代物，先做；测试台在 M4 测试页已有能用的版本；Preview 迷你版已内嵌在 Editor，独立页非急需。

## 9. 防坑清单

1. **DTO 漂移**：C# 改字段必须同步 `dto.ts`——建议在 M3 后写个脚本从 `data/npcs/*.json` 示例生成/校验 TS 类型
2. **表单丢稿**：所有长文本编辑挂 localStorage 自动暂存，保存成功才清除
3. **gameId 切换残留**：切换时清空所有 store 与路由参数里的旧 NPC
4. **SSE 半行**：残行必须回缓冲（§4 已处理），常见 bug 来源
5. **枚举与游戏对齐**：情绪/动作枚举改了要提醒同步 Animator 参数——在 EnumTagEditor 旁放常驻提示文案
6. **不要提前做**：schema 未稳定（M3 前）动工 = 反复重写
