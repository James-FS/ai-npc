using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>对话日志持久化接缝。JSON 在写入时自清理超期文件；Mongo 由 TTL 索引接管。</summary>
    public interface IChatLogStore
    {
        void Write(string gameId, ChatLogService.ChatLogEntry entry);
        JObject Query(string gameId, string date, string npcId, int limit, int offset);
    }
}
