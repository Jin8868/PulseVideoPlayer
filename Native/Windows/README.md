# Windows 原生插件

当前实现仅验证 Windows x64 / D3D11 的本地 Pulse 首帧链路。
已在当前 Unity 2022.3 Windows 宿主编译并验证首帧与基础打开/关闭；连续播放、音频、暂停、Seek 尚未实现。

## 生成 DLL

安装 Visual Studio 2022 或 Build Tools 2022，选择“使用 C++ 的桌面开发”，
确保包含 MSVC v143、Windows SDK、C++ CMake tools for Windows。
Rider 和 Unity 自身不提供这里需要的完整 C++ 构建环境。

关闭 Unity（避免替换已加载 DLL 时被锁定），在仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\BuildWindowsNative.ps1 `
  -UnityEditorPath "D:\Unity\2022.3.17f1c1\Editor\Unity.exe"
```

日志逐行写入 stdout；加 `-JsonLog` 后每行是一个 JSON 对象。
后续 Unity 面板可启动该进程并同时异步读取 stdout/stderr，根据退出码判断完成。
脚本自动寻找 PATH 或 VS 安装目录里的 CMake；也支持 `-CMakePath` 显式指定。

源码：`Native/Windows/PulseVideoNative.cpp`。
构建目录：`Native/Windows/build`（不提交）。
构建结果：`Native/Windows/build/Release/PulseVideoNative.dll`。
自动安装到：`Packages/com.pulse.videoplayer/Runtime/Plugins/Windows/x86_64/PulseVideoNative.dll`。
脚本生成仅允许 Windows x64 Editor/Player 使用的插件导入配置。
构建目录保留供增量编译使用，可在不构建时手动删除。

## 验证

1. 在 Unity 插件 Inspector 中确认仅启用 Windows Editor 与 Windows x86_64。
2. 当前 Editor 必须运行于 Direct3D11；可用 Unity 启动参数 `-force-d3d11`。
3. 在 game-template 工程选择 `★AlloyFramework★/视频/创建或打开首帧测试场景`。
4. 打开 Game 窗口并进入 Play。默认读取 Synthetic/all_in_one.pulse。
5. Console 应显示 `[Pulse] 首帧就绪`，画面应为第 0 帧；保持静止是当前预期。
6. 使用“关闭播放器”“打开首帧”重复切换；再在 Inspector 修改 Relative Path
   为 `PulseTests/Alpha/alpha_test.pulse` 或 `PulseTests/Sintel/Sintel.2010.1080p.pulse`
   （以 StreamingAssets 下实际文件名为准）。检查透明区域、方向、比例和颜色。
7. 把路径改为不存在的文件，检查清晰报错；恢复路径应能重新打开。
8. 最后单独验证 Windows x64 Player，避免仅 Editor 可用。

成功生成 DLL 仅表示编译成功，不能替代 Unity 解码/显示/关闭验证。
首帧测试不能证明连续播放、Seek、音视频同步已可靠。

## 其他平台

这份 C++ 后端依赖 Windows Media Foundation 和 D3D11，不能仅切换 CMake 目标
就得到其他平台实现。共享文件格式、C# API 和错误语义；每个平台单独实现解码、
GPU 纹理交接、线程及资源释放。需要在对应平台和图形 API 上分别构建和验证。

候选后端：Android MediaCodec，macOS/iOS VideoToolbox + Metal，
Linux FFmpeg 或 GStreamer + 平台图形接口。它们是规划，还没有实现。

## 测试资源统计

测试界面每秒刷新 Unity 已分配、保留和托管已用内存，单位为 MiB。
Console 在测试开始、首帧就绪、关闭请求和关闭后 1 秒输出 [Pulse][资源]。
关闭后采样被后续重开取代时会跳过该条日志，避免把新会话误认成旧会话未释放。
新增 Pulse_GetResources 导出，需要重新构建 DLL；旧 DLL 仍可播放，但提示原生统计不可用。

原生会话、待关闭会话、未回收线程句柄、输出纹理及持有解码表面均为已登记资源计数。
关闭完成且不再打开后应趋向零；关闭后 1 秒只是采样时间，并非关闭完成承诺。
输出纹理字节数按宽 × 高 × 4 估算，不包含驱动对齐、解码器内部池或完整显存占用。
Unity 内存数字覆盖编辑器/进程中的其他 Unity 内容，日志、托管 GC 和缓存都可能影响它们。
不要仅凭一次内存增长断定泄漏，需比较多轮关闭后的计数与稳定后的内存趋势。

测试宿主使用 UNITY_EDITOR || DEVELOPMENT_BUILD 控制按钮、周期采样与统计日志；正式构建不执行这些诊断调用。
