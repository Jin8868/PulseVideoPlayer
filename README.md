# PulseVideoPlayer

独立的 Unity 视频播放器与离线视频制作工具。当前目录是源码工程，不是单独的 Unity 项目。

## 目录

- `Packages/com.pulse.videoplayer`：供 Unity 加载的 UPM 包，运行时依赖 Unity 和 UniTask。
- `Native`：原生解码插件源码与构建脚本。运行时插件产物放入包的 `Runtime/Plugins`。
- `Tools/VideoPipeline`：调用 FFmpeg/ffprobe 的离线转码、音轨提取和元数据工具。
- `Tests`：测试素材生成脚本及验证资源。Unity 测试场景放在宿主项目中。

## 当前宿主

`D:/WorkSpeace/UnityProject/game-template/UnityProj`，Unity 2022.3.17f1c1。

宿主通过 `file:../../../PulseVideoPlayer/Packages/com.pulse.videoplayer` 引用本地包。
播放器核心不依赖 AlloyFramework 或 YooAsset；业务适配器放在宿主项目中。

## FFmpeg

FFmpeg/ffprobe 供离线工具调用，可解压到 Tools/FFmpeg/Windows，保留许可证文件；该目录不提交 Git，也不随游戏发布。
`Tools`、`Native` 源码和仓库根部的 `Tests` 均位于 UPM 包外，正常 Unity 构建不会收集它们。
不要将离线工具和源素材复制到宿主的 StreamingAssets 或资源打包目录。

## 开发状态

当前仅包含目录骨架与包配置，尚未实现播放器、原生插件或转码工具。
此目录位于游戏仓库之外，使用独立 Git 仓库管理。
