# 离线依赖

本目录随组件提交 GitHub，与上级目录的工具 EXE、Source 和 Scripts 分开。

- FFmpeg/ffmpeg-9.0.2-full_build-shared.7z：原始离线包，56.99 MiB。
- FFmpeg/LICENSE：原包许可证的独立副本，原包中的许可证继续保留。
- 7zip/7zr.exe：官方 7-Zip 26.04 独立解压程序（x86，可运行于 Windows x64）。
- 7zip/LICENSE.txt：7-Zip 官方许可说明。

FFmpeg 来源：https://www.gyan.dev/ffmpeg/builds/
FFmpeg 压缩包 SHA-256：4D2060A8B34A940AA47D785142055BB92A63053781E55F2ACE4546EDD519A8F5
当前构建启用了 GPL/version3，分发说明与授权以原包 LICENSE 为准。
FFmpeg 源码与构建配方入口：https://github.com/GyanD/codexffmpeg

7zr 来源：https://github.com/ip7z/7zip/releases/download/26.04/7zr.exe
7zr SHA-256：256FECA8E274E5DA655E2A284FABAFD9F554365EB164862089DACD4E8276D282
该可执行程序自身标注 Public domain，源码入口：https://github.com/ip7z/7zip/tree/26.04

工具解压目录位于本目录的 FFmpeg/Installed，已加入 Git 忽略规则；只提交原始 7z 和解压程序。
解压后的 DLL 不提交 Git，游戏运行时也不使用这份离线转码依赖。
