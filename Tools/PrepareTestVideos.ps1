<#
.SYNOPSIS
准备 Sintel、帧号合成视频和带 Alpha 合成视频，并调用批量导出工具。
.PARAMETER SkipSintel
只生成两组合成素材。
.PARAMETER SintelOnly
只下载并导出 Sintel。
#>
[CmdletBinding()]
param([switch]$SkipSintel, [switch]$SintelOnly, [switch]$JsonLog)

$script:UseJsonLog = [bool]$JsonLog
. (Join-Path $PSScriptRoot 'VideoPipeline/VideoPipelineCommon.ps1')
$testRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Tests'))
$ffmpegBin = Join-Path $PSScriptRoot 'FFMpeg/ffmpeg-9.0.2-essentials_build/bin'
$ffmpegPath = Join-Path $ffmpegBin 'ffmpeg.exe'
$ffprobePath = Join-Path $ffmpegBin 'ffprobe.exe'
$exportScript = Join-Path $PSScriptRoot 'VideoPipeline/ExportVideos.ps1'
$utf8 = New-Object Text.UTF8Encoding($false)

function Export-TestVideo {
    param([string]$SourcePath, [string]$OutputDirectory, [switch]$Alpha)
    $outputPath = Join-Path $OutputDirectory ([IO.Path]::GetFileNameWithoutExtension($SourcePath) + '.mp4')
    if (Test-Path -LiteralPath $outputPath) {
        Get-MediaInfo -FFprobe $ffprobePath -Path $outputPath | Out-Null
        Write-PipelineLog -Stage 'Reuse' -Message "复用已生成视频：$outputPath"
        return
    }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $exportScript,
        '-InputDirectory', [IO.Path]::GetDirectoryName($SourcePath), '-OutputDirectory', $OutputDirectory)
    if ($Alpha) { $arguments += '-Alpha' }
    if ($JsonLog) { $arguments += '-JsonLog' }
    # 子脚本保留实时输出，不聚合到最后再显示。
    & powershell.exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "导出失败：$SourcePath" }
}

