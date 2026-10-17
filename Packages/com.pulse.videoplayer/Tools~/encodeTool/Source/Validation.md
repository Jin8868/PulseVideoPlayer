# 本次实现验证记录

验证日期：2026-10-17。平台：Windows x64；FFmpeg 9.0.2 full shared。

- Build.ps1 使用本机 .NET Framework csc 成功生成 WPF WinExe，无 NuGet 依赖。
- 最终发布目录的 EXE 全新初始化成功：校验 7z、离线解压、验证版本、实际 libx264 编码、写入就绪标记。
- 再次初始化复用缓存，就绪标记时间未变化，没有再次解压。
- 移走一个必要 DLL 后，验证返回失败；恢复后可继续使用。
- 损坏离线包 SHA-256 不匹配时拒绝解压，没有生成安装缓存。
- 含中文、空格、方括号和 & 的输入路径成功导出 MP4、Pulse、独立 AAC 音轨、SRT 和元数据。
- ffprobe 验证最终 Pulse 为 H.264、yuv420p、30/1 固定帧率。
- 已有输出拒绝覆盖，原 Pulse 文件字节保持不变。
- 损坏 MP4 返回失败，没有提交半成品，任务临时目录已清理。
- 实际 FFmpeg 子进程启动后触发取消，任务及时退出，未遗留 FFmpeg，输出无半成品和临时目录。
- WPF 窗口可离屏创建和渲染；调用复制日志入口后，剪贴板包含中文完整日志。
- 菜单脚本使用本机 Unity 2022.3 的程序集和 netstandard 2.1 引用独立编译通过。
- 发布目录全部文件低于 100 MiB；最大为 56.99 MiB 的 FFmpeg 压缩包。

未执行整个 Unity 工程构建，未在 Unity 编辑器内点击菜单或执行完整交互式验收。
AES-CTR 加密与播放器解密不在本次实现范围内，界面已明确提示尚未启用。
临时素材、隔离缓存和验证程序不属于发布目录，也不提交 Git。

## 初始化界面与视频格式补充验证

- 未就绪时目录、文件、参数及转码按钮全部折叠；就绪后展开，缓存失效后重新折叠。
- 帧率、CRF、编码速度均为不可编辑的下拉框；默认 30 / 18 / veryfast 保持，24 / 20 / slow 保存恢复通过。
- 两次启动复用工具旁的 log/VideoTool.log 并追加内容，没有向用户配置目录创建 Logs。
- 实际导出验证单路 H.264、yuv420p、30/1 CFR、BT.709 SDR 和 MP4 容器，音轨独立导出。
- 明确标记为 PQ 和 HLG 的 8 位源视频均被拒绝，未留下半成品。
- 普通视频 RGB 区域与 Alpha None 正确；透明视频 RGB、Alpha 区域和 Straight 模式正确。
- 普通与透明产物均验证 moov 前置，Pulse UUID 元数据可回读。
- 未标记传递特性的源视频按 SDR BT.709 处理，并明确记录无法自动识别未标记 HDR 的警告。

## 组件工具整理与批量产物验证

- Tools~ 下仅有 encodeTool、buildTool；仓库根目录旧 Tools 和 Native 已清理。
- 窗口与转码脚本同处 encodeTool，脚本只维护 Scripts/VideoPipeline 一套；Unity 菜单路径已更新并独立编译通过。
- FFmpeg 缓存已逐文件校验迁移到 encodeTool/Dependencies/FFmpeg/Installed，新位置运行验证通过，旧 C 盘 FFmpeg 专属缓存已删除。
- 已验证 Installed、log、原生 Build 目录被 Git 忽略，原始 7z 可正常纳入版本管理。
- 复选框实际点击可累计多选，取消一个不影响另一个，全选和取消选择同步复选框状态。
- 使用两个真实 MP4、输入输出同目录完成批量导出：两个 Pulse 均生成，原 MP4 哈希未变，没有新增 MP4 或 metadata.json。
- 历史 metadata.json 保持原内容，独立音频和字幕保留，临时目录已清理。
- 所有 PowerShell 脚本语法解析通过；迁移后的 Windows CMake 源码配置生成通过。

最终检查：Windows x64 原生 CMake Release 构建成功，在隔离目录生成 DLL，保留原有 C4245 警告；未替换现有 Runtime 插件。最终 encodeTool 目录全新初始化通过，离线包解压至其内部 Dependencies/FFmpeg/Installed，未重新创建 C 盘 FFmpeg 缓存。
