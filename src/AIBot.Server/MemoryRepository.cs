using System;
using System.Collections.Concurrent;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Memory;
using Newtonsoft.Json;

namespace AIBot.Server
{
    public sealed class MemoryListItem
    {
        public string gameId;
        public string npcId;
        public string playerId;
        public int memoryVersion;
        public int factCount;
        public bool hasSummary;
        public DateTime? lastSummarizedUtc;
        public DateTime updatedUtc;
    }

    public sealed class MemoryListPage
    {
        public int total;
        public int limit;
        public int offset;
        public List<MemoryListItem> items = new List<MemoryListItem>();
    }

    public interface IMemoryRepository
    {
        Task<PlayerLongTermMemory> LoadPlayerMemoryAsync(string gameId, string npcId,
            string playerId, CancellationToken ct);
        Task<PlayerLongTermMemory> SavePlayerMemoryAsync(PlayerLongTermMemory memory,
            int expectedVersion, CancellationToken ct);
        Task<MemoryListPage> ListPlayerMemoriesAsync(string gameId, string npcId,
            string playerId, int limit, int offset, CancellationToken ct);
        Task DeletePlayerMemoryAsync(string gameId, string npcId, string playerId,
            int? expectedVersion, CancellationToken ct);
    }

    public sealed class MemoryVersionConflictException : Exception
    {
        public int ExpectedVersion { get; }
        public int ActualVersion { get; }

        public MemoryVersionConflictException(int expectedVersion, int actualVersion)
            : base("memory version conflict: expected=" + expectedVersion + ", actual=" + actualVersion)
        {
            ExpectedVersion = expectedVersion;
            ActualVersion = actualVersion;
        }
    }

    /// <summary>单机 JSON 仓储：逐文件互斥、乐观版本检查、临时文件原子替换。</summary>
    public sealed class JsonMemoryRepository : IMemoryRepository
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string> _dataRoot;

        public JsonMemoryRepository() : this(DataStore.FindDataRoot) { }

        public JsonMemoryRepository(Func<string> dataRoot)
        {
            _dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
        }

