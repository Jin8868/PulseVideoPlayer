<#
.SYNOPSIS
批量导出 H.264 CFR 无音轨 MP4、独立 AAC 音轨及字幕。
.PARAMETER InputPaths
一个或多个视频文件路径。
.PARAMETER InputDirectory
扫描此目录中的视频文件，可与 InputPaths 一起使用。
.PARAMETER Alpha
将带透明通道的源视频导出为 RGB 在上、Alpha 在下的单流视频。
.PARAMETER JsonLog
输出供 Unity 面板解析的 UTF-8 JSON 行日志。
#>
[CmdletBinding()]
param(
    [string[]]$InputPaths = @(),
    [string]$InputDirectory,
    [string]$InputListFile,
    [string]$FFmpegBinDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [switch]$Recurse,
    [switch]$Alpha,
    [switch]$JsonLog,
    [ValidateRange(1, 120)][int]$FrameRate = 30,
    [ValidateRange(0, 51)][int]$CRF = 18,
    [ValidateRange(1, 600)][int]$GOP = 60,
    [ValidateSet('ultrafast', 'superfast', 'veryfast', 'faster', 'fast', 'medium', 'slow')]
    [string]$Preset = 'veryfast'
)

$script:UseJsonLog = [bool]$JsonLog
. (Join-Path $PSScriptRoot 'VideoPipelineCommon.ps1')
. (Join-Path $PSScriptRoot 'PulseContainer.ps1')
$ffmpegBin = $FFmpegBinDirectory
if ([string]::IsNullOrWhiteSpace($ffmpegBin)) {
    $ffmpegBin = Get-PulseFFmpegBinDirectory
}
$ffmpegPath = Join-Path $ffmpegBin 'ffmpeg.exe'
$ffprobePath = Join-Path $ffmpegBin 'ffprobe.exe'
$utf8 = New-Object Text.UTF8Encoding($false)
$failedCount = 0

