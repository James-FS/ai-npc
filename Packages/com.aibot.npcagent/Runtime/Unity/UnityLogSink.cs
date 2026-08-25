using System;
using AIBot.Core.Logging;
using UnityEngine;

namespace AIBot.Unity
{
    /// <summary>Core 日志 → Unity Console 桥接。</summary>
    public sealed class UnityLogSink : ILogSink
    {
        public static readonly UnityLogSink Instance = new UnityLogSink();

        public void Log(LogLevel level, string message, Exception ex = null)
        {
            string line = "[AIBot] " + message + (ex == null ? string.Empty : "\n" + ex);
            switch (level)
            {
                case LogLevel.Warning: Debug.LogWarning(line); break;
                case LogLevel.Error: Debug.LogError(line); break;
                default: Debug.Log(line); break;
            }
        }
    }
}
