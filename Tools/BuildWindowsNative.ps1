[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$UnityEditorPath,
    [string]$CMakePath,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$JsonLog
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
$repositoryPath = Split-Path -Parent $PSScriptRoot
$buildPath = Join-Path $repositoryPath 'Native\Windows\build'

function Write-BuildLog {
    param([string]$Level, [string]$Stage, [string]$Message)
    if ($JsonLog) {
        $line = [ordered]@{
            timestamp = [DateTime]::UtcNow.ToString('o')
            level = $Level
            stage = $Stage
            message = $Message
        } | ConvertTo-Json -Compress
    } else {
        $line = "[$Level][$Stage] $Message"
    }
    [Console]::Out.WriteLine($line)
    [Console]::Out.Flush()
}

function Invoke-CMakeStep {
    param([string]$Stage, [string[]]$Arguments)
    Write-BuildLog 'info' $Stage '开始执行 CMake。'
    # PowerShell 5 将原生 stderr 包装成错误对象，允许逐行记录后再依据退出码判断失败。
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $script:CMakePath @Arguments 2>&1 | ForEach-Object {
            Write-BuildLog 'info' $Stage ([string]$_)
        }
        $nativeExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($nativeExitCode -ne 0) {
        throw "CMake $Stage 失败，退出码：$nativeExitCode。"
    }
}

try {
    # 使用安装的 Unity 原生插件头文件，不复制或下载另一份接口定义。
    $editorExecutable = (Resolve-Path -LiteralPath $UnityEditorPath).Path
    $pluginAPI = Join-Path (Split-Path -Parent $editorExecutable) 'Data\PluginAPI'
    if (!(Test-Path -LiteralPath (Join-Path $pluginAPI 'IUnityGraphicsD3D11.h'))) {
        throw "找不到 Unity PluginAPI：$pluginAPI"
    }
    if (!$CMakePath) {
        $cmakeCommand = Get-Command cmake.exe -ErrorAction SilentlyContinue
        if ($cmakeCommand) {
            $CMakePath = $cmakeCommand.Source
        } else {
            $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
            if (Test-Path -LiteralPath $vswhere) {
                $visualStudioPath = & $vswhere -latest -products '*' `
                    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
                if ($visualStudioPath) {
                    $CMakePath = Join-Path $visualStudioPath `
                        'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
                }
            }
        }
    }
    if (!$CMakePath -or !(Test-Path -LiteralPath $CMakePath)) {
        throw '未找到 CMake。安装 VS2022 的 C++ CMake 工具，或通过 -CMakePath 指定 cmake.exe。'
    }
    Write-BuildLog 'info' 'prepare' "Unity PluginAPI：$pluginAPI"
    Write-BuildLog 'info' 'prepare' "CMake：$CMakePath"
    Invoke-CMakeStep 'configure' @(
        '-S', (Join-Path $repositoryPath 'Native\Windows'), '-B', $buildPath,
        '-G', 'Visual Studio 17 2022', '-A', 'x64', "-DUNITY_PLUGIN_API=$pluginAPI"
    )
    Invoke-CMakeStep 'build' @('--build', $buildPath, '--config', $Configuration, '--parallel')

    # 只有构建成功才安装 DLL；Unity 已加载旧 DLL 时需要先关闭编辑器。
    $compiledPlugin = Join-Path $buildPath "$Configuration\PulseVideoNative.dll"
    $pluginDirectory = Join-Path $repositoryPath `
        'Packages\com.pulse.videoplayer\Runtime\Plugins\Windows\x86_64'
    New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
    $installedPlugin = Join-Path $pluginDirectory 'PulseVideoNative.dll'
    Copy-Item -LiteralPath $compiledPlugin -Destination $installedPlugin -Force
    $metadataPath = "$installedPlugin.meta"
    if (!(Test-Path -LiteralPath $metadataPath)) {
        $pluginGuid = [Guid]::NewGuid().ToString('N')
        $metadata = @"
fileFormatVersion: 2
guid: $pluginGuid
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: x86_64
        OS: Windows
  - first:
      Standalone: Win64
    second:
      enabled: 1
      settings:
        CPU: x86_64
  userData:
  assetBundleName:
  assetBundleVariant:
"@
        # Unity 的 YAML 解析器需要完整的末行；空映射值也保留空格。
        $metadata = $metadata.Replace('Any:', 'Any: ').Replace('userData:', 'userData: ')
        $metadata = $metadata.Replace('assetBundleName:', 'assetBundleName: ')
        $metadata = $metadata.Replace('assetBundleVariant:', 'assetBundleVariant: ')
        [IO.File]::WriteAllText($metadataPath, $metadata + "`n", (New-Object Text.UTF8Encoding($false)))
    }
    # 保留已有 GUID 和平台设置，修补早期脚本生成文件时遗漏的末尾换行。
    $existingMetadata = [IO.File]::ReadAllText($metadataPath)
    if (!$existingMetadata.EndsWith("`n")) {
        [IO.File]::WriteAllText($metadataPath, $existingMetadata + "`n", (New-Object Text.UTF8Encoding($false)))
    }
    Write-BuildLog 'info' 'complete' "DLL 已安装：$installedPlugin"
    exit 0
} catch {
    Write-BuildLog 'error' 'failed' $_.Exception.Message
    exit 1
}
