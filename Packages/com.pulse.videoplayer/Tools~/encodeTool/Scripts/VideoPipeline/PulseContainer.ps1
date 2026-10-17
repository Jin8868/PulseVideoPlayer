Set-StrictMode -Version Latest

# Pulse v1 元数据 UUID；按网络字节序保存，不能使用 Guid.ToByteArray 的混合字节序。
$script:PulseMetadataUUID = '6fbdab98401e4e52b44a97fed50761d2'

function Get-PulseMetadata {
    param([string]$MP4Path, [string]$FFprobe, $BaseMetadata = $null)
    $mediaInfo = Get-MediaInfo -FFprobe $FFprobe -Path $MP4Path
    $videos = @($mediaInfo.streams | Where-Object { $_.codec_type -eq 'video' })
    if ($videos.Count -ne 1 -or $mediaInfo.streams.Count -ne 1) {
        throw 'Pulse 输入必须只有一条视频流，请先通过 ExportVideos.ps1 剥离音轨和字幕。'
    }
    $video = $videos[0]
    Assert-SDRVideo -Video $video
    if ($video.codec_name -ne 'h264' -or $video.pix_fmt -ne 'yuv420p' -or
        $video.avg_frame_rate -ne $video.r_frame_rate) {
        throw 'Pulse 输入必须为 H.264、8bit yuv420p、固定帧率。'
    }
    $color = [ordered]@{ primaries = 'Unspecified'; transfer = 'Unspecified'; matrix = 'Unspecified'; range = 'Unspecified' }
    $propertyMap = @{ primaries = 'color_primaries'; transfer = 'color_transfer'; matrix = 'color_space'; range = 'color_range' }
    foreach ($colorName in $propertyMap.Keys) {
        $property = $video.PSObject.Properties[$propertyMap[$colorName]]
        if ($null -ne $property) { $color[$colorName] = $property.Value }
    }
    $metadata = [ordered]@{
        schemaVersion = 1
        encodedSize = @([int]$video.width, [int]$video.height)
        displaySize = @([int]$video.width, [int]$video.height)
        rgbRect = @(0, 0, [int]$video.width, [int]$video.height)
        alphaRect = $null
        alphaMode = 'None'
        frameCount = [int64]$video.nb_frames
        frameRate = [string]$video.avg_frame_rate
        timeBase = [string]$video.time_base
        color = $color
    }
    # 沿用导出器的显示区域；编码尺寸和帧信息始终以实际 MP4 为准。
    if ($null -ne $BaseMetadata) {
        foreach ($fieldName in @('displaySize', 'rgbRect', 'alphaRect', 'alphaMode')) {
            $metadata[$fieldName] = $BaseMetadata.$fieldName
        }
    }
    if ($metadata.alphaMode -notin @('None', 'Straight', 'Premultiplied')) { throw 'Alpha 模式无效。' }
    foreach ($rectName in @('rgbRect', 'alphaRect')) {
        $rect = $metadata[$rectName]
        if ($null -eq $rect -and $rectName -eq 'alphaRect' -and $metadata.alphaMode -eq 'None') { continue }
        if ($null -eq $rect -or $rect.Count -ne 4 -or $rect[0] -lt 0 -or $rect[1] -lt 0 -or
            $rect[2] -le 0 -or $rect[3] -le 0 -or
            $rect[0] + $rect[2] -gt $video.width -or $rect[1] + $rect[3] -gt $video.height) {
            throw "元数据区域越界：$rectName"
        }
    }
    if ($metadata.frameCount -le 0) { throw '帧数无效。' }
    return $metadata
}

function Read-PulseMetadata {
    param([string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $header = New-Object byte[] 8
        while ($stream.Position -lt $stream.Length) {
            $boxStart = $stream.Position
            if ($stream.Read($header, 0, 8) -ne 8) { throw 'MP4 Box 头不完整。' }
            $boxSize = [int64]$header[0] * 16777216 + [int64]$header[1] * 65536 +
                [int64]$header[2] * 256 + [int64]$header[3]
            $boxType = [Text.Encoding]::ASCII.GetString($header, 4, 4)
            $headerSize = 8
            if ($boxSize -eq 1) {
                $extendedSize = New-Object byte[] 8
                if ($stream.Read($extendedSize, 0, 8) -ne 8) { throw 'MP4 扩展 Box 头不完整。' }
                $boxSize = [int64]0
                foreach ($sizeByte in $extendedSize) {
                    if ($boxSize -gt ([int64]::MaxValue - $sizeByte) / 256) { throw 'Box 大小超出支持范围。' }
                    $boxSize = $boxSize * 256 + $sizeByte
                }
                $headerSize = 16
            } elseif ($boxSize -eq 0) { $boxSize = $stream.Length - $boxStart }
            if ($boxSize -lt $headerSize -or $boxSize -gt $stream.Length - $boxStart) {
                throw 'MP4 Box 大小无效。'
            }
            if ($boxType -eq 'uuid') {
                if ($boxSize -lt $headerSize + 16) { throw 'UUID Box 大小无效。' }
                $uuidBytes = New-Object byte[] 16
                if ($stream.Read($uuidBytes, 0, 16) -ne 16) { throw 'UUID 不完整。' }
                $uuid = [BitConverter]::ToString($uuidBytes).Replace('-', '').ToLowerInvariant()
                if ($uuid -eq $script:PulseMetadataUUID) {
                    $payloadSize = $boxSize - $headerSize - 16
                    if ($payloadSize -gt 1048576) { throw 'Pulse 元数据超过 1 MiB。' }
                    $payload = New-Object byte[] ([int]$payloadSize)
                    if ($stream.Read($payload, 0, $payload.Length) -ne $payload.Length) {
                        throw 'Pulse 元数据不完整。'
                    }
                    return [Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json
                }
            }
            # 跳过 mdat 等其他 Box，不将大视频载入托管内存。
            $stream.Position = $boxStart + $boxSize
        }
        throw '未找到 Pulse 元数据 UUID Box。'
    } finally { $stream.Dispose() }
}

function Write-PulseContainer {
    param([string]$MP4Path, [string]$OutputPath, $Metadata)
    if ([IO.Path]::GetFullPath($MP4Path) -eq [IO.Path]::GetFullPath($OutputPath)) {
        throw 'Pulse 输出路径不能与源 MP4 相同。'
    }
    $payload = [Text.Encoding]::UTF8.GetBytes(($Metadata | ConvertTo-Json -Depth 8 -Compress))
    if ($payload.Length -gt 1048576) { throw 'Pulse 元数据超过 1 MiB。' }
    $boxSize = 24 + $payload.Length
    $header = New-Object byte[] 24
    $header[0] = [byte](($boxSize -shr 24) -band 255)
    $header[1] = [byte](($boxSize -shr 16) -band 255)
    $header[2] = [byte](($boxSize -shr 8) -band 255)
    $header[3] = [byte]($boxSize -band 255)
    [Text.Encoding]::ASCII.GetBytes('uuid').CopyTo($header, 4)
    for ($uuidIndex = 0; $uuidIndex -lt 16; $uuidIndex++) {
        $header[8 + $uuidIndex] = [Convert]::ToByte($script:PulseMetadataUUID.Substring($uuidIndex * 2, 2), 16)
    }
    $source = [IO.File]::OpenRead($MP4Path)
    try {
        $destination = [IO.File]::Open($OutputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        try {
            # 在文件末尾追加 UUID Box，保持 moov 前置和原有 sample 偏移不变。
            $buffer = New-Object byte[] 1048576
            $copiedBytes = [int64]0
            $logTimer = [Diagnostics.Stopwatch]::StartNew()
            Write-PipelineLog -Stage 'Container' -InputFile $MP4Path -Percent 0 -Message '开始生成 Pulse 容器。'
            while (($readCount = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $destination.Write($buffer, 0, $readCount)
                $copiedBytes += $readCount
                if ($logTimer.ElapsedMilliseconds -ge 500 -or $copiedBytes -eq $source.Length) {
                    $percent = [Math]::Round($copiedBytes / $source.Length * 100, 1)
                    Write-PipelineLog -Stage 'Container' -InputFile $MP4Path -Percent $percent `
                        -Message "已复制 $copiedBytes / $($source.Length) 字节。"
                    $logTimer.Restart()
                }
            }
            $destination.Write($header, 0, $header.Length)
            $destination.Write($payload, 0, $payload.Length)
            $destination.Flush()
        } finally { $destination.Dispose() }
    } finally { $source.Dispose() }
    Write-PipelineLog -Stage 'Verify' -InputFile $MP4Path -Message '回读并校验 Pulse UUID 元数据。'
    $readback = Read-PulseMetadata -Path $OutputPath
    if (($readback | ConvertTo-Json -Depth 8 -Compress) -ne ($Metadata | ConvertTo-Json -Depth 8 -Compress)) {
        throw 'Pulse 元数据回读验证失败。'
    }
    return $readback
}
