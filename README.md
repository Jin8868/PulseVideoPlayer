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

在仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\SetupFFmpeg.ps1
```

脚本固定下载 FFmpeg 9.0.2 essentials，并使用仓库记录的 SHA-256 校验 ZIP。
安装路径为 `Tools/FFMpeg/ffmpeg-9.0.2-essentials_build/bin`，保留下载包中的许可证文件。
已有该版本目录时只验证并复用，不覆盖，也不验证已有文件来源；需要重新下载安装时先移走该目录。
安装结束后自动删除 Tools/Cache/FFmpeg 中的 ZIP、临时解压目录与失败下载文件。清理失败会输出错误并返回非零退出码；VerifyOnly 模式不清理缓存。
这些本机目录均不提交 Git，也不随游戏发布，无需设置系统 PATH。

仅验证已有安装：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\SetupFFmpeg.ps1 -VerifyOnly
```

验证内容：ffmpeg/ffprobe 版本与退出码，以及实际合成一帧并执行 libx264 编码。
`Tools`、`Native` 源码和仓库根部的 `Tests` 均位于 UPM 包外，正常 Unity 构建不会收集它们。
不要将离线工具和源素材复制到宿主的 StreamingAssets 或资源打包目录。

## 开发状态

当前包含目录骨架、包配置和 FFmpeg 初始化脚本，尚未实现播放器、原生插件或转码工具。
此目录位于游戏仓库之外，使用独立 Git 仓库管理。

## Unity 面板日志接口

安装脚本默认输出带时间、级别和阶段的文本日志。添加 `-JsonLog` 后，每行输出一个 JSON 对象：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\SetupFFmpeg.ps1 -JsonLog
```

字段为 `timestamp`、`level`、`stage`、`message`，每行输出后立即刷新，编码为 UTF-8。
阶段包含 Start、Download、Checksum、Extract、Verify、Install、Cleanup、Completed、Error 和 Failed。
这是阶段日志，不包含下载百分比。未来视频导出工具需要独立提供转码进度接口。

Unity 面板启动隐藏进程，设置 UseShellExecute=false、CreateNoWindow=true，并异步读取标准输出和标准错误。
两路输出都指定 UTF-8。进程回调将日志入队，由 EditorApplication.update 在 Unity 主线程更新面板。
使用异步进程等待，避免阻塞编辑器。退出码 0 表示完成，1 表示失败；仅在收到 Completed 且退出码为 0 时提示安装成功。
脚本自身日志全部写到标准输出；标准错误用于接收原生工具或 PowerShell 启动异常。
运行安装时会持有独占锁，避免多个面板同时安装或清理同一份缓存。