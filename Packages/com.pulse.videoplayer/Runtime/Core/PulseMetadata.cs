namespace PulseVideoPlayer
{
    /// <summary>视频透明通道的解释方式。</summary>
    public enum EPulseAlphaMode
    {
        None,
        Straight,
        Premultiplied
    }

    /// <summary>以左上角为原点的像素区域。</summary>
    public readonly struct PulseRegion
    {
        private readonly int m_x; // 区域起始横坐标。
        private readonly int m_y; // 区域起始纵坐标。
        private readonly int m_width; // 区域宽度。
        private readonly int m_height; // 区域高度。

        internal PulseRegion(int x, int y, int width, int height)
        {
            m_x = x;
            m_y = y;
            m_width = width;
            m_height = height;
        }

        /// <summary>区域起始横坐标。</summary>
        public int X => m_x;
        /// <summary>区域起始纵坐标。</summary>
        public int Y => m_y;
        /// <summary>区域宽度。</summary>
        public int Width => m_width;
        /// <summary>区域高度。</summary>
        public int Height => m_height;
    }

    /// <summary>使用整数分子与分母保存帧率或时间基准。</summary>
    public readonly struct PulseRational
    {
        private readonly long m_numerator; // 有理数分子。
        private readonly long m_denominator; // 有理数分母。

        internal PulseRational(long numerator, long denominator)
        {
            m_numerator = numerator;
            m_denominator = denominator;
        }

        /// <summary>有理数分子。</summary>
        public long Numerator => m_numerator;
        /// <summary>有理数分母。</summary>
        public long Denominator => m_denominator;
        /// <summary>供展示使用的浮点值。</summary>
        public double Value => (double)m_numerator / m_denominator;
    }

    /// <summary>经校验的 Pulse v1 元数据。</summary>
    public sealed class PulseMetadata
    {
        /// <summary>容器元数据版本。</summary>
        public int SchemaVersion { get; internal set; }
        /// <summary>解码图像宽度。</summary>
        public int EncodedWidth { get; internal set; }
        /// <summary>解码图像高度。</summary>
        public int EncodedHeight { get; internal set; }
        /// <summary>显示宽度。</summary>
        public int DisplayWidth { get; internal set; }
        /// <summary>显示高度。</summary>
        public int DisplayHeight { get; internal set; }
        /// <summary>RGB 像素区域。</summary>
        public PulseRegion RGBRegion { get; internal set; }
        /// <summary>灰度 Alpha 区域；普通视频为空。</summary>
        public PulseRegion? AlphaRegion { get; internal set; }
        /// <summary>透明通道解释方式。</summary>
        public EPulseAlphaMode AlphaMode { get; internal set; }
        /// <summary>总帧数。</summary>
        public long FrameCount { get; internal set; }
        /// <summary>每秒帧数。</summary>
        public PulseRational FrameRate { get; internal set; }
        /// <summary>一个 MP4 时间戳单位对应的秒数。</summary>
        public PulseRational TimeBase { get; internal set; }
        /// <summary>色彩原色标识。</summary>
        public string ColorPrimaries { get; internal set; }
        /// <summary>传递函数标识。</summary>
        public string ColorTransfer { get; internal set; }
        /// <summary>色彩转换矩阵标识。</summary>
        public string ColorMatrix { get; internal set; }
        /// <summary>像素数值范围标识。</summary>
        public string ColorRange { get; internal set; }
        /// <summary>固定帧率视频的时长，单位为秒。</summary>
        public double DurationSeconds => FrameCount / FrameRate.Value;

        internal PulseMetadata() { }
    }
}
