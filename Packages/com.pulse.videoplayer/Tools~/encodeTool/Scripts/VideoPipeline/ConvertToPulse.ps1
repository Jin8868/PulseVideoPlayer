<#
.SYNOPSIS
将一个或多个已标准化的无音轨 MP4 封装为含 UUID 元数据的 Pulse v1 文件。
.DESCRIPTION
支持文件数组、目录和 JSON 文件清单。读取同名 metadata.json 的区域信息。
不会再次转码；普通源视频请使用 ExportVideos.ps1。
.PARAMETER JsonLog
逐行实时输出 UTF-8 JSON，供 Unity 面板异步读取。
#>
[CmdletBinding()]
param(
    [string[]]$InputPaths = @(),
    [string]$InputDirectory,
    [string]$InputListFile,
    [string]$FFmpegBinDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [switch]$Recurse,
    [switch]$JsonLog
)

$script:UseJsonLog = [bool]$JsonLog
. (Join-Path $PSScriptRoot 'VideoPipelineCommon.ps1')
. (Join-Path $PSScriptRoot 'PulseContainer.ps1')
if ([string]::IsNullOrWhiteSpace($FFmpegBinDirectory)) { $FFmpegBinDirectory = Get-PulseFFmpegBinDirectory }
$ffprobe = Join-Path $FFmpegBinDirectory 'ffprobe.exe'
$failedCount = 0

try {
    if (-not (Test-Path -LiteralPath $ffprobe)) { throw '请先打开 encodeTool/VideoTool.exe 完成离线初始化。' }
    $files = @($InputPaths)
    if ($InputListFile) {
        $inputList = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $InputListFile).Path) | ConvertFrom-Json
        foreach ($inputPath in @($inputList)) {
            if ($inputPath -isnot [string]) { throw '输入清单必须为 JSON 路径字符串数组。' }
            $files += $inputPath
        }
    }
    if ($InputDirectory) {
        $files += @(Get-ChildItem -LiteralPath $InputDirectory -File -Filter '*.mp4' -Recurse:$Recurse |
            Select-Object -ExpandProperty FullName)
    }
    $files = @($files | ForEach-Object { (Resolve-Path -LiteralPath $_).Path } | Select-Object -Unique)
    if ($files.Count -eq 0) { throw '没有输入 MP4。' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
    $names = @{}
    foreach ($inputFile in $files) {
        $stem = [IO.Path]::GetFileNameWithoutExtension($inputFile)
        if ($names.ContainsKey($stem)) { throw "输入文件同名：$stem" }
        $names[$stem] = $true
        if (Test-Path -LiteralPath (Join-Path $outputRoot "$stem.pulse")) { throw "产物已存在：$stem.pulse" }
    }
    Write-PipelineLog -Stage 'BatchStart' -Message "开始封装 $($files.Count) 个 Pulse 文件。"
    $fileIndex = 0
    foreach ($inputFile in $files) {
        $fileIndex++
        try {
            Write-PipelineLog -Stage 'FileStart' -InputFile $inputFile -Message "处理第 $fileIndex/$($files.Count) 个文件。"
            $sidecarPath = [IO.Path]::ChangeExtension($inputFile, '.metadata.json')
            $baseMetadata = $null
            if (Test-Path -LiteralPath $sidecarPath) {
                $baseMetadata = [IO.File]::ReadAllText($sidecarPath) | ConvertFrom-Json
            }
            $metadata = Get-PulseMetadata -MP4Path $inputFile -FFprobe $ffprobe -BaseMetadata $baseMetadata
            $outputPath = Join-Path $outputRoot ([IO.Path]::GetFileNameWithoutExtension($inputFile) + '.pulse')
            Write-PulseContainer -MP4Path $inputFile -OutputPath $outputPath -Metadata $metadata | Out-Null
            $outputInfo = Get-MediaInfo -FFprobe $ffprobe -Path $outputPath
            if ($outputInfo.streams.Count -ne 1 -or $outputInfo.streams[0].codec_name -ne 'h264') {
                throw 'Pulse 文件未能通过 ffprobe 视频校验。'
            }
            Write-PipelineLog -Stage 'FileCompleted' -InputFile $inputFile -Percent 100 -Message "已生成：$outputPath"
        } catch {
            $failedCount++
            Write-PipelineLog -Stage 'FileFailed' -InputFile $inputFile -Level 'Error' -Message $_.Exception.Message
        }
    }
    if ($failedCount -gt 0) { throw "$failedCount 个 Pulse 文件生成失败，其他产物已保留。" }
    Write-PipelineLog -Stage 'Completed' -Message "全部 $($files.Count) 个 Pulse 文件生成成功。"
    exit 0
} catch {
    Write-PipelineLog -Stage 'Failed' -Level 'Error' -Message $_.Exception.Message
    exit 1
}
