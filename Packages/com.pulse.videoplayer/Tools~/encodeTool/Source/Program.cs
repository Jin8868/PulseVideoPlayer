using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace PulseVideoTool
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] arguments)
        {
            bool ownsMutex;
            using (var mutex = new Mutex(true, "Local\\PulseVideoPlayer.VideoTool", out ownsMutex))
            {
                if (!ownsMutex)
                {
                    if (arguments.Length == 0) MessageBox.Show("视频处理工具已经打开，请使用已有窗口。", "视频处理工具");
                    return 2;
                }
                try
                {
                    using (var log = new ToolLog())
                    {
                        log.Write("Pulse 视频处理工具启动。日志：" + log.FilePath);
                        if (arguments.Length > 0) return RunCommand(arguments, log);
                        var application = new Application();
                        return application.Run(new MainWindow(log));
                    }
                }
                catch (Exception exception)
                {
                    if (arguments.Length == 0) MessageBox.Show(exception.ToString(), "工具启动失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static int RunCommand(string[] arguments, ToolLog log)
        {
            // 无界面入口便于验证同一套初始化和转码逻辑，不维护额外测试实现。
            try
            {
                if (arguments[0] == "--initialize")
                {
                    if (!OfflineInstaller.IsReady(log.Write, CancellationToken.None))
                        OfflineInstaller.Install(log.Write, CancellationToken.None);
                    return 0;
                }
                if (!OfflineInstaller.IsReady(log.Write, CancellationToken.None))
                    throw new InvalidOperationException("尚未初始化 FFmpeg，请先运行 --initialize 或打开工具初始化。");
                if (arguments[0] == "--verify") return 0;
                if (arguments[0] == "--export" && arguments.Length == 3)
                {
                    var options = new ExportOptions();
                    options.InputFiles = new[] { Path.GetFullPath(arguments[1]) };
                    options.OutputDirectory = Path.GetFullPath(arguments[2]);
                    options.FrameRate = 30;
                    options.CRF = 18;
                    options.GOP = 60;
                    options.Preset = "veryfast";
                    VideoExporter.Export(options, log.Write, delegate(int count) { }, CancellationToken.None);
                    return 0;
                }
                throw new ArgumentException("参数：--initialize | --verify | --export <输入文件> <输出目录>");
            }
            catch (Exception exception)
            {
                log.Write("[错误] " + exception);
                return 1;
            }
        }
    }
}