try {
    if ($SkipSintel -and $SintelOnly) { throw 'SkipSintel 与 SintelOnly 不能同时使用。' }
    if (-not (Test-Path -LiteralPath $ffmpegPath)) { throw '请先运行 Tools/SetupFFmpeg.ps1。' }
    foreach ($group in @('Synthetic', 'Alpha', 'Sintel')) {
        foreach ($category in @('Source', 'Generated')) {
            New-Item -ItemType Directory -Path (Join-Path $testRoot "$category/$group") -Force | Out-Null
        }
    }

    if (-not $SintelOnly) {
        # 中英文各有独立时间轴，合成源内嵌英文字幕供提取测试。
        $syntheticRoot = Join-Path $testRoot 'Source/Synthetic'
        $englishSRT = ''
        $chineseSRT = ''
        for ($subtitleIndex = 0; $subtitleIndex -lt 6; $subtitleIndex++) {
            $startSeconds = $subtitleIndex * 10
            $endSeconds = $startSeconds + 8
            $entryID = $subtitleIndex + 1
            $startTime = '00:00:{0:D2},000' -f $startSeconds
            $endTime = '00:00:{0:D2},000' -f $endSeconds
            $englishSRT += "$entryID`r`n$startTime --> $endTime`r`nSynthetic subtitle $entryID`r`n`r`n"
            $chineseSRT += "$entryID`r`n$startTime --> $endTime`r`n合成测试字幕 $entryID`r`n`r`n"
        }
        [IO.File]::WriteAllText((Join-Path $syntheticRoot 'test.en.srt'), $englishSRT, $utf8)
        [IO.File]::WriteAllText((Join-Path $syntheticRoot 'test.zh.srt'), $chineseSRT, $utf8)
        $syntheticPath = Join-Path $syntheticRoot 'all_in_one.mp4'
        if (-not (Test-Path -LiteralPath $syntheticPath)) {
            Write-PipelineLog -Stage 'Generate' -Message '生成 60 秒 1080p/30fps 帧号、时间码、每秒提示音与字幕源。'
            $drawFilter = "drawtext=fontfile='C\:/Windows/Fonts/consola.ttf':" +
                "text='frame=%{n} time=%{pts\:hms}':x=16:y=16:fontsize=48:fontcolor=white:box=1:boxcolor=black@0.7"
            Invoke-FFmpeg -Executable $ffmpegPath -InputFile $syntheticPath -Duration 60 -ProgressFrameRate 30 -Stage 'Generate' `
                -Arguments @('-n', '-f', 'lavfi', '-i', 'testsrc2=size=1920x1080:rate=30',
                    '-f', 'lavfi', '-i', 'aevalsrc=if(lt(mod(t\,1)\,0.1)\,0.2*sin(2*PI*1000*t)\,0):s=48000:d=60',
                    '-i', (Join-Path $syntheticRoot 'test.en.srt'), '-vf', $drawFilter,
                    '-map', '0:v:0', '-map', '1:a:0', '-map', '2:s:0', '-c:v', 'libx264',
                    '-threads', '4', '-preset', 'veryfast', '-crf', '18', '-pix_fmt', 'yuv420p',
                    '-g', '60', '-sc_threshold', '0', '-c:a', 'aac', '-c:s', 'mov_text',
                    '-t', '60', '-movflags', '+faststart', $syntheticPath)
        }
        Export-TestVideo -SourcePath $syntheticPath -OutputDirectory (Join-Path $testRoot 'Generated/Synthetic')

        # MOV/ProRes 4444 保留透明通道；圆形软边随时间移动，覆盖渐隐、渐变与高对比画面。
        $alphaPath = Join-Path $testRoot 'Source/Alpha/alpha_test.mov'
        if (-not (Test-Path -LiteralPath $alphaPath)) {
            Write-PipelineLog -Stage 'Generate' -Message '生成 30 秒 800x800/30fps 带透明通道的 ProRes 4444 源。'
            $alphaFilter = "format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':" +
                "a='255*clip((300-sqrt(pow(X-400-80*sin(T),2)+pow(Y-400,2)))/120,0,1)'"
            Invoke-FFmpeg -Executable $ffmpegPath -InputFile $alphaPath -Duration 30 -ProgressFrameRate 30 -Stage 'Generate' `
                -Arguments @('-n', '-f', 'lavfi', '-i', 'testsrc2=size=800x800:rate=30',
                    '-filter_threads', '4', '-vf', $alphaFilter, '-an', '-c:v', 'prores_ks',
                    '-threads', '4', '-profile:v', '4', '-pix_fmt', 'yuva444p10le',
                    '-qscale:v', '12', '-t', '30', $alphaPath)
        }
        Export-TestVideo -SourcePath $alphaPath -OutputDirectory (Join-Path $testRoot 'Generated/Alpha') -Alpha
    }

    if (-not $SkipSintel) {
        $sintelRoot = Join-Path $testRoot 'Source/Sintel'
        $sintelPath = Join-Path $sintelRoot 'Sintel.2010.1080p.mkv'
        # 镜像文件大小已核实，下载支持断点续传；已有文件还会接受 ffprobe 检查。
        $expectedSourceSize = 1172428172
        if (-not (Test-Path -LiteralPath $sintelPath) -or
            (Get-Item -LiteralPath $sintelPath).Length -ne $expectedSourceSize) {
            Write-PipelineLog -Stage 'Download' -Message '下载 Sintel 1080p 原片，约 1.17 GB。'
            & curl.exe -f -L --connect-timeout 20 --max-time 3600 --retry 3 -C - -o $sintelPath `
                'https://ftp.nluug.nl/pub/graphics/blender/demo/movies/Sintel.2010.1080p.mkv'
            if ($LASTEXITCODE -ne 0) { throw 'Sintel 下载失败，可重新运行断点续传。' }
        }
        $sourceInfo = Get-MediaInfo -FFprobe $ffprobePath -Path $sintelPath
        Write-PipelineLog -Stage 'Verify' -Message "Sintel 原片已下载：$($sourceInfo.format.duration) 秒。"
        foreach ($download in @(
            @('sintel.en.srt', 'https://durian.blender.org/wp-content/content/subtitles/sintel_en.srt'),
            @('sintel.zh.srt', 'https://commons.wikimedia.org/w/index.php?title=TimedText:Sintel_movie_-_Blender_Fondation.ogv.zh.srt&action=raw')
        )) {
            $subtitlePath = Join-Path $sintelRoot $download[0]
            if (-not (Test-Path -LiteralPath $subtitlePath)) {
                & curl.exe -sS -f -L --connect-timeout 20 --max-time 60 -o $subtitlePath $download[1]
                if ($LASTEXITCODE -ne 0) { throw "字幕下载失败：$($download[0])" }
            }
            $subtitleText = [IO.File]::ReadAllText($subtitlePath)
            if ($subtitleText -notmatch '\d\d:\d\d:\d\d,\d\d\d\s+-->') { throw "字幕格式无效：$subtitlePath" }
        }
        Export-TestVideo -SourcePath $sintelPath -OutputDirectory (Join-Path $testRoot 'Generated/Sintel')
    }
    Write-PipelineLog -Stage 'Completed' -Message "测试素材已准备：$testRoot"
    exit 0
} catch {
    Write-PipelineLog -Stage 'Failed' -Level 'Error' -Message $_.Exception.Message
    exit 1
}
