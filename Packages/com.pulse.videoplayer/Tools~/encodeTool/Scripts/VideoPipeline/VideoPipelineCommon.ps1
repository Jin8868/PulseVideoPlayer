Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../ToolEnvironment.ps1')
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)

function Write-PipelineLog {
    param([string]$Stage, [string]$Message, [string]$Level = 'Info',
        [string]$InputFile = '', $Percent = $null)
    $entry = [ordered]@{
        timestamp = [DateTimeOffset]::Now.ToString('o')
        level = $Level
        stage = $Stage
        message = $Message
        inputFile = $InputFile
        percent = $Percent
    }
    if ($script:UseJsonLog) { $line = $entry | ConvertTo-Json -Compress }
    else { $line = "[$($entry.timestamp)][$Level][$Stage] $Message" }
    [Console]::Out.WriteLine($line)
    [Console]::Out.Flush()
}

function New-NativeProcess {
    param([string]$Executable, [string[]]$Arguments)
    # 按 Windows 原生命令行规则转义每个参数，保留空格、引号和尾部反斜杠。
    $quotedArguments = foreach ($argument in $Arguments) {
        '"' + [regex]::Replace(
            [regex]::Replace($argument, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
    }
    $processInfo = New-Object Diagnostics.ProcessStartInfo
    $processInfo.FileName = $Executable
    $processInfo.Arguments = $quotedArguments -join ' '
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $processInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $processInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    return [Diagnostics.Process]::Start($processInfo)
}

function Invoke-NativeCapture {
    param([string]$Executable, [string[]]$Arguments)
    $nativeProcess = New-NativeProcess -Executable $Executable -Arguments $Arguments
    try {
        $outputTask = $nativeProcess.StandardOutput.ReadToEndAsync()
        $errorTask = $nativeProcess.StandardError.ReadToEndAsync()
        $nativeProcess.WaitForExit()
        if ($nativeProcess.ExitCode -ne 0) { throw $errorTask.GetAwaiter().GetResult() }
        return $outputTask.GetAwaiter().GetResult()
    } finally { $nativeProcess.Dispose() }
}

function Invoke-FFmpeg {
    param([string]$Executable, [string[]]$Arguments, [string]$InputFile,
        [double]$Duration = 0, [string]$Stage = 'Encode', [double]$ProgressFrameRate = 0)
    $nativeArguments = @('-hide_banner', '-loglevel', 'error', '-nostdin', '-nostats',
        '-stats_period', '0.5', '-progress', 'pipe:1') + $Arguments
    $nativeProcess = New-NativeProcess -Executable $Executable -Arguments $nativeArguments
    try {
        # 异步读取错误流，避免任一路管道写满阻塞转码进程。
        $errorTask = $nativeProcess.StandardError.ReadToEndAsync()
        $elapsedSeconds = 0.0
        $frame = '0'
        while ($null -ne ($line = $nativeProcess.StandardOutput.ReadLine())) {
            if ($line -match '^out_time_us=(\d+)$') {
                $elapsedSeconds = [double]$Matches[1] / 1000000.0
            } elseif ($line -match '^frame=(\d+)$') { $frame = $Matches[1] }
            elseif ($line -match '^progress=(continue|end)$') {
                if ($ProgressFrameRate -gt 0) { $elapsedSeconds = [double]$frame / $ProgressFrameRate }
                $percent = $null
                if ($Duration -gt 0) { $percent = [Math]::Round([Math]::Min(100, $elapsedSeconds / $Duration * 100), 1) }
                Write-PipelineLog -Stage $Stage -InputFile $InputFile -Percent $percent `
                    -Message "已处理 $frame 帧，视频时间 $([Math]::Round($elapsedSeconds, 2)) 秒。"
            }
        }
        $nativeProcess.WaitForExit()
        $errorText = $errorTask.GetAwaiter().GetResult()
        if ($errorText.Length -gt 0) {
            Write-PipelineLog -Stage $Stage -InputFile $InputFile -Level 'Warning' -Message $errorText.Trim()
        }
        if ($nativeProcess.ExitCode -ne 0) { throw "FFmpeg 失败，退出码 $($nativeProcess.ExitCode)：$errorText" }
    } finally {
        if (-not $nativeProcess.HasExited) { $nativeProcess.Kill(); $nativeProcess.WaitForExit() }
        $nativeProcess.Dispose()
    }
}

function Get-MediaInfo {
    param([string]$FFprobe, [string]$Path)
    $json = Invoke-NativeCapture -Executable $FFprobe -Arguments @(
        '-v', 'error', '-show_streams', '-show_format', '-of', 'json', $Path)
    return $json | ConvertFrom-Json
}

function Assert-SDRVideo {
    param($Video)
    # 8 位像素格式不代表 SDR；必须在编码前后排除 PQ、HLG 和已知 HDR 辅助元数据。
    $transferProperty = $Video.PSObject.Properties['color_transfer']
    if ($null -ne $transferProperty -and $transferProperty.Value -in @('smpte2084', 'arib-std-b67')) {
        throw "检测到 HDR 视频（$($transferProperty.Value)），当前工具只导出 SDR，请先完成 HDR 到 SDR 色调映射。"
    }
    $sideDataProperty = $Video.PSObject.Properties['side_data_list']
    if ($null -ne $sideDataProperty) {
        foreach ($sideData in @($sideDataProperty.Value)) {
            $typeProperty = $sideData.PSObject.Properties['side_data_type']
            if ($null -ne $typeProperty -and
                $typeProperty.Value -match 'HDR|DOVI|Dolby Vision|Mastering display|Content light level') {
                throw "检测到 HDR 元数据（$($typeProperty.Value)），请先转换为 SDR。"
            }
        }
    }
}
