using System;
using System.Runtime.InteropServices;

namespace PulseVideoPlayer
{
    internal enum EPulseNativeState
    {
        Opening = 1, Decoded = 2, Submitted = 3, Ready = 4, Closing = 5, Faulted = 6
    }

    internal enum EPulseNativeStage
    {
        Starting, DeviceManager, OpeningFile, SelectingNV12, Decoding,
        AcquiringSurface, VideoProcessor, Converting, Fence,
        InspectingSourceType, ReadingSample, InspectingDecodedType, ValidatingDecodedSize
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PulseNativeSnapshot
    {
        internal EPulseNativeState State;
        internal int Result;
        internal EPulseNativeStage Stage;
        internal int Width;
        internal int Height;
        internal IntPtr Texture;
        internal long Timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PulseNativeResources
    {
        internal int Sessions;
        internal int ClosingSessions;
        internal int WorkerHandles;
        internal int OutputTextures;
        internal int DecoderSurfaces;
        internal long EstimatedOutputBytes;
    }

    internal static class WindowsPulseNative
    {
        private const string LIBRARYNAME = "PulseVideoNative"; // Windows x64 原生插件名称。

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_GetRenderEvent", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr GetRenderEvent();

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_IsDeviceReady", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int IsDeviceReady();

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_CreateSession", CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Unicode)]
        internal static extern int CreateSession(string filePath, int width, int height, int matrix, out ulong handle);

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_GetSnapshot", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetSnapshot(ulong handle, out PulseNativeSnapshot snapshot);

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_CloseSession", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void CloseSession(ulong handle);

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_CloseAll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void CloseAll();

        [DllImport(LIBRARYNAME, EntryPoint = "Pulse_GetResources", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetResources(out PulseNativeResources resources);
    }
}
