using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace PulseVideoPlayer
{
    internal static class PulseNative
    {
        private const string LIBRARYNAME = "PulseVideoNative"; // 各平台使用相同的原生库名称。
        private static bool m_isInitialized; // 主线程完成 Java 桥接准备后不重复创建 JNI 包装。

        internal static bool IsSupported => IntPtr.Size == 8 &&
            (Application.platform == RuntimePlatform.WindowsEditor ||
             Application.platform == RuntimePlatform.WindowsPlayer ||
             Application.platform == RuntimePlatform.Android);

        internal static TextureFormat OutputFormat => Application.platform == RuntimePlatform.Android
            ? TextureFormat.RGBA32
            : TextureFormat.BGRA32;

        internal static void Initialize()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_isInitialized) return;
            // 先经 Unity 的插件加载器触发 UnityPluginLoad，再由 Java 取得同一个原生库。
            GetApiVersion();
            // 在主线程加载 Java 类并缓存全局引用，渲染线程不依赖 Unity 的类加载器。
            using (var bridge = new AndroidJavaClass("com.pulse.videoplayer.PulseSurfaceBridge"))
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (AndroidJavaObject assets = activity.Call<AndroidJavaObject>("getAssets"))
            {
                bridge.CallStatic("Initialize", assets);
            }
            m_isInitialized = true;
#endif
        }

        internal static int CreateSession(
            string filePath,
            int width,
            int height,
            int matrix,
            long frames,
            uint numerator,
            uint denominator,
            out ulong handle)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Android 文件路径采用 UTF-8，Windows 接口继续保留原有 UTF-16 ABI。
            byte[] pathBytes = Encoding.UTF8.GetBytes(NormalizeAndroidPath(filePath) + '\0');
            return CreateAndroidSession(pathBytes, width, height, matrix, frames, numerator, denominator, out handle);
#else
            return WindowsPulseNative.CreateSession(
                filePath, width, height, matrix, frames, numerator, denominator, out handle);
#endif
        }

        internal static string NormalizeAndroidPath(string filePath)
        {
            return filePath.StartsWith("jar:", StringComparison.OrdinalIgnoreCase)
                ? "jar:" + Uri.UnescapeDataString(filePath.Substring(4))
                : filePath;
        }

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_CreateSession", CallingConvention = CallingConvention.Cdecl)]
        private static extern int CreateAndroidSession(
            byte[] filePath, int width, int height, int matrix,
            long frames, uint numerator, uint denominator, out ulong handle);

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_GetRenderEvent", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr GetRenderEvent();

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_IsDeviceReady", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int IsDeviceReady();

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_GetApiVersion", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetApiVersion();

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_GetSnapshot", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetSnapshot(ulong handle, out PulseNativeSnapshot snapshot);

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_GetPlayback", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetPlayback(ulong handle, out PulseNativePlayback playback);

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_SetTargetFrame", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetTargetFrame(ulong handle, long frameIndex, int playing);

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_Seek", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong Seek(ulong handle, long frameIndex, long relativeTimestamp);

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_CloseSession", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void CloseSession(ulong handle);

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_CloseAll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void CloseAll();

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_GetResources", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetResources(out PulseNativeResources resources);
    }
}
