using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PulseVideoPlayer
{
    /// <summary>由播放器内部创建，驱动主线程时钟、GPU 完成检查与帧末纹理提交。</summary>
    [AddComponentMenu("")]
    public sealed class PulseNativePump : MonoBehaviour
    {
        private const int EXPECTEDNATIVEAPIVERSION = 3; // 支持跳转完成代次的原生接口版本。
        private static PulseNativePump m_instance; // 跨场景保留的帧末驱动器。
        private readonly WaitForEndOfFrame m_endOfFrame = new WaitForEndOfFrame(); // 重用帧末等待对象。
        private IntPtr m_renderEvent; // 不会捕获托管上下文的原生回调地址。
        private CommandBuffer m_renderCommands; // 重用的原生渲染回调命令缓冲区。
        private CommandBuffer m_completionCommands; // 帧初仅处理 GPU 完成，不修改输出纹理。
        private readonly List<PulsePlayer> m_players = new List<PulsePlayer>(); // 主线程时钟驱动对象。

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

            // 先核对 ABI，避免旧 DLL 被错误传入新参数布局。
            try
            {
                if (WindowsPulseNative.GetApiVersion() != EXPECTEDNATIVEAPIVERSION)
                {
                    throw new InvalidOperationException("Pulse DLL 接口版本不匹配，请重新运行 BuildWindowsNative.ps1。");
                }
            }
            catch (EntryPointNotFoundException exception)
            {
                throw new InvalidOperationException("Pulse DLL 仍为首帧版本，请重新运行 BuildWindowsNative.ps1。", exception);
            }

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
            m_instance.m_completionCommands = new CommandBuffer();
            m_instance.m_completionCommands.name = "Pulse Native GPU Completion";
            m_instance.m_completionCommands.IssuePluginEventAndData(renderEvent, 2, IntPtr.Zero);
            m_instance.StartCoroutine(m_instance.RenderLoop());
        }

        internal static void Register(PulsePlayer player)
        {
            if (!m_instance.m_players.Contains(player)) m_instance.m_players.Add(player);
        }

        internal static void Unregister(PulsePlayer player)
        {
            if (m_instance != null) m_instance.m_players.Remove(player);
        }

        private void Update()
        {
            // 脚本重载或未通过工厂初始化的组件不能提交空的渲染命令。
            if (m_completionCommands == null) return;
            // 回调可能关闭当前播放器，检查当前位置是否仍是同一对象再递增索引。
            int index = 0;
            while (index < m_players.Count)
            {
                PulsePlayer player = m_players[index];
                player.UpdatePlayback();
                if (index < m_players.Count && ReferenceEquals(m_players[index], player)) index++;
            }
            // 提前归还上一帧 GPU 已使用完的租约，使工作线程在帧末之前准备下一帧。
            Graphics.ExecuteCommandBuffer(m_completionCommands);
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
            m_completionCommands?.Release();
            m_completionCommands = null;
            m_players.Clear();
        }
    }
}
