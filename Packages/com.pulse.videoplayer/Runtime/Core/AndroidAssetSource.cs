#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PulseVideoPlayer
{
    /// <summary>使用 APK 内未压缩资源的 fd 区间，不创建视频副本。</summary>
    internal sealed class AndroidAssetSource : IPulseDataSource
    {
        private const string LIBRARYNAME = "PulseVideoNative"; // Android 原生文件读取入口。
        private readonly object m_syncRoot = new object(); // 防止读取与关闭竞争同一个 fd。
        private readonly long m_baseOffset; // 资源在 APK 文件中的起点。
        private readonly long m_length; // 仅允许读取当前资源的区间。
        private int m_descriptor; // 独立拥有的只读 fd，关闭后为负一。

        internal AndroidAssetSource(string filePath)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(PulseNative.NormalizeAndroidPath(filePath) + '\0');
            m_descriptor = OpenFile(pathBytes, out m_baseOffset, out m_length);
            if (m_descriptor < 0)
            {
                throw new IOException("无法打开 APK 视频。请使用异步加载入口，并将 pulse 配置为不压缩。");
            }
            if (m_baseOffset < 0 || m_length <= 0 || m_baseOffset > long.MaxValue - m_length)
            {
                CloseFile(m_descriptor);
                m_descriptor = -1;
                throw new IOException("APK 视频的文件区间无效。");
            }
        }

        /// <summary>当前 APK 资源的字节长度。</summary>
        public long Length => m_length;

        /// <summary>在当前资源的有界区间内按需读取。</summary>
        /// <param name="offset">相对资源起点的字节偏移。</param>
        /// <param name="buffer">接收数据的数组。</param>
        /// <param name="bufferOffset">数组写入起点。</param>
        /// <param name="count">请求读取的字节数。</param>
        /// <returns>实际读取的字节数。</returns>
        /// <exception cref="ArgumentNullException">接收数组为空。</exception>
        /// <exception cref="ArgumentOutOfRangeException">文件或数组范围无效。</exception>
        /// <exception cref="ObjectDisposedException">数据源已经关闭。</exception>
        /// <exception cref="IOException">原生读取失败。</exception>
        public int ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
        {
            // 先验证所有边界，再将相对偏移换算成 APK 的绝对偏移。
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset > m_length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (bufferOffset < 0 || bufferOffset > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(bufferOffset));
            if (count < 0 || count > buffer.Length - bufferOffset)
                throw new ArgumentOutOfRangeException(nameof(count));
            lock (m_syncRoot)
            {
                if (m_descriptor < 0) throw new ObjectDisposedException(nameof(AndroidAssetSource));
                int readCount = (int)Math.Min(count, m_length - offset);
                if (readCount == 0) return 0;
                int result = ReadFile(m_descriptor, m_baseOffset + offset, buffer, bufferOffset, readCount);
                if (result < 0) throw new IOException("APK 视频读取失败。");
                return result;
            }
        }

        /// <summary>关闭当前 fd；重复调用安全。</summary>
        public void Dispose()
        {
            lock (m_syncRoot)
            {
                if (m_descriptor < 0) return;
                CloseFile(m_descriptor);
                m_descriptor = -1;
            }
        }

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_OpenFile", CallingConvention = CallingConvention.Cdecl)]
        private static extern int OpenFile(byte[] path, out long offset, out long length);

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_ReadFile", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ReadFile(
            int descriptor, long offset, [Out] byte[] buffer, int bufferOffset, int count);

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_CloseFile", CallingConvention = CallingConvention.Cdecl)]
        private static extern void CloseFile(int descriptor);
    }
}
#endif
