@echo off
setlocal
chcp 65001 >nul

rem 优先使用首个参数，其次使用环境变量，最后使用本机当前的 Unity。
set "pulseUnityEditor=%PULSE_UNITY_EDITOR%"
if not defined pulseUnityEditor set "pulseUnityEditor=D:\Unity\2022.3.17f1c1\Editor\Unity.exe"
if not "%~1"=="" set "pulseUnityEditor=%~1"
if not exist "%pulseUnityEditor%" goto missingEditor
if not exist "%~dp0BuildAndroidNative.ps1" goto missingScript

echo [Pulse] 构建 Android ARM64 原生库和 Java 桥接包。
echo [Pulse] Unity: "%pulseUnityEditor%"
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0BuildAndroidNative.ps1" -UnityEditorPath "%pulseUnityEditor%" %2 %3 %4 %5 %6 %7 %8 %9
set "pulseExitCode=%ERRORLEVEL%"
echo.
if not "%pulseExitCode%"=="0" goto buildFailed
echo [Pulse] 构建完成，产物已放入组件的 Runtime\Plugins\Android：
echo         libPulseVideoNative.so
echo         PulseVideoBridge.aar
echo [Pulse] 接下来可回到 Unity 打 Android 包。
goto finish

:buildFailed
echo [Pulse] 构建失败，退出码：%pulseExitCode%。请查看上面的错误信息。
goto finish

:missingEditor
echo [Pulse] 找不到 Unity.exe："%pulseUnityEditor%"
echo [Pulse] 可将 Unity.exe 拖到此 BAT 上，或设置 PULSE_UNITY_EDITOR 环境变量。
set "pulseExitCode=1"
goto finish

:missingScript
echo [Pulse] 缺少脚本："%~dp0BuildAndroidNative.ps1"
set "pulseExitCode=1"

:finish
if "%~1"=="" pause
exit /b %pulseExitCode%
