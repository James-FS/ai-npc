using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AIBot.Server
{
    /// <summary>
    /// Session caches and summary generations are process-local. Until they support
    /// distributed coordination, enforce one active writer per database, including migrations.
    /// A crashed process can be replaced after expiry; losing renewal stops the old host.
    /// </summary>
    public sealed class MongoServerLease : IAsyncDisposable
    {
        internal const string LeaseId = "server-writer-lease";
        internal static readonly TimeSpan Duration = TimeSpan.FromMinutes(2);
        private readonly IMongoCollection<BsonDocument> _leases;
        private readonly string _owner = Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Task _renewal;
        private volatile bool _held;
        private int _disposed;
        public bool IsHeld => _held;

        public MongoServerLease(MongoConnectionFactory factory)
        {
            _leases = factory.Collection<BsonDocument>(MongoInitializer.MetaCollection);
        }

        public async Task AcquireAsync(IHostApplicationLifetime lifetime, CancellationToken ct)
        {
            if (!await TryAcquireAsync(ct))
                throw new InvalidOperationException("该 Mongo 数据库已有活跃 Server。当前版本仅支持单活写入；异常退出后请等待最多两分钟再启动。");
            _renewal = RenewLoopAsync(lifetime);
        }

        internal async Task<bool> TryAcquireAsync(CancellationToken ct)
        {
            try
            {
                await _leases.UpdateOneAsync(new BsonDocument("_id", LeaseId),
                    Builders<BsonDocument>.Update.SetOnInsert("owner", string.Empty)
                        .SetOnInsert("expiresUtc", DateTime.UnixEpoch),
                    new UpdateOptions { IsUpsert = true }, ct);
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey) { }
            var filter = new BsonDocument
            {
                ["_id"] = LeaseId,
                ["$or"] = new BsonArray
                {
                    new BsonDocument("owner", _owner),
                    new BsonDocument("$expr", new BsonDocument("$lte", new BsonArray { "$expiresUtc", "$$NOW" }))
                }
            };
            try
            {
                BsonDocument result = await _leases.FindOneAndUpdateAsync(filter, LeaseUpdate(),
                    new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
                _held = result != null;
                return _held;
            }
            catch (MongoCommandException ex) when (ex.Code == 11000) { return false; }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey) { return false; }
        }

        private UpdateDefinition<BsonDocument> LeaseUpdate()
        {
            // Database time avoids granting overlapping leases when hosts' clocks differ.
            return Builders<BsonDocument>.Update.Pipeline(new[]
            {
                new BsonDocument("$set", new BsonDocument
                {
                    ["owner"] = _owner,
                    ["expiresUtc"] = new BsonDocument("$add", new BsonArray { "$$NOW", (long)Duration.TotalMilliseconds })
                })
            });
        }

        internal async Task<bool> RenewAsync(CancellationToken ct)
        {
            var filter = new BsonDocument { ["_id"] = LeaseId, ["owner"] = _owner,
                ["$expr"] = new BsonDocument("$gt", new BsonArray { "$expiresUtc", "$$NOW" }) };
            UpdateResult result = await _leases.UpdateOneAsync(filter, LeaseUpdate(), cancellationToken: ct);
            return _held = result.MatchedCount == 1;
        }

        private async Task RenewLoopAsync(IHostApplicationLifetime lifetime)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token);
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        if (!await RenewAsync(timeout.Token)) throw new InvalidOperationException("Mongo 单活租约已丢失");
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception)
            {
                _held = false;
                Console.Error.WriteLine("Mongo 单活租约续期失败，Server 停止接收请求并退出。请检查数据库连接。");
                lifetime.StopApplication();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            if (_renewal != null) await _renewal;
            if (_held)
            {
                try
                {
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        await _leases.DeleteOneAsync(new BsonDocument { ["_id"] = LeaseId, ["owner"] = _owner }, timeout.Token);
                }
                catch { /* On failed release, the lease expires instead. */ }
            }
            _held = false;
            _stop.Dispose();
        }
    }
}
