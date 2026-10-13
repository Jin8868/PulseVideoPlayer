using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PulseVideoPlayer
{
    /// <summary>扫描 MP4 顶层 Box 并读取 Pulse v1 元数据。</summary>
    public static class PulseMetadataReader
    {
        private const int MAXMETADATABYTES = 1048576; // 元数据最大字节数，避免损坏文件触发巨额分配。
        private const uint UUIDBOXTYPE = 0x75756964; // uuid 顶层 Box 类型。
        private const uint FILETYPEBOXTYPE = 0x66747970; // ftyp 顶层 Box 类型。
        private const uint MOVIEBOXTYPE = 0x6d6f6f76; // moov 顶层 Box 类型。
        private const uint MEDIABOXTYPE = 0x6d646174; // mdat 顶层 Box 类型。
        private static readonly byte[] m_metadataUUID =
        {
            0x6f, 0xbd, 0xab, 0x98, 0x40, 0x1e, 0x4e, 0x52,
            0xb4, 0x4a, 0x97, 0xfe, 0xd5, 0x07, 0x61, 0xd2
        }; // Pulse v1 UUID 的网络字节序。

        /// <summary>从真实文件路径读取元数据，并在完成后关闭文件。</summary>
        /// <param name="filePath">本地 Pulse 文件路径。</param>
        /// <param name="cancellationToken">取消本次读取的令牌。</param>
        /// <returns>经结构和区域边界校验的元数据。</returns>
        /// <exception cref="ArgumentException">文件路径为空。</exception>
        /// <exception cref="IOException">文件无法打开或读取。</exception>
        /// <exception cref="InvalidDataException">文件结构或元数据无效。</exception>
        /// <exception cref="OperationCanceledException">读取已取消。</exception>
        public static PulseMetadata Read(string filePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var source = new FileSource(filePath))
            {
                return Read(source, cancellationToken);
            }
        }

        /// <summary>从随机访问数据源读取元数据，不关闭调用方的数据源。</summary>
        /// <param name="source">文件、内置资源或其他随机访问数据源。</param>
        /// <param name="cancellationToken">取消本次读取的令牌。</param>
        /// <returns>经结构和区域边界校验的元数据。</returns>
        /// <exception cref="ArgumentNullException">数据源为空。</exception>
        /// <exception cref="InvalidDataException">容器、版本或元数据无效。</exception>
        /// <exception cref="OperationCanceledException">读取已取消。</exception>
        public static PulseMetadata Read(IPulseDataSource source, CancellationToken cancellationToken = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            cancellationToken.ThrowIfCancellationRequested();
            long length = source.Length;
            long position = 0;
            bool hasFileType = false;
            bool hasMovie = false;
            bool hasMedia = false;
            PulseMetadata metadata = null;
            var header = new byte[16];
            var uuid = new byte[16];

            // 按 Box 大小跳过视频载荷，读取量不随视频总大小增长。
            while (position < length)
            {
                ReadExactly(source, position, header, 8, cancellationToken);
                long boxSize = ReadUInt32(header, 0);
                uint boxType = ReadUInt32(header, 4);
                int headerSize = 8;
                if (boxSize == 1)
                {
                    ReadExactly(source, position + 8, header, 8, cancellationToken);
                    boxSize = ReadInt64(header);
                    headerSize = 16;
                }
                else if (boxSize == 0)
                {
                    boxSize = length - position;
                }
                if (boxSize < headerSize || boxSize > length - position)
                {
                    throw new InvalidDataException("MP4 Box 大小越界。");
                }
                if (position == 0 && boxType != FILETYPEBOXTYPE)
                {
                    throw new InvalidDataException("Pulse 文件必须以 ftyp 开始。");
                }

                if (boxType == FILETYPEBOXTYPE) hasFileType = boxSize >= headerSize + 8;
                if (boxType == MOVIEBOXTYPE) hasMovie = boxSize > headerSize;
                if (boxType == MEDIABOXTYPE) hasMedia = boxSize > headerSize;
                if (boxType == UUIDBOXTYPE)
                {
                    if (boxSize < headerSize + 16) throw new InvalidDataException("UUID Box 不完整。");
                    ReadExactly(source, position + headerSize, uuid, 16, cancellationToken);
                    if (IsMetadataUUID(uuid))
                    {
                        if (metadata != null) throw new InvalidDataException("存在重复的 Pulse 元数据。");
                        long payloadSize = boxSize - headerSize - 16;
                        if (payloadSize <= 0 || payloadSize > MAXMETADATABYTES)
                        {
                            throw new InvalidDataException("Pulse 元数据大小无效。");
                        }
                        var payload = new byte[(int)payloadSize];
                        ReadExactly(source, position + headerSize + 16, payload, payload.Length, cancellationToken);
                        try
                        {
                            var encoding = new UTF8Encoding(false, true);
                            metadata = ParseMetadata(encoding.GetString(payload));
                        }
                        catch (DecoderFallbackException exception)
                        {
                            throw new InvalidDataException("Pulse 元数据不是有效 UTF-8。", exception);
                        }
                    }
                }
                position += boxSize;
            }

            // 元数据存在并不代表文件完整，必须同时具备标准 MP4 的关键 Box。
            cancellationToken.ThrowIfCancellationRequested();
            if (!hasFileType || !hasMovie || !hasMedia || metadata == null)
            {
                throw new InvalidDataException("缺少 ftyp、moov、mdat 或 Pulse UUID 元数据。");
            }
            return metadata;
        }

        private static PulseMetadata ParseMetadata(string json)
        {
            Dictionary<string, object> root = PulseJsonReader.Parse(json);
            var metadata = new PulseMetadata();
            metadata.SchemaVersion = GetPositiveInt(root, "schemaVersion");
            if (metadata.SchemaVersion != 1) throw new InvalidDataException("不支持的 Pulse 元数据版本。");
            int[] encodedSize = GetIntegerArray(root, "encodedSize", 2);
            int[] displaySize = GetIntegerArray(root, "displaySize", 2);
            if (encodedSize[0] <= 0 || encodedSize[1] <= 0 || displaySize[0] <= 0 || displaySize[1] <= 0)
            {
                throw new InvalidDataException("视频尺寸必须为正数。");
            }
            metadata.EncodedWidth = encodedSize[0];
            metadata.EncodedHeight = encodedSize[1];
            metadata.DisplayWidth = displaySize[0];
            metadata.DisplayHeight = displaySize[1];
            metadata.RGBRegion = GetRegion(root, "rgbRect", metadata);
            string alphaMode = GetString(root, "alphaMode");
            switch (alphaMode)
            {
                case "None": metadata.AlphaMode = EPulseAlphaMode.None; break;
                case "Straight": metadata.AlphaMode = EPulseAlphaMode.Straight; break;
                case "Premultiplied": metadata.AlphaMode = EPulseAlphaMode.Premultiplied; break;
                default: throw new InvalidDataException("Alpha 模式无效。");
            }
            object alphaValue = GetValue(root, "alphaRect");
            if (metadata.AlphaMode == EPulseAlphaMode.None)
            {
                if (alphaValue != null) throw new InvalidDataException("普通视频不能携带 Alpha 区域。");
            }
            else
            {
                metadata.AlphaRegion = GetRegion(root, "alphaRect", metadata);
                PulseRegion alpha = metadata.AlphaRegion.Value;
                PulseRegion rgb = metadata.RGBRegion;
                if (alpha.Width != rgb.Width || alpha.Height != rgb.Height ||
                    ((long)alpha.X < (long)rgb.X + rgb.Width && (long)rgb.X < (long)alpha.X + alpha.Width &&
                     (long)alpha.Y < (long)rgb.Y + rgb.Height && (long)rgb.Y < (long)alpha.Y + alpha.Height))
                {
                    throw new InvalidDataException("RGB 与 Alpha 区域必须同尺寸且不重叠。");
                }
            }
            if (metadata.DisplayWidth != metadata.RGBRegion.Width ||
                metadata.DisplayHeight != metadata.RGBRegion.Height)
            {
                throw new InvalidDataException("显示尺寸与 RGB 区域不一致。");
            }
            metadata.FrameCount = GetInteger(root, "frameCount");
            if (metadata.FrameCount <= 0) throw new InvalidDataException("视频帧数必须为正数。");
            metadata.FrameRate = GetRational(root, "frameRate");
            metadata.TimeBase = GetRational(root, "timeBase");
            var color = GetValue(root, "color") as Dictionary<string, object>;
            if (color == null) throw new InvalidDataException("缺少色彩元数据。");
            metadata.ColorPrimaries = GetString(color, "primaries");
            metadata.ColorTransfer = GetString(color, "transfer");
            metadata.ColorMatrix = GetString(color, "matrix");
            metadata.ColorRange = GetString(color, "range");
            return metadata;
        }

        private static PulseRegion GetRegion(Dictionary<string, object> root, string key, PulseMetadata metadata)
        {
            int[] rect = GetIntegerArray(root, key, 4);
            if (rect[0] < 0 || rect[1] < 0 || rect[2] <= 0 || rect[3] <= 0 ||
                (long)rect[0] + rect[2] > metadata.EncodedWidth ||
                (long)rect[1] + rect[3] > metadata.EncodedHeight)
            {
                throw new InvalidDataException("像素区域越界：" + key);
            }
            return new PulseRegion(rect[0], rect[1], rect[2], rect[3]);
        }

        private static PulseRational GetRational(Dictionary<string, object> root, string key)
        {
            string[] parts = GetString(root, key).Split('/');
            if (parts.Length != 2 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long numerator) ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long denominator) ||
                numerator <= 0 || denominator <= 0)
            {
                throw new InvalidDataException("有理数无效：" + key);
            }
            return new PulseRational(numerator, denominator);
        }

        private static int[] GetIntegerArray(Dictionary<string, object> root, string key, int count)
        {
            var values = GetValue(root, key) as List<object>;
            if (values == null || values.Count != count) throw new InvalidDataException("数组长度无效：" + key);
            var result = new int[count];
            for (int index = 0; index < count; index++)
            {
                if (!(values[index] is long integer) || integer < int.MinValue || integer > int.MaxValue)
                {
                    throw new InvalidDataException("数组整数无效：" + key);
                }
                result[index] = (int)integer;
            }
            return result;
        }

        private static int GetPositiveInt(Dictionary<string, object> root, string key)
        {
            long value = GetInteger(root, key);
            if (value <= 0 || value > int.MaxValue) throw new InvalidDataException("整数越界：" + key);
            return (int)value;
        }

        private static long GetInteger(Dictionary<string, object> root, string key)
        {
            if (!(GetValue(root, key) is long value)) throw new InvalidDataException("缺少整数字段：" + key);
            return value;
        }

        private static string GetString(Dictionary<string, object> root, string key)
        {
            var value = GetValue(root, key) as string;
            if (string.IsNullOrEmpty(value)) throw new InvalidDataException("缺少字符串字段：" + key);
            return value;
        }

        private static object GetValue(Dictionary<string, object> root, string key)
        {
            if (!root.TryGetValue(key, out object value)) throw new InvalidDataException("缺少字段：" + key);
            return value;
        }

        private static bool IsMetadataUUID(byte[] uuid)
        {
            for (int index = 0; index < m_metadataUUID.Length; index++)
            {
                if (uuid[index] != m_metadataUUID[index]) return false;
            }
            return true;
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) |
                ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];
        }

        private static long ReadInt64(byte[] buffer)
        {
            if ((buffer[0] & 0x80) != 0) throw new InvalidDataException("Box 大小超出支持范围。");
            long value = 0;
            for (int index = 0; index < 8; index++) value = (value << 8) | buffer[index];
            return value;
        }

        private static void ReadExactly(
            IPulseDataSource source, long offset, byte[] buffer, int count, CancellationToken cancellationToken)
        {
            if (offset < 0 || offset > source.Length || count > source.Length - offset)
            {
                throw new InvalidDataException("文件片段不完整。");
            }
            int readCount = 0;
            while (readCount < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int received = source.ReadAt(offset + readCount, buffer, readCount, count - readCount);
                if (received <= 0 || received > count - readCount)
                {
                    throw new InvalidDataException("数据源未提供完整文件片段。");
                }
                readCount += received;
            }
        }
    }
}
