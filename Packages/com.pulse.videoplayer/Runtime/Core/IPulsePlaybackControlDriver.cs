using System.Threading;
using Cysharp.Threading.Tasks;

namespace PulseVideoPlayer
{
    /// <summary>宿主可选的起点播放与可等待重播能力，不改变原有播放驱动接口。</summary>
    public interface IPulsePlaybackControlDriver
    {
        /// <summary>先准备并定位音画，再从指定时间开始播放。</summary>
        /// <param name="videoLocation">视频资源地址。</param>
        /// <param name="audioEventKey">音频事件，空值表示无音轨。</param>
        /// <param name="subtitleLocation">字幕地址，空值表示无字幕。</param>
        /// <param name="startTimeSeconds">首次播放起点秒数，后续循环从零开始。</param>
        /// <param name="loopMode">循环策略。</param>
        /// <param name="playCount">指定总播放次数。</param>
        /// <param name="cancellationToken">播放生命周期令牌。</param>
        /// <returns>目标画面与音轨准备完成并开始播放的任务。</returns>
        UniTask PlayAsync(string videoLocation, string audioEventKey, string subtitleLocation,
            double startTimeSeconds, EPulseLoopMode loopMode, int playCount, CancellationToken cancellationToken);

        /// <summary>保留最近一次资源和循环配置，从零开始一轮新的播放请求。</summary>
        /// <param name="cancellationToken">新播放请求的生命周期令牌。</param>
        /// <returns>重新开始播放的任务。</returns>
        UniTask ReplayAsync(CancellationToken cancellationToken);
    }
}
