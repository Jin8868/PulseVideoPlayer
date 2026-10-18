using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace PulseVideoPlayer
{
    /// <summary>在独立读取线程完成文件元数据加载，随后回到 Unity 主线程。</summary>
    public static class PulseMetadataLoader
    {
        /// <summary>异步读取 Pulse 文件或 Android APK 内置视频，结果在 Unity 主线程交付。</summary>
        /// <param name="filePath">真实文件路径，或 Android 未压缩资源的 jar 地址。</param>
        /// <param name="cancellationToken">用于取消读取的令牌。</param>
        /// <returns>完成结构校验的视频元数据。</returns>
        /// <exception cref="ArgumentException">文件路径为空。</exception>
        /// <exception cref="System.IO.InvalidDataException">容器或元数据无效。</exception>
        /// <exception cref="OperationCanceledException">读取已取消。</exception>
        public static async UniTask<PulseMetadata> ReadAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            // 先回主线程，使参数校验和线程启动失败也遵循统一的交付线程约定。
            await UniTask.SwitchToMainThread();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("文件路径不能为空。", nameof(filePath));
            }
            cancellationToken.ThrowIfCancellationRequested();

#if UNITY_ANDROID && !UNITY_EDITOR
            if (filePath.StartsWith("jar:", StringComparison.OrdinalIgnoreCase))
            {
                // Java 类和 AssetManager 在主线程准备，后续按需读取不调用 Unity API。
                PulseNative.Initialize();
            }
#endif

            // 使用显式操作对象承载上下文，不创建捕获参数的运行时闭包。
            var operation = new PulseMetadataReadOperation(filePath, cancellationToken);
            operation.Start();
            try
            {
                PulseMetadata metadata = await operation.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return metadata;
            }
            finally
            {
                // 工作线程完成源不会自动切回 PlayerLoop，必须显式切换后再交付调用者。
                await UniTask.SwitchToMainThread();
            }
        }
    }

    internal sealed class PulseMetadataReadOperation
    {
        private readonly string m_filePath; // 本次操作的文件路径。
        private readonly CancellationToken m_cancellationToken; // 本次操作的取消令牌。
        private readonly UniTaskCompletionSource<PulseMetadata> m_completionSource; // 跨线程完成信号。
        private readonly Thread m_thread; // 元数据读取线程，不访问 Unity 对象。

        internal PulseMetadataReadOperation(string filePath, CancellationToken cancellationToken)
        {
            m_filePath = filePath;
            m_cancellationToken = cancellationToken;
            m_completionSource = new UniTaskCompletionSource<PulseMetadata>();
            m_thread = new Thread(Execute)
            {
                IsBackground = true,
                Name = "Pulse metadata reader"
            };
        }

        internal UniTask<PulseMetadata> Task => m_completionSource.Task;

        internal void Start()
        {
            m_thread.Start();
        }

        private void Execute()
        {
            // 同步读取器返回前已关闭文件句柄，完成信号不携带未释放的文件资源。
            try
            {
                PulseMetadata metadata = PulseMetadataReader.Read(m_filePath, m_cancellationToken);
                m_cancellationToken.ThrowIfCancellationRequested();
                m_completionSource.TrySetResult(metadata);
            }
            catch (OperationCanceledException) when (m_cancellationToken.IsCancellationRequested)
            {
                m_completionSource.TrySetCanceled(m_cancellationToken);
            }
            catch (Exception exception)
            {
                m_completionSource.TrySetException(exception);
            }
        }
    }
}
