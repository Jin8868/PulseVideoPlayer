using System;
using System.IO;

namespace PulseVideoPlayer
{
    /// <summary>通过文件句柄按需读取视频，不将整文件装入内存。</summary>
    public sealed class FileSource : IPulseDataSource
    {
        private readonly object m_syncRoot = new object(); // 串行化定位、读取和关闭操作。
        private readonly FileStream m_stream; // 只读文件句柄。
        private readonly long m_length; // 打开时确定的文件长度。
        private bool m_isDisposed; // 关闭标记，防止继续访问句柄。

        /// <summary>打开本地只读文件。</summary>
        /// <param name="filePath">真实文件系统路径，不支持 APK 内的 jar URI。</param>
        /// <exception cref="ArgumentException">路径为空。</exception>
        /// <exception cref="IOException">文件无法打开。</exception>
        public FileSource(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("文件路径不能为空。", nameof(filePath));
            }

            // 仅允许其他读取者共享，避免播放期间文件被改写。
            m_stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                m_length = m_stream.Length;
            }
            catch
            {
                // 打开后查询长度失败时仍须释放句柄，避免失败构造泄漏资源。
                m_stream.Dispose();
                throw;
            }
        }

        /// <summary>打开时的数据源长度。</summary>
        public long Length => m_length;

        /// <summary>按指定偏移读取文件片段。</summary>
        /// <param name="offset">文件中的字节偏移。</param>
        /// <param name="buffer">接收数据的缓冲区。</param>
        /// <param name="bufferOffset">缓冲区写入起点。</param>
        /// <param name="count">请求读取的字节数。</param>
        /// <returns>实际读取的字节数。</returns>
        /// <exception cref="ArgumentNullException">缓冲区为空。</exception>
        /// <exception cref="ArgumentOutOfRangeException">偏移或缓冲区范围无效。</exception>
        /// <exception cref="ObjectDisposedException">数据源已经关闭。</exception>
        public int ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
        {
            // 使用减法验证边界，避免偏移相加发生溢出。
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset > m_length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (bufferOffset < 0 || bufferOffset > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferOffset));
            }
            if (count < 0 || count > buffer.Length - bufferOffset)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            lock (m_syncRoot)
            {
                if (m_isDisposed) throw new ObjectDisposedException(nameof(FileSource));
                m_stream.Position = offset;
                return m_stream.Read(buffer, bufferOffset, count);
            }
        }

        /// <summary>释放文件句柄；重复调用不产生额外操作。</summary>
        public void Dispose()
        {
            lock (m_syncRoot)
            {
                if (m_isDisposed) return;
                m_isDisposed = true;
                m_stream.Dispose();
            }
        }
    }
}
