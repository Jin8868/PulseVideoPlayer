using System;
using UnityEngine;

namespace PulseVideoPlayer
{
    /// <summary>已登记原生会话持有的资源统计，不代表驱动实际内存或全部解码缓存。</summary>
    public readonly struct PulseResourceSnapshot
    {
        /// <summary>已登记且尚未从退役队列移除的会话数量。</summary>
        public int Sessions { get; }
        /// <summary>已请求关闭但尚未完成退役的会话数量。</summary>
        public int ClosingSessions { get; }
        /// <summary>尚未回收的解码线程句柄数量，包含已结束而等待回收的线程。</summary>
        public int WorkerHandles { get; }
        /// <summary>已登记会话持有的输出纹理数量。</summary>
        public int OutputTextures { get; }
        /// <summary>已登记会话持有的解码表面数量，不含解码器内部池。</summary>
        public int DecoderSurfaces { get; }
        /// <summary>按宽度乘高度乘四估算的输出纹理字节数，不含对齐和驱动开销。</summary>
        public long EstimatedOutputBytes { get; }

        internal PulseResourceSnapshot(PulseNativeResources resources)
        {
            Sessions = resources.Sessions;
            ClosingSessions = resources.ClosingSessions;
            WorkerHandles = resources.WorkerHandles;
            OutputTextures = resources.OutputTextures;
            DecoderSurfaces = resources.DecoderSurfaces;
            EstimatedOutputBytes = resources.EstimatedOutputBytes;
        }
    }

    /// <summary>提供播放器原生资源的手动验证入口。</summary>
    public static class PulseDiagnostics
    {
        /// <summary>读取当前 Windows 原生后端的全局资源引用统计。</summary>
        /// <returns>采样时已登记会话持有的资源数量及输出纹理字节估算。</returns>
        /// <exception cref="PlatformNotSupportedException">当前不是 Windows x64 平台。</exception>
        /// <exception cref="DllNotFoundException">原生 DLL 未安装。</exception>
        /// <exception cref="EntryPointNotFoundException">DLL 尚未更新到包含诊断入口的版本。</exception>
        /// <exception cref="InvalidOperationException">原生统计读取失败。</exception>
        public static PulseResourceSnapshot ReadResources()
        {
            // 统计不创建播放器和驱动器，也不主动触发解码。
            if (IntPtr.Size != 8 || (Application.platform != RuntimePlatform.WindowsEditor &&
                Application.platform != RuntimePlatform.WindowsPlayer))
            {
                throw new PlatformNotSupportedException("当前资源诊断仅支持 Windows x64。");
            }
            if (WindowsPulseNative.GetResources(out PulseNativeResources resources) == 0)
            {
                throw new InvalidOperationException("无法读取 Pulse 原生资源统计。");
            }
            return new PulseResourceSnapshot(resources);
        }
    }
}
