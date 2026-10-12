<#
.SYNOPSIS
准备 PulseVideoPlayer 离线工具使用的 Windows FFmpeg。
.DESCRIPTION
下载固定版本并校验 SHA-256。安装结束后删除 ZIP 与临时解压文件。
.PARAMETER VerifyOnly
仅验证已有安装，不下载、解压或清理缓存。
.PARAMETER JsonLog
每行输出一个 JSON 日志对象，供 Unity 面板实时读取。
#>
[CmdletBinding()]
param(
    [switch]$VerifyOnly,
    [switch]$JsonLog
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

$ffmpegVersion = '9.0.2'
$archiveName = "ffmpeg-$ffmpegVersion-essentials_build.zip"
$downloadURL = "https://www.gyan.dev/ffmpeg/builds/packages/$archiveName"
$expectedSHA256 = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
$installRoot = Join-Path $PSScriptRoot 'FFMpeg'
$versionDirectoryName = "ffmpeg-$ffmpegVersion-essentials_build"
$installDirectory = Join-Path $installRoot $versionDirectoryName
$binaryDirectory = Join-Path $installDirectory 'bin'
$cacheDirectory = Join-Path $PSScriptRoot 'Cache/FFmpeg'
$archivePath = Join-Path $cacheDirectory $archiveName
$setupExitCode = 0
$installationLock = $null

function Write-SetupLog {
    param(
        [string]$Stage,
        [string]$Message,
        [ValidateSet('Info', 'Warning', 'Error')]
        [string]$Level = 'Info'
    )

    # 逐行写入 UTF-8 标准输出并立即刷新，让面板在进程运行期间收到日志。
    $timestamp = [DateTimeOffset]::Now.ToString('o')
    if ($JsonLog) {
        $logLine = [ordered]@{
            timestamp = $timestamp
            level = $Level
            stage = $Stage
            message = $Message
        } | ConvertTo-Json -Compress
    } else {
        $logLine = "[$timestamp][$Level][$Stage] $Message"
    }
    [Console]::Out.WriteLine($logLine)
    [Console]::Out.Flush()
}

function Remove-FFmpegCache {
    # 只删除此工具的缓存；验证绝对路径并拒绝链接，避免清理越界。
    $expectedCachePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Cache/FFmpeg'))
    $resolvedCachePath = [IO.Path]::GetFullPath($cacheDirectory)
    if (-not $resolvedCachePath.Equals($expectedCachePath, [StringComparison]::OrdinalIgnoreCase)) {
        throw '缓存清理路径超出预期目录。'
    }
    if (-not (Test-Path -LiteralPath $resolvedCachePath)) {
        Write-SetupLog -Stage 'Cleanup' -Message '没有需要清理的缓存。'
        return
    }
    foreach ($ancestorPath in @($PSScriptRoot, (Join-Path $PSScriptRoot 'Cache'), $resolvedCachePath)) {
        $ancestorItem = Get-Item -LiteralPath $ancestorPath -Force
        if (($ancestorItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "缓存路径包含链接，停止清理：$ancestorPath"
        }
    }
    $pendingDirectories = New-Object 'System.Collections.Generic.Queue[string]'
    $pendingDirectories.Enqueue($resolvedCachePath)
    while ($pendingDirectories.Count -gt 0) {
        $currentDirectory = $pendingDirectories.Dequeue()
        foreach ($cacheItem in @(Get-ChildItem -LiteralPath $currentDirectory -Force)) {
            if (($cacheItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "缓存内容包含链接，停止清理：$($cacheItem.FullName)"
            }
            if ($cacheItem.PSIsContainer) {
                $pendingDirectories.Enqueue($cacheItem.FullName)
            }
        }
    }
    Write-SetupLog -Stage 'Cleanup' -Message '删除下载 ZIP、临时解压目录和失败下载文件。'
    Remove-Item -LiteralPath $resolvedCachePath -Recurse -Force
    Write-SetupLog -Stage 'Cleanup' -Message 'FFmpeg 缓存已全部删除。'
}

function Test-FFmpegInstallation {
    param(
        [string]$BinaryDirectory,
        [string]$ExpectedVersion
    )

    # 完整读取原生进程输出再判断退出码，避免截断管道提前终止进程。
    foreach ($toolName in @('ffmpeg', 'ffprobe')) {
        $executablePath = Join-Path $BinaryDirectory "$toolName.exe"
        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            throw "缺少程序：$executablePath"
        }
        $versionOutput = @(& $executablePath -version)
        if ($LASTEXITCODE -ne 0) {
            throw "$toolName 运行失败，退出码：$LASTEXITCODE"
        }
        $versionPattern = '^' + $toolName + ' version ' + [regex]::Escape($ExpectedVersion) + '(?:[-\s]|$)'
        if ($versionOutput.Count -eq 0 -or $versionOutput[0] -notmatch $versionPattern) {
            throw "$toolName 版本不符合要求，应为 $ExpectedVersion。"
        }
        Write-SetupLog -Stage 'Verify' -Message $versionOutput[0]
    }

    # 合成一帧并实际编码，验证 libx264 能正常工作。
    Write-SetupLog -Stage 'Verify' -Message '开始验证 H.264 编码。'
    $ffmpegPath = Join-Path $BinaryDirectory 'ffmpeg.exe'
    $encodingOutput = @(& $ffmpegPath -hide_banner -loglevel error -f lavfi `
        -i 'color=c=black:s=64x64:r=1' -frames:v 1 -an -c:v libx264 -pix_fmt yuv420p -f null -)
    if ($LASTEXITCODE -ne 0) {
        throw "H.264 编码验证失败，退出码：$LASTEXITCODE"
    }
    foreach ($outputLine in $encodingOutput) {
        Write-SetupLog -Stage 'Verify' -Message ([string]$outputLine)
    }
    Write-SetupLog -Stage 'Verify' -Message 'H.264 编码验证通过。'
}

try {
    Write-SetupLog -Stage 'Start' -Message "准备 FFmpeg $ffmpegVersion。"
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
        -not [Environment]::Is64BitOperatingSystem) {
        throw '此脚本用于 64 位 Windows。'
    }

    # 安装锁覆盖安装与清理全过程，避免并发运行删除另一实例的下载文件。
    if (-not $VerifyOnly) {
        New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
        $lockPath = Join-Path $installRoot '.setup.lock'
        $installationLock = [IO.File]::Open(
            $lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }

    # 已有安装只验证并复用，不覆盖文件。
    if (Test-Path -LiteralPath $installDirectory) {
        Write-SetupLog -Stage 'Verify' -Message "验证已有安装：$installDirectory"
        Test-FFmpegInstallation -BinaryDirectory $binaryDirectory -ExpectedVersion $ffmpegVersion
        Write-SetupLog -Stage 'Verify' -Message '已复用本机安装；未校验已有文件的来源。'
    } else {
        if ($VerifyOnly) {
            throw "尚未安装 FFmpeg。请先运行：& '$PSCommandPath'"
        }

        # 固定 URL 与校验值；下载进度条关闭，阶段日志始终实时输出。
        New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null
        if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
            $downloadPath = "$archivePath.$([Guid]::NewGuid().ToString('N')).download"
            Write-SetupLog -Stage 'Download' -Message "开始下载：$downloadURL"
            $previousSecurityProtocol = [Net.ServicePointManager]::SecurityProtocol
            try {
                [Net.ServicePointManager]::SecurityProtocol =
                    $previousSecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -Uri $downloadURL -OutFile $downloadPath -UseBasicParsing -TimeoutSec 120
            } finally {
                [Net.ServicePointManager]::SecurityProtocol = $previousSecurityProtocol
            }
            Write-SetupLog -Stage 'Download' -Message '下载完成，开始校验 SHA-256。'
            $downloadHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash
            if ($downloadHash -ne $expectedSHA256) {
                throw '下载文件 SHA-256 不匹配，未安装。'
            }
            Move-Item -LiteralPath $downloadPath -Destination $archivePath
        }
        $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
        if ($archiveHash -ne $expectedSHA256) {
            throw 'ZIP SHA-256 不匹配，未安装。'
        }
        Write-SetupLog -Stage 'Checksum' -Message 'ZIP SHA-256 校验通过。'

        # 先验证解压产物，再复制到安装目录，并保留许可证文件。
        $stagingDirectory = Join-Path $cacheDirectory ([Guid]::NewGuid().ToString('N'))
        Write-SetupLog -Stage 'Extract' -Message '开始解压 FFmpeg。'
        Expand-Archive -LiteralPath $archivePath -DestinationPath $stagingDirectory
        $stagedInstallDirectory = Join-Path $stagingDirectory $versionDirectoryName
        $stagedBinaryDirectory = Join-Path $stagedInstallDirectory 'bin'
        Test-FFmpegInstallation -BinaryDirectory $stagedBinaryDirectory -ExpectedVersion $ffmpegVersion
        $resolvedSource = [IO.Path]::GetFullPath($stagedInstallDirectory)
        $resolvedDestination = [IO.Path]::GetFullPath($installDirectory)
        $sourceBoundary = [IO.Path]::GetFullPath($stagingDirectory).TrimEnd('\') + '\'
        $destinationBoundary = [IO.Path]::GetFullPath($installRoot).TrimEnd('\') + '\'
        if (-not $resolvedSource.StartsWith($sourceBoundary, [StringComparison]::OrdinalIgnoreCase) -or
            -not $resolvedDestination.StartsWith($destinationBoundary, [StringComparison]::OrdinalIgnoreCase)) {
            throw '安装路径超出预期目录。'
        }
        if (Test-Path -LiteralPath $installDirectory) {
            throw '安装目标已存在，停止以避免覆盖。'
        }
        Write-SetupLog -Stage 'Install' -Message "复制程序和许可证到：$resolvedDestination"
        Copy-Item -LiteralPath $resolvedSource -Destination $resolvedDestination -Recurse
        Test-FFmpegInstallation -BinaryDirectory $binaryDirectory -ExpectedVersion $ffmpegVersion
    }
} catch {
    $setupExitCode = 1
    Write-SetupLog -Stage 'Error' -Level 'Error' -Message $_.Exception.Message
} finally {
    # 安装成功或失败都清理大文件；验证模式不修改本机文件。
    if ($null -ne $installationLock) {
        try {
            Remove-FFmpegCache
        } catch {
            $setupExitCode = 1
            Write-SetupLog -Stage 'Cleanup' -Level 'Error' -Message $_.Exception.Message
        } finally {
            $installationLock.Dispose()
        }
    }
}

if ($setupExitCode -eq 0) {
    Write-SetupLog -Stage 'Completed' -Message "FFmpeg 已就绪：$binaryDirectory"
} else {
    Write-SetupLog -Stage 'Failed' -Level 'Error' -Message '初始化失败，请检查前面的错误日志。'
}
exit $setupExitCode
