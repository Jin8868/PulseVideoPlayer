# PulseVideoPlayer 运行时读取

当前已实现本地文件数据源、Pulse v1 容器扫描、严格 UTF-8/JSON 解析、元数据校验和异步加载。
已加入 Windows x64 / D3D11 首帧解码、GPU 纹理桥接与 PulsePlayer 门面，已在当前 Windows 宿主编译并验证首帧显示及基础打开/关闭。当前新增连续播放与暂停/恢复的待验证源码；音频与 Seek 尚未实现。构建及验证步骤见Tools~/buildTool/Source/Windows/README.md。

```csharp
PulseMetadata metadata = await PulseMetadataLoader.ReadAsync(filePath, cancellationToken);
```

ReadAsync 使用独立线程完成文件 IO，成功、失败、取消均切回 Unity 主线程交付。
用于运行中的 Unity PlayerLoop；同步调用可使用 PulseMetadataReader.Read。

## 容器约定

.pulse 为标准 MP4，元数据位于顶层 uuid Box。
UUID：6fbdab98-401e-4e52-b44a-97fed50761d2，按文本顺序保存 16 字节，不使用 Guid.ToByteArray。
读取器支持 32/64 位 Box 大小和延伸至文件末尾的 Box，按偏移跳过 mdat，不加载视频载荷。
必须存在 ftyp、moov、mdat 和唯一的 Pulse UUID；元数据最大 1 MiB。

UUID 内为无 BOM 的 UTF-8 JSON，schemaVersion 当前仅支持 1。
字段：encodedSize、displaySize、rgbRect、alphaRect、alphaMode、frameCount、frameRate、timeBase、color。
frameRate 与 timeBase 是有理数字符串；前者为每秒帧数，后者为一个时间戳 tick 的秒数。
区域是左上角起算的 [x, y, width, height]，RGB/Alpha 必须同尺寸且不重叠。
JSON 解析器仅支持 v1 使用的整数值，不接受浮点或指数数值；避免额外 JSON 包及反射序列化依赖。
运行时不依赖旁路 .metadata.json。

## 数据源边界

IPulseDataSource.ReadAt 允许返回短片段，读取器循环补齐并检查取消。
FileSource 串行化定位、读取与关闭，支持幂等 Dispose；关闭后拒绝读取。
当前仅支持真实文件路径。Android APK 内的 StreamingAssets 需要后续 ContentFdSource 后端。

## 工程工具入口

组件只分发 Tools~/encodeTool/VideoTool.exe；宿主工程负责提供菜单入口。
当前宿主入口：★AlloyFramework★/视频/打开转码工具。
旧首帧测试场景、元数据测试菜单及读取器检查程序已移除。

## 连续播放接口

```csharp
await player.OpenAsync(filePath, cancellationToken);
player.Play();
player.Pause();
player.Resume();
double timeMs = player.GetPlayTimeMs();
await player.CloseAsync();
player.Dispose();
```

全部操作从 Unity 主线程调用。OpenAsync 只准备首帧；Play 开始时钟，Pause 冻结时间，Resume 从保存位置继续。
IPulseClock 只暴露毫秒时钟，IPulsePlayerEvents 暴露 Started/Paused/Resumed/Stopped/Completed/Failed。
末帧完成后保持 Ended 和最后一帧，需要 Close/OpenAsync 后再 Play 才能重播；Stop 等同请求关闭。
使用 RawImage 时先解除 texture/material 绑定，再关闭或释放播放器。
PresentedFrame、PresentedTimeMs、TextureVersion、DroppedFrames 可观察调度结果。
连续播放已由用户验证。本次 Seek 使用 Windows ABI 3，必须按 Tools~/buildTool/Source/Windows/README.md 重新构建 DLL。


## 项目 UI 组件

在项目自有的预制体上添加 PulseVideoPlayer 组件，类型为 PulseVideoPlayer.UI.PulseVideoPlayer。
组件位于 Runtime/UI，独立程序集 PulseVideoPlayer.UI 引用核心、UniTask 和 Unity.ugui；文字使用 UnityEngine.UI.Text。
预制体保存在各项目的 Assets 中，样式、层级和名称由项目决定。

Inspector 引用：

| 字段 | 控件 | 用途 |
| --- | --- | --- |
| Video Image | RawImage | 必填，显示视频画面 |
| Loading Progress Image | Image | 可选，设置 Type 为 Filled，显示加载进度 |
| Loading Percentage Text | Text | 可选，显示加载百分比 |
| Playback Progress Slider | Slider | 可选，支持点击、键盘调整及拖动预览，松手后跳转 |
| Playback Time Text | Text | 可选，显示 0:00/0:36；超过一小时使用 h:mm:ss |
| Play Button | Button | 可选，打开并播放、从暂停恢复、结束后重播 |
| Pause Button | Button | 可选，暂停播放 |

