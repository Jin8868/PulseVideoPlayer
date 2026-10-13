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
        Closed, Opening, Ready, Faulted, Disposed
    }

    /// <summary>Windows 原生首帧播放器门面，负责会话与 Unity 纹理的生命周期。</summary>
    public sealed class PulsePlayer : IDisposable
    {
        private const double OPENTIMEOUTSECONDS = 20; // 设备初始化与首帧提交的等待上限。
        private readonly int m_ownerThreadID; // 限定 Unity 对象操作发生在创建线程。
        private readonly List<ulong> m_retiringHandles = new List<ulong>(); // 已解除绑定但尚未回收的会话。
        private ulong m_nativeHandle; // 当前原生会话的单调标识。
        private int m_generation; // 打开和关闭请求的代次，阻止旧结果重新绑定。
        private CancellationTokenSource m_openCancellation; // 当前打开操作的取消源。
        private UniTaskCompletionSource m_openFinished; // 打开操作收尾完成信号。
        private Texture2D m_texture; // 主线程包装的外部 BGRA 纹理。
        private PulseMetadata m_metadata; // 当前已就绪视频的元数据。
        private long m_firstFrameTimestamp; // 首帧原始 PTS，单位为一千万分之一秒。
        private EPulsePlayerState m_state; // 对外可读的主线程状态。
        private Exception m_lastError; // 最近一次打开失败的具体原因。
        private bool m_isDisposed; // 对象终态标记。

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

        /// <summary>打开本地 Pulse 视频，等待解码和 GPU 首帧转换完成。</summary>
        /// <param name="filePath">本地 Pulse 文件的真实路径。</param>
        /// <param name="cancellationToken">取消打开的令牌。</param>
        /// <returns>首帧已经可供显示的完成任务。</returns>
        /// <exception cref="InvalidOperationException">对象线程、状态或原生会话无效。</exception>
        /// <exception cref="ObjectDisposedException">播放器已释放。</exception>
        /// <exception cref="TimeoutException">原生设备或首帧未在时限内就绪。</exception>
        /// <exception cref="OperationCanceledException">打开被取消或被关闭请求取代。</exception>
        public async UniTask OpenAsync(string filePath, CancellationToken cancellationToken = default)
        {
            EnsureUsable();
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
                PulseNativePump.EnsureCreated();
                double deadline = Time.realtimeSinceStartupAsDouble + OPENTIMEOUTSECONDS;
                while (WindowsPulseNative.IsDeviceReady() == 0)
                {
                    EnsureRequestCurrent(generation, openCancellation.Token);
                    if (Time.realtimeSinceStartupAsDouble > deadline) throw new TimeoutException("D3D11 设备未就绪。");
                    await UniTask.NextFrame(openCancellation.Token);
                }
                EnsureRequestCurrent(generation, openCancellation.Token);
                int matrix = metadata.ColorMatrix == "bt709" ? 1 : 0;
                int result = WindowsPulseNative.CreateSession(
                    filePath, metadata.EncodedWidth, metadata.EncodedHeight, matrix, out ulong handle);
                if (result < 0) throw new InvalidOperationException($"创建原生会话失败：0x{result:X8}");
                m_nativeHandle = handle;
                while (true)
                {
                    EnsureRequestCurrent(generation, openCancellation.Token);
                    if (WindowsPulseNative.GetSnapshot(handle, out PulseNativeSnapshot snapshot) == 0)
                    {
                        throw new InvalidOperationException("原生会话在首帧就绪前消失。");
                    }
                    if (snapshot.State == EPulseNativeState.Faulted)
                    {
                        throw new InvalidOperationException(
                            $"Pulse 原生失败：阶段 {snapshot.Stage}，HRESULT 0x{snapshot.Result:X8}，" +
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
                            snapshot.Width, snapshot.Height, TextureFormat.BGRA32, false, true, snapshot.Texture);
                        m_texture.name = "Pulse First Frame";
                        m_texture.wrapMode = TextureWrapMode.Clamp;
                        m_texture.filterMode = FilterMode.Bilinear;
                        m_metadata = metadata;
                        // 保留源时间轴，后续连续播放使用相对起点计算时间，不要求原始 PTS 为零。
                        m_firstFrameTimestamp = snapshot.Timestamp;
                        m_state = EPulsePlayerState.Ready;
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

        /// <summary>解除当前纹理并请求异步关闭，不等待解码线程或 GPU。</summary>
        /// <exception cref="InvalidOperationException">调用线程与创建线程不同。</exception>
        public void Close()
        {
            EnsureOwnerThread();
            if (m_isDisposed) return;
            m_generation++;
            m_openCancellation?.Cancel();
            ulong handle = m_nativeHandle;
            m_nativeHandle = 0;
            Texture2D texture = m_texture;
            m_texture = null;
            m_metadata = null;
            m_firstFrameTimestamp = 0;
            m_state = EPulsePlayerState.Closed;
            // 使用者应先解除 RawImage 绑定；原生帧末围栏覆盖此前的纹理采样。
            if (texture != null) UnityEngine.Object.Destroy(texture);
            if (handle != 0)
            {
                WindowsPulseNative.CloseSession(handle);
                m_retiringHandles.Add(handle);
            }
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
                while (WindowsPulseNative.GetSnapshot(handle, out _) != 0) await UniTask.NextFrame();
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
