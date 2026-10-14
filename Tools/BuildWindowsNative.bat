@echo off
setlocal
chcp 65001 >nul
set "pulseUnityEditor=%~1"
if not defined pulseUnityEditor if exist "D:\Unity\2022.3.17f1c1\Editor\Unity.exe" set "pulseUnityEditor=D:\Unity\2022.3.17f1c1\Editor\Unity.exe"
if not defined pulseUnityEditor (
    echo Enter the full path to Unity.exe, without quotes:
    set /p "pulseUnityEditor=> "
)
if not defined pulseUnityEditor goto missingEditor
if not exist "%pulseUnityEditor%" goto missingEditor
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0BuildWindowsNative.ps1" -UnityEditorPath "%pulseUnityEditor%" %2 %3 %4 %5 %6 %7 %8 %9
set "pulseExitCode=%ERRORLEVEL%"
if "%~1"=="" pause
exit /b %pulseExitCode%

:missingEditor
echo [error][prepare] Unity.exe was not found. Pass its full path as the first argument.
if "%~1"=="" pause
exit /b 1
