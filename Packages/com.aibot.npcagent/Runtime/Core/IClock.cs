using System;

namespace AIBot.Core
{
    /// <summary>时间抽象：可注入，测试可控制；Unity/Server 均用 SystemClock。</summary>
    public interface IClock
    {
        long TimestampMilliseconds { get; }
    }

    public sealed class SystemClock : IClock
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public long TimestampMilliseconds => (long)(DateTime.UtcNow - Epoch).TotalMilliseconds;
    }
}
