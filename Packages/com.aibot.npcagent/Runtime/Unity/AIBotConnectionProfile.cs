using UnityEngine;

namespace AIBot.Unity
{
    /// <summary>
    /// Server 模式的轻量连接配置。
    /// NPC 人设、模型、记忆策略和工具由 AIBot.Server 管理，Unity 端只保存连接与会话标识。
    /// </summary>
    [CreateAssetMenu(menuName = "AI NPC/Server Connection Profile", fileName = "AIBotServerConnection")]
    public sealed class AIBotConnectionProfile : ScriptableObject
    {
        [Header("Server")]
        public string serverBaseUrl = "http://127.0.0.1:5000";
        public string gameId = "default";
        public string npcId = "blacksmith_wang";

        [Header("会话")]
        [Tooltip("可选：填写后启用玩家范围长期记忆。")]
        public string playerId;
        public string sessionId = "s-unity";

        [Header("连接")]
        public int timeoutMs = 20000;
        [Tooltip("Server 聊天客户端令牌；与服务端 AIBOT_CLIENT_TOKEN 一致。请通过安全配置注入，不要提交到公共仓库。")]
        public string serverAuthToken;

        [Header("工具（仅调试）")]
        [Tooltip("显式启用 Server 的 SimulatedToolHost。它只修改会话模拟状态，不能修改正式游戏背包、任务或好感度。")]
        public bool enableSimulatedTools;

        private void OnValidate()
        {
            timeoutMs = Mathf.Max(1000, timeoutMs);
        }
    }
}
