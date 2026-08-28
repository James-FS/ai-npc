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

        private void OnValidate()
        {
            timeoutMs = Mathf.Max(1000, timeoutMs);
        }
    }
}
