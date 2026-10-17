using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace PulseVideoTool
{
    internal sealed class ToolLog : IDisposable
    {
        private readonly object m_syncRoot = new object(); // 串行化标准输出和错误输出的日志写入。
        private readonly StreamWriter m_writer; // 本次运行持久化日志文件。
        private readonly ConcurrentQueue<string> m_pending = new ConcurrentQueue<string>(); // 等待主线程显示的日志。
        private readonly StringBuilder m_text = new StringBuilder(); // 提供完整日志复制。
        private bool m_isDisposed; // 避免后台回调写入已关闭的日志文件。

        internal string FilePath { get; private set; }

        internal ToolLog()
        {
            string directory = Path.Combine(ToolPaths.BaseDirectory, "log");
            Directory.CreateDirectory(directory);
            // 固定文件追加记录，反复启动也不会生成新的带时间戳日志文件。
            FilePath = Path.Combine(directory, "VideoTool.log");
            // 使用可定位流保留追加行为，同时允许清空历史内容并复用当前文件句柄。
            m_writer = new StreamWriter(
                new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));
            m_writer.BaseStream.Seek(0, SeekOrigin.End);
            m_writer.AutoFlush = true;
        }

        internal void Write(string message)
        {
            lock (m_syncRoot)
            {
                if (m_isDisposed) return;
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message;
                m_writer.WriteLine(line);
                m_text.AppendLine(line);
                m_pending.Enqueue(line);
            }
        }

        internal void Clear()
        {
            lock (m_syncRoot)
            {
                if (m_isDisposed) throw new ObjectDisposedException("ToolLog");

                // 与后台写入互斥，先清空磁盘，再丢弃复制缓存和尚未显示的旧日志。
                m_writer.Flush();
                m_writer.BaseStream.SetLength(0);
                m_writer.BaseStream.Position = 0;
                m_text.Clear();
                string discardedLine;
                while (m_pending.TryDequeue(out discardedLine)) { }
            }
        }

        internal bool TryRead(out string line) { return m_pending.TryDequeue(out line); }

        internal string GetText()
        {
            lock (m_syncRoot) return m_text.ToString();
        }

        /// <summary>刷新并关闭本次运行日志。</summary>
        public void Dispose()
        {
            lock (m_syncRoot)
            {
                if (m_isDisposed) return;
                m_isDisposed = true;
                m_writer.Dispose();
            }
        }
    }
}
