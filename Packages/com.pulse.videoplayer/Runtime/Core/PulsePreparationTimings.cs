using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PulseVideoPlayer
{
    /// <summary>按实际经过时间记录串行准备阶段，不自行输出日志；由宿主汇总打印。</summary>
    public sealed class PulsePreparationTimings
    {
        private readonly Stopwatch m_clock = Stopwatch.StartNew(); // 单调时钟，包含异步等待时间。
        private readonly StringBuilder m_stages = new StringBuilder(); // 已结束阶段的耗时。
        private readonly StringBuilder m_details = new StringBuilder(); // 嵌套阶段和请求上下文。
        private string m_currentStage; // 当前尚未结束的阶段。
        private double m_stageStartedAt; // 当前阶段开始时的累计毫秒数。
        private string m_summary; // 完成后缓存结果，避免多次结束同一阶段。

        /// <summary>结束上一阶段并开始下一阶段，仅用于同一请求的串行操作。</summary>
        /// <param name="name">本阶段的可读名称。</param>
        public void BeginStage(string name)
        {
            if (m_summary != null) return;
            double elapsed = m_clock.Elapsed.TotalMilliseconds;
            AppendStage(elapsed);
            m_currentStage = name;
            m_stageStartedAt = elapsed;
        }

        /// <summary>附加请求上下文或子操作统计，不改变当前阶段的计时边界。</summary>
        /// <param name="name">明细名称。</param>
        /// <param name="value">明细内容；空内容跳过。</param>
        public void AddDetail(string name, string value)
        {
            if (m_summary != null || string.IsNullOrEmpty(value)) return;
            // 保持单行输出，便于从 Android 日志一次复制完整结果。
            m_details.Append(" | ").Append(name).Append("=[")
                .Append(value.Replace('\r', ' ').Replace('\n', ' ')).Append(']');
        }

        /// <summary>结束计时并生成可重复读取的单行汇总，子操作合计不重复计入外层总计。</summary>
        /// <returns>包含总耗时、串行阶段耗时和嵌套明细的文本，单位为毫秒。</returns>
        public string Finish()
        {
            if (m_summary != null) return m_summary;
            m_clock.Stop();
            double elapsed = m_clock.Elapsed.TotalMilliseconds;
            AppendStage(elapsed);
            m_summary = "总计=" + elapsed.ToString("F1", CultureInfo.InvariantCulture) + "ms" +
                m_stages + m_details;
            return m_summary;
        }

        private void AppendStage(double elapsed)
        {
            if (m_currentStage == null) return;
            m_stages.Append("; ").Append(m_currentStage).Append('=')
                .Append((elapsed - m_stageStartedAt).ToString("F1", CultureInfo.InvariantCulture)).Append("ms");
        }
    }
}
