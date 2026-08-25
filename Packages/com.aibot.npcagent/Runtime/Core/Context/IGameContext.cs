using System.Collections.Generic;
using Newtonsoft.Json;

namespace AIBot.Core.Context
{
    /// <summary>状态桥：游戏宿主提供真实实现，测试台用 SimGameContext。</summary>
    public interface IGameContext
    {
        int CurrentStage { get; }
        string SnapshotJson { get; }
    }

    /// <summary>测试台/预览用的模拟状态替身（服务端来自请求体的 simState，会话内有状态）。</summary>
    public class SimGameState
    {
        public int stage;
        public int favorability;
        public Dictionary<string, string> extras = new Dictionary<string, string>();
        public Dictionary<string, int> items = new Dictionary<string, int>();   // give_item 工具累积

        public int GetItemCount(string itemId)
        {
            int count;
            return items.TryGetValue(itemId, out count) ? count : 0;
        }

        public string ToSnapshotJson()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }
    }

    public sealed class SimGameContext : IGameContext
    {
        private readonly SimGameState _state;
        public SimGameContext(SimGameState state) { _state = state ?? new SimGameState(); }
        public int CurrentStage { get { return _state.stage; } }
        public string SnapshotJson { get { return _state.ToSnapshotJson(); } }
    }
}
