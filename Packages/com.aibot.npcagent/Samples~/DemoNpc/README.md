# DemoNpc 示例

M1 示例 NPC：铁匠老王（配置在 monorepo `data/games/default/npcs/blacksmith_wang.json`）。

## 使用步骤

1. Unity 游戏工程的 `Packages/manifest.json` 添加：
   `"com.aibot.npcagent": "file:D:/Code/aibot/Packages/com.aibot.npcagent"`
2. 在 NPC 配置 JSON 的 `model.apiKey` 填入 DeepSeek/GLM 的 key（仅开发机本地，勿提交）
3. 菜单 **AIBot → Demo → Create Demo Scene**，一键生成 NPC 与对话 UI
4. Play，在输入框里与老王对话

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
