namespace PulseVideoPlayer
{
    /// <summary>供音频、字幕与业务读取的视频播放时钟，不依赖纹理或平台解码器。</summary>
    public interface IPulseClock
    {
        /// <summary>读取从视频起点开始的播放时间，暂停时保持不变。</summary>
        /// <returns>播放时间，单位为毫秒；未打开或已关闭时为零。</returns>
        double GetPlayTimeMs();
    }
}
