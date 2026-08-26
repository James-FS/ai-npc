using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AIBot.Core.Config;
using AIBot.Core.Llm;
using AIBot.Core.Logging;
using AIBot.Core.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    public sealed class MemorySummaryJob
    {
        public string GameId;
        public string NpcId;
        public string PlayerId;
        public string SessionId;
        public bool Force;
        public string Actor;
        public long Generation;
    }

    /// <summary>有界、去重的后台摘要服务；成功写长期记忆后才确认消费待摘要消息。</summary>
    public sealed class MemorySummaryQueue : BackgroundService
    {
        private readonly Channel<MemorySummaryJob> _channel;
        private readonly ConcurrentDictionary<string, byte> _scheduled =
            new ConcurrentDictionary<string, byte>();
        private readonly ConcurrentDictionary<string, long> _playerGenerations =
            new ConcurrentDictionary<string, long>();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _playerGates =
            new ConcurrentDictionary<string, SemaphoreSlim>();
        private readonly PlayerMemoryService _memoryService;
        private readonly IConfiguration _configuration;
        private readonly MemoryAuditService _audit;
        private readonly ILogSink _log = new ConsoleLogSink();
        private long _failedJobs;

        public MemorySummaryQueue(PlayerMemoryService memoryService, IConfiguration configuration,
            MemoryAuditService audit)
        {
            _memoryService = memoryService;
            _configuration = configuration;
            _audit = audit;
            int capacity = Math.Max(16, configuration.GetValue<int?>("Memory:SummaryQueueCapacity") ?? 256);
            _channel = Channel.CreateBounded<MemorySummaryJob>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public int PendingCount { get { return _scheduled.Count; } }
        public long FailedJobs { get { return Interlocked.Read(ref _failedJobs); } }

        public bool Enqueue(string gameId, string npcId, string playerId, string sessionId)
        {
            return EnqueueInternal(gameId, npcId, playerId, sessionId, false, "system");
        }

        public bool EnqueueManual(string gameId, string npcId, string playerId, string sessionId,
            string actor)
        {
            return EnqueueInternal(gameId, npcId, playerId, sessionId, true, actor);
        }

        /// <summary>使该玩家所有已排队/执行中的旧任务失效；删除记忆时调用。</summary>
        public void InvalidatePlayer(string gameId, string npcId, string playerId)
        {
            string playerKey = PlayerKey(gameId, npcId, playerId);
            _playerGenerations.AddOrUpdate(playerKey, 1, (_, current) => current + 1);
            string scheduledPrefix = playerKey + "|";
            foreach (string key in _scheduled.Keys)
            {
                if (key.StartsWith(scheduledPrefix, StringComparison.Ordinal))
                    _scheduled.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// 使旧任务失效并取得玩家级独占锁。调用方应在 lease 生命周期内完成长期记忆删除和
        /// Session 清理，防止已进入处理阶段的摘要任务与删除操作交错。
        /// </summary>
        public async Task<IDisposable> InvalidatePlayerAsync(string gameId, string npcId,
            string playerId, CancellationToken ct)
        {
            InvalidatePlayer(gameId, npcId, playerId);
            SemaphoreSlim gate = PlayerGate(gameId, npcId, playerId);
            await gate.WaitAsync(ct);
            return new GateLease(gate);
        }

        private bool EnqueueInternal(string gameId, string npcId, string playerId, string sessionId,
            bool force, string actor)
        {
            if (!DataStore.IsValidId(gameId) || !DataStore.IsValidId(npcId)
                || !DataStore.IsValidPlayerId(playerId) || !DataStore.IsValidSessionId(sessionId)) return false;
            var job = new MemorySummaryJob
            {
                GameId = gameId,
                NpcId = npcId,
                PlayerId = playerId,
                SessionId = sessionId,
                Force = force,
                Actor = actor,
                Generation = CurrentGeneration(gameId, npcId, playerId)
            };
            string key = Key(job);
            if (!_scheduled.TryAdd(key, 0)) return true;
            if (_channel.Writer.TryWrite(job)) return true;
            _scheduled.TryRemove(key, out _);
            _log.Log(LogLevel.Warning, "记忆摘要队列已满，任务保留在 session 文件中等待下次恢复: " + key);
            return false;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            foreach (PendingMemorySession pending in SessionStore.ScanPendingPlayerSessions())
                Enqueue(pending.GameId, pending.NpcId, pending.PlayerId, pending.SessionId);

            await foreach (MemorySummaryJob job in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                string key = Key(job);
                try
                {
                    Exception last = null;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            await ProcessAsync(job, stoppingToken);
                            last = null;
                            break;
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            last = ex;
                            if (attempt < 2)
                                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), stoppingToken);
                        }
                    }
                    if (last != null)
                    {
                        Interlocked.Increment(ref _failedJobs);
                        _log.Log(LogLevel.Warning, "后台记忆摘要重试耗尽，待摘要消息仍保留: " + key
                            + " - " + last.Message);
                    }
                }
                finally
                {
                    _scheduled.TryRemove(key, out _);
                }
            }
        }

        private async Task ProcessAsync(MemorySummaryJob job, CancellationToken ct)
        {
            if (!IsCurrent(job)) return;
            AgentConfigDto cfg = DataStore.LoadNpc(job.GameId, job.NpcId);
            if (cfg == null) return;
            cfg.model = cfg.model ?? new ModelSettings();
            cfg.memory = cfg.memory ?? new MemorySettings();
            if (string.IsNullOrEmpty(cfg.model.apiKey))
                cfg.model.apiKey = Environment.GetEnvironmentVariable("AIBOT_LLM_KEY") ?? _configuration["Llm:ApiKey"];

            EffectiveMemoryPolicy effective = MemoryPolicyService.Resolve(job.GameId, cfg, null, _configuration);
            MemoryPolicy policy = effective.policy;
            if (policy.memoryScope != MemoryPolicyValues.ScopePlayerNpc
                || (!policy.backgroundSummarization && !job.Force)) return;

            SessionState session = SessionStore.GetOrCreate(job.GameId, job.NpcId, job.PlayerId,
                job.SessionId, policy.shortTermTurns);
            SemaphoreSlim playerGate = PlayerGate(job.GameId, job.NpcId, job.PlayerId);
            bool playerGateHeld = false;
            try
            {
                // 所有后台摘要先取得玩家锁，再取得 Session 锁；删除也使用相同顺序，避免死锁。
                await playerGate.WaitAsync(ct);
                playerGateHeld = true;
                await session.Gate.WaitAsync(ct);
                try
                {
                if (!IsCurrent(job)) return;
                if (!job.Force && (policy.summaryThreshold <= 0
                    || session.Memory.EvictedCount < policy.summaryThreshold)) return;
                List<LlmMessage> snapshot = session.Memory.SnapshotEvicted();
                if (snapshot.Count == 0) return;

                // 版本冲突时用最新长期记忆重新运行摘要，避免旧摘要覆盖新事实。
                for (int versionAttempt = 0; versionAttempt < 4; versionAttempt++)
                {
                    if (!IsCurrent(job)) return;
                    PlayerLongTermMemory existing = await _memoryService.LoadAsync(job.GameId,
                        job.NpcId, job.PlayerId, ct);
                    JToken before = JToken.FromObject(existing);
                    ModelSettings settings = ResolveSummarySettings(policy.summaryModel, cfg.model);
                    var backend = new HttpLlmBackend(settings);
                    PlayerMemorySummaryResult summarized = await MemorySummarizer.RunStructuredAsync(
                        backend, settings, existing, snapshot, policy.maxFacts, job.SessionId, _log, ct,
                        policy);
                    summarized.Facts.RemoveAll(f => !PlayerMemoryService.IsCategoryEnabled(f.category, policy));
                    try
                    {
                        if (!IsCurrent(job)) return;
                        PlayerLongTermMemory saved = await _memoryService.SaveStructuredAsync(
                            existing, summarized, policy.maxFacts, ct);
                        if (!IsCurrent(job)) return;
                        _audit.RecordRequired(new MemoryAuditEntry
                        {
                            gameId = job.GameId,
                            npcId = job.NpcId,
                            playerId = job.PlayerId,
                            actor = string.IsNullOrWhiteSpace(job.Actor) ? "system" : job.Actor,
                            action = job.Force ? "memory.summarize.manual" : "memory.summarize.background",
                            before = before,
                            after = JToken.FromObject(saved),
                            metadata = new JObject
                            {
                                ["sessionId"] = job.SessionId,
                                ["messageCount"] = snapshot.Count
                            }
                        });
                        session.Memory.RemoveEvictedPrefix(snapshot.Count);
                        if (!SessionStore.Save(session))
                        {
                            session.Memory.RestoreEvicted(snapshot);
                            throw new IOException("session acknowledgement save failed");
                        }
                        return;
                    }
                    catch (MemoryVersionConflictException) when (versionAttempt < 3) { }
                }
                throw new MemoryVersionConflictException(-1, -1);
                }
                finally { session.Gate.Release(); }
            }
            finally
            {
                if (playerGateHeld) playerGate.Release();
            }
        }

        private static ModelSettings ResolveSummarySettings(ModelSettings summary, ModelSettings fallback)
        {
            ModelSettings source = summary ?? fallback ?? new ModelSettings();
            return new ModelSettings
            {
                baseUrl = string.IsNullOrEmpty(source.baseUrl) ? fallback?.baseUrl : source.baseUrl,
                apiKey = string.IsNullOrEmpty(source.apiKey) ? fallback?.apiKey : source.apiKey,
                model = string.IsNullOrEmpty(source.model) ? fallback?.model : source.model,
                temperature = source.temperature,
                maxTokens = source.maxTokens,
                timeoutMs = source.timeoutMs
            };
        }

        private static string Key(MemorySummaryJob job)
        {
            // generation 必须属于去重键；旧任务结束时不能误删删除后新一代任务的 scheduled 标记。
            return PlayerKey(job.GameId, job.NpcId, job.PlayerId) + "|" + job.SessionId
                + "|g" + job.Generation;
        }

        private long CurrentGeneration(string gameId, string npcId, string playerId)
        {
            return _playerGenerations.TryGetValue(PlayerKey(gameId, npcId, playerId), out long value)
                ? value : 0;
        }

        private bool IsCurrent(MemorySummaryJob job)
        {
            return job.Generation == CurrentGeneration(job.GameId, job.NpcId, job.PlayerId);
        }

        private SemaphoreSlim PlayerGate(string gameId, string npcId, string playerId)
        {
            return _playerGates.GetOrAdd(PlayerKey(gameId, npcId, playerId),
                _ => new SemaphoreSlim(1, 1));
        }

        private sealed class GateLease : IDisposable
        {
            private SemaphoreSlim _gate;

            public GateLease(SemaphoreSlim gate) { _gate = gate; }

            public void Dispose()
            {
                SemaphoreSlim gate = Interlocked.Exchange(ref _gate, null);
                gate?.Release();
            }
        }

        private static string PlayerKey(string gameId, string npcId, string playerId)
        {
            return gameId + "|" + npcId + "|" + playerId;
        }
    }
}
