<#
.SYNOPSIS
构建组件内的离线转码工具，使用随 Windows 提供的 .NET Framework 编译器。
#>
[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$toolRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = $toolRoot }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows x64 的 .NET Framework 编译器。' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$references = @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll',
    'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Xaml.dll')
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/langversion:5', '/utf8output',
    ('/out:' + (Join-Path $OutputDirectory 'VideoTool.exe')),
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')))
foreach ($reference in $references) { $arguments += '/reference:' + (Join-Path $frameworkDirectory $reference) }
foreach ($reference in @('PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory ('WPF/' + $reference))
}
$arguments += @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Select-Object -ExpandProperty FullName)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw '视频工具编译失败。' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'VideoTool.exe.config') -Destination $OutputDirectory -Force

# 默认直接使用同目录下的唯一脚本源；仅为隔离验证输出复制一套脚本。
if (-not $OutputDirectory.Equals($toolRoot, [StringComparison]::OrdinalIgnoreCase)) {
    $scriptTarget = Join-Path $OutputDirectory 'Scripts'
    New-Item -ItemType Directory -Path $scriptTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $toolRoot 'Scripts/*') -Destination $scriptTarget -Recurse -Force
}
$dependenciesDirectory = Join-Path $OutputDirectory 'Dependencies'
$localInstallPrefix = [IO.Path]::GetFullPath(
    (Join-Path $dependenciesDirectory 'FFmpeg/Installed')).TrimEnd('\') + '\'
$publishRoots = @((Join-Path $OutputDirectory 'VideoTool.exe'),
    (Join-Path $OutputDirectory 'VideoTool.exe.config'), (Join-Path $OutputDirectory 'Scripts'))
if (Test-Path -LiteralPath $dependenciesDirectory) { $publishRoots += $dependenciesDirectory }
foreach ($publishRoot in $publishRoots) {
    foreach ($file in @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File)) {
        if ($file.FullName.StartsWith($localInstallPrefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($file.Length -gt 100MB) { throw "发布文件超过 GitHub 100 MiB 限制：$($file.FullName)" }
    }
}
Write-Host "已生成：$OutputDirectory/VideoTool.exe"