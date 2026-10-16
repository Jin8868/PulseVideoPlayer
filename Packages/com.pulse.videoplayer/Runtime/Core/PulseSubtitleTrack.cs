using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PulseVideoPlayer
{
    /// <summary>解析 SRT 并按视频毫秒时间查询字幕，支持多行、乱序和重叠条目。</summary>
    public sealed class PulseSubtitleTrack
    {
        private static readonly Regex m_tags = new Regex("<[^>]*>"); // 去除字幕中的展示标签，交给 Text 输出纯文本。
        private readonly Cue[] m_cues; // 按开始时间排序的有效字幕。
        private readonly long[] m_prefixEnd; // 前缀最大结束时间，用于快速跳过已经结束的条目。
        private readonly long[] m_boundaries; // 所有起止边界，边界之间的字幕内容保持不变。
        private readonly List<int> m_active = new List<int>(); // 重用重叠条目的索引容器。
        private readonly StringBuilder m_builder = new StringBuilder(); // 仅跨字幕边界时拼接文本。
        private int m_cachedInterval = -2; // 上次查询所在的边界区间。
        private string m_cachedText = string.Empty; // 当前区间的文字，不逐帧创建字符串。

        private PulseSubtitleTrack(List<Cue> cues)
        {
            cues.Sort(CompareCues);
            m_cues = cues.ToArray();
            m_prefixEnd = new long[m_cues.Length];
            m_boundaries = new long[m_cues.Length * 2];
            long maximumEnd = 0;
            for (int index = 0; index < m_cues.Length; index++)
            {
                maximumEnd = Math.Max(maximumEnd, m_cues[index].End);
                m_prefixEnd[index] = maximumEnd;
                m_boundaries[index * 2] = m_cues[index].Start;
                m_boundaries[index * 2 + 1] = m_cues[index].End;
            }
            Array.Sort(m_boundaries);
        }

        /// <summary>解析完整 SRT 文本，空文件得到空时间轴。</summary>
        /// <param name="source">UTF-8 解码后的字幕文本，支持 BOM 与常见换行符。</param>
        /// <returns>独立的字幕时间轴。</returns>
        /// <exception cref="ArgumentNullException">文本为空引用。</exception>
        /// <exception cref="FormatException">字幕序号或起止时间格式无效。</exception>
        public static PulseSubtitleTrack Parse(string source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var cues = new List<Cue>();
            var text = new StringBuilder();
            using (var reader = new StringReader(source.TrimStart('\uFEFF')))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // SRT 一般带数字序号，同时允许省略序号的标准时间行。
                    if (!line.Contains("-->"))
                    {
                        if (!int.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                            throw new FormatException("SRT 序号无效：" + line);
                        line = reader.ReadLine();
                    }
                    int arrow = line == null ? -1 : line.IndexOf("-->", StringComparison.Ordinal);
                    if (arrow < 0) throw new FormatException("SRT 缺少时间范围。");
                    long start = ParseTime(line.Substring(0, arrow));
                    long end = ParseTime(line.Substring(arrow + 3));
                    if (end <= start) throw new FormatException("SRT 结束时间必须晚于开始时间：" + line);
                    text.Clear();
                    while ((line = reader.ReadLine()) != null && !string.IsNullOrWhiteSpace(line))
                    {
                        if (text.Length > 0) text.Append('\n');
                        text.Append(line);
                    }
                    string caption = System.Net.WebUtility.HtmlDecode(m_tags.Replace(text.ToString(), string.Empty));
                    if (!string.IsNullOrWhiteSpace(caption)) cues.Add(new Cue(start, end, caption, cues.Count));
                }
            }
            return new PulseSubtitleTrack(cues);
        }

        /// <summary>查询时间点上的文字，结束边界不包含在该条字幕中。</summary>
        /// <param name="timeMs">视频时间，单位为毫秒。</param>
        /// <returns>当前字幕，重叠条目按开始时间分行；没有字幕时为空字符串。</returns>
        public string GetText(long timeMs)
        {
            int interval = FindBoundary(timeMs);
            if (interval == m_cachedInterval) return m_cachedText;
            m_cachedInterval = interval;
            m_active.Clear();
            m_builder.Clear();

            // 二分定位最后一个已经开始的条目，再通过前缀结束时间收集重叠字幕。
            int lower = 0;
            int upper = m_cues.Length;
            while (lower < upper)
            {
                int middle = lower + (upper - lower) / 2;
                if (m_cues[middle].Start <= timeMs) lower = middle + 1;
                else upper = middle;
            }
            for (int index = lower - 1; index >= 0 && m_prefixEnd[index] > timeMs; index--)
            {
                if (m_cues[index].End > timeMs) m_active.Add(index);
            }
            for (int index = m_active.Count - 1; index >= 0; index--)
            {
                if (m_builder.Length > 0) m_builder.Append('\n');
                m_builder.Append(m_cues[m_active[index]].Text);
            }
            m_cachedText = m_builder.ToString();
            return m_cachedText;
        }

        private int FindBoundary(long timeMs)
        {
            int lower = 0;
            int upper = m_boundaries.Length;
            while (lower < upper)
            {
                int middle = lower + (upper - lower) / 2;
                if (m_boundaries[middle] <= timeMs) lower = middle + 1;
                else upper = middle;
            }
            return lower - 1;
        }

        private static long ParseTime(string value)
        {
            string[] tokens = value.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) throw new FormatException("SRT 时间不能为空。");
            string token = tokens[0];
            string[] parts = token.Replace('.', ',').Split(':', ',');
            if (parts.Length != 4 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long hours) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) ||
                !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int milliseconds) ||
                minutes > 59 || seconds > 59 || milliseconds > 999 || parts[3].Length != 3)
                throw new FormatException("SRT 时间格式无效：" + value);
            try { return checked(hours * 3600000 + minutes * 60000L + seconds * 1000L + milliseconds); }
            catch (OverflowException exception) { throw new FormatException("SRT 时间超出范围。", exception); }
        }

        private static int CompareCues(Cue left, Cue right)
        {
            int comparison = left.Start.CompareTo(right.Start);
            return comparison != 0 ? comparison : left.Order.CompareTo(right.Order);
        }

        private readonly struct Cue
        {
            internal readonly long Start;
            internal readonly long End;
            internal readonly string Text;
            internal readonly int Order;

            internal Cue(long start, long end, string text, int order)
            {
                Start = start;
                End = end;
                Text = text;
                Order = order;
            }
        }
    }
}
