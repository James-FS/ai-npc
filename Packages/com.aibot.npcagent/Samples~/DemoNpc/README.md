# DemoNpc 示例

示例 NPC：铁匠老王。Demo 菜单会在 Unity 工程的 `Assets/AIBotDemo/` 下创建 Local 配置 Asset、世界观和场景，不依赖仓库 `data/` 目录。

## 使用步骤

1. Unity 游戏工程的 `Packages/manifest.json` 添加：
   `"com.aibot.npcagent": "file:D:/Code/aibot/Packages/com.aibot.npcagent"`
2. 菜单 **AIBot → Demo → Create Demo Scene**，一键生成 NPC、Local 配置与对话 UI
3. 在 `Assets/AIBotDemo/BlacksmithConfig.asset` 填入模型 API Key，并确认模型地址和模型 ID（仅开发机本地使用，勿提交含 Key 的 Asset）
4. Play，在输入框里与老王对话

切换到 Server 模式、迁移旧 JSON 和恢复工具执行的方法见[插件接入说明](../../README.md)。

## 注册真实游戏工具

```csharp
public class GiveItemTool : IAgentTool
{
    public string Id => "give_item";
    public string Description => "送给玩家一件道具";
    public string ParametersSchema => "{\"type\":\"object\",\"properties\":{\"item_id\":{\"type\":\"string\"},\"count\":{\"type\":\"integer\"}},\"required\":[\"item_id\"]}";

    public Task<ToolResult> ExecuteAsync(string argsJson, object hostContext)
    {
        var args = JsonConvert.DeserializeObject<Dictionary<string, string>>(argsJson);
        // hostContext 是 NpcAgent.gameObject —— 在这里真正给玩家背包加道具
        return Task.FromResult(ToolResult.Ok("已给玩家 " + args["item_id"]));
    }
}

// 游戏启动时：
npcAgent.Tools.Register(new GiveItemTool());
```