按钮拖入引用后自动注册监听，无需额外配置 On Click。只移除组件自己的监听。
Aspect Ratio Fitter 可引用项目已有适配器，或自动读取 RawImage 上已有适配器；组件不会创建布局。
显示比例随视频元数据更新，关闭后还原。

Source Path 可填写相对 StreamingAssets 的路径，例如 Videos/example.pulse。
勾选 Open On Enable 后启用时打开；Play When Ready 决定打开完成后是否自动播放。
也可以不勾选 Open On Enable，通过播放按钮或无参 Play() 按 Inspector 路径加载并播放；暂停时 Play() 恢复，结束时 Play() 重播。显式路径仍可使用 OpenAsync(真实本地路径)，带资源地址、音频事件 Key 和循环配置的 Play/PlayAsync 接口由宿主驱动提供，见下文。
业务程序集直接引用组件时，需要在自己的 asmdef 中添加 PulseVideoPlayer.UI 引用。

加载图只更新 fillAmount，文字显示 0%～100%，不旋转。本地首帧没有连续解码进度，按准备完成节点更新。
加载 Image 使用 Filled / Radial 360，并保持 GameObject 及父对象激活。调用 SetLoadingProgress(0~1) 直接更新填充和百分比，完成、关闭或失败时隐藏。
加载控件的 GameObject 应保持激活，组件仅切换 Graphic.enabled。
时间文字只在整秒变化时更新；暂停后冻结，结束后显示总时长，关闭后显示 0:00/0:00。

OpenAsync、Play、Pause、Resume、Replay、Close、CloseAsync、Stop 均从 Unity 主线程调用。
Ready、Started、Paused、Resumed、Stopped、Completed、Failed 和 LoadingProgressChanged 可供业务订阅。
关闭或禁用组件时，先解除 RawImage 的视频纹理和材质绑定，再请求释放底层播放器。
CloseAsync 等待本次播放器的原生资源退役，需要 Unity PlayerLoop 持续运行。
本次 Seek 将原生 ABI 升至 3，需要关闭 Unity 后重新构建 DLL。
当前仍使用 Windows/D3D11 后端，音频尚未接入。


## 进度条与 Seek（ABI 3）

组件自动为 Playback Progress Slider 注册值变化和指针事件。
拖动期间显示目标时间并暂停自动覆盖滑块；松手后跳转，点击和键盘调整也会提交跳转。
播放中拖动后继续播放，暂停中拖动只更新画面；结束后拖动进入暂停状态，可点击播放按钮恢复。
目标画面未提交前冻结播放时钟，避免把定位耗时算入播放时间。
拖动预览期间原视频继续播放，不逐帧提交定位请求；松手后才采用最终目标。
非拖动的值变化延迟到 LateUpdate 处理，避免 Slider 的按下事件早于指针转发而误触发定位；松手后才提交拖动结果。
连续请求采用最新请求，旧请求不会被标记为新请求的完成结果。

PulsePlayer 和 UI 组件都提供 Seek(double timeSeconds)、SeekAsync(double timeSeconds, CancellationToken)、IsSeeking。
Seek 是非阻塞请求；SeekAsync 等待目标画面的 GPU 围栏完成。
取消令牌只取消等待，不撤销原生请求；关闭或新的 Seek 取消旧的等待。
超出范围的有限时间会钳制到视频区间，末尾映射到最后一帧；NaN/Infinity 会抛出异常。
Seek 不重建 Unity 纹理或材质，原生读取和定位都由同一解码线程执行。
GPU 正在使用的样本必须等围栏结束后才能归还及刷新解码器。
定位先到关键帧，再解码并丢弃目标前的样本；相对 PTS 依据导出器的固定帧率换算帧序号。
目标帧等待上限为 20 秒，超时通过 Failed 报告并关闭会话。

Windows 原生读取到 EOF 后保留工作线程等待新 Seek 或 Close，关闭后资源计数仍应归零。
这里只增加视频跳转，独立音频或字幕尚未实现联动。


## 宿主播放适配与新业务入口

组件仍为 PulseVideoPlayer.UI.PulseVideoPlayer，业务使用 Play/PlayAsync。
新增重载参数为 videoLocation、audioEventKey、EPulseLoopMode、playCount 和 CancellationToken。
Once 为一次，Infinite 为无限，Count 的次数包含第一次播放。
旧 Open/OpenAsync 保留兼容并标记 Obsolete；新业务不再使用它们。

