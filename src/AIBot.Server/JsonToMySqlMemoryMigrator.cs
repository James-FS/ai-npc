using System;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Memory;

namespace AIBot.Server
{
    /// <summary>将现有 data/games/*/memories JSON 迁移到 MySQL；目标已有记录时保持幂等并跳过。</summary>
    public sealed class JsonToMySqlMemoryMigrator
    {
        private readonly IMemoryRepository _source;
        private readonly IMemoryRepository _target;

        public JsonToMySqlMemoryMigrator(IMemoryRepository source, IMemoryRepository target)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _target = target ?? throw new ArgumentNullException(nameof(target));
        }

        public async Task<MigrationResult> RunAsync(string gameId, CancellationToken ct)
        {
            int limit = 200;
            int offset = 0;
            var result = new MigrationResult();
            while (true)
            {
                MemoryListPage page = await _source.ListPlayerMemoriesAsync(gameId, null, null, limit, offset, ct);
                foreach (MemoryListItem item in page.items)
                {
                    ct.ThrowIfCancellationRequested();
                    result.Scanned++;
                    PlayerLongTermMemory existing = await _target.LoadPlayerMemoryAsync(
                        item.gameId, item.npcId, item.playerId, ct);
                    if (existing.memoryVersion > 0)
                    {
                        result.Skipped++;
                        continue;
                    }
                    PlayerLongTermMemory source = await _source.LoadPlayerMemoryAsync(
                        item.gameId, item.npcId, item.playerId, ct);
                    await _target.SavePlayerMemoryAsync(source, 0, ct);
                    result.Migrated++;
                }
                offset += page.items.Count;
                if (page.items.Count == 0 || offset >= page.total) break;
            }
            return result;
        }
    }

    public sealed class MigrationResult
    {
        public int Scanned;
        public int Migrated;
        public int Skipped;
    }
}
