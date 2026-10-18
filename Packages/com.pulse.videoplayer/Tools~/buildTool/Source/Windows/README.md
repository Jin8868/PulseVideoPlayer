# Windows 原生插件

本文命令均从 PulseVideoPlayer 仓库根目录执行；相对路径以该目录为基准，Unity 安装路径请按实际位置调整。

当前平台范围为 Windows x64 / D3D11，要求 D3D feature level 至少为 11.0。
上一阶段已验证首帧与基础打开/关闭。当前新增连续播放、暂停/恢复和播放结束状态，尚未由用户编译运行验证。音频和 Seek 尚未实现。

## 生成 DLL

安装 Visual Studio 2022 或 Build Tools 2022，选择“使用 C++ 的桌面开发”，
确保包含 MSVC v143、Windows SDK、C++ CMake tools for Windows。
Rider 和 Unity 自身不提供这里需要的完整 C++ 构建环境。

关闭 Unity（避免替换已加载 DLL 时被锁定），在仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Packages\com.pulse.videoplayer\Tools~\buildTool\Scripts\Windows\BuildWindowsNative.ps1 `
  -UnityEditorPath "..\Unity\Editor\Unity.exe"
```

日志逐行写入 stdout；加 `-JsonLog` 后每行是一个 JSON 对象。
后续 Unity 面板可启动该进程并同时异步读取 stdout/stderr，根据退出码判断完成。
脚本自动寻找 PATH 或 VS 安装目录里的 CMake；也支持 `-CMakePath` 显式指定。

源码：`PulseVideoNative.cpp（本目录）`。
构建目录：`Packages/com.pulse.videoplayer/Tools~/buildTool/Build/Windows`（不提交）。
构建结果：`Packages/com.pulse.videoplayer/Tools~/buildTool/Build/Windows/Release/PulseVideoNative.dll`。
自动安装到：`Packages/com.pulse.videoplayer/Runtime/Plugins/Windows/x86_64/PulseVideoNative.dll`。
脚本生成仅允许 Windows x64 Editor/Player 使用的插件导入配置。
构建目录保留供增量编译使用，可在不构建时手动删除。

## 验证

1. 在 Unity 插件 Inspector 中确认仅启用 Windows Editor 与 Windows x86_64。
2. 当前 Editor 必须运行于 Direct3D11；可用 Unity 启动参数 `-force-d3d11`。
3. 在 game-template 工程选择 `★AlloyFramework★/视频/创建或打开首帧测试场景`。
4. 打开 Game 窗口并进入 Play。默认读取 Synthetic/all_in_one.pulse。
5. Console 应显示 `[Pulse] 首帧就绪` 并开始连续播放；关闭 Play Automatically 可只准备首帧。
6. 使用“关闭播放器”“打开首帧”重复切换；再在 Inspector 修改 Relative Path
   为 `PulseTests/Alpha/alpha_test.pulse` 或 `PulseTests/Sintel/Sintel.2010.1080p.pulse`
   （以 StreamingAssets 下实际文件名为准）。检查透明区域、方向、比例和颜色。
7. 把路径改为不存在的文件，检查清晰报错；恢复路径应能重新打开。
8. 最后单独验证 Windows x64 Player，避免仅 Editor 可用。

成功生成 DLL 仅表示编译成功，不能替代 Unity 解码/显示/关闭验证。
连续播放验收步骤见下方；当前测试不能证明 Seek 或音视频同步已可靠。

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

## 连续播放阶段（ABI 2，待用户验证）

必须关闭 Unity 后重新运行 BuildWindowsNative.ps1。仓库中原有 DLL 属于上一阶段；
本次未执行编译，不会自动替换它。托管代码检查 Pulse_GetApiVersion，不兼容时提示重新构建。

PulsePlayer 增加 Play、Pause、Resume、Stop、GetPlayTimeMs 和 Playing/Paused/Ended 状态。
OpenAsync 仍只准备首帧，只有 Play 会启动时钟；宿主测试组件默认在准备完成后自动 Play。
IPulseClock 与 IPulsePlayerEvents 可供未来的独立音频/字幕模块使用；通知由 Unity 主线程发出。
Pause 保存当前时间，Resume 重置实时时钟锚点；暂停期间的时间不计入进度。

C# 使用实时钟与有理帧率决定目标帧；原生线程顺序解码并丢弃过时帧追赶目标。
源 PTS 可以不从零开始，原生快照保留首帧原始 PTS，后续帧时间减去首帧起点。
本阶段原始实现没有 Seek；新增跳转实现与 ABI 3 构建方式见下文。循环播放、播放倍速和音轨尚未接入。
末帧已提交、源帧数与元数据匹配、时钟到结尾三个条件同时成立才触发 Completed。

