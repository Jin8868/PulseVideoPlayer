# 原生插件构建工具

本文命令均从 PulseVideoPlayer 仓库根目录执行；相对路径以该目录为基准，Unity 安装路径请按实际位置调整。

本目录只管理原生插件源码、构建入口和本地构建缓存。视频转码使用旁边的 encodeTool。

```text
buildTool/
├── Scripts/                 构建入口
├── Source/Windows/          Windows x64 / D3D11 原生源码
└── Build/Windows/           本地 CMake 缓存与中间产物，Git 忽略
```

关闭 Unity 后，在仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\Packages\com.pulse.videoplayer\Tools~\buildTool\Scripts\BuildWindowsNative.ps1 `
  -UnityEditorPath '..\Unity\Editor\Unity.exe'
```

也可以双击 Scripts/BuildWindowsNative.bat，或将 Unity.exe 路径作为首个参数传入。
需要 Visual Studio 2022 C++ 工具链与 CMake；脚本从 Unity 安装目录读取 PluginAPI 头文件。
脚本只在构建成功后更新组件 Runtime/Plugins/Windows/x86_64/PulseVideoNative.dll，保留现有 .meta。

| 平台 | 目标产物 | 当前状态 |
| --- | --- | --- |
| Windows x64 | .dll | 已有原生实现和构建脚本 |
| Android / Linux | .so | 原生后端与构建入口尚未实现 |
| iOS | .a | 原生后端与构建入口尚未实现 |

后续平台源码分别放到 Source 对应平台目录，构建入口放到 Scripts，中间产物放 Build，
发布产物统一放组件 Runtime/Plugins 对应平台目录。不要将本地 Build 提交 Git。
Windows 运行验收见 Source/Windows/README.md。
