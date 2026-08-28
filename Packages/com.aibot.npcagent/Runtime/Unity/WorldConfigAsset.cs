using System.Collections.Generic;
using AIBot.Core.Config;
using UnityEngine;

namespace AIBot.Unity
{
    /// <summary>
    /// Local 模式可选的世界观资源。使用它时不需要把 data/ 目录复制进 Unity 工程。
    /// </summary>
    [CreateAssetMenu(menuName = "AI NPC/World Config", fileName = "NewWorldConfig")]
    public sealed class WorldConfigAsset : ScriptableObject
    {
        public string worldId = "default";
        [TextArea(3, 12)] public string description;
        [TextArea(2, 6)] public List<string> extraRules = new List<string>();

        private void OnValidate()
        {
            extraRules = extraRules ?? new List<string>();
        }

        public WorldConfigDto ToDto()
        {
            return new WorldConfigDto
            {
                worldId = worldId,
                description = description ?? string.Empty,
                extraRules = extraRules == null
                    ? new List<string>() : new List<string>(extraRules)
            };
        }
    }
}
