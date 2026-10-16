using System.Threading;
using Cysharp.Threading.Tasks;

namespace PulseVideoPlayer
{
    /// <summary>可选的外置字幕加载能力，已有宿主驱动无需修改原接口。</summary>
    public interface IPulseSubtitlePlaybackDriver
    {
        /// <summary>准备视频、外置 SRT 与可选音轨，并开始播放。</summary>
        /// <param name="videoLocation">视频地址。</param>
        /// <param name="audioEventKey">宿主音频事件，空值表示无音轨。</param>
        /// <param name="subtitleLocation">外置 SRT 地址。</param>
        /// <param name="loopMode">循环模式。</param>
        /// <param name="playCount">指定模式的总播放次数。</param>
        /// <param name="cancellationToken">播放生命周期令牌。</param>
        /// <returns>资源就绪并开始播放的任务。</returns>
        UniTask PlayAsync(string videoLocation,
            string audioEventKey, string subtitleLocation,
            EPulseLoopMode loopMode, int playCount, CancellationToken cancellationToken);
    }
}
