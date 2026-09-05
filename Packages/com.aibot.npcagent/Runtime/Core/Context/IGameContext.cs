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
            if (items == null) items = new Dictionary<string, int>();
            int count;
            return items.TryGetValue(itemId, out count) ? count : 0;
        }

        public string ToSnapshotJson()
        {
            if (extras == null) extras = new Dictionary<string, string>();
            if (items == null) items = new Dictionary<string, int>();
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

    /// <summary>
    /// game 模式的组合状态：SimState（stage 门控等已知字段）+ 客户端上报的原始游戏快照。
    /// 原文完整进入"当前状况"层，不会被 SimState 的固定字段结构吞掉。
    /// </summary>
    public sealed class CompositeGameContext : IGameContext
    {
        private readonly SimGameState _state;
        private readonly string _gameContextJson;
        public CompositeGameContext(SimGameState state, string gameContextJson)
        {
            _state = state ?? new SimGameState();
            _gameContextJson = string.IsNullOrWhiteSpace(gameContextJson) ? null : gameContextJson.Trim();
        }
        public int CurrentStage { get { return _state.stage; } }
        public string SnapshotJson
        {
            get
            {
                string baseSnapshot = _state.ToSnapshotJson();
                return _gameContextJson == null
                    ? baseSnapshot
                    : baseSnapshot + "\n【游戏上报状态】\n" + _gameContextJson;
            }
        }
    }
}
