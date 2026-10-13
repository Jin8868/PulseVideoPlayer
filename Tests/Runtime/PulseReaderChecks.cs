using System;
using System.IO;
using System.Text;
using System.Threading;
using PulseVideoPlayer;

internal static class PulseReaderChecks
{
    private const string VALIDJSON =
        "{\"schemaVersion\":1,\"encodedSize\":[64,64],\"displaySize\":[64,64]," +
        "\"rgbRect\":[0,0,64,64],\"alphaRect\":null,\"alphaMode\":\"None\",\"frameCount\":30," +
        "\"frameRate\":\"30/1\",\"timeBase\":\"1/15360\",\"color\":{\"primaries\":\"Unspecified\"," +
        "\"transfer\":\"Unspecified\",\"matrix\":\"Unspecified\",\"range\":\"Unspecified\"}}"; // 最小合法 v1 元数据。

    /// <summary>验证实际素材、有界读取以及损坏容器拒收。</summary>
    /// <param name="arguments">工程内 PulseTests 目录。</param>
    /// <returns>成功返回零，失败返回一。</returns>
    public static int Main(string[] arguments)
    {
        try
        {
            string sampleRoot = arguments[0];
            string[] paths =
            {
                "Synthetic/all_in_one.pulse", "Alpha/alpha_test.pulse", "Sintel/Sintel.2010.1080p.pulse"
            };
            long[] frameCounts = { 1800, 900, 26640 };
            for (int index = 0; index < paths.Length; index++)
            {
                using (var source = new TrackingSource(new FileSource(Path.Combine(sampleRoot, paths[index])), 3))
                {
                    PulseMetadata metadata = PulseMetadataReader.Read(source);
                    Check(metadata.FrameCount == frameCounts[index], "实际视频帧数");
                    Check(metadata.FrameRate.Numerator == 30 && metadata.FrameRate.Denominator == 1, "实际帧率");
                    Check(source.BytesRead < 4096, "不得读取完整视频载荷");
                    Check((index == 1) == metadata.AlphaRegion.HasValue, "Alpha 区域");
                    if (index == 1)
                    {
                        Check(metadata.EncodedHeight == 1602 && metadata.AlphaRegion.Value.Y == 802, "Alpha 拼接坐标");
                    }
                    Console.WriteLine(paths[index] + "：帧数 " + metadata.FrameCount +
                        "，读取字节 " + source.BytesRead + " / 文件字节 " + source.Length);
                }
            }

            // 测试扩展 Box、重复 UUID、未知版本和字段约束，而不是仅复述正常解析流程。
            using (var source = new TrackingSource(new MemorySource(BuildContainer(VALIDJSON, true, false)), 1))
            {
                Check(PulseMetadataReader.Read(source).FrameCount == 30, "扩展 Box 与单字节分片读取");
            }
            ExpectInvalid(BuildContainer(VALIDJSON, false, true), "重复 UUID");
            ExpectInvalid(BuildContainer(VALIDJSON.Replace("\"schemaVersion\":1", "\"schemaVersion\":2")), "未知版本");
            ExpectInvalid(
                BuildContainer(VALIDJSON.Replace("\"rgbRect\":[0,0,64,64]", "\"rgbRect\":[1,0,64,64]")),
                "区域越界");
            ExpectInvalid(BuildContainer(VALIDJSON.Replace("\"frameRate\":\"30/1\"", "\"frameRate\":\"30/0\"")), "零分母");
            ExpectInvalid(BuildContainer(VALIDJSON.Replace("\"frameCount\":30", "\"frameCount\":-1")), "负帧数");
            ExpectInvalid(
                BuildContainer(VALIDJSON.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")),
                "重复 JSON 字段");
            byte[] truncated = BuildContainer(VALIDJSON);
            Array.Resize(ref truncated, truncated.Length - 1);
            ExpectInvalid(truncated, "截断文件");
            byte[] missingUUID = BuildContainer(VALIDJSON);
            Array.Resize(ref missingUUID, 34);
            ExpectInvalid(missingUUID, "缺失 UUID");
            byte[] excessiveBox = BuildContainer(VALIDJSON);
            excessiveBox[34] = 0x7f;
            ExpectInvalid(excessiveBox, "Box 大小越界");

            // 取消必须在读取数据前生效；关闭操作必须幂等并拒绝后续访问。
            using (var source = new TrackingSource(new MemorySource(BuildContainer(VALIDJSON)), 8))
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                bool cancelled = false;
                try { PulseMetadataReader.Read(source, cancellation.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && source.BytesRead == 0, "预取消不读取文件");
            }
            var disposedSource = new FileSource(Path.Combine(sampleRoot, paths[0]));
            disposedSource.Dispose();
            disposedSource.Dispose();
            bool disposedRejected = false;
            try { disposedSource.ReadAt(0, new byte[1], 0, 1); }
            catch (ObjectDisposedException) { disposedRejected = true; }
            Check(disposedRejected, "关闭后拒绝读取");
            Console.WriteLine("实际素材、分片读取、扩展 Box、损坏拒收、取消和句柄生命周期验证通过。");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static byte[] BuildContainer(string json, bool extended = false, bool duplicate = false)
    {
        using (var output = new MemoryStream())
        {
            WriteBox(output, "ftyp", new byte[8], false);
            WriteBox(output, "moov", new byte[1], false);
            WriteBox(output, "mdat", new byte[1], false);
            byte[] uuid = { 0x6f, 0xbd, 0xab, 0x98, 0x40, 0x1e, 0x4e, 0x52,
                0xb4, 0x4a, 0x97, 0xfe, 0xd5, 0x07, 0x61, 0xd2 };
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            var payload = new byte[16 + jsonBytes.Length];
            uuid.CopyTo(payload, 0);
            jsonBytes.CopyTo(payload, 16);
            WriteBox(output, "uuid", payload, extended);
            if (duplicate) WriteBox(output, "uuid", payload, false);
            return output.ToArray();
        }
    }

    private static void WriteBox(Stream output, string type, byte[] payload, bool extended)
    {
        long size = payload.Length + (extended ? 16 : 8);
        long firstSize = extended ? 1 : size;
        for (int shift = 24; shift >= 0; shift -= 8) output.WriteByte((byte)((firstSize >> shift) & 255));
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes, 0, 4);
        if (extended)
        {
            for (int shift = 56; shift >= 0; shift -= 8) output.WriteByte((byte)((size >> shift) & 255));
        }
        output.Write(payload, 0, payload.Length);
    }

    private static void ExpectInvalid(byte[] bytes, string name)
    {
        bool rejected = false;
        using (var source = new MemorySource(bytes))
        {
            try { PulseMetadataReader.Read(source); }
            catch (InvalidDataException) { rejected = true; }
        }
        Check(rejected, name);
    }

    private static void Check(bool result, string name)
    {
        if (!result) throw new InvalidOperationException("验证失败：" + name);
    }

    private sealed class TrackingSource : IPulseDataSource
    {
        private readonly IPulseDataSource m_source; // 被包装的数据源。
        private readonly int m_maxRead; // 模拟数据源返回短片段。
        private long m_bytesRead; // 累计返回字节数。

        internal TrackingSource(IPulseDataSource source, int maxRead) { m_source = source; m_maxRead = maxRead; }
        public long Length => m_source.Length;
        internal long BytesRead => m_bytesRead;

        /// <summary>限制单次返回长度并统计读取量。</summary>
        /// <param name="offset">源偏移。</param>
        /// <param name="buffer">目标缓冲区。</param>
        /// <param name="bufferOffset">目标偏移。</param>
        /// <param name="count">请求长度。</param>
        /// <returns>实际返回长度。</returns>
        public int ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
        {
            int received = m_source.ReadAt(offset, buffer, bufferOffset, Math.Min(count, m_maxRead));
            m_bytesRead += received;
            return received;
        }

        /// <summary>关闭底层数据源。</summary>
        public void Dispose() { m_source.Dispose(); }
    }

    private sealed class MemorySource : IPulseDataSource
    {
        private readonly byte[] m_bytes; // 用于构造损坏容器的测试数据。
        internal MemorySource(byte[] bytes) { m_bytes = bytes; }
        public long Length => m_bytes.Length;

        /// <summary>读取测试内存片段。</summary>
        /// <param name="offset">源偏移。</param>
        /// <param name="buffer">目标缓冲区。</param>
        /// <param name="bufferOffset">目标偏移。</param>
        /// <param name="count">请求长度。</param>
        /// <returns>实际返回长度。</returns>
        public int ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
        {
            int received = (int)Math.Min(count, m_bytes.Length - offset);
            Buffer.BlockCopy(m_bytes, (int)offset, buffer, bufferOffset, received);
            return received;
        }

        /// <summary>测试内存数据源无需释放句柄。</summary>
        public void Dispose() { }
    }
}