IPulsePlaybackDriver 定义在独立包中，只依赖 UniTask，不引用 YooAssets、Alloy 或 Wwise。
宿主通过 SetPlaybackDriver 注入实现，由实现负责交付文件、持有资源、音轨和循环。
本仓库宿主实现为 Game.Video.VideoPlaybackCoordinator，支持 YooAssets 地址、HTTP/HTTPS URL、本地路径和 file URI。
其他项目可实现自己的驱动，使用 Addressables、自建下载器或其他音频系统。
未注入驱动时继续支持本地单次无音轨播放；带音频或循环的参数需要宿主驱动，缺失时明确报错。

PrepareVideoAsync、StartPreparedVideo、PauseVideo、ResumeVideo、SeekVideoAsync、CloseVideoAsync 是驱动控制视频后端的辅助入口。
普通业务调用 Play/PlayAsync、Pause、Resume、Seek/SeekAsync、Replay、Close/CloseAsync。
不要在驱动回调里调用同一个业务入口，否则会再次进入驱动。

预览界面已自动注入驱动。宿主 VideoPackage 使用 RawFileBuildPipeline，Wwise 继续使用 AudioPackage。
CDN 视频先完整下载到独占临时文件再解码；宿主将下载和各准备阶段映射为整体进度，组件只更新填充量与百分比。
循环复用文件、纹理和材质，最终轮次结束才发送 Completed，Seek 不重置已完成轮次。
关闭后等待视频原生读取退出，再释放快照或下载文件；Wwise 播放依赖由音频模块持有到原生声音退出。
此次未修改原生 ABI，继续使用 ABI 3 DLL。
宿主具体接入、音轨制作和验收步骤见 Unity 工程 Assets/Res/Video/README.md。
本次只做静态检查，尚未由用户编译运行验证。

## 外置 SRT 字幕

组件 Inspector 新增“字幕 / Subtitle Text”，绑定普通 Unity UI Text，保留项目字体、字号和排版。
自动换行，保留 SRT 多行文字；关闭、失败、视频结束或切换无字幕请求时清空文字。
外置字幕不需要重新编译原生插件。

宿主可额外实现 IPulseSubtitlePlaybackDriver，不需要修改原有 IPulsePlaybackDriver。
通过 Play(videoLocation, audioEventKey, subtitleLocation, loopMode, playCount)
或 PlayAsync 发起请求，字幕文件交付仍由宿主实现，播放器包不依赖 YooAssets。
宿主在首帧准备完成后调用 SetSubtitleTrack(PulseSubtitleTrack.Parse(srtText)) 绑定时间轴。
SetSubtitlesEnabled(bool) 只控制显示，暂停、Seek、Replay 和循环跟随现有视频时钟。

PulseSubtitleTrack 支持 UTF-8 解码后的 SRT、BOM、常见换行、多行、乱序和重叠字幕，样式标签转为纯文本。
整体解析后保留时间索引和文字，逐帧二分查找，跨起止边界才重建显示字符串。
不实现 ASS 动效、卡拉 OK 或两行分页。Text 的宽度与字体由项目预制体设置，字幕物体保持激活。
工程侧参考 Assets/Res/Video/README.md，字幕可通过 TextAsset、本地文件或 HTTP/HTTPS 交付。
带字幕重载的字符串参数顺序为视频地址、音轨事件、字幕地址；无音轨时显式传入 null。原有无字幕 Play 重载保持兼容。

## 指定起点与播放控制

Play / PlayAsync 增加指定起点重载，例如：

```csharp
player.Play(videoLocation, audioEventKey, subtitleLocation, startTimeSeconds: 4.0);
await player.PlayAsync(videoLocation, audioEventKey, startTimeSeconds: 4.0);
```

起点必须为非负有限秒数，超过时长定位末帧；先定位再绑定显示，不先展示开头画面。
宿主可实现可选 IPulsePlaybackControlDriver，原有驱动接口保持兼容；本地无音轨模式直接支持。
首次起点仅影响第一轮；Replay / ReplayAsync 和后续循环从零开始。没有新增重复的 Restart。
ReplayAsync 等待新请求准备并开始播放，不等待视频播放结束。

组件提供 Pause、Resume、TogglePause、Seek / SeekAsync、SeekNormalized / SeekNormalizedAsync、
Close / CloseAsync、SetSubtitlesEnabled，以及 IsPlaying、IsPaused、PositionSeconds、NormalizedProgress。
按秒定位和按零至一的进度定位均保持原来的播放或暂停状态；准备期间仍禁止普通 Seek。
关键生命周期日志保留，不输出逐步骤耗时、吞吐、校验明细或测试探测日志。
本次未修改原生 ABI，无需重新编译 DLL；尚未编译或运行验证。