[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$UnityEditorPath,
    [string]$NdkPath,
    [string]$SdkPath,
    [string]$JdkPath,
    [string]$CMakePath,
    [string]$NinjaPath,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [ValidateRange(24, 35)]
    [int]$MinApiLevel = 24,
    [string]$GradleProjectPath,
    [switch]$SkipBridgeBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
$buildToolPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$packagePath = [IO.Path]::GetFullPath((Join-Path $buildToolPath '../..'))
$buildPath = Join-Path $buildToolPath "Build/Android/arm64-v8a/$Configuration"
$sourcePath = Join-Path $buildToolPath 'Source/Android'
$pluginPath = Join-Path $packagePath 'Runtime/Plugins/Android'

function Invoke-NativeTool {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "工具执行失败：$Executable，退出码 $LASTEXITCODE。"
    }
}

function Write-PluginMetadata {
    param([string]$AssetPath, [string]$CPU)
    $metadataPath = "$AssetPath.meta"
    if (Test-Path -LiteralPath $metadataPath) { return }
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
      Android: Android
    second:
      enabled: 1
      settings:
        CPU: $CPU
  userData: 
  assetBundleName: 
  assetBundleVariant: 
"@
    [IO.File]::WriteAllText($metadataPath, $metadata + "`n", $script:utf8)
}

try {
    # 默认复用指定 Unity 安装携带的 Android 工具链，也允许逐项覆盖。
    $editorExecutable = (Resolve-Path -LiteralPath $UnityEditorPath).Path
    $editorPath = Split-Path -Parent $editorExecutable
    $androidPlayerPath = Join-Path $editorPath 'Data/PlaybackEngines/AndroidPlayer'
    $pluginAPI = Join-Path $editorPath 'Data/PluginAPI'
    if (!$NdkPath) { $NdkPath = Join-Path $androidPlayerPath 'NDK' }
    if (!$SdkPath) { $SdkPath = Join-Path $androidPlayerPath 'SDK' }
    if (!$JdkPath) { $JdkPath = Join-Path $androidPlayerPath 'OpenJDK' }
    $NdkPath = (Resolve-Path -LiteralPath $NdkPath).Path
    $toolchainPath = Join-Path $NdkPath 'build/cmake/android.toolchain.cmake'
    if (!(Test-Path -LiteralPath $toolchainPath) -or
        !(Test-Path -LiteralPath (Join-Path $pluginAPI 'IUnityGraphics.h'))) {
        throw '找不到 NDK 工具链或 Unity PluginAPI，请检查传入路径。'
    }
    if ($GradleProjectPath) {
        $GradleProjectPath = (Resolve-Path -LiteralPath $GradleProjectPath).Path
        if (!(Test-Path -LiteralPath (Join-Path $GradleProjectPath 'unityLibrary/build.gradle'))) {
            throw 'GradleProjectPath 必须指向 Unity 导出工程的根目录。'
        }
    }

    # CMake 与 Ninja 也可以使用 VS 的安装，不要求额外安装 Android Studio。
    if (!$CMakePath) {
        $cmakeCommand = Get-Command cmake.exe -ErrorAction SilentlyContinue
        if ($cmakeCommand) { $CMakePath = $cmakeCommand.Source }
    }
    if (!$CMakePath) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $visualStudioPath = & $vswhere -latest -products '*' `
                -requires Microsoft.VisualStudio.Component.VC.CMake.Project -property installationPath
            if ($visualStudioPath) {
                $CMakePath = Join-Path $visualStudioPath `
                    'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
            }
        }
    }
    if (!$CMakePath) {
        $sdkCMakePath = Join-Path $SdkPath 'cmake'
        if (Test-Path -LiteralPath $sdkCMakePath) {
            $sdkCMake = Get-ChildItem -LiteralPath $sdkCMakePath -Directory |
                Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
            if ($sdkCMake) { $CMakePath = Join-Path $sdkCMake.FullName 'bin/cmake.exe' }
        }
    }
    if (!$CMakePath -or !(Test-Path -LiteralPath $CMakePath)) {
        throw '找不到 CMake 3.20+，请通过 -CMakePath 指定，或安装 VS C++ CMake 工具。'
    }
    $CMakePath = (Resolve-Path -LiteralPath $CMakePath).Path
    if (!$NinjaPath) {
        $ninjaCommand = Get-Command ninja.exe -ErrorAction SilentlyContinue
        if ($ninjaCommand) { $NinjaPath = $ninjaCommand.Source }
        else {
            $cmakeDirectory = Split-Path -Parent $CMakePath
            foreach ($candidate in @(
                (Join-Path $cmakeDirectory 'ninja.exe'),
                (Join-Path $cmakeDirectory '../../Ninja/ninja.exe')
            )) {
                if (Test-Path -LiteralPath $candidate) {
                    $NinjaPath = [IO.Path]::GetFullPath($candidate)
                    break
                }
            }
        }
    }
    if (!$NinjaPath -or !(Test-Path -LiteralPath $NinjaPath)) {
        throw '找不到 Ninja，请通过 -NinjaPath 指定 ninja.exe。'
    }

    # 先完成全部构建，再发布产物，避免生成一半的桥接包被 Unity 导入。
    Invoke-NativeTool $CMakePath @(
        '-S', $sourcePath, '-B', $buildPath, '-G', 'Ninja',
        "-DCMAKE_MAKE_PROGRAM=$NinjaPath", "-DCMAKE_TOOLCHAIN_FILE=$toolchainPath",
        '-DANDROID_ABI=arm64-v8a', "-DANDROID_PLATFORM=android-$MinApiLevel",
        '-DANDROID_STL=c++_static', "-DCMAKE_BUILD_TYPE=$Configuration", "-DUNITY_PLUGIN_API=$pluginAPI"
    )
    Invoke-NativeTool $CMakePath @('--build', $buildPath, '--parallel')
    $compiledPlugin = Join-Path $buildPath 'libPulseVideoNative.so'
    if (!(Test-Path -LiteralPath $compiledPlugin)) { throw '未生成 Android 原生库。' }

    $bridgeArtifact = Join-Path $pluginPath 'PulseVideoBridge.aar'
    if (!$SkipBridgeBuild) {
        $javacPath = Join-Path $JdkPath 'bin/javac.exe'
        $jarPath = Join-Path $JdkPath 'bin/jar.exe'
        if (!(Test-Path -LiteralPath $javacPath) -or !(Test-Path -LiteralPath $jarPath)) {
            throw '缺少 JDK 编译工具，请通过 -JdkPath 指定 JDK。'
        }
        $androidPlatform = Get-ChildItem -LiteralPath (Join-Path $SdkPath 'platforms') -Directory |
            Where-Object { $_.Name -match '^android-\d+$' } |
            Sort-Object { [int]($_.Name.Substring(8)) } -Descending | Select-Object -First 1
        if (!$androidPlatform) { throw 'Android SDK 中未找到可用的 android.jar。' }
        $androidJar = Join-Path $androidPlatform.FullName 'android.jar'
        $bridgeBuildPath = Join-Path $buildPath 'Bridge'
        $classesPath = Join-Path $bridgeBuildPath 'Classes'
        $aarPath = Join-Path $bridgeBuildPath 'Aar'
        New-Item -ItemType Directory -Path $classesPath, $aarPath -Force | Out-Null
        Invoke-NativeTool $javacPath @(
            '-encoding', 'UTF-8', '-source', '8', '-target', '8', '-bootclasspath', $androidJar,
            '-d', $classesPath, (Join-Path $sourcePath 'Java/com/pulse/videoplayer/PulseSurfaceBridge.java')
        )
        Invoke-NativeTool $jarPath @('cf', (Join-Path $aarPath 'classes.jar'), '-C', $classesPath, '.')
        $manifest = @"
<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="com.pulse.videoplayer">
  <uses-sdk android:minSdkVersion="$MinApiLevel" />
</manifest>
"@
        [IO.File]::WriteAllText((Join-Path $aarPath 'AndroidManifest.xml'), $manifest + "`n", $utf8)
        [IO.File]::WriteAllText((Join-Path $aarPath 'proguard.txt'),
            "-keep class com.pulse.videoplayer.PulseSurfaceBridge { *; }`n", $utf8)
        $bridgeArtifact = Join-Path $bridgeBuildPath 'PulseVideoBridge.aar'
        Invoke-NativeTool $jarPath @('cf', $bridgeArtifact, '-C', $aarPath, '.')
    }
    elseif (!(Test-Path -LiteralPath $bridgeArtifact)) {
        throw '首次构建必须生成 Java 桥接 AAR，不能使用 -SkipBridgeBuild。'
    }

    New-Item -ItemType Directory -Path $pluginPath -Force | Out-Null
    $installedPlugin = Join-Path $pluginPath 'libPulseVideoNative.so'
    Copy-Item -LiteralPath $compiledPlugin -Destination $installedPlugin -Force
    Write-PluginMetadata $installedPlugin 'ARM64'
    $installedBridge = Join-Path $pluginPath 'PulseVideoBridge.aar'
    if (!$SkipBridgeBuild) { Copy-Item -LiteralPath $bridgeArtifact -Destination $installedBridge -Force }
    Write-PluginMetadata $installedBridge 'AnyCPU'

    if ($GradleProjectPath) {
        # 仅更新指定的插件，不修改导出工程里的 C#、资源或其他原生库。
        $exportedNativePath = Join-Path $GradleProjectPath 'unityLibrary/src/main/jniLibs/arm64-v8a'
        $exportedLibraryPath = Join-Path $GradleProjectPath 'unityLibrary/libs'
        New-Item -ItemType Directory -Path $exportedNativePath, $exportedLibraryPath -Force | Out-Null
        Copy-Item -LiteralPath $installedPlugin -Destination $exportedNativePath -Force
        Copy-Item -LiteralPath $installedBridge -Destination $exportedLibraryPath -Force
        Write-Output '已更新导出工程。接下来由你执行 Gradle 打包，再 adb install -r 覆盖安装。'
    }
    Write-Output "Android ARM64 原生库：$installedPlugin"
    Write-Output "Java 桥接 AAR：$installedBridge"
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
