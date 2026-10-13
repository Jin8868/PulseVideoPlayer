# PulseVideoPlayer 运行时读取

当前已实现本地文件数据源、Pulse v1 容器扫描、严格 UTF-8/JSON 解析、元数据校验和异步加载。
已加入 Windows x64 / D3D11 首帧解码、GPU 纹理桥接与 PulsePlayer 门面，已在当前 Windows 宿主编译并验证首帧显示及基础打开/关闭。连续播放、音频与 Seek 尚未实现。构建及验证步骤见仓库 Native/Windows/README.md。

```csharp
PulseMetadata metadata = await PulseMetadataLoader.ReadAsync(filePath, cancellationToken);
```

ReadAsync 使用独立线程完成文件 IO，成功、失败、取消均切回 Unity 主线程交付。
用于运行中的 Unity PlayerLoop；编辑器菜单检查使用同步 PulseMetadataReader.Read。

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

## 工程验证

宿主菜单：★AlloyFramework★/视频/读取Pulse测试元数据。
读取 StreamingAssets/PulseTests 下的 Synthetic、Alpha、Sintel 三组素材并打印尺寸和时间信息。
验证程序源码位于仓库 Tests/Runtime/PulseReaderChecks.cs，可使用 Unity 随附 Mono/Roslyn 独立运行。
素材不提交 Git，需要从 Tests/Generated 复制到宿主的 StreamingAssets/PulseTests。