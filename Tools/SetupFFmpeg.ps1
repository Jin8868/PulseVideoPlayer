<#
.SYNOPSIS
准备 PulseVideoPlayer 离线工具使用的 Windows FFmpeg。
.DESCRIPTION
固定 FFmpeg 9.0.2 essentials 版本。首次运行下载 ZIP、校验 SHA-256，再安装并验证工具。
已有约定版本时复用本机文件，仅验证版本、运行能力及 H.264 编码，不重新下载。
.PARAMETER VerifyOnly
只验证已有安装，不下载或解压。
#>
[CmdletBinding()]
param(
    [switch]$VerifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

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

function Test-FFmpegInstallation {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BinaryDirectory,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion
    )

    # 完整读取进程输出后再检查退出码，避免截断管道提前终止原生程序。
    foreach ($toolName in @('ffmpeg', 'ffprobe')) {
        $executablePath = Join-Path $BinaryDirectory "$toolName.exe"
        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            throw "缺少程序：$executablePath"
        }
        $versionOutput = @(& $executablePath -version)
        $processExitCode = $LASTEXITCODE
        if ($processExitCode -ne 0) {
            throw "$toolName 运行失败，退出码：$processExitCode"
        }
        $versionPattern = '^' + $toolName + ' version ' + [regex]::Escape($ExpectedVersion) + '(?:[-\s]|$)'
        if ($versionOutput.Count -eq 0 -or $versionOutput[0] -notmatch $versionPattern) {
            throw "$toolName 版本不符合要求，应为 $ExpectedVersion。"
        }
        Write-Host $versionOutput[0]
    }

    # 合成一帧并实际执行 H.264 编码，验证流水线所需的 libx264 可用。
    $ffmpegPath = Join-Path $BinaryDirectory 'ffmpeg.exe'
    & $ffmpegPath -hide_banner -loglevel error -f lavfi -i 'color=c=black:s=64x64:r=1' `
        -frames:v 1 -an -c:v libx264 -pix_fmt yuv420p -f null -
    if ($LASTEXITCODE -ne 0) {
        throw 'H.264 编码验证失败。'
    }
    Write-Host 'H.264 编码验证通过。'
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
        -not [Environment]::Is64BitOperatingSystem) {
        throw '此脚本用于 64 位 Windows。'
    }

    # 已有安装不覆盖；验证失败时保留原文件，供用户检查或移走后重装。
    if (Test-Path -LiteralPath $installDirectory) {
        Write-Host "验证已有安装：$installDirectory"
        Test-FFmpegInstallation -BinaryDirectory $binaryDirectory -ExpectedVersion $ffmpegVersion
        Write-Host '已复用本机安装；本次未下载 ZIP，也未校验已有安装文件的来源。'
    } else {
        if ($VerifyOnly) {
            throw "尚未安装 FFmpeg。请先运行：& '$PSCommandPath'"
        }

        # 使用固定版本 URL 和仓库记录的校验值，避免 latest 链接升级导致工具变化。
        New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null
        if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
            $downloadPath = "$archivePath.$([Guid]::NewGuid().ToString('N')).download"
            Write-Host "下载 FFmpeg $ffmpegVersion：$downloadURL"
            $previousSecurityProtocol = [Net.ServicePointManager]::SecurityProtocol
            try {
                [Net.ServicePointManager]::SecurityProtocol = `
                    $previousSecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -Uri $downloadURL -OutFile $downloadPath -UseBasicParsing -TimeoutSec 120
            } finally {
                [Net.ServicePointManager]::SecurityProtocol = $previousSecurityProtocol
            }
            $downloadHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash
            if ($downloadHash -ne $expectedSHA256) {
                throw "下载文件 SHA-256 不匹配，未安装。文件保留在：$downloadPath"
            }
            Move-Item -LiteralPath $downloadPath -Destination $archivePath
        }
        $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
        if ($archiveHash -ne $expectedSHA256) {
            throw "缓存 ZIP 的 SHA-256 不匹配，请移走该文件后重试：$archivePath"
        }
        Write-Host 'ZIP SHA-256 校验通过。'

        # 先解压并验证到独立缓存目录，成功后才发布安装目录，避免留下半成品。
        $stagingDirectory = Join-Path $cacheDirectory ([Guid]::NewGuid().ToString('N'))
        Expand-Archive -LiteralPath $archivePath -DestinationPath $stagingDirectory
        $stagedInstallDirectory = Join-Path $stagingDirectory $versionDirectoryName
        $stagedBinaryDirectory = Join-Path $stagedInstallDirectory 'bin'
        Test-FFmpegInstallation -BinaryDirectory $stagedBinaryDirectory -ExpectedVersion $ffmpegVersion

        # 限制安装复制的源和目标范围，保留解压包中的许可证与说明文件。
        $resolvedSource = [IO.Path]::GetFullPath($stagedInstallDirectory)
        $resolvedDestination = [IO.Path]::GetFullPath($installDirectory)
        $sourceBoundary = [IO.Path]::GetFullPath($stagingDirectory).TrimEnd('\') + '\'
        $destinationBoundary = [IO.Path]::GetFullPath($installRoot).TrimEnd('\') + '\'
        if (-not $resolvedSource.StartsWith($sourceBoundary, [StringComparison]::OrdinalIgnoreCase) -or
            -not $resolvedDestination.StartsWith($destinationBoundary, [StringComparison]::OrdinalIgnoreCase)) {
            throw '安装路径超出预期目录。'
        }
        New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
        if (Test-Path -LiteralPath $installDirectory) {
            throw '安装目标已存在，停止以避免覆盖。'
        }
        Copy-Item -LiteralPath $resolvedSource -Destination $resolvedDestination -Recurse
        Test-FFmpegInstallation -BinaryDirectory $binaryDirectory -ExpectedVersion $ffmpegVersion
    }

    Write-Host "FFmpeg 已就绪：$binaryDirectory"
    Write-Host '无需配置系统 PATH；FFMpeg 与 Cache 目录不提交 Git，也不进入游戏包。'
} catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
    exit 1
}
