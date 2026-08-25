# AI NPC Agent

可插拔的游戏 NPC 智能Agent平台：纯 C# 核心 + Unity 包 + 独立 Server + Web 管理台，接入 OpenAI 兼容 API（OpenCode Zen / DeepSeek / GLM）。

- 方案文档：[AI-NPC-Agent-实施方案.md](./AI-NPC-Agent-实施方案.md)（v1.4，含数据契约与附录A/B）
- Vue 管理端设计：[docs/Vue管理端方案.md](./docs/Vue管理端方案.md)
- 当前进度：M1 完成 + M2/M4 大部分提前实现（详见主方案 §9 与 v1.4 变更记录）

## 目录

```
Packages/com.aibot.npcagent   Unity 包（Runtime/Core = 三端共享的 AIBot.Core 源码）
src/AIBot.Server              ASP.NET Core 独立宿主 + 管理台（wwwroot/index.html）
src/AIBot.Tests               xUnit 测试（33 项，与 Unity EditMode 共享用例）
data/games/{gameId}           NPC 配置/世界观/会话/日志（JSON，唯一真源）
```

## 快速开始（脱离 Unity 独立运行）

管理台（`http://localhost:5000`）六个标签页：**对话**（流式输出+思考过程折叠+停止按钮）、**NPC 编辑**（人设/剧情块/模型参数，保存即生效，空 apiKey 不覆盖已有 key）、**世界观**、**Prompt 预览**（七层着色+token 估算）、**会话与记忆**（查看/清空，重启服务不丢记忆）、**用量统计**。左侧栏支持基于模板新建/删除 NPC、模拟游戏状态（剧情阶段/好感度）、临时模型覆盖。

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
