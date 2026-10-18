# 分平台构建入口

关闭 Unity 后，双击目标平台的 BAT。脚本只构建、发布 Pulse 原生插件，不打游戏包。

| 平台 | 一键入口 | 产物目录 |
| --- | --- | --- |
| Windows x64 / D3D11 | Windows/BuildWindowsNative.bat | Runtime/Plugins/Windows/x86_64 |
| Android ARM64 / OpenGLES3 | Android/BuildAndroidNative.bat | Runtime/Plugins/Android |

Windows 生成 PulseVideoNative.dll；Android 同时生成 libPulseVideoNative.so 与 PulseVideoBridge.aar。
iOS 入口暂不创建。

两个 BAT 默认使用 `D:\Unity\2022.3.17f1c1\Editor\Unity.exe`。
更换 Unity 时，可将目标 Unity.exe 拖到对应 BAT 上，或设置 `PULSE_UNITY_EDITOR` 环境变量。
也可以把 Unity.exe 路径作为第一个命令行参数，其后继续传递对应 PowerShell 脚本的参数。
从命令行传入 Unity 路径时不会暂停；双击运行会保留结果窗口。

每个平台的 PowerShell 脚本与 BAT 放在同一目录；源码仍位于 `Source/<平台>`，
中间产物仍位于 `Build/<平台>`。全部路径从脚本所在目录解析，不依赖启动时的当前目录。
首次运行仍需安装相应工具链，缺少依赖会保留明确错误，不自动下载安装。
