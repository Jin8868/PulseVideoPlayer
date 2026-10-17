# 视频转码工具

直接打开本目录的 VideoTool.exe，或在 Unity 中选择「★AlloyFramework★ → 视频 → 打开转码工具」。
未初始化时只显示初始化与日志，点击离线初始化后直接进入转码界面，无需联网或重启。

```text
encodeTool/
├── VideoTool.exe            窗口入口
├── Dependencies/            FFmpeg 原始 7z 与 7zr.exe
│   └── FFmpeg/Installed/    就地解压的依赖，Git 忽略
├── Scripts/                 唯一一套转码规则脚本
├── Source/                  窗口源码、构建脚本、验证说明
└── log/VideoTool.log        固定追加日志，Git 忽略
```

输入目录会扫描 MP4，可递归扫描。直接勾选多个文件，无需 Ctrl / Shift，也可点击
「选择 MP4 文件…」从文件对话框同时添加多个视频；支持全选与取消选择。
选择输出目录及帧率、CRF、编码速度后开始批量转码。默认 30 FPS、CRF 18、GOP 60、veryfast。
编码速度是 FFmpeg 的 preset，影响编码耗时与压缩效率，不影响播放速度。

最终只保留 .pulse、独立音频和字幕。MP4、metadata.json 为临时产物，不复制到最终目录。
播放器使用 .pulse 内嵌的 RGB/Alpha 等元数据，不需要旁路 JSON。原始 MP4 不覆盖、不复制，
允许输入与输出使用同一目录；已有 .pulse 等最终产物拒绝覆盖。

产物为标准 MP4 容器、单路 CFR H.264、无音轨、8 位 SDR，明确记录 RGB/Alpha 区域与 Alpha 模式。
检测到 PQ、HLG 或 HDR 元数据时拒绝导出；未标记传递特性时按 SDR BT.709 处理并记录警告。
当前 Pulse v1 为明文，尚未支持 AES-CTR。

工具支持 Windows x64 / .NET Framework 4.8（Windows 10 1903+、Windows 11 已包含）。
本目录的 EXE、Dependencies 和 Scripts 需一起保留。日志固定追加到 log/VideoTool.log；
失败时弹窗，按钮或 Ctrl+Shift+C 复制本次运行的完整日志。配置仍保存到用户 LocalAppData。
工具与原生插件构建职责分开：重新编译窗口见 Source/README.md，原生插件构建见 ../buildTool/README.md。

点击「清空日志」同步清空窗口、复制缓冲区、待显示队列及 log/VideoTool.log，复用原文件。
任务运行期间也可清空，清空后继续记录新日志，不影响转码或进度。
