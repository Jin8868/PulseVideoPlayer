# PulseVideoPlayer

独立 Unity 视频播放器组件。Windows x64 / D3D11 原生后端已实现，视频由 Windows Media Foundation 解码，
FFmpeg 只用于离线制作。业务资源加载、Wwise 音轨和循环协调位于宿主工程。

## 目录

```text
Packages/com.pulse.videoplayer/
├── Runtime/                         播放器与已发布的原生插件
└── Tools~/
    ├── encodeTool/                  视频转码 EXE、源码、唯一转码脚本、离线依赖和日志
    └── buildTool/                   原生源码、构建脚本与本地构建缓存
Tests/                               本地原始素材与导出产物（Git 忽略）
```

所有组件工具都放在包内的 Tools~，随 UPM 一起分发并被 Unity 资源导入忽略。
仓库根目录不再保留第二套 Tools 或 Native。Runtime/Plugins 存放可直接使用的插件成品。
宿主 Unity 2022.3 通过 file:../../../PulseVideoPlayer/Packages/com.pulse.videoplayer 引用本地包。

## 离线视频转码

直接打开 Packages/com.pulse.videoplayer/Tools~/encodeTool/VideoTool.exe。宿主工程也可提供菜单入口：
「★AlloyFramework★/视频/打开转码工具」。首次点击初始化，在组件内部解压依赖，无需安装软件或联网下载。

- 原始依赖包：encodeTool/Dependencies/FFmpeg/ffmpeg-9.0.2-full_build-shared.7z，56.99 MiB。
- 解压依赖：encodeTool/Dependencies/FFmpeg/Installed，Git 忽略，不再使用 C 盘 FFmpeg 缓存。
- 固定日志：encodeTool/log/VideoTool.log，重复启动追加同一文件，Git 忽略。
- 配置：%LOCALAPPDATA%/PulseVideoPlayer/VideoTool/settings.json。

支持复选框批量选择和文件对话框多选。最终只提交 .pulse、独立音频和字幕；中间 MP4 和 metadata.json 清理。
输入和输出可以同目录，原始 MP4 保持不变，已有最终产物拒绝覆盖。
标准：MP4 容器、单路 CFR H.264、无音轨、8 位 SDR，RGB/Alpha 区域写入 Pulse UUID 元数据。
当前尚未启用 AES-CTR 加密。

使用见 [转码工具说明](Packages/com.pulse.videoplayer/Tools~/encodeTool/README.md)，
开发见 [转码源码说明](Packages/com.pulse.videoplayer/Tools~/encodeTool/Source/README.md)，
命令行见 [转码规则](Packages/com.pulse.videoplayer/Tools~/encodeTool/Scripts/VideoPipeline/README.md)。

## 构建工具

重新构建视频窗口：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Packages\com.pulse.videoplayer\Tools~\encodeTool\Source\Build.ps1
```

以下命令均在本仓库根目录执行。Unity.exe 示例为相对路径，请按实际安装位置调整。关闭 Unity 后构建 Windows 原生插件：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\Packages\com.pulse.videoplayer\Tools~\buildTool\Scripts\BuildWindowsNative.ps1 `
  -UnityEditorPath '..\Unity\Editor\Unity.exe'
```

原生源码位于 buildTool/Source，CMake 中间文件位于 buildTool/Build（Git 忽略），
构建成功后插件更新到 Runtime/Plugins/Windows/x86_64。当前只有 Windows 构建入口，其他平台后端尚未实现。
见 [原生构建说明](Packages/com.pulse.videoplayer/Tools~/buildTool/README.md)。

## 运行与验证

运行时接口与读取器说明见 [组件说明](Packages/com.pulse.videoplayer/README.md)。
本地素材管理见 [测试说明](Tests/README.md)；素材 Source、Generated 不随 Git 分发。
Windows 手动验收见 [原生运行说明](Packages/com.pulse.videoplayer/Tools~/buildTool/Source/Windows/README.md)。
工具验证记录见 [验证说明](Packages/com.pulse.videoplayer/Tools~/encodeTool/Source/Validation.md)。
