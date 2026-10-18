# Android 原生后端

源码实现目标：Android ARM64、API 24+、OpenGLES3、MP4 单路 CFR H.264、8 位 SDR。
本次没有执行编译、测试、打包或真机验收；实际解码器兼容性与硬件状态须在目标设备确认。
Vulkan 与 32 位 ARM 尚未实现；不要把源码已接入视为实机验证通过。

## 渲染与回退

- 首选 MediaExtractor → MediaCodec → Surface / SurfaceTexture → OES 纹理 → RGBA8 输出。
- Surface 创建、解码配置、到帧或更新异常时，最多重试一次线性 ByteBuffer 输出。
- 线性输出接受明确的 COLOR_FormatYUV420Planar（19，I420）与
  COLOR_FormatYUV420SemiPlanar（21，NV12）。按 stride、slice-height、crop 提取有效区域，
  复用一份单帧 I420 缓冲，上传三张 R8 平面，使用 GLES Shader 转换 BT.601 / BT.709 limited-range RGB。
- flexible / 厂商私有格式没有可确定的平面布局时报告 Faulted，不猜测 NV12 / NV21 顺序。
  回退仍使用系统 MediaCodec，不引入 FFmpeg 运行时软解，也不保证换成另一个硬件解码器。
- Surface 路径中的系统 YUV 转换是否严格遵循颜色元数据仍需目标机测试。
- 输出保持与 Windows 一致的左上角图像行序，复用现有双区域透明视频 UI Shader。
- 解码线程不访问 Unity 对象。目标帧来自 C# 游戏时钟，未来帧等待目标时刻，旧帧可丢弃。
- Seek 回退前一个关键帧并精确追帧；新代次会废弃旧解码结果，GPU 围栏完成后才发布 CompletedSeek。
- Close 先停止解码线程，再于帧末放入 GLES 围栏，完成后释放 Surface、原生窗口和 GL 对象。

## 首次构建

一键入口：双击 `Tools~/buildTool/Scripts/Android/BuildAndroidNative.bat`。
默认使用本机 `D:\Unity\2022.3.17f1c1\Editor\Unity.exe`，同时构建 `.so` 与 `.aar`，
成功后自动放入组件 `Runtime/Plugins/Android`。窗口会保留构建结果，失败时保留错误信息。
更换 Unity 时，可将目标 `Unity.exe` 拖到 BAT 上，或设置 `PULSE_UNITY_EDITOR` 环境变量。
命令行传参时，首个参数是 Unity.exe，后面可继续传入 PowerShell 构建脚本的选项。
BAT 不执行 Unity 打包、Gradle 打包或手机安装。

关闭 Unity 后，从 PulseVideoPlayer 仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\Packages\com.pulse.videoplayer\Tools~\buildTool\Scripts\Android\BuildAndroidNative.ps1 `
  -UnityEditorPath 'D:\Unity\2022.3.17f1c1\Editor\Unity.exe'
```

默认使用该 Unity 的 NDK、SDK 和 OpenJDK。已查看当前本机 NDK 为 23.1.7779620，
脚本不固定 NDK 版本。需要 CMake 3.20+ 和 Ninja；可使用 VS 安装的工具或手动指定。
可选参数：`-NdkPath`、`-SdkPath`、`-JdkPath`、`-CMakePath`、`-NinjaPath`、
`-Configuration Debug`、`-MinApiLevel 24`。

构建中间文件只进入 `Tools~/buildTool/Build/Android`。
全部构建完成后发布到 `Runtime/Plugins/Android`：

| 文件 | 用途 |
| --- | --- |
| libPulseVideoNative.so | ARM64 系统解码、游戏目标帧调度与 GLES 渲染 |
| PulseVideoBridge.aar | SurfaceTexture 桥接与供 R8 使用的 JNI 保留规则 |

首次必须同时构建两者，Java 桥接有变化时也要重新生成 AAR。
脚本生成 Android 插件导入设置并保留已有 meta / GUID。
原生库使用静态 libc++，不额外覆盖宿主已有的 libc++_shared.so。

## Unity 配置

