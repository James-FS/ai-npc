# AI NPC Agent

可插拔的游戏 NPC 智能Agent平台：纯 C# 核心 + Unity 包 + 独立 Server + Web 管理台，接入 OpenAI 兼容 API（OpenCode Zen / DeepSeek / GLM）。

- 方案文档：[AI-NPC-Agent-实施方案.md](./AI-NPC-Agent-实施方案.md)（v2.5，含数据契约、四阶段记忆管理、统一 Vue 调试工作台和附录A/B）
- 记忆管理与 Vue 控制台设计：[docs/记忆管理与Vue控制台设计方案.md](./docs/记忆管理与Vue控制台设计方案.md)
- 当前进度：M1、M4、M5 已完成，M2 基本完成（详见主方案 §9）

## 目录

```
Packages/com.aibot.npcagent   Unity 包（Runtime/Core = 三端共享的 AIBot.Core 源码）
src/AIBot.Server              ASP.NET Core 独立宿主 + 静态托管（根入口跳转 wwwroot/app）
src/AIBot.Web                 Vue 3 + TypeScript 统一管理/调试控制台
src/AIBot.Tests               xUnit 测试（68 项，Core/记忆仓储与审计免网全链路）
data/games/{gameId}           NPC 配置/世界观/玩家会话/长期记忆/日志（JSON，唯一真源）
```

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

## Unity 接入

游戏工程 `Packages/manifest.json` 添加
`"com.aibot.npcagent": "file:D:/Code/aibot/Packages/com.aibot.npcagent"`，
菜单 **AIBot → Demo → Create Demo Scene** 一键生成示例场景。

API key 永不入库（.gitignore 已隔离含 key 的真实配置；模板见 `new_npc.template.json`）。
