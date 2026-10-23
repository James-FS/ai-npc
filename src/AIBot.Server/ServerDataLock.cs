using System;
using System.IO;

namespace AIBot.Server
{
    public static class ServerDataLock
    {
        public static IDisposable Acquire(string root)
        {
            if (root == null) return null;
            try
            {
                return new FileStream(Path.Combine(root, ".server.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                throw new IOException("data 目录已被另一个 Server 使用，拒绝并发写入", ex);
            }
        }
    }
}
