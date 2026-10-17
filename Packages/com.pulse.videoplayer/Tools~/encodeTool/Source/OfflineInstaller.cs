using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PulseVideoTool
{
    internal static class OfflineInstaller
    {
        private static readonly string[] m_requiredFiles = // 当前共享构建的运行依赖清单。
        {
            "ffmpeg.exe", "ffprobe.exe", "avcodec-63.dll", "avdevice-63.dll", "avfilter-12.dll",
            "avformat-63.dll", "avutil-61.dll", "swresample-7.dll", "swscale-10.dll"
        };

        internal static bool IsReady(Action<string> log, CancellationToken cancellationToken)
        {
            try
            {
                string marker = Path.Combine(ToolPaths.InstallDirectory, "ready.json");
                if (!File.Exists(marker)) return false;
                var serializer = new JavaScriptSerializer();
                var inventory = serializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(marker));
                if (inventory["archive"] != ToolPaths.ARCHIVEHASH) return false;
                foreach (string name in m_requiredFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string path = Path.Combine(ToolPaths.BinDirectory, name);
                    if (!File.Exists(path) || !inventory.ContainsKey(name) || inventory[name] != HashFile(path))
                        return false;
                }
                VerifyExecutables(ToolPaths.BinDirectory, log, cancellationToken);
                log("[初始化] FFmpeg " + ToolPaths.VERSION + " 已就绪，复用本地缓存。");
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                log("[初始化] 缓存验证未通过，需要重新初始化：" + exception.Message);
                return false;
            }
        }

        internal static void Install(Action<string> log, CancellationToken cancellationToken)
        {
            string dependencies = ToolPaths.DependenciesDirectory;
            string archive = Path.Combine(dependencies, "FFmpeg", ToolPaths.ARCHIVENAME);
            string extractor = Path.Combine(dependencies, "7zip", "7zr.exe");
            if (!File.Exists(archive)) throw new FileNotFoundException("缺少离线 FFmpeg 压缩包。", archive);
            if (!File.Exists(extractor)) throw new FileNotFoundException("缺少随工具分发的解压程序。", extractor);

            // 固定压缩包校验通过后才允许执行解压，拒绝损坏或不配套的离线包。
            log("[校验] 正在计算离线包 SHA-256……");
            if (HashFile(archive) != ToolPaths.ARCHIVEHASH)
                throw new InvalidDataException("FFmpeg 压缩包 SHA-256 不匹配，请恢复仓库中的原始压缩包。");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(ToolPaths.CacheDirectory);
            string temporary = Path.Combine(ToolPaths.CacheDirectory, ".install-" + Guid.NewGuid().ToString("N"));
            string previous = Path.Combine(ToolPaths.CacheDirectory, ".previous-" + Guid.NewGuid().ToString("N"));
            using (File.Open(Path.Combine(ToolPaths.CacheDirectory, "install.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    Directory.CreateDirectory(temporary);
                    log("[解压] 离线解压至：" + temporary);
                    int exitCode = ProcessRunner.Run(extractor,
                        new[] { "x", archive, "-o" + temporary, "-y", "-bb1", "-bd", "-sccUTF-8" },
                        log, cancellationToken);
                    if (exitCode != 0) throw new IOException("解压失败，退出码：" + exitCode);
                    string bin = Path.Combine(temporary, ToolPaths.ARCHIVEROOT, "bin");
                    var inventory = new Dictionary<string, string>();
                    inventory.Add("archive", ToolPaths.ARCHIVEHASH);
                    foreach (string name in m_requiredFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        inventory.Add(name, HashFile(Path.Combine(bin, name)));
                    }
                    VerifyExecutables(bin, log, cancellationToken);
                    VerifyEncoding(bin, log, cancellationToken);
                    File.WriteAllText(Path.Combine(temporary, "ready.json"),
                        new JavaScriptSerializer().Serialize(inventory), new UTF8Encoding(false));
                    cancellationToken.ThrowIfCancellationRequested();

                    // 只在全部校验成功后切换目录，解压中断不会留下虚假的就绪状态。
                    if (Directory.Exists(ToolPaths.InstallDirectory))
                        MoveDirectory(ToolPaths.InstallDirectory, previous);
                    try { MoveDirectory(temporary, ToolPaths.InstallDirectory); }
                    catch
                    {
                        if (Directory.Exists(previous)) MoveDirectory(previous, ToolPaths.InstallDirectory);
                        throw;
                    }
                    log("[完成] FFmpeg 初始化成功，可以直接开始转码，无需重新打开工具。");
                }
                finally
                {
                    Cleanup(temporary, log);
                    Cleanup(previous, log);
                }
            }
        }

        internal static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "");
        }

        private static void VerifyExecutables(string bin, Action<string> log, CancellationToken cancellationToken)
        {
            foreach (string name in new[] { "ffmpeg", "ffprobe" })
            {
                bool matchesVersion = false;
                Action<string> receive = delegate(string line)
                {
                    if (line.StartsWith(name + " version " + ToolPaths.VERSION + "-", StringComparison.Ordinal))
                        matchesVersion = true;
                    log(line);
                };
                int exitCode = ProcessRunner.Run(Path.Combine(bin, name + ".exe"), new[] { "-version" },
                    receive, cancellationToken);
                if (exitCode != 0 || !matchesVersion)
                    throw new InvalidDataException(name + " 版本或运行验证失败。");
            }
        }

        private static void VerifyEncoding(string bin, Action<string> log, CancellationToken cancellationToken)
        {
            log("[验证] 检查滤镜和 libx264 实际编码能力……");
            int exitCode = ProcessRunner.Run(Path.Combine(bin, "ffmpeg.exe"), new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i",
                "color=c=black:s=16x16:r=1", "-frames:v", "1", "-c:v", "libx264", "-pix_fmt",
                "yuv420p", "-f", "null", "-"
            }, log, cancellationToken);
            if (exitCode != 0) throw new InvalidDataException("FFmpeg 滤镜或 libx264 编码验证失败。");
        }

        private static void Cleanup(string directory, Action<string> log)
        {
            try { ToolPaths.DeleteOwnedDirectory(directory, ToolPaths.CacheDirectory); }
            catch (Exception exception) { log("[清理警告] " + exception.Message); }
        }

        private static void MoveDirectory(string source, string destination)
        {
            // Windows 原生 DLL 卸载和安全扫描结束前可能暂时拒绝目录重命名。
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try { Directory.Move(source, destination); return; }
                catch (IOException) { if (attempt == 19) throw; }
                catch (UnauthorizedAccessException) { if (attempt == 19) throw; }
                Thread.Sleep(150);
            }
        }
    }
}