        public async Task<PlayerLongTermMemory> LoadPlayerMemoryAsync(string gameId, string npcId,
            string playerId, CancellationToken ct)
        {
            string path = ResolvePath(gameId, npcId, playerId);
            SemaphoreSlim gate = FileGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (!File.Exists(path)) return NewMemory(gameId, npcId, playerId);
                string json = await File.ReadAllTextAsync(path, ct);
                PlayerLongTermMemory memory = JsonConvert.DeserializeObject<PlayerLongTermMemory>(json)
                    ?? NewMemory(gameId, npcId, playerId);
                memory.gameId = gameId;
                memory.npcId = npcId;
                memory.playerId = playerId;
                memory.facts = memory.facts ?? new System.Collections.Generic.List<MemoryFact>();
                return memory;
            }
            finally { gate.Release(); }
        }

        public async Task<PlayerLongTermMemory> SavePlayerMemoryAsync(PlayerLongTermMemory memory,
            int expectedVersion, CancellationToken ct)
        {
            if (memory == null) throw new ArgumentNullException(nameof(memory));
            string path = ResolvePath(memory.gameId, memory.npcId, memory.playerId);
            SemaphoreSlim gate = FileGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                int actualVersion = 0;
                if (File.Exists(path))
                {
                    PlayerLongTermMemory current = JsonConvert.DeserializeObject<PlayerLongTermMemory>(
                        await File.ReadAllTextAsync(path, ct));
                    actualVersion = current?.memoryVersion ?? 0;
                }
                if (actualVersion != expectedVersion)
                    throw new MemoryVersionConflictException(expectedVersion, actualVersion);

                PlayerLongTermMemory saved = Clone(memory);
                saved.schemaVersion = 2;
                saved.memoryVersion = actualVersion + 1;
                saved.facts = saved.facts ?? new System.Collections.Generic.List<MemoryFact>();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    await File.WriteAllTextAsync(temp,
                        JsonConvert.SerializeObject(saved, Formatting.Indented), ct);
                    File.Move(temp, path, true);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                return Clone(saved);
            }
            finally { gate.Release(); }
        }

        public async Task<MemoryListPage> ListPlayerMemoriesAsync(string gameId, string npcId,
            string playerId, int limit, int offset, CancellationToken ct)
        {
            if (!DataStore.IsValidId(gameId)) throw new ArgumentException("invalid game id");
            if (npcId != null && !DataStore.IsValidId(npcId)) throw new ArgumentException("invalid npc id");
            if (playerId != null && !DataStore.IsValidPlayerId(playerId)) throw new ArgumentException("invalid player id");
            string root = _dataRoot();
            string memoryRoot = root == null ? null : Path.Combine(root, "games", gameId, "memories");
            var items = new List<MemoryListItem>();
            if (memoryRoot != null && Directory.Exists(memoryRoot))
            {
                foreach (string path in Directory.GetFiles(memoryRoot, "*.json", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        PlayerLongTermMemory memory = JsonConvert.DeserializeObject<PlayerLongTermMemory>(
                            await File.ReadAllTextAsync(path, ct));
                        if (memory == null || !DataStore.IsValidId(memory.npcId)
                            || !DataStore.IsValidPlayerId(memory.playerId)) continue;
                        if (npcId != null && memory.npcId != npcId) continue;
                        if (playerId != null && memory.playerId != playerId) continue;
                        DateTime updated = File.GetLastWriteTimeUtc(path);
                        if (memory.lastSummarizedUtc.HasValue && memory.lastSummarizedUtc.Value > updated)
                            updated = memory.lastSummarizedUtc.Value;
                        DateTime? factUpdated = (memory.facts ?? new List<MemoryFact>())
                            .Where(f => f != null && f.updatedUtc != default(DateTime))
                            .Select(f => (DateTime?)f.updatedUtc).OrderByDescending(x => x).FirstOrDefault();
                        if (factUpdated.HasValue && factUpdated.Value > updated) updated = factUpdated.Value;
                        items.Add(new MemoryListItem
                        {
                            gameId = gameId,
                            npcId = memory.npcId,
                            playerId = memory.playerId,
                            memoryVersion = memory.memoryVersion,
                            factCount = memory.facts?.Count ?? 0,
                            hasSummary = !string.IsNullOrWhiteSpace(memory.summary),
                            lastSummarizedUtc = memory.lastSummarizedUtc,
                            updatedUtc = updated
                        });
                    }
                    catch (JsonException) { }
                    catch (IOException) { }
                }
            }
            items = items.OrderByDescending(i => i.updatedUtc)
                .ThenBy(i => i.npcId).ThenBy(i => i.playerId).ToList();
            int safeOffset = Math.Max(0, offset);
            int safeLimit = Math.Max(1, Math.Min(200, limit));
            return new MemoryListPage
            {
                total = items.Count,
                limit = safeLimit,
                offset = safeOffset,
                items = items.Skip(safeOffset).Take(safeLimit).ToList()
            };
        }

        public async Task DeletePlayerMemoryAsync(string gameId, string npcId, string playerId,
            int? expectedVersion, CancellationToken ct)
        {
            string path = ResolvePath(gameId, npcId, playerId);
            SemaphoreSlim gate = FileGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (!File.Exists(path)) return;
                if (expectedVersion.HasValue)
                {
                    PlayerLongTermMemory current = JsonConvert.DeserializeObject<PlayerLongTermMemory>(
                        await File.ReadAllTextAsync(path, ct));
                    int actual = current?.memoryVersion ?? 0;
                    if (actual != expectedVersion.Value)
                        throw new MemoryVersionConflictException(expectedVersion.Value, actual);
                }
                File.Delete(path);
            }
            finally { gate.Release(); }
        }

        private string ResolvePath(string gameId, string npcId, string playerId)
        {
            if (!DataStore.IsValidId(gameId) || !DataStore.IsValidId(npcId)
                || !DataStore.IsValidPlayerId(playerId))
                throw new ArgumentException("invalid memory key");
            string root = _dataRoot();
            if (root == null) throw new InvalidOperationException("data root not found");
            return Path.Combine(root, "games", gameId, "memories",
                Uri.EscapeDataString(npcId), Uri.EscapeDataString(playerId) + ".json");
        }

        private static PlayerLongTermMemory NewMemory(string gameId, string npcId, string playerId)
        {
            return new PlayerLongTermMemory
            {
                gameId = gameId,
                npcId = npcId,
                playerId = playerId,
                memoryVersion = 0
            };
        }

        private static PlayerLongTermMemory Clone(PlayerLongTermMemory source)
        {
            return JsonConvert.DeserializeObject<PlayerLongTermMemory>(JsonConvert.SerializeObject(source));
        }
    }
}
