# AI NPC Agent Unity Package

面向 Unity 2022.3 的 NPC 对话插件，提供 Local 和 Server 两种运行模式。

## 安装

在 Unity 游戏工程的 `Packages/manifest.json` 中加入本地包路径：

```json
{
  "dependencies": {
    "com.aibot.npcagent": "file:D:/Code/aibot/Packages/com.aibot.npcagent"
  }
}
```

也可以把本仓库复制到其他位置，再将路径改为实际目录。Unity 需要能够访问该目录。

## 快速创建 Demo

1. 等待 Unity 完成 Package 编译。
2. 在菜单中选择 **AIBot → Demo → Create Demo Scene**。
3. 场景会自动生成 NPC、对话气泡、输入框和发送按钮。
4. 点击 Play，在输入框中发送消息。

Demo 创建逻辑位于 `Editor/DemoSceneBuilder.cs`，不需要 `Samples~` 目录或额外示例资源。

## 选择运行模式

### Local 模式

Unity 直接调用 OpenAI 兼容模型，适合单机 Demo 和原型。

1. 创建 **AI NPC → Agent Config**（`AgentConfigAsset`）。
2. 填写模型地址、模型名称和开发期 API Key。
3. 将资源拖到 `NpcAgent.agentConfigAsset`。
4. 可选设置 `WorldConfigAsset` 和 `NpcAgent.worldConfigAsset`。

Local 模式不需要启动 AIBot.Server，但 API Key 会进入客户端运行环境，只建议用于开发或可信场景。

### Server 模式

Unity 通过 HTTP/SSE 调用独立的 AIBot.Server，适合集中管理 NPC、长期记忆、日志和多个客户端。

1. 先启动 AIBot.Server。
2. 创建 **AI NPC → Server Connection Profile**（`AIBotConnectionProfile`）。
3. 填写 Server 地址、`gameId`、`npcId`，可选填写 `playerId` 和 `sessionId`。
4. 将 Profile 拖到 `NpcAgent.connectionProfile`。
5. 可调用 `await agent.CheckServerAsync()` 检查服务状态。

正式部署时，在 Server 设置环境变量 `AIBOT_CLIENT_TOKEN`，并在 Connection Profile 的 `serverAuthToken` 填写同一令牌。令牌只用于游戏客户端调用聊天 API，不要提交到公共仓库；本机开发可留空，但 Server 会输出安全警告。

Server 模式不需要在 Unity 中保存模型 API Key。服务端启动方式和配置请参考仓库根目录 README。

## 代码调用

```csharp
using UnityEngine;

public class NpcDemo : MonoBehaviour
{
    [SerializeField] private AIBot.Unity.NpcAgent agent;

    public async void SendMessageToNpc(string message)
    {
        await agent.ChatAsync(message);
    }
}
```

常用事件包括：

- `onToken`：流式文本片段；
- `onReply`：完整回复；
- `onFallback`：模型调用失败但插件交付了兜底回复时的诊断信息（仍会触发 `onReply`）；
- `onCancelled`：当前请求被取消时触发，适合复位 UI；
- `onToolExecuted`：工具执行结果；
- `onServerStatus`：Server 连接状态。

Server 模式每轮请求使用幂等 `requestId`。断线时插件会自动进行有限次数恢复；需要手动恢复时，可使用 `agent.LastServerRequestId` 和 `RetryServerRequestAsync`。Server 的 JSON 存储模式适合单实例运行；维护任务会按 `Sessions:MemoryIdleHours` 清理长期不活跃的 Session 文件。正式环境若需要多实例，应切换到 MySQL 等共享存储。

Local 模式若从仓库 `data/` 目录加载配置，可在游戏启动阶段调用
`DevConfigStore.SetDataRoot(path)`，进行目录存在性校验后再创建 NPC；不要在运行中依赖绝对开发机路径。

## 注册游戏工具

Local 模式可直接在 Unity 注册工具，将 NPC 的工具调用连接到背包、任务或其他游戏系统。

Server 模式有三种工具形态：

- `none`（默认）：Server 侧不执行任何工具；
- `simulated`（仅调试）：Server 的 SimulatedToolHost 只写会话模拟状态；
- `game`（工具回传）：在 Connection Profile 勾选 **Enable Game Tools**，Server 遇到模型工具调用时挂起对话并下发 `tool_pending` 事件，Unity 用本地注册的工具真实执行后自动续跑。要求：NPC 配置的 `enabledToolIds` 包含对应工具、NpcAgent 已注册本地工具；同一挂起轮只会执行一次（断线重放安全），工具本身仍应保持幂等。

```csharp
using System.Threading.Tasks;
using AIBot.Core.Tools;

public class GiveItemTool : IAgentTool
{
    public string Id => "give_item";
    public string Description => "送给玩家一件道具";
    public string ParametersSchema =>
        "{\"type\":\"object\",\"properties\":{\"item_id\":{\"type\":\"string\"}},\"required\":[\"item_id\"]}";

    public Task<ToolResult> ExecuteAsync(string argsJson, object hostContext)
    {
        // 在这里调用正式游戏背包系统。
        return Task.FromResult(ToolResult.Ok("道具已发放"));
    }
}
```

注册到 NPC：

```csharp
agent.Tools.Register(new GiveItemTool());
```

## 安全注意事项

- 不要把真实 API Key 写入公共仓库。
- 不要把含 Key 的 `AgentConfigAsset` 提交到 Git。
- Server 模式优先将模型 Key 放在服务端环境变量 `AIBOT_LLM_KEY`。
- `playerId` 和 `sessionId` 应由游戏稳定生成并持久化，避免会话串线。

## 常见问题

**Demo 菜单不存在**：检查 Unity 是否完成 Package 编译，并确认 `Editor` 文件夹没有编译错误。

**Server 无法连接**：确认 Server 已启动，地址包含正确端口，并访问 `/api/ready` 检查就绪状态。

**TMP 编译错误**：确认项目已安装 `com.unity.textmeshpro` 3.0.6 或兼容版本。
