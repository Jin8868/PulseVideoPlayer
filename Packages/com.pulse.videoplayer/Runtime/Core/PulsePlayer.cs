using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PulseVideoPlayer
{
    /// <summary>播放器当前的托管状态。</summary>
    public enum EPulsePlayerState
    {
        Closed,
        Opening,
        Ready,
        Faulted,
        Disposed,
        Playing,
        Paused,
        Ended
    }

    /// <summary>由游戏时钟驱动的跨平台视频播放器，负责状态、解码会话和 Unity 纹理。</summary>
    public sealed class PulsePlayer : IDisposable, IPulseClock, IPulsePlayerEvents
    {
        private const double OPENTIMEOUTSECONDS = 20; // 设备初始化与首帧提交的等待上限。
        private const long TICKSPERSECOND = 10000000; // 原生时间戳采用的每秒 tick 数。
        private readonly int m_ownerThreadID; // 限定 Unity 对象操作发生在创建线程。
        private readonly List<ulong> m_retiringHandles = new List<ulong>(); // 已解除绑定但尚未回收的会话。
        private ulong m_nativeHandle; // 当前原生会话的单调标识。
        private int m_generation; // 打开和关闭请求的代次，阻止旧结果重新绑定。
        private CancellationTokenSource m_openCancellation; // 当前打开操作的取消源。
        private UniTaskCompletionSource m_openFinished; // 打开操作收尾完成信号。
        private Texture2D m_texture; // 主线程包装的外部视频纹理。
        private PulseMetadata m_metadata; // 当前已就绪视频的元数据。
        private long m_firstFrameTimestamp; // 首帧原始 PTS，单位为一千万分之一秒。
        private EPulsePlayerState m_state; // 对外可读的主线程状态。
        private Exception m_lastError; // 最近一次打开失败的具体原因。
        private bool m_isDisposed; // 对象终态标记。
        private double m_playTimeSeconds; // 暂停、结束或当前锚点之前累计的播放时间。
        private double m_clockAnchor; // 最近一次开始或恢复时的 Unity 实时时钟。
        private long m_presentedFrame = -1; // 最近完成 GPU 提交的帧序号。
        private long m_presentedTimestamp; // 最近完成 GPU 提交的帧相对时间戳。
        private long m_textureVersion; // 最近完成 GPU 提交的纹理版本。
        private long m_droppedFrames; // 原生后端为追赶时钟丢弃的帧数量。
        private bool m_isSeeking; // 目标帧尚未通过 GPU 围栏，暂时冻结时钟。
        private ulong m_seekRequest; // 当前跳转的原生代次，防止旧结果覆盖新请求。
        private double m_seekDeadline; // 定位和目标帧解码的等待上限。

        /// <summary>在 Unity 主线程创建播放器。</summary>
        public PulsePlayer()
        {
            m_ownerThreadID = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>当前播放器状态。</summary>
        public EPulsePlayerState State => m_state;
        /// <summary>首帧提交完成后可用于 RawImage 的纹理。</summary>
        public Texture2D Texture => m_texture;
        /// <summary>当前已打开视频的元数据。</summary>
        public PulseMetadata Metadata => m_metadata;
        /// <summary>首帧原始 PTS，单位为一千万分之一秒；关闭后归零，不能直接作为播放进度。</summary>
        public long FirstFrameTimestamp => m_firstFrameTimestamp;
        /// <summary>最近一次打开错误。</summary>
        public Exception LastError => m_lastError;
        /// <summary>当前视频总时长，单位为秒；未打开时为零。</summary>
        public double DurationSeconds => m_metadata?.DurationSeconds ?? 0;
        /// <summary>最近完成 GPU 提交的帧序号；未准备好时为负一。</summary>
        public long PresentedFrame => m_presentedFrame;
        /// <summary>最近完成 GPU 提交的帧相对时间，单位为毫秒。</summary>
        public double PresentedTimeMs => m_presentedTimestamp / 10000d;
        /// <summary>每次 GPU 帧写入完成后递增的版本，关闭后归零。</summary>
        public long TextureVersion => m_textureVersion;
        /// <summary>为追赶播放时钟而丢弃的过时帧数量，关闭后归零。</summary>
        public long DroppedFrames => m_droppedFrames;
        /// <summary>是否正在定位并等待目标帧显示。</summary>
        public bool IsSeeking => m_isSeeking;
        /// <summary>从已准备状态开始播放时在 Unity 主线程触发。</summary>
        public event Action Started;
        /// <summary>暂停并冻结播放时钟时在 Unity 主线程触发。</summary>
        public event Action Paused;
        /// <summary>恢复播放时在 Unity 主线程触发。</summary>
        public event Action Resumed;
        /// <summary>解除纹理并请求关闭时在 Unity 主线程触发。</summary>
        public event Action Stopped;
        /// <summary>末帧提交且时钟到达结尾时在 Unity 主线程触发。</summary>
        public event Action Completed;
        /// <summary>运行期间发生原生错误时在 Unity 主线程触发。</summary>
        public event Action<Exception> Failed;

        /// <summary>打开 Pulse 视频，等待解码和 GPU 首帧转换完成。</summary>
        /// <param name="filePath">本地真实路径，或 Android APK 内未压缩资源的 jar 地址。</param>
        /// <param name="cancellationToken">取消打开的令牌。</param>
        /// <returns>首帧已经可供显示的完成任务。</returns>
        /// <exception cref="InvalidOperationException">对象线程、状态或原生会话无效。</exception>
        /// <exception cref="ObjectDisposedException">播放器已释放。</exception>
        /// <exception cref="TimeoutException">原生设备或首帧未在时限内就绪。</exception>
        /// <exception cref="OperationCanceledException">打开被取消或被关闭请求取代。</exception>
        public async UniTask OpenAsync(string filePath, CancellationToken cancellationToken = default)
        {
            EnsureUsable();
            PruneRetiringHandles();
            if (m_state == EPulsePlayerState.Opening || m_nativeHandle != 0)
            {
                throw new InvalidOperationException("请先关闭当前会话再打开新文件。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            int generation = ++m_generation;
            var openCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var finished = new UniTaskCompletionSource();
            m_openCancellation = openCancellation;
            m_openFinished = finished;
            m_lastError = null;
            m_state = EPulsePlayerState.Opening;
            try
            {
                PulseMetadata metadata = await PulseMetadataLoader.ReadAsync(filePath, openCancellation.Token);
                EnsureRequestCurrent(generation, openCancellation.Token);
                // 原生视频处理器使用 UINT 帧率；播放时间不能超出 100ns 时间戳的整数范围。
                if (metadata.FrameRate.Numerator > uint.MaxValue || metadata.FrameRate.Denominator > uint.MaxValue ||
                    double.IsInfinity(metadata.DurationSeconds) ||
                    metadata.DurationSeconds >= long.MaxValue / (double)TICKSPERSECOND)
                {
                    throw new InvalidOperationException("视频帧率或时长超出当前原生后端支持范围。");
                }
                PulseNativePump.EnsureCreated();
                double deadline = Time.realtimeSinceStartupAsDouble + OPENTIMEOUTSECONDS;
                while (PulseNative.IsDeviceReady() == 0)
                {
                    EnsureRequestCurrent(generation, openCancellation.Token);
                    if (Time.realtimeSinceStartupAsDouble > deadline) throw new TimeoutException("Pulse 图形设备未就绪。");
                    await UniTask.NextFrame(openCancellation.Token);
                }
                EnsureRequestCurrent(generation, openCancellation.Token);
                int matrix = metadata.ColorMatrix == "bt709" ? 1 : 0;
                int result = PulseNative.CreateSession(
                    filePath, metadata.EncodedWidth, metadata.EncodedHeight, matrix,
                    metadata.FrameCount, (uint)metadata.FrameRate.Numerator,
                    (uint)metadata.FrameRate.Denominator, out ulong handle);
                if (result < 0) throw new InvalidOperationException($"创建原生会话失败：0x{result:X8}");
                m_nativeHandle = handle;
                while (true)
                {
                    EnsureRequestCurrent(generation, openCancellation.Token);
                    if (PulseNative.GetSnapshot(handle, out PulseNativeSnapshot snapshot) == 0)
                    {
                        throw new InvalidOperationException("原生会话在首帧就绪前消失。");
                    }
                    if (snapshot.State == EPulseNativeState.Faulted)
                    {
                        throw new InvalidOperationException(
                            $"Pulse 原生失败：阶段 {snapshot.Stage}，错误码 0x{snapshot.Result:X8}，" +
                            $"媒体尺寸 {snapshot.Width}×{snapshot.Height}，" +
                            $"Pulse 尺寸 {metadata.EncodedWidth}×{metadata.EncodedHeight}");
                    }
                    if (snapshot.State == EPulseNativeState.Ready)
                    {
                        if (snapshot.Texture == IntPtr.Zero)
                        {
                            throw new InvalidOperationException("首帧纹理为空。");
                        }
                        // 像素仍为编码颜色，Shader 单独处理 RGB 的颜色空间，Alpha 不参与转换。
                        m_texture = Texture2D.CreateExternalTexture(
                            snapshot.Width, snapshot.Height, PulseNative.OutputFormat, false, true, snapshot.Texture);
                        m_texture.name = "Pulse First Frame";
                        m_texture.wrapMode = TextureWrapMode.Clamp;
                        m_texture.filterMode = FilterMode.Bilinear;
                        m_metadata = metadata;
                        // 保留源时间轴，后续连续播放使用相对起点计算时间，不要求原始 PTS 为零。
                        m_firstFrameTimestamp = snapshot.Timestamp;
                        if (PulseNative.GetPlayback(handle, out PulseNativePlayback playback) == 0)
                        {
                            throw new InvalidOperationException("首帧就绪后无法读取播放快照。");
                        }
                        ApplyPlaybackSnapshot(playback);
                        m_playTimeSeconds = 0;
                        m_state = EPulsePlayerState.Ready;
                        PulseNativePump.Register(this);
                        return;
                    }
                    if (Time.realtimeSinceStartupAsDouble > deadline)
                    {
                        throw new TimeoutException($"等待首帧超时：阶段 {snapshot.Stage}，状态 {snapshot.State}");
                    }
                    await UniTask.NextFrame(openCancellation.Token);
                }
            }
            catch (OperationCanceledException)
            {
                if (generation == m_generation) Close();
                throw;
            }
            catch (Exception exception)
            {
                if (generation == m_generation)
                {
                    Close();
                    m_lastError = exception;
                    m_state = EPulsePlayerState.Faulted;
                }
                throw;
            }
            finally
            {
                if (ReferenceEquals(m_openCancellation, openCancellation)) m_openCancellation = null;
                if (ReferenceEquals(m_openFinished, finished)) m_openFinished = null;
                openCancellation.Dispose();
                finished.TrySetResult();
            }
        }

        /// <summary>从已经准备好的位置开始播放；可以先通过 Seek 调整起始位置。</summary>
        /// <exception cref="InvalidOperationException">线程、当前状态或原生会话不允许开始播放。</exception>
        /// <exception cref="ObjectDisposedException">播放器已释放。</exception>
        public void Play()
        {
            EnsureUsable();
            if (m_isSeeking) throw new InvalidOperationException("请等待视频跳转完成后开始播放。");
            if (m_state == EPulsePlayerState.Playing) return;
            if (m_state != EPulsePlayerState.Ready)
            {
                throw new InvalidOperationException("Play 需要 Ready 状态；暂停时使用 Resume，结束后重新 OpenAsync。");
            }
            SetNativeTarget(GetTargetFrame(m_playTimeSeconds), true);
            m_clockAnchor = Time.realtimeSinceStartupAsDouble;
            m_state = EPulsePlayerState.Playing;
            Notify(Started);
        }

        /// <summary>冻结播放时钟并停止提交新的视频帧，保留当前画面。</summary>
        /// <exception cref="InvalidOperationException">线程、当前状态或原生会话不允许暂停。</exception>
        /// <exception cref="ObjectDisposedException">播放器已释放。</exception>
        public void Pause()
        {
            EnsureUsable();
            if (m_state == EPulsePlayerState.Paused) return;
            if (m_state != EPulsePlayerState.Playing) throw new InvalidOperationException("Pause 需要 Playing 状态。");
            double playTime = ReadPlayTimeSeconds();
            if (!m_isSeeking) SetNativeTarget(GetTargetFrame(playTime), false);
            m_playTimeSeconds = playTime;
            m_state = EPulsePlayerState.Paused;
            Notify(Paused);
        }

        /// <summary>从暂停时保存的时间继续播放，不把暂停期间经过的实时时间计入进度。</summary>
        /// <exception cref="InvalidOperationException">线程、当前状态或原生会话不允许恢复。</exception>
        /// <exception cref="ObjectDisposedException">播放器已释放。</exception>
        public void Resume()
        {
            EnsureUsable();
            if (m_isSeeking) throw new InvalidOperationException("请等待视频跳转完成后恢复播放。");
            if (m_state == EPulsePlayerState.Playing) return;
            if (m_state != EPulsePlayerState.Paused) throw new InvalidOperationException("Resume 需要 Paused 状态。");
            SetNativeTarget(GetTargetFrame(m_playTimeSeconds), true);
            m_clockAnchor = Time.realtimeSinceStartupAsDouble;
            m_state = EPulsePlayerState.Playing;
            Notify(Resumed);
        }

        /// <summary>读取从视频起点开始的播放时间，不受 Unity timeScale 影响。</summary>
        /// <returns>播放时间，单位为毫秒；未打开或已关闭时为零。</returns>
        /// <exception cref="InvalidOperationException">调用线程与创建线程不同。</exception>
        public double GetPlayTimeMs()
        {
            EnsureOwnerThread();
            return ReadPlayTimeSeconds() * 1000;
        }

        /// <summary>停止播放并请求关闭当前会话；重播需要重新打开文件。</summary>
        /// <exception cref="InvalidOperationException">调用线程与创建线程不同。</exception>
        public void Stop()
        {
            Close();
        }

        /// <summary>请求跳转到指定时间；播放中跳转后继续播放，暂停或结束后跳转保持暂停。</summary>
        /// <param name="timeSeconds">目标秒数，有限值会限制在零至视频总时长内。</param>
        /// <exception cref="ArgumentOutOfRangeException">时间为 NaN 或无穷大。</exception>
        /// <exception cref="InvalidOperationException">播放器未就绪或原生会话拒绝定位。</exception>
        /// <exception cref="ObjectDisposedException">播放器已释放。</exception>
        public void Seek(double timeSeconds)
        {
            EnsureUsable();
            if (double.IsNaN(timeSeconds) || double.IsInfinity(timeSeconds))
                throw new ArgumentOutOfRangeException(nameof(timeSeconds), "跳转时间必须是有限数值。");
            if (m_metadata == null || m_nativeHandle == 0 ||
                (m_state != EPulsePlayerState.Ready && m_state != EPulsePlayerState.Playing &&
                m_state != EPulsePlayerState.Paused && m_state != EPulsePlayerState.Ended))
                throw new InvalidOperationException("请先打开并准备视频再跳转。");

            // 时长端点映射到末帧起点，避免定位到 EOF 而无法获得画面。
            double playTime = Math.Max(0, Math.Min(timeSeconds, DurationSeconds));
            long targetFrame = GetTargetFrame(playTime);
            decimal targetTicks = (decimal)targetFrame * TICKSPERSECOND * m_metadata.FrameRate.Denominator /
                m_metadata.FrameRate.Numerator;
            ulong request = PulseNative.Seek(m_nativeHandle, targetFrame, (long)targetTicks);
            if (request == 0) throw new InvalidOperationException("原生会话拒绝视频跳转。");
            m_playTimeSeconds = playTime;
            m_seekRequest = request;
            m_isSeeking = true;
            m_seekDeadline = Time.realtimeSinceStartupAsDouble + OPENTIMEOUTSECONDS;
            if (m_state == EPulsePlayerState.Ended) m_state = EPulsePlayerState.Paused;
        }

        /// <summary>跳转并等待目标画面通过 GPU 围栏；后续跳转或关闭会取消本次等待。</summary>
        /// <param name="timeSeconds">目标时间，单位为秒。</param>
        /// <param name="cancellationToken">只取消等待，不撤销已提交的原生定位请求。</param>
        /// <returns>目标画面已就绪的任务。</returns>
        /// <exception cref="ArgumentOutOfRangeException">时间为 NaN 或无穷大。</exception>
        /// <exception cref="InvalidOperationException">播放器未就绪或定位失败。</exception>
        /// <exception cref="TimeoutException">目标帧未在时限内完成解码或提交。</exception>
        /// <exception cref="OperationCanceledException">等待被取消、关闭或更新的定位请求替代。</exception>
        /// <exception cref="ObjectDisposedException">播放器已释放。</exception>
        public async UniTask SeekAsync(double timeSeconds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Seek(timeSeconds);
            int generation = m_generation;
            ulong request = m_seekRequest;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != m_generation || request != m_seekRequest)
                {
                    if (m_state == EPulsePlayerState.Faulted && m_lastError != null) throw m_lastError;
                    throw new OperationCanceledException(cancellationToken);
                }
                if (!m_isSeeking) return;
                await UniTask.NextFrame(cancellationToken);
            }
        }

        internal void UpdatePlayback()
        {
            if (m_nativeHandle == 0 || m_isDisposed) return;
            try
            {
                PruneRetiringHandles();
                if (PulseNative.GetSnapshot(m_nativeHandle, out PulseNativeSnapshot session) == 0)
                {
                    throw new InvalidOperationException("播放期间原生会话消失。");
                }
                if (session.State == EPulseNativeState.Faulted)
                {
                    throw new InvalidOperationException(
                        $"Pulse 播放失败：阶段 {session.Stage}，错误码 0x{session.Result:X8}，" +
                        $"媒体尺寸 {session.Width}×{session.Height}");
                }
                if (PulseNative.GetPlayback(m_nativeHandle, out PulseNativePlayback playback) == 0)
                {
                    throw new InvalidOperationException("无法读取当前原生播放快照。");
                }
                ApplyPlaybackSnapshot(playback);
                if (m_isSeeking)
                {
                    if (playback.CompletedSeek != m_seekRequest)
                    {
                        if (Time.realtimeSinceStartupAsDouble > m_seekDeadline)
                            throw new TimeoutException("等待视频跳转目标帧超时。");
                        return;
                    }
                    // 仅在最新请求的目标画面完成提交后重新锚定时钟。
                    m_isSeeking = false;
                    m_clockAnchor = Time.realtimeSinceStartupAsDouble;
                    SetNativeTarget(GetTargetFrame(m_playTimeSeconds), m_state == EPulsePlayerState.Playing);
                }
                if (m_state != EPulsePlayerState.Playing) return;
                double playTime = ReadPlayTimeSeconds();
                long targetFrame = GetTargetFrame(playTime);
                SetNativeTarget(targetFrame, true);

                // 不能仅凭时钟到末尾宣告结束，还必须确认最后一帧已提交且完整读取已结束。
                if (playTime >= DurationSeconds && playback.EndOfStream != 0 && playback.InFlight == 0 &&
                    playback.FrameIndex == m_metadata.FrameCount - 1)
                {
                    SetNativeTarget(targetFrame, false);
                    m_playTimeSeconds = DurationSeconds;
                    m_state = EPulsePlayerState.Ended;
                    Notify(Completed);
                }
            }
            catch (Exception exception)
            {
                int failedGeneration = m_generation;
                Close();
                // Stopped 订阅者可能已重开或释放；旧会话的错误不能覆盖新请求的状态。
                if (m_generation == failedGeneration + 1 && !m_isDisposed)
                {
                    m_lastError = exception;
                    m_state = EPulsePlayerState.Faulted;
                }
                try
                {
                    if (Failed != null) Failed.Invoke(exception);
                    else Debug.LogException(exception);
                }
                catch (Exception listenerException)
                {
                    Debug.LogException(listenerException);
                }
            }
        }

        /// <summary>解除当前纹理并请求异步关闭，不等待解码线程或 GPU。</summary>
        /// <exception cref="InvalidOperationException">调用线程与创建线程不同。</exception>
        public void Close()
        {
            EnsureOwnerThread();
            if (m_isDisposed) return;
            bool wasActive = m_state != EPulsePlayerState.Closed;
            PulseNativePump.Unregister(this);
            m_generation++;
            m_openCancellation?.Cancel();
            ulong handle = m_nativeHandle;
            m_nativeHandle = 0;
            Texture2D texture = m_texture;
            m_texture = null;
            m_metadata = null;
            m_firstFrameTimestamp = 0;
            m_playTimeSeconds = 0;
            m_clockAnchor = 0;
            m_presentedFrame = -1;
            m_presentedTimestamp = 0;
            m_textureVersion = 0;
            m_droppedFrames = 0;
            m_isSeeking = false;
            m_seekRequest = 0;
            m_seekDeadline = 0;
            m_state = EPulsePlayerState.Closed;
            // 使用者应先解除 RawImage 绑定；原生帧末围栏覆盖此前的纹理采样。
            if (texture != null) UnityEngine.Object.Destroy(texture);
            if (handle != 0)
            {
                PulseNative.CloseSession(handle);
                m_retiringHandles.Add(handle);
            }
            if (wasActive) Notify(Stopped);
        }

        /// <summary>关闭当前会话，并等待本次打开操作和原生资源回收结束。</summary>
        /// <returns>关闭和 GPU 回收均完成的任务。</returns>
        /// <exception cref="InvalidOperationException">调用线程与创建线程不同。</exception>
        public async UniTask CloseAsync()
        {
            EnsureOwnerThread();
            UniTaskCompletionSource finished = m_openFinished;
            Close();
            ulong[] handles = m_retiringHandles.ToArray();
            if (finished != null) await finished.Task;
            foreach (ulong handle in handles)
            {
                while (PulseNative.GetSnapshot(handle, out _) != 0) await UniTask.NextFrame();
                m_retiringHandles.Remove(handle);
            }
        }

        /// <summary>请求关闭并进入终态；原生退役由独立帧末驱动器继续处理。</summary>
        /// <exception cref="InvalidOperationException">调用线程与创建线程不同。</exception>
        public void Dispose()
        {
            EnsureOwnerThread();
            if (m_isDisposed) return;
            Close();
            m_isDisposed = true;
            m_state = EPulsePlayerState.Disposed;
        }

        private void EnsureUsable()
        {
            EnsureOwnerThread();
            if (m_isDisposed) throw new ObjectDisposedException(nameof(PulsePlayer));
        }

        private double ReadPlayTimeSeconds()
        {
            if (m_metadata == null) return 0;
            double playTime = m_playTimeSeconds;
            if (m_state == EPulsePlayerState.Playing && !m_isSeeking)
            {
                playTime += Math.Max(0, Time.realtimeSinceStartupAsDouble - m_clockAnchor);
            }
            return Math.Min(m_metadata.DurationSeconds, playTime);
        }

        private long GetTargetFrame(double playTime)
        {
            // 使用整数 tick 与有理帧率换算，避免每帧浮点累计误差。
            long ticks = (long)(playTime * TICKSPERSECOND);
            decimal frame = (decimal)ticks * m_metadata.FrameRate.Numerator /
                ((decimal)TICKSPERSECOND * m_metadata.FrameRate.Denominator);
            if (frame >= m_metadata.FrameCount - 1) return m_metadata.FrameCount - 1;
            return (long)frame;
        }

        private void SetNativeTarget(long targetFrame, bool playing)
        {
            if (PulseNative.SetTargetFrame(m_nativeHandle, targetFrame, playing ? 1 : 0) == 0)
            {
                if (PulseNative.GetSnapshot(m_nativeHandle, out PulseNativeSnapshot snapshot) != 0 &&
                    snapshot.State == EPulseNativeState.Faulted)
                {
                    throw new InvalidOperationException(
                        $"Pulse 原生失败：阶段 {snapshot.Stage}，错误码 0x{snapshot.Result:X8}");
                }
                throw new InvalidOperationException("原生会话拒绝更新播放目标。");
            }
        }

        private void ApplyPlaybackSnapshot(PulseNativePlayback playback)
        {
            m_presentedFrame = playback.FrameIndex;
            m_presentedTimestamp = playback.Timestamp;
            m_textureVersion = playback.TextureVersion;
            m_droppedFrames = playback.DroppedFrames;
        }

        private void PruneRetiringHandles()
        {
            // 无需等待 GPU；只移除已经消失的旧句柄，避免长期重开累积托管列表。
            for (int index = m_retiringHandles.Count - 1; index >= 0; index--)
            {
                if (PulseNative.GetSnapshot(m_retiringHandles[index], out _) == 0)
                {
                    m_retiringHandles.RemoveAt(index);
                }
            }
        }

        private static void Notify(Action callback)
        {
            // 订阅者异常不应破坏资源回收和播放器状态迁移。
            try
            {
                callback?.Invoke();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        private void EnsureOwnerThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != m_ownerThreadID)
            {
                throw new InvalidOperationException("PulsePlayer 必须在创建它的 Unity 主线程使用。");
            }
        }

        private void EnsureRequestCurrent(int generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != m_generation || m_isDisposed) throw new OperationCanceledException(cancellationToken);
        }
    }
}
