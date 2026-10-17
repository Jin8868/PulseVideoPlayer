using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PulseVideoTool
{
    internal static class ProcessRunner
    {
        internal static int Run(string executable, IEnumerable<string> arguments, Action<string> log,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quoted = new List<string>();
            foreach (string argument in arguments) quoted.Add(QuoteArgument(argument));
            var start = new ProcessStartInfo(executable, string.Join(" ", quoted.ToArray()));
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;

            // Job Object 保证取消或工具退出时，PowerShell 和 FFmpeg 子进程同时退役。
            using (var job = new ProcessJob())
            using (var process = new Process())
            {
                process.StartInfo = start;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
                {
                    if (eventArgs.Data != null) log(eventArgs.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
                {
                    if (eventArgs.Data != null) log("[stderr] " + eventArgs.Data);
                };
                process.Start();
                try { job.Attach(process); }
                catch
                {
                    if (!process.HasExited) process.Kill();
                    throw;
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                using (cancellationToken.Register(job.Cancel))
                {
                    process.WaitForExit();
                    cancellationToken.ThrowIfCancellationRequested();
                    return process.ExitCode;
                }
            }
        }

        internal static string QuoteArgument(string argument)
        {
            // Windows 参数转义同时处理引号前与参数末尾的反斜杠。
            return "\"" + Regex.Replace(Regex.Replace(argument, "(\\\\*)\"", "$1$1\\\""),
                "(\\\\+)$", "$1$1") + "\"";
        }
    }

    internal sealed class ProcessJob : IDisposable
    {
        private IntPtr m_handle; // 当前操作拥有的原生进程作业句柄。

        internal ProcessJob()
        {
            m_handle = CreateJobObject(IntPtr.Zero, null);
            if (m_handle == IntPtr.Zero) throw new Win32Exception();
            var limits = new ExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x2000;
            int size = Marshal.SizeOf(limits);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(m_handle, 9, buffer, (uint)size)) throw new Win32Exception();
            }
            catch
            {
                CloseHandle(m_handle);
                m_handle = IntPtr.Zero;
                throw;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        internal void Attach(Process process)
        {
            if (!AssignProcessToJobObject(m_handle, process.Handle)) throw new Win32Exception();
        }

        internal void Cancel() { TerminateJobObject(m_handle, 1); }

        /// <summary>关闭作业句柄并终止仍在运行的子进程。</summary>
        public void Dispose()
        {
            if (m_handle == IntPtr.Zero) return;
            CloseHandle(m_handle);
            m_handle = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            internal long PerProcessUserTimeLimit;
            internal long PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSetSize;
            internal UIntPtr MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass;
            internal uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            internal ulong ReadOperationCount;
            internal ulong WriteOperationCount;
            internal ulong OtherOperationCount;
            internal ulong ReadTransferCount;
            internal ulong WriteTransferCount;
            internal ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            internal BasicLimitInformation BasicLimitInformation;
            internal IoCounters IoInfo;
            internal UIntPtr ProcessMemoryLimit;
            internal UIntPtr JobMemoryLimit;
            internal UIntPtr PeakProcessMemoryUsed;
            internal UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
