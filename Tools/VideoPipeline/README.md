# 批量视频导出

先运行 Tools/SetupFFmpeg.ps1 安装 FFmpeg。

## 目录输入

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\VideoPipeline\ExportVideos.ps1 `
    -InputDirectory .\Tests\Source\Synthetic `
    -OutputDirectory .\Tests\Generated\MyExport -JsonLog
```

`-Recurse` 扫描子目录；`-Alpha` 对本批次采用透明上下拼接模式。
普通视频与透明视频应分成两次导出，Alpha 输入当前要求偶数宽高与直通 Alpha。
可配置 FrameRate、CRF、GOP、Preset，默认 30、18、60、veryfast。

## 多选文件

PowerShell 会话内可以直接传数组：

```powershell
& .\Tools\VideoPipeline\ExportVideos.ps1 `
    -InputPaths @('D:\Videos\first.mp4', 'D:\Videos\second.mov') `
    -OutputDirectory 'D:\Videos\Export' -JsonLog
```

Unity 面板用 UTF-8 JSON 文件传递多选路径，避免 powershell.exe -File 的数组参数限制：

```json
["D:/Videos/first.mp4", "D:/Videos/second.mov"]
```

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\VideoPipeline\ExportVideos.ps1 `
    -InputListFile 'D:\Videos\inputs.json' -OutputDirectory 'D:\Videos\Export' -JsonLog
```

InputPaths、InputDirectory 和 InputListFile 可合并使用，重复路径自动去重。
同名输入会提前拒绝；已有产物不覆盖，失败时可能留下部分产物，需移走后重试。
单个文件失败后继续处理其余文件，整批存在任何失败则退出码为 1。

## 产物

- `<name>.mp4`：H.264 High、8bit yuv420p、固定帧率、moov 前置、无音轨。
- `<name>.audio.m4a`：源有音轨时，提取第一条并统一转为 AAC 192k。
- `<name>.subtitleN.srt`：提取支持的文本字幕流；图片字幕不转换。
- `<name>.metadata.json`：测试用编码尺寸、显示尺寸、RGB/Alpha 区域和帧数。

目前没有 UUID Box 注入或加密，不输出完整 .pulse 文件。HDR 色调映射、预乘 Alpha 转换等也尚未实现。

## Unity 实时日志

`-JsonLog` 输出 UTF-8 JSON 行，公共字段与安装脚本相同：timestamp、level、stage、message。
另有 inputFile（当前文件）和 percent（当前阶段进度，未知时为 null）。
阶段包括 BatchStart、FileStart、Encode、Audio、Subtitle、FileCompleted、FileFailed、Completed、Failed。
视频进度根据已处理帧数计算，FFmpeg 默认每 0.5 秒报告一次；它不是整个批次的加权总进度。

面板隐藏启动进程、异步读取 UTF-8 标准输出及错误输出，日志入队后在编辑器主线程刷新。
不要在 Unity 主线程等待进程或读完整输出。收到 Completed 并确认退出码为 0 才表示本批次成功。
若面板增加取消功能，需要同时终止 PowerShell 和其 FFmpeg 子进程，并处理部分输出文件。