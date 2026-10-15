using System.Threading;
using Cysharp.Threading.Tasks;

namespace PulseVideoPlayer
{
    /// <summary>本次请求的循环模式，指定次数包含第一次播放。</summary>
    public enum EPulseLoopMode
    {
        Once,
        Infinite,
        Count
    }

    /// <summary>由宿主实现资源加载和独立音轨控制，播放器包不依赖资源或音频 SDK。</summary>
    public interface IPulsePlaybackDriver
    {
        /// <summary>是否正在准备资源或协调音画跳转。</summary>
        bool IsBusy { get; }

        /// <summary>加载并播放视频与可选音轨。</summary>
        /// <param name="videoLocation">本地路径、资源地址或 HTTP/HTTPS 地址。</param>
        /// <param name="audioEventKey">宿主音频事件，空值表示无音轨。</param>
        /// <param name="loopMode">循环模式。</param>
        /// <param name="playCount">Count 模式下的总播放次数，必须大于零。</param>
        /// <param name="cancellationToken">取消准备或本次播放生命周期。</param>
        /// <returns>视频和音频已经开始播放的任务。</returns>
        UniTask PlayAsync(string videoLocation, string audioEventKey, EPulseLoopMode loopMode,
            int playCount, CancellationToken cancellationToken);

        /// <summary>暂停当前音画。</summary>
        void Pause();

        /// <summary>恢复当前音画。</summary>
        void Resume();

        /// <summary>重新播放最近的请求。</summary>
        void Replay();

        /// <summary>协调视频和音频跳转。</summary>
        /// <param name="timeSeconds">目标时间，单位为秒。</param>
        /// <param name="cancellationToken">取消本次等待。</param>
        /// <returns>跳转协调完成的任务。</returns>
        UniTask SeekAsync(double timeSeconds, CancellationToken cancellationToken);

        /// <summary>停止并等待原生读取结束后释放视频资源租约。</summary>
        /// <returns>本次请求的清理任务。</returns>
        UniTask CloseAsync();

        /// <summary>检查音频状态、同步偏差和生命周期取消。</summary>
        void Tick();

        /// <summary>处理单轮视频结束，决定是否向业务发送最终完成事件。</summary>
        /// <returns>最后一轮完成时返回 true，内部重播时返回 false。</returns>
        bool OnVideoCompleted();
    }
}
