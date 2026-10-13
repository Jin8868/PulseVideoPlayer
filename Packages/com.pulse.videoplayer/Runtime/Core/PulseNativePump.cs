using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace PulseVideoPlayer
{
    /// <summary>在帧末驱动原生纹理提交和退役队列。</summary>
    public sealed class PulseNativePump : MonoBehaviour
    {
        private static PulseNativePump m_instance; // 跨场景保留的帧末驱动器。
        private readonly WaitForEndOfFrame m_endOfFrame = new WaitForEndOfFrame(); // 重用帧末等待对象。
        private IntPtr m_renderEvent; // 不会捕获托管上下文的原生回调地址。
        private CommandBuffer m_renderCommands; // 重用的原生渲染回调命令缓冲区。

        internal static void EnsureCreated()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor &&
                Application.platform != RuntimePlatform.WindowsPlayer)
            {
                throw new PlatformNotSupportedException("首版 Pulse 解码后端仅支持 Windows。");
            }
            if (IntPtr.Size != 8 || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
            {
                throw new PlatformNotSupportedException("首版 Pulse 需要 Windows x64 与 Direct3D11。");
            }
            if (m_instance != null) return;

            // 先解析 DLL 回调再创建对象，缺失 DLL 时不留下半初始化驱动器。
            IntPtr renderEvent = WindowsPulseNative.GetRenderEvent();
            if (renderEvent == IntPtr.Zero) throw new InvalidOperationException("原生渲染回调为空。");
            var root = new GameObject("Pulse Native Pump");
            root.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(root);
            m_instance = root.AddComponent<PulseNativePump>();
            m_instance.m_renderEvent = renderEvent;
            // 只记录一次固定回调，每帧重复提交，避免逐帧分配命令缓冲区。
            m_instance.m_renderCommands = new CommandBuffer();
            m_instance.m_renderCommands.name = "Pulse Native Render Events";
            m_instance.m_renderCommands.IssuePluginEventAndData(renderEvent, 1, IntPtr.Zero);
            m_instance.StartCoroutine(m_instance.RenderLoop());
        }

        private IEnumerator RenderLoop()
        {
            // 回调位于 UI 绘制之后，关闭围栏才能覆盖当帧最后一次纹理采样。
            while (true)
            {
                yield return m_endOfFrame;
                Graphics.ExecuteCommandBuffer(m_renderCommands);
            }
        }

        private void OnApplicationQuit()
        {
            if (m_renderEvent != IntPtr.Zero) WindowsPulseNative.CloseAll();
        }

        private void OnDestroy()
        {
            if (m_instance != this) return;
            m_instance = null;
            if (m_renderEvent != IntPtr.Zero) WindowsPulseNative.CloseAll();
            // 销毁驱动器时释放命令缓冲区持有的原生资源。
            m_renderCommands?.Release();
            m_renderCommands = null;
        }
    }
}
