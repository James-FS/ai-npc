using System;

namespace AIBot.Core.Logging
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3
    }

    /// <summary>Core 层唯一日志出口：宿主负责桥接到各自日志系统（Unity→Debug.Log，Server→ILogger）。</summary>
    public interface ILogSink
    {
        void Log(LogLevel level, string message, Exception ex = null);
    }

    public sealed class NullLogSink : ILogSink
    {
        public static readonly NullLogSink Instance = new NullLogSink();
        public void Log(LogLevel level, string message, Exception ex = null) { }
    }

    public sealed class ConsoleLogSink : ILogSink
    {
        public void Log(LogLevel level, string message, Exception ex = null)
        {
            Console.WriteLine("[AIBot:" + level + "] " + message + (ex == null ? "" : " | " + ex.Message));
        }
    }
}
