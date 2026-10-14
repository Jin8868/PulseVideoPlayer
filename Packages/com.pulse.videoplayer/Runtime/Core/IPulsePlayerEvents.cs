using System;

namespace PulseVideoPlayer
{
    /// <summary>在 Unity 主线程通知播放生命周期，供独立音频与字幕模块订阅。</summary>
    public interface IPulsePlayerEvents
    {
        /// <summary>从已准备状态开始播放。</summary>
        event Action Started;
        /// <summary>播放时钟被冻结。</summary>
        event Action Paused;
        /// <summary>从暂停位置继续播放。</summary>
        event Action Resumed;
        /// <summary>播放器已解除展示并请求关闭。</summary>
        event Action Stopped;
        /// <summary>末帧提交完成且播放时间到达视频末尾。</summary>
        event Action Completed;
        /// <summary>运行中的原生解码或提交发生失败。</summary>
        event Action<Exception> Failed;
    }
}
