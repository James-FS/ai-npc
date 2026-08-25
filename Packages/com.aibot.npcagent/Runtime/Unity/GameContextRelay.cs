using System;
using AIBot.Core.Context;

namespace AIBot.Unity
{
    /// <summary>
    /// 游戏状态桥：最简实现用序列化字段；需要动态状态的游戏自建类实现 IGameContext。
    /// snapshotJson 为空时自动从 stage/favorability 生成。
    /// </summary>
    public sealed class GameContextRelay : IGameContext
    {
        public int stage;
        public int favorability;
        [UnityEngine.TextArea(2, 6)] public string snapshotJsonOverride;

        public int CurrentStage { get { return stage; } }

        public string SnapshotJson
        {
            get
            {
                if (!string.IsNullOrEmpty(snapshotJsonOverride)) return snapshotJsonOverride;
                var sim = new SimGameState { stage = stage, favorability = favorability };
                return sim.ToSnapshotJson();
            }
        }
    }
}
