# 转码工具源码与维护

源码只维护本目录这一份，转码规则只维护 ../Scripts/VideoPipeline，不生成第二份脚本。
在仓库根目录重新构建窗口：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Packages\com.pulse.videoplayer\Tools~\encodeTool\Source\Build.ps1
```

使用 Windows 自带 .NET Framework 编译器，无需 SDK、NuGet 或联网还原。也可以通过 Rider / Visual Studio
打开 VideoTool.csproj，需安装 .NET Framework 4.8 开发工具。修改后运行 Build.ps1 更新已发布 EXE。

依赖、脚本和 EXE 都位于 encodeTool 内，UPM 包可独立分发。FFmpeg 固定为 9.0.2 shared，压缩包 SHA-256
记录在 ToolPaths.cs；解压缓存位于 Dependencies/FFmpeg/Installed，日志固定追加到 log/VideoTool.log。
初始化验证压缩包、运行依赖、版本和实际 libx264 编码，成功才写入就绪标记。
升级 FFmpeg 需同步 ToolPaths.cs、OfflineInstaller.cs、Scripts/ToolEnvironment.ps1 与依赖说明。

配置保存在 %LOCALAPPDATA%/PulseVideoPlayer/VideoTool/settings.json。PULSE_VIDEO_TOOL_HOME 仅覆盖配置目录，
FFmpeg 与日志始终保留在工具目录；自动化验证使用独立的输出目录。

文件列表复选框与 SelectedItems 双向同步，普通点击即可多选。开始操作时取得全部勾选路径，再逐个导出。
在选定输出目录内创建本次临时目录，视频校验和 Pulse 元数据回读通过后，只提交 .pulse、独立音频与字幕。
MP4、metadata.json 随临时目录清理，不覆盖原始文件。Job Object 在取消或关闭时终止完整进程树。
日志记录初始化、解压、转码和错误；错误窗口与主窗口均支持 Ctrl+Shift+C 复制本次完整日志。

视频保持标准 MP4、单路 CFR H.264、8 位 SDR、无音轨。RGB/Alpha 区域保存在 Pulse UUID Box。
PQ / HLG 与已知 HDR 元数据会被拒绝；未标记内容按 SDR BT.709 处理，日志明确说明该假设。
AES-CTR 仍需配套容器协议和播放器解密层，尚未实现。

无界面入口与窗口共用实现，退出码 0 成功、1 失败、2 工具已经打开，结果查看固定日志：

```text
VideoTool.exe --initialize
VideoTool.exe --verify
# 在 encodeTool 目录执行，相对视频路径以当前目录为基准。
VideoTool.exe --export ".\Videos\input.mp4" ".\Videos\Output"
```

验证记录见 Validation.md。原生插件构建只由 ../../buildTool 管理，不属于转码窗口的构建流程。