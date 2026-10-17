Set-StrictMode -Version Latest
$script:PulseEncodeToolRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Get-PulseFFmpegBinDirectory {
    # 与工具固定版本保持一致，所有脚本共用组件内的离线安装，不使用系统 PATH。
    return Join-Path $script:PulseEncodeToolRoot `
        'Dependencies/FFmpeg/Installed/9.0.2-4D2060A8B34A/ffmpeg-9.0.2-full_build-shared/bin'
}
