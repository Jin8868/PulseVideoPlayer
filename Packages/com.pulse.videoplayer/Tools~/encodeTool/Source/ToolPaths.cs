using System;
using System.IO;
using System.Threading;

namespace PulseVideoTool
{
    internal static class ToolPaths
    {
        internal const string VERSION = "9.0.2";
        internal const string ARCHIVENAME = "ffmpeg-9.0.2-full_build-shared.7z";
        internal const string ARCHIVEROOT = "ffmpeg-9.0.2-full_build-shared";
        internal const string ARCHIVEHASH = "4D2060A8B34A940AA47D785142055BB92A63053781E55F2ACE4546EDD519A8F5";

        internal static string BaseDirectory { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        internal static string DependenciesDirectory
        {
            get { return Path.Combine(BaseDirectory, "Dependencies"); }
        }
        internal static string ScriptsDirectory { get { return Path.Combine(BaseDirectory, "Scripts"); } }
        internal static string UserDirectory
        {
            get
            {
                string overrideDirectory = Environment.GetEnvironmentVariable("PULSE_VIDEO_TOOL_HOME");
                if (!string.IsNullOrWhiteSpace(overrideDirectory)) return Path.GetFullPath(overrideDirectory);
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PulseVideoPlayer", "VideoTool");
            }
        }
        internal static string CacheDirectory
        {
            get { return Path.Combine(DependenciesDirectory, "FFmpeg", "Installed"); }
        }
        internal static string InstallDirectory
        {
            get { return Path.Combine(CacheDirectory, VERSION + "-" + ARCHIVEHASH.Substring(0, 12)); }
        }
        internal static string BinDirectory { get { return Path.Combine(InstallDirectory, ARCHIVEROOT, "bin"); } }
        internal static string SettingsPath { get { return Path.Combine(UserDirectory, "settings.json"); } }

        internal static void DeleteOwnedDirectory(string directory, string ownerDirectory)
        {
            // 递归清理仅允许发生在明确的任务目录内部，绝不删除用户选择的输出根目录。
            string root = Path.GetFullPath(ownerDirectory).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(directory);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("拒绝清理任务目录之外的路径：" + target);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    return;
                }
                catch (IOException) { if (attempt == 19) throw; }
                catch (UnauthorizedAccessException) { if (attempt == 19) throw; }
                // 原生进程刚退出时，扫描器或映像卸载可能短暂占用 DLL。
                Thread.Sleep(150);
            }
        }
    }
}
