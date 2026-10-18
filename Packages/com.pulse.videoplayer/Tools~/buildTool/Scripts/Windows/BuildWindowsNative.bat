@echo off
setlocal
chcp 65001 >nul

rem 优先使用首个参数，其次使用环境变量，最后使用本机当前的 Unity。
set "pulseUnityEditor=%PULSE_UNITY_EDITOR%"
if not defined pulseUnityEditor set "pulseUnityEditor=D:\Unity\2022.3.17f1c1\Editor\Unity.exe"
if not "%~1"=="" set "pulseUnityEditor=%~1"
if not exist "%pulseUnityEditor%" goto missingEditor
if not exist "%~dp0BuildWindowsNative.ps1" goto missingScript

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0BuildWindowsNative.ps1" -UnityEditorPath "%pulseUnityEditor%" %2 %3 %4 %5 %6 %7 %8 %9
set "pulseExitCode=%ERRORLEVEL%"
goto finish

:missingEditor
echo [Pulse] 找不到 Unity.exe："%pulseUnityEditor%"
echo [Pulse] 可将 Unity.exe 拖到此 BAT 上，或设置 PULSE_UNITY_EDITOR 环境变量。
set "pulseExitCode=1"
goto finish

:missingScript
echo [Pulse] 缺少脚本："%~dp0BuildWindowsNative.ps1"
set "pulseExitCode=1"

:finish
if "%~1"=="" pause
exit /b %pulseExitCode%
