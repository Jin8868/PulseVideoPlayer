# 测试素材与读取器验证

Source 保存原片，Generated 保存导出产物；两个素材目录均忽略 Git。
Runtime/PulseReaderChecks.cs 是读取器验证源码，正常纳入版本管理。

## 准备素材

Tools/PrepareTestVideos.ps1 可生成帧号视频和透明视频，并导出独立音轨。
Source/Sintel/Sintel.2010.1080p.mkv 是用户下载的完整原片，MD5：377aa3357df96a9ae085169a74d2a3bf。
Sintel：Blender Foundation / Durian Open Movie Project，2010，CC BY 3.0。
来源：https://durian.blender.org/download/

Generated 的三组 .pulse 与已有的 *.audio.m4a 复制到宿主工程 StreamingAssets/PulseTests 同名子目录。
Alpha 没有音轨，不生成空音频。源码和测试素材分别管理，素材不随 GitHub 拉取自动出现。

## 当前读取验证

Synthetic：1920x1080，30fps，1800 帧。
Alpha：编码 800x1602、显示 800x800，30fps，900 帧；Alpha 从 y=802 开始。
Sintel：1920x818，30fps，26640 帧。

验证覆盖实际素材、短片段读取、扩展 Box、截断文件、重复 UUID、未知版本、区域越界、
零分母、负帧数、重复 JSON 字段、预取消和关闭后的读取。
使用包装数据源统计读取字节，证明读取元数据时跳过视频载荷。
核心源码不依赖 Unity 对象，可使用 Unity 随附的 Mono/Roslyn 独立编译及运行检查。
异步加载器额外依赖宿主 UniTask，编译检查需引用 UniTask.dll 与 netstandard 门面。

宿主菜单：★AlloyFramework★/视频/读取Pulse测试元数据。
当前检查容器与元数据，不执行视频解码或音频播放。