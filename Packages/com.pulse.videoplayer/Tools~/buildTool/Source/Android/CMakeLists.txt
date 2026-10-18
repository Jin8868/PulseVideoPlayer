cmake_minimum_required(VERSION 3.20)
project(PulseVideoNative LANGUAGES CXX)

if(NOT ANDROID OR NOT ANDROID_ABI STREQUAL "arm64-v8a")
    message(FATAL_ERROR "Pulse Android currently requires arm64-v8a")
endif()
set(UNITY_PLUGIN_API "" CACHE PATH "Unity Editor/Data/PluginAPI directory")
if(NOT EXISTS "${UNITY_PLUGIN_API}/IUnityGraphics.h")
    message(FATAL_ERROR "Set UNITY_PLUGIN_API to the installed Unity PluginAPI directory")
endif()

add_library(PulseVideoNative SHARED PulseVideoNative.cpp)
target_compile_features(PulseVideoNative PRIVATE cxx_std_17)
target_include_directories(PulseVideoNative PRIVATE "${UNITY_PLUGIN_API}")
target_compile_options(PulseVideoNative PRIVATE -Wall -Wextra -Werror=return-type -fvisibility=hidden)
target_link_libraries(PulseVideoNative PRIVATE android mediandk GLESv3 EGL log)
# 兼容要求 16 KiB ELF 页对齐的设备；APK 的 ZIP 对齐由宿主 Android 构建工具负责。
target_link_options(PulseVideoNative PRIVATE "-Wl,-z,max-page-size=16384")