try {
    if (-not (Test-Path -LiteralPath $ffmpegPath) -or -not (Test-Path -LiteralPath $ffprobePath)) {
        throw '缺少 FFmpeg，请在视频工具中离线初始化，或通过 FFmpegBinDirectory 指定已有安装。'
    }
    $files = @($InputPaths)
    if ($InputListFile) {
        $inputList = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $InputListFile).Path) | ConvertFrom-Json
        foreach ($inputPath in @($inputList)) {
            if ($inputPath -isnot [string]) { throw 'InputListFile 必须包含视频路径的 JSON 字符串数组。' }
            $files += $inputPath
        }
    }
    if ($InputDirectory) {
        $supportedExtensions = @('.mp4', '.mkv', '.mov', '.webm', '.avi', '.m4v', '.wmv', '.ts')
        foreach ($file in @(Get-ChildItem -LiteralPath $InputDirectory -File -Recurse:$Recurse)) {
            if ($supportedExtensions -contains $file.Extension.ToLowerInvariant()) { $files += $file.FullName }
        }
    }
    $files = @($files | ForEach-Object { (Resolve-Path -LiteralPath $_).Path } | Select-Object -Unique)
    if ($files.Count -eq 0) { throw '没有输入视频。' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
    $outputNames = @{}
    # 批量导出前检查重名和已有产物，避免覆盖源文件或上一批结果。
    foreach ($inputFile in $files) {
        $stem = [IO.Path]::GetFileNameWithoutExtension($inputFile)
        if ($outputNames.ContainsKey($stem)) { throw "输入文件存在同名：$stem，请分开导出。" }
        $outputNames[$stem] = $true
        foreach ($suffix in @('.mp4', '.pulse', '.audio.m4a', '.metadata.json')) {
            if (Test-Path -LiteralPath (Join-Path $outputRoot ($stem + $suffix))) {
                throw "产物已存在：$stem$suffix。请使用新的输出目录或先移走旧产物。"
            }
        }
    }
    Write-PipelineLog -Stage 'BatchStart' -Message "开始导出 $($files.Count) 个视频。"
    $fileIndex = 0
    foreach ($inputFile in $files) {
        $fileIndex++
        try {
            Write-PipelineLog -Stage 'FileStart' -InputFile $inputFile `
                -Message "处理第 $fileIndex/$($files.Count) 个文件。"
            $mediaInfo = Get-MediaInfo -FFprobe $ffprobePath -Path $inputFile
            $video = @($mediaInfo.streams | Where-Object { $_.codec_type -eq 'video' })[0]
            Assert-SDRVideo -Video $video
            $transferProperty = $video.PSObject.Properties['color_transfer']
            $outputTransfer = 'bt709'
            if ($null -ne $transferProperty -and
                -not [string]::IsNullOrWhiteSpace([string]$transferProperty.Value) -and
                $transferProperty.Value -notin @('unknown', 'unspecified', 'reserved', '')) {
                $outputTransfer = [string]$transferProperty.Value
            } else {
                Write-PipelineLog -Stage 'Validate' -InputFile $inputFile -Level 'Warning' `
                    -Message '源视频未标记传递特性，按 SDR BT.709 处理；无法自动识别未标记的 HDR 内容。'
            }
            $duration = [double]::Parse([string]$mediaInfo.format.duration, [Globalization.CultureInfo]::InvariantCulture)
            $stem = [IO.Path]::GetFileNameWithoutExtension($inputFile)
            $outputPath = Join-Path $outputRoot "$stem.mp4"
            $arguments = @('-n', '-i', $inputFile)
            if ($Alpha) {
                if ($video.pix_fmt -notmatch '^(yuva|rgba|bgra|argb|abgr|gbrap|ya)' ) {
                    throw "源视频没有可识别的 Alpha 通道：$($video.pix_fmt)"
                }
                if ($video.width % 2 -ne 0 -or $video.height % 2 -ne 0) {
                    throw 'Alpha 拼接暂要求源视频宽高均为偶数。'
                }
                $filter = 'format=rgba,split=2[rgb][a];[rgb]format=rgb24,pad=iw:ih+2:0:0:black[top];' +
                    '[a]alphaextract,format=rgb24[bottom];[top][bottom]vstack=inputs=2,' +
                    "setparams=color_trc=${outputTransfer}[v]"
                $arguments += @('-filter_complex', $filter, '-map', '[v]')
            } else {
                $arguments += @('-map', '0:v:0', '-vf',
                    ('pad=ceil(iw/2)*2:ceil(ih/2)*2,setparams=color_trc=' + $outputTransfer))
            }
            $arguments += @('-an', '-sn', '-dn', '-map_chapters', '-1', '-c:v', 'libx264', '-preset', $Preset,
                '-crf', [string]$CRF, '-pix_fmt', 'yuv420p', '-profile:v', 'high',
                '-color_trc', $outputTransfer,
                '-r', [string]$FrameRate, '-fps_mode', 'cfr', '-g', [string]$GOP,
                '-keyint_min', [string]$GOP, '-sc_threshold', '0', '-movflags', '+faststart', $outputPath)
            Invoke-FFmpeg -Executable $ffmpegPath -Arguments $arguments -Duration $duration -InputFile $inputFile -ProgressFrameRate $FrameRate
            if (@($mediaInfo.streams | Where-Object { $_.codec_type -eq 'audio' }).Count -gt 0) {
                Invoke-FFmpeg -Executable $ffmpegPath -InputFile $inputFile -Duration $duration -Stage 'Audio' `
                    -Arguments @('-n', '-i', $inputFile, '-map', '0:a:0', '-vn', '-sn', '-c:a', 'aac',
                        '-b:a', '192k', '-map_chapters', '-1', (Join-Path $outputRoot "$stem.audio.m4a"))
            }
            $subtitleIndex = 0
            foreach ($subtitle in @($mediaInfo.streams | Where-Object { $_.codec_type -eq 'subtitle' })) {
                if ($subtitle.codec_name -notin @('subrip', 'mov_text', 'ass', 'ssa', 'webvtt', 'text')) { continue }
                Invoke-FFmpeg -Executable $ffmpegPath -InputFile $inputFile -Stage 'Subtitle' -Arguments @(
                    '-n', '-i', $inputFile, '-map', "0:$($subtitle.index)", '-c:s', 'srt',
                    (Join-Path $outputRoot "$stem.subtitle$subtitleIndex.srt"))
                $subtitleIndex++
            }
            $outputInfo = Get-MediaInfo -FFprobe $ffprobePath -Path $outputPath
            $outputVideo = @($outputInfo.streams | Where-Object { $_.codec_type -eq 'video' })[0]
            Assert-SDRVideo -Video $outputVideo
            $outputTransferProperty = $outputVideo.PSObject.Properties['color_transfer']
            if ($outputInfo.streams.Count -ne 1 -or $outputVideo.codec_name -ne 'h264' -or $outputVideo.pix_fmt -ne 'yuv420p' -or
                $outputVideo.avg_frame_rate -ne "$FrameRate/1" -or
                $outputVideo.r_frame_rate -ne "$FrameRate/1" -or
                $null -eq $outputTransferProperty -or $outputTransferProperty.Value -ne $outputTransfer -or
                $outputInfo.format.format_name -notmatch 'mp4' -or
                @($outputInfo.streams | Where-Object { $_.codec_type -eq 'audio' }).Count -ne 0) {
                throw '导出产物不符合标准 MP4、单路 CFR H.264、8 位 SDR、无音轨规格。'
            }
            $metadata = [ordered]@{
                source = [IO.Path]::GetFileName($inputFile)
                encodedSize = @($outputVideo.width, $outputVideo.height)
                displaySize = @($video.width, $video.height)
                frameRate = $outputVideo.avg_frame_rate
                frameCount = $outputVideo.nb_frames
                rgbRect = @(0, 0, $video.width, $video.height)
                alphaRect = $null
                alphaMode = 'None'
                note = '测试用旁路元数据，尚未注入 Pulse UUID Box。'
            }
            if ($Alpha) {
                $metadata.alphaRect = @(0, ($video.height + 2), $video.width, $video.height)
                $metadata.alphaMode = 'Straight'
            }
            $pulseMetadata = Get-PulseMetadata -MP4Path $outputPath -FFprobe $ffprobePath -BaseMetadata $metadata
            [IO.File]::WriteAllText((Join-Path $outputRoot "$stem.metadata.json"),
                ($pulseMetadata | ConvertTo-Json -Depth 8), $utf8)
            $pulsePath = Join-Path $outputRoot "$stem.pulse"
            Write-PulseContainer -MP4Path $outputPath -OutputPath $pulsePath -Metadata $pulseMetadata | Out-Null
            $pulseInfo = Get-MediaInfo -FFprobe $ffprobePath -Path $pulsePath
            if ($pulseInfo.streams.Count -ne 1 -or $pulseInfo.streams[0].codec_name -ne 'h264') {
                throw 'Pulse 容器 ffprobe 校验失败。'
            }
            Write-PipelineLog -Stage 'FileCompleted' -InputFile $inputFile -Percent 100 -Message "已导出：$pulsePath"
        } catch {
            $failedCount++
            Write-PipelineLog -Stage 'FileFailed' -InputFile $inputFile -Level 'Error' -Message $_.Exception.Message
        }
    }
    if ($failedCount -gt 0) { throw "$failedCount 个文件导出失败，其他文件的产物已保留。" }
    Write-PipelineLog -Stage 'Completed' -Message "全部 $($files.Count) 个视频导出成功。"
    exit 0
} catch {
    Write-PipelineLog -Stage 'Failed' -Level 'Error' -Message $_.Exception.Message
    exit 1
}