播放器只保留一个尚未归还的解码表面，工作线程在表面被消费前等待。
帧初回调仅检查 GPU 完成，帧末回调才写入同一张 BGRA 输出纹理。
GPU 围栏完成后才归还解码样本；暂停不提交新帧，已经提交的 GPU 工作允许收尾。
单张输出纹理复用，不逐帧创建 Unity Texture2D 或 Material。

### 手动验收

1. 选择 `★AlloyFramework★/视频/创建或打开播放测试场景`，按 Play。
   菜单保留旧的首帧入口，并自动为旧场景建立右侧视频区域。
   推荐 Game 窗口至少 1280×720，左侧显示控制、时间和资源，右侧显示视频。
2. Synthetic 的帧号/时间码应随画面推进，已提交帧和纹理版本持续增加。
   观察“追赶丢帧”与卡顿；丢帧数不是泄漏计数，持续大量增长需后续定位性能。
3. 播放几秒后暂停，等待 3～5 秒：时钟保持不变，已提交的 GPU 工作收尾后帧号也应稳定。
   恢复后从保存时间继续，不能把暂停时长计入进度。
4. 测试 Time.timeScale 为零：视频时钟仍应推进。测试结束后恢复原值。
5. Synthetic 应在 60 秒结束，保留帧 1799；Alpha 为 30 秒、末帧 899；
   Sintel 为 888 秒、末帧 26639。Completed 每次播放只触发一次。
6. 结束后点击“重新播放”：重新打开文件并从零开始。连续点击暂停/恢复或开关视频，不能挂住或旧帧回绑。
7. 播放和暂停时线程句柄通常为 1，输出纹理为 1，持有解码表面至多为 1，这是背压队列的正常状态。
   ABI 3 播放结束后保留一个线程以处理 Seek，输出纹理仍为 1；关闭并等待退役后资源计数应全部归零。
8. 切换 Alpha 与 Sintel 检查比例、颜色、透明效果；最后再单独验证 Windows x64 Player。

按钮、统计与测试日志仍只在 UNITY_EDITOR 或 DEVELOPMENT_BUILD 下启用。
暂停/开始命令更新原子目标，不等待解码；关闭由原生工作线程和 GPU 退役回调继续处理。


## Seek 阶段（ABI 3，待用户编译运行）

新增 Pulse_Seek(handle, frameIndex, relativeTimestamp)，返回单调跳转代次。
PlaybackSnapshot 从 40 字节扩展为 48 字节，末尾为 uint64 CompletedSeek。
Pulse_GetApiVersion 返回 3；旧 ABI 2 DLL 必须重建。
诊断 Stage 新增 Seeking，其他阶段编号保持不变。

先关闭 Unity，再双击 Packages/com.pulse.videoplayer/Tools~/buildTool/Scripts/Windows/BuildWindowsNative.bat。
双击默认使用 D:\Unity\2022.3.17f1c1\Editor\Unity.exe，也可通过 PULSE_UNITY_EDITOR 环境变量覆盖。
更换版本可将目标 Unity.exe 拖到 BAT 上；命令行相对编辑器路径以启动时的工作目录为基准。
也可以在命令行指定编辑器：

```bat
Packages\com.pulse.videoplayer\Tools~\buildTool\Scripts\Windows\BuildWindowsNative.bat "..\Unity\Editor\Unity.exe"
```

BAT 内部调用现有 PS1，使用 ExecutionPolicy Bypass，仅作用于本次 PowerShell 进程。
双击时构建后暂停以查看日志；传入编辑器路径时不暂停，便于 Unity 工具调用。
指定路径后可继续传入 -JsonLog、-CMakePath 或 -Configuration 等 PS1 参数。
例如：BuildWindowsNative.bat "..\Unity\Editor\Unity.exe" -JsonLog。
BAT 透传标准输出、标准错误和退出码，不改变日志格式，不修改机器执行策略。
Unity 面板使用隐藏的 cmd.exe /c 调用 BAT，并传入编辑器路径；异步读取 UTF-8 日志。

手动验证建议使用 Synthetic 的可见时间码：

1. 播放中拖到约 45 秒，再向后拖到约 5 秒；松手后画面、时间与滑条应一致并继续播放。
2. 暂停后拖到 30 秒；画面更新但时间不推进，点击播放后从该位置继续。
3. 拖到零和最右端，不能越界、卡死或显示旧帧；播放中拖到末尾应正常结束。
4. 自然结束后向后拖，再点击播放；应从选定位置恢复。
5. 快速连续拖动多次、定位中关闭及重开；最终以最新请求为准，关闭后不能回绑画面。
6. 连续 Seek 不应新增输出纹理；关闭并等待后会话、线程和纹理计数都应归零。
7. Sintel 和 Alpha 重复上述检查，确认长视频、裁剪和透明通道正常。

本次仅静态检查，没有编译 DLL 或运行 Unity 验证。
