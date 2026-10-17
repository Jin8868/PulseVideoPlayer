using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PulseVideoTool
{
    internal sealed class ExportOptions
    {
        internal string[] InputFiles { get; set; }
        internal string OutputDirectory { get; set; }
        internal bool Alpha { get; set; }
        internal int FrameRate { get; set; }
        internal int CRF { get; set; }
        internal int GOP { get; set; }
        internal string Preset { get; set; }
    }

    internal static class VideoExporter
    {
        internal static void Export(ExportOptions options, Action<string> log, Action<int> completed,
            CancellationToken cancellationToken)
        {
            Validate(options);
            Directory.CreateDirectory(options.OutputDirectory);
            string staging = Path.Combine(options.OutputDirectory, ".pulse-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var failures = new List<string>();
            try
            {
                for (int index = 0; index < options.InputFiles.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string source = options.InputFiles[index];
                    string work = Path.Combine(staging, index.ToString());
                    Directory.CreateDirectory(work);
                    string inputList = Path.Combine(work, "inputs.json");
                    string products = Path.Combine(work, "products");
                    File.WriteAllText(inputList, new JavaScriptSerializer().Serialize(new[] { source }),
                        new UTF8Encoding(false));
                    log("[批次] " + (index + 1) + "/" + options.InputFiles.Length + "：" + source);
                    try
                    {
                        var arguments = new List<string>
                        {
                            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                            Path.Combine(ToolPaths.ScriptsDirectory, "VideoPipeline", "ExportVideos.ps1"),
                            "-FFmpegBinDirectory", ToolPaths.BinDirectory, "-InputListFile", inputList,
                            "-OutputDirectory", products, "-FrameRate", options.FrameRate.ToString(),
                            "-CRF", options.CRF.ToString(), "-GOP", options.GOP.ToString(),
                            "-Preset", options.Preset, "-JsonLog"
                        };
                        if (options.Alpha) arguments.Add("-Alpha");
                        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                            "WindowsPowerShell", "v1.0", "powershell.exe");
                        int exitCode = ProcessRunner.Run(powershell, arguments, log, cancellationToken);
                        if (exitCode != 0) throw new IOException("转码进程失败，退出码：" + exitCode);
                        cancellationToken.ThrowIfCancellationRequested();
                        Commit(products, options.OutputDirectory);
                        log("[提交] 已完成：" + Path.GetFileNameWithoutExtension(source) + ".pulse");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        string message = Path.GetFileName(source) + "：" + exception.Message;
                        failures.Add(message);
                        log("[错误] " + message);
                    }
                    completed(index + 1);
                }
                if (failures.Count > 0)
                    throw new IOException(failures.Count + " 个视频处理失败，成功产物已保留。\r\n"
                        + string.Join("\r\n", failures.ToArray()));
                log("[完成] 全部 " + options.InputFiles.Length + " 个视频已导出。");
            }
            finally
            {
                try { ToolPaths.DeleteOwnedDirectory(staging, options.OutputDirectory); }
                catch (Exception exception) { log("[清理警告] 临时目录未能删除：" + exception.Message); }
            }
        }

        private static void Validate(ExportOptions options)
        {
            // 提交前再次检查所有输入和名称，防止覆盖源文件或已有产物。
            if (options.InputFiles.Length == 0) throw new ArgumentException("请至少选择一个 MP4 文件。");
            string output = Path.GetFullPath(options.OutputDirectory);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string input in options.InputFiles)
            {
                if (!File.Exists(input)) throw new FileNotFoundException("输入视频不存在。", input);
                string stem = Path.GetFileNameWithoutExtension(input);
                if (!names.Add(stem)) throw new IOException("存在同名输入，请分批处理：" + stem);
                if (!Directory.Exists(output)) continue;
                foreach (string path in Directory.GetFiles(output))
                {
                    string name = Path.GetFileName(path);
                    if (name.Equals(stem + ".pulse", StringComparison.OrdinalIgnoreCase)
                        || name.Equals(stem + ".audio.m4a", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(stem + ".subtitle", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("输出目录已有产物，请选择其他目录：" + name);
                }
            }
        }

        private static void Commit(string products, string output)
        {
            string[] sources = Directory.GetFiles(products);
            if (Directory.GetFiles(products, "*.pulse").Length != 1)
                throw new InvalidDataException("未生成唯一的 Pulse 视频产物。");
            var committed = new List<string>();
            try
            {
                // 同卷移动已校验的产物；竞态下 File.Move 拒绝覆盖，失败则回滚本次移动。
                foreach (string source in sources)
                {
                    // MP4 和旁路 JSON 仅供封装与校验；播放器所需元数据已内嵌在 Pulse UUID Box。
                    string name = Path.GetFileName(source);
                    if (!name.EndsWith(".pulse", StringComparison.OrdinalIgnoreCase)
                        && !name.EndsWith(".audio.m4a", StringComparison.OrdinalIgnoreCase)
                        && !name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)) continue;
                    string target = Path.Combine(output, Path.GetFileName(source));
                    File.Move(source, target);
                    committed.Add(target);
                }
            }
            catch
            {
                foreach (string target in committed)
                    File.Move(target, Path.Combine(products, Path.GetFileName(target)));
                throw;
            }
        }
    }
}
