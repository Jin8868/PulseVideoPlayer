using System;

namespace PulseVideoPlayer
{
    /// <summary>
    /// 提供按偏移读取的有界视频数据源。
    /// </summary>
    public interface IPulseDataSource : IDisposable
    {
        /// <summary>数据源总字节数。</summary>
        long Length { get; }

        /// <summary>从指定偏移读取数据，允许返回少于请求数量的字节。</summary>
        /// <param name="offset">数据源中的字节偏移。</param>
        /// <param name="buffer">接收数据的缓冲区。</param>
        /// <param name="bufferOffset">缓冲区写入起点。</param>
        /// <param name="count">请求读取的字节数。</param>
        /// <returns>实际读取的字节数；到达末尾时返回零。</returns>
        int ReadAt(long offset, byte[] buffer, int bufferOffset, int count);
    }
}