1. 切换 Android，使用 IL2CPP 并勾选 ARM64；本版没有 ARMv7 原生库。
2. 最低 Android API 至少 24，并且不低于构建脚本传入的 MinApiLevel。
3. 关闭 Auto Graphics API，将图形 API 设置为 OpenGLES3，移除 Vulkan。
4. 首次原生构建完成后回到 Unity，确认 `.so` 与 `.aar` 仅启用 Android，so 的 CPU 为 ARM64。
5. 由你执行 Unity 打包与真机验证。

## APK 内置视频

真实文件直接按需读；Unity 的 `jar:file://...!/assets/...pulse` 使用 AssetManager 获取
fd + offset + length，C# 元数据读取和 MediaExtractor 都读取该区间，不整文件拷出、不加载 TextAsset.bytes。
APK 内路径请通过异步 PulseMetadataLoader / PulsePlayer 入口打开，Java 初始化在主线程完成。
同步 PulseMetadataReader 的 jar 分支要求此前已完成该初始化。
Android `content://` 通用 ContentResolver URI 尚未接入，本次只支持真实文件与 APK assets jar 路径。

APK 内资源必须不压缩。在项目已有的 `Assets/Plugins/Android/mainTemplate.gradle`
的 `android` 块中合并以下配置；没有模板时先在 Publishing Settings 启用 Custom Main Gradle Template：

```groovy
aaptOptions {
    noCompress 'pulse', 'pa'
}
```

不要用这段覆盖其他插件的 noCompress 条目。压缩条目无法获得文件区间，组件会明确报错。
宿主若已将 YooAsset 内置视频拷出为文件，仍可沿用其真实路径；上述 jar 支持不改变宿主分发策略。

## 仅修改原生代码时的迭代

先由 Unity 导出一次 Gradle 工程。后续 C++ 改动可运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\Packages\com.pulse.videoplayer\Tools~\buildTool\Scripts\Android\BuildAndroidNative.ps1 `
  -UnityEditorPath 'D:\Unity\2022.3.17f1c1\Editor\Unity.exe' `
  -SkipBridgeBuild `
  -GradleProjectPath 'D:\你的Android导出工程'
```

脚本更新导出工程的 `unityLibrary/src/main/jniLibs/arm64-v8a/libPulseVideoNative.so`
和 `unityLibrary/libs/PulseVideoBridge.aar`，不会执行 Gradle 或安装应用。
然后由你在导出工程根目录执行：

```powershell
.\gradlew.bat :launcher:assembleDebug
adb install -r .\launcher\build\outputs\apk\debug\launcher-debug.apk
```

APK 实际名称以导出工程产物为准。覆盖安装要求签名与现有安装一致；安装后重新启动应用。
不能只替换项目内的 so 就更新手机已安装应用，也不能在压缩 APK 中替换条目后沿用旧签名。
C# / 资源有变化时需要 Unity 更新导出内容；Java 有变化时去掉 SkipBridgeBuild。

## 待真机确认与后续边界

普通视频、透明渐变、上下方向、颜色、暂停/恢复、精确 Seek、连续 Seek、末帧结束、
快速开关、Surface 回退、资源计数归零均尚未真机验收。
按 todo 的完整生命周期要求，前后台保存进度并自动关闭/重建播放器仍是后续工作；
本版图形设备丢失会关闭原生会话并由现有 Failed 通道报告，不承诺后台恢复后继续原会话。
推荐先在前台完成播放闭环，再补目标机型与生命周期验收。

排查日志可使用 `adb logcat -s Pulse Unity`，回退会输出
`Surface unavailable; falling back to linear I420/NV12`，线性输出日志包含 format / stride / slice。
硬解与软解不能单靠“使用 MediaCodec”判断，需结合实机编解码器和性能记录。

参考：[NDK Media API](https://developer.android.com/ndk/reference/group/media)、
[SurfaceTexture](https://developer.android.com/reference/android/graphics/SurfaceTexture)、
[Unity Gradle 结构](https://docs.unity3d.com/2022.3/Documentation/Manual/android-gradle-overview.html)、
[ADB](https://developer.android.com/tools/adb)。
