#include <GLES3/gl3.h>
#include <GLES2/gl2ext.h>
#include <EGL/egl.h>
#include <android/log.h>
#include <android/native_window_jni.h>
#include <android/asset_manager_jni.h>
#include <media/NdkMediaCodec.h>
#include <media/NdkMediaExtractor.h>
#include <jni.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cerrno>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <fcntl.h>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <sys/stat.h>
#include <thread>
#include <unordered_map>
#include <vector>
#include <unistd.h>
#include "IUnityInterface.h"
#include "IUnityGraphics.h"

namespace
{
    constexpr int OPENING = 1;
    constexpr int SUBMITTED = 3;
    constexpr int READY = 4;
    constexpr int CLOSING = 5;
    constexpr int FAULTED = 6;
    constexpr int FAILURE = -10000;

    struct SessionSnapshot
    {
        int32_t State;
        int32_t Result;
        int32_t Stage;
        int32_t Width;
        int32_t Height;
        void* Texture;
        int64_t Timestamp;
    };

    struct PlaybackSnapshot
    {
        int64_t FrameIndex;
        int64_t Timestamp;
        int64_t TextureVersion;
        int64_t DroppedFrames;
        int32_t EndOfStream;
        int32_t InFlight;
        uint64_t CompletedSeek;
    };

    struct ResourceSnapshot
    {
        int32_t Sessions;
        int32_t ClosingSessions;
        int32_t WorkerHandles;
        int32_t OutputTextures;
        int32_t DecoderSurfaces;
        int64_t EstimatedOutputBytes;
    };

    static_assert(sizeof(SessionSnapshot) == 40, "Snapshot ABI must match managed ARM64 layout");
    static_assert(sizeof(PlaybackSnapshot) == 48, "Playback ABI must match managed ARM64 layout");
    static_assert(sizeof(ResourceSnapshot) == 32, "Resources ABI must match managed ARM64 layout");

    JavaVM* m_javaVM = nullptr; // 初始化后保持到插件卸载。
    jclass m_bridgeClass = nullptr; // 从 Unity 类加载器获得的全局类引用。
    jobject m_assetManagerReference = nullptr; // 保持 APK 资源管理器存活。
    AAssetManager* m_assetManager = nullptr; // APK 内未压缩资源按 fd 区间读取。
    jmethodID m_constructor = nullptr; // SurfaceTexture 桥接构造函数。
    jmethodID m_getSurface = nullptr; // 获取 Java Surface。
    jmethodID m_updateFrame = nullptr; // 消费到帧通知并获得变换矩阵。
    jmethodID m_releaseBridge = nullptr; // 释放 Java 表面。
    IUnityGraphics* m_graphics = nullptr; // Unity 图形事件接口。
    std::atomic<bool> m_deviceReady{false}; // 仅支持 GLES3，设备丢失后立即拒绝新会话。
    std::atomic<uint64_t> m_nextHandle{1}; // 防止旧句柄误命中新会话。
    std::mutex m_registryMutex; // 锁顺序固定为索引锁、会话锁。

    class JavaEnvironment
    {
    private:
        JNIEnv* m_environment = nullptr; // 当前线程的 JNI 环境。
        bool m_attached = false; // 仅分离由本对象附加的原生线程。

    public:
        JavaEnvironment()
        {
            if (m_javaVM == nullptr) return;
            jint result = m_javaVM->GetEnv(reinterpret_cast<void**>(&m_environment), JNI_VERSION_1_6);
            if (result == JNI_EDETACHED)
            {
                m_attached = m_javaVM->AttachCurrentThread(&m_environment, nullptr) == JNI_OK;
                if (!m_attached) m_environment = nullptr;
            }
            else if (result != JNI_OK) m_environment = nullptr;
        }

        ~JavaEnvironment()
        {
            if (m_attached) m_javaVM->DetachCurrentThread();
        }

        JNIEnv* Get() const { return m_environment; }
    };

    bool ClearJavaException(JNIEnv* environment)
    {
        if (!environment->ExceptionCheck()) return false;
        environment->ExceptionDescribe();
        environment->ExceptionClear();
        return true;
    }

    // 原生绘制不能破坏 Unity 的状态缓存；包括 SurfaceTexture 内部可能修改的纹理绑定。
    class GraphicsState
    {
    private:
        GLint m_program;
        GLint m_vertexArray;
        GLint m_drawFramebuffer;
        GLint m_readFramebuffer;
        GLint m_activeTexture;
        GLint m_texture;
        GLint m_externalTexture;
        GLint m_sampler;
        GLint m_planeTextures[2];
        GLint m_planeSamplers[2];
        GLint m_unpackBuffer;
        GLint m_unpackAlignment;
        GLint m_unpackRowLength;
        GLint m_unpackSkipRows;
        GLint m_unpackSkipPixels;
        GLint m_viewport[4];
        GLboolean m_colorMask[4];
        GLboolean m_depth;
        GLboolean m_blend;
        GLboolean m_scissor;
        GLboolean m_cull;
        GLboolean m_stencil;
        GLboolean m_discard;

        static void RestoreCapability(GLenum capability, GLboolean enabled)
        {
            if (enabled) glEnable(capability);
            else glDisable(capability);
        }

    public:
        GraphicsState()
        {
            glGetIntegerv(GL_CURRENT_PROGRAM, &m_program);
            glGetIntegerv(GL_VERTEX_ARRAY_BINDING, &m_vertexArray);
            glGetIntegerv(GL_DRAW_FRAMEBUFFER_BINDING, &m_drawFramebuffer);
            glGetIntegerv(GL_READ_FRAMEBUFFER_BINDING, &m_readFramebuffer);
            glGetIntegerv(GL_ACTIVE_TEXTURE, &m_activeTexture);
            glGetIntegerv(GL_PIXEL_UNPACK_BUFFER_BINDING, &m_unpackBuffer);
            glGetIntegerv(GL_UNPACK_ALIGNMENT, &m_unpackAlignment);
            glGetIntegerv(GL_UNPACK_ROW_LENGTH, &m_unpackRowLength);
            glGetIntegerv(GL_UNPACK_SKIP_ROWS, &m_unpackSkipRows);
            glGetIntegerv(GL_UNPACK_SKIP_PIXELS, &m_unpackSkipPixels);
            glGetIntegerv(GL_VIEWPORT, m_viewport);
            glGetBooleanv(GL_COLOR_WRITEMASK, m_colorMask);
            glActiveTexture(GL_TEXTURE0);
            glGetIntegerv(GL_TEXTURE_BINDING_2D, &m_texture);
            glGetIntegerv(GL_TEXTURE_BINDING_EXTERNAL_OES, &m_externalTexture);
            glGetIntegerv(GL_SAMPLER_BINDING, &m_sampler);
            for (int index = 0; index < 2; ++index)
            {
                glActiveTexture(GL_TEXTURE1 + index);
                glGetIntegerv(GL_TEXTURE_BINDING_2D, &m_planeTextures[index]);
                glGetIntegerv(GL_SAMPLER_BINDING, &m_planeSamplers[index]);
            }
            glActiveTexture(GL_TEXTURE0);
            m_depth = glIsEnabled(GL_DEPTH_TEST);
            m_blend = glIsEnabled(GL_BLEND);
            m_scissor = glIsEnabled(GL_SCISSOR_TEST);
            m_cull = glIsEnabled(GL_CULL_FACE);
            m_stencil = glIsEnabled(GL_STENCIL_TEST);
            m_discard = glIsEnabled(GL_RASTERIZER_DISCARD);
        }

        ~GraphicsState()
        {
            glUseProgram(m_program);
            glBindVertexArray(m_vertexArray);
            glBindFramebuffer(GL_DRAW_FRAMEBUFFER, m_drawFramebuffer);
            glBindFramebuffer(GL_READ_FRAMEBUFFER, m_readFramebuffer);
            glBindBuffer(GL_PIXEL_UNPACK_BUFFER, m_unpackBuffer);
            glPixelStorei(GL_UNPACK_ALIGNMENT, m_unpackAlignment);
            glPixelStorei(GL_UNPACK_ROW_LENGTH, m_unpackRowLength);
            glPixelStorei(GL_UNPACK_SKIP_ROWS, m_unpackSkipRows);
            glPixelStorei(GL_UNPACK_SKIP_PIXELS, m_unpackSkipPixels);
            glActiveTexture(GL_TEXTURE0);
            glBindTexture(GL_TEXTURE_2D, m_texture);
            glBindTexture(GL_TEXTURE_EXTERNAL_OES, m_externalTexture);
            glBindSampler(0, m_sampler);
            for (int index = 0; index < 2; ++index)
            {
                glActiveTexture(GL_TEXTURE1 + index);
                glBindTexture(GL_TEXTURE_2D, m_planeTextures[index]);
                glBindSampler(index + 1, m_planeSamplers[index]);
            }
            glActiveTexture(m_activeTexture);
            glViewport(m_viewport[0], m_viewport[1], m_viewport[2], m_viewport[3]);
            glColorMask(m_colorMask[0], m_colorMask[1], m_colorMask[2], m_colorMask[3]);
            RestoreCapability(GL_DEPTH_TEST, m_depth);
            RestoreCapability(GL_BLEND, m_blend);
            RestoreCapability(GL_SCISSOR_TEST, m_scissor);
            RestoreCapability(GL_CULL_FACE, m_cull);
            RestoreCapability(GL_STENCIL_TEST, m_stencil);
            RestoreCapability(GL_RASTERIZER_DISCARD, m_discard);
        }
    };

    GLuint CompileShader(GLenum type, const char* source)
    {
        GLuint shader = glCreateShader(type);
        glShaderSource(shader, 1, &source, nullptr);
        glCompileShader(shader);
        GLint compiled = GL_FALSE;
        glGetShaderiv(shader, GL_COMPILE_STATUS, &compiled);
        if (compiled == GL_TRUE) return shader;
        char message[1024]{};
        glGetShaderInfoLog(shader, sizeof(message), nullptr, message);
        __android_log_print(ANDROID_LOG_ERROR, "Pulse", "Shader: %s", message);
        glDeleteShader(shader);
        return 0;
    }

    GLuint CreateProgram(bool linear)
    {
        const char* vertexSource = R"(#version 300 es
out vec2 coordinates;
void main() {
    vec2 point = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    coordinates = point;
    gl_Position = vec4(point * 2.0 - 1.0, 0.0, 1.0);
})";
        const char* surfaceFragment = R"(#version 300 es
#extension GL_OES_EGL_image_external_essl3 : require
precision highp float;
uniform samplerExternalOES sourceTexture;
uniform mat4 sourceTransform;
in vec2 coordinates;
out vec4 color;
void main() {
    // 输出纹理保持与 Windows 一致的左上角图像约定，UI Shader 统一处理区域坐标。
    vec2 topLeft = vec2(coordinates.x, 1.0 - coordinates.y);
    vec2 samplePoint = (sourceTransform * vec4(topLeft, 0.0, 1.0)).xy;
    color = vec4(texture(sourceTexture, samplePoint).rgb, 1.0);
})";
        const char* linearFragment = R"(#version 300 es
precision highp float;
uniform sampler2D sourceTexture;
uniform sampler2D sourceU;
uniform sampler2D sourceV;
uniform int colorMatrix;
in vec2 coordinates;
out vec4 color;
void main() {
    // 三张线性 R8 平面保持原始左上角行序，输出统一为编码 RGB。
    vec3 yuv = vec3(texture(sourceTexture, coordinates).r,
        texture(sourceU, coordinates).r, texture(sourceV, coordinates).r);
    float y = (yuv.r - 16.0 / 255.0) * (255.0 / 219.0);
    float u = (yuv.g - 128.0 / 255.0) * (255.0 / 224.0);
    float v = (yuv.b - 128.0 / 255.0) * (255.0 / 224.0);
    vec3 rgb;
    if (colorMatrix == 1)
        rgb = vec3(y + 1.5748 * v, y - 0.187324 * u - 0.468124 * v, y + 1.8556 * u);
    else
        rgb = vec3(y + 1.402 * v, y - 0.344136 * u - 0.714136 * v, y + 1.772 * u);
    color = vec4(clamp(rgb, 0.0, 1.0), 1.0);
})";
        GLuint vertex = CompileShader(GL_VERTEX_SHADER, vertexSource);
        GLuint fragment = CompileShader(GL_FRAGMENT_SHADER, linear ? linearFragment : surfaceFragment);
        if (vertex == 0 || fragment == 0)
        {
            if (vertex != 0) glDeleteShader(vertex);
            if (fragment != 0) glDeleteShader(fragment);
            return 0;
        }
        GLuint program = glCreateProgram();
        glAttachShader(program, vertex);
        glAttachShader(program, fragment);
        glLinkProgram(program);
        glDeleteShader(vertex);
        glDeleteShader(fragment);
        GLint linked = GL_FALSE;
        glGetProgramiv(program, GL_LINK_STATUS, &linked);
        if (linked == GL_TRUE) return program;
        char message[1024]{};
        glGetProgramInfoLog(program, sizeof(message), nullptr, message);
        __android_log_print(ANDROID_LOG_ERROR, "Pulse", "Program: %s", message);
        glDeleteProgram(program);
        return 0;
    }

    struct Session
    {
        std::mutex Mutex; // 保护快照、控制请求和单帧槽。
        std::condition_variable Wake; // 播放目标、Seek、到帧完成或关闭后唤醒线程。
        std::thread Worker; // 唯一接触 MediaExtractor 与 MediaCodec 的线程。
        std::atomic<bool> Closing{false}; // 关闭可打断全部有界等待。
        std::atomic<bool> WorkerDone{false}; // 退出后才允许销毁解码表面。
        std::string FilePath; // UTF-8 本地路径。
        int Width;
        int Height;
        int Matrix;
        int64_t Frames;
        uint32_t Numerator;
        uint32_t Denominator;
        int State = OPENING;
        int Result = 0;
        int Stage = 0;
        bool Playing = false;
        bool Initialized = false;
        bool Pending = false;
        bool EndOfStream = false;
        bool HasFirstTimestamp = false;
        bool Linear = false;
        bool FallbackRequested = false;
        int64_t FirstTimestampUs = 0;
        int64_t TargetFrame = 0;
        int64_t PresentedFrame = -1;
        int64_t PresentedTimestamp = 0;
        int64_t TextureVersion = 0;
        int64_t DroppedFrames = 0;
        int64_t PendingFrame = -1;
        int64_t PendingTimestamp = 0;
        uint64_t SeekGeneration = 0;
        uint64_t AppliedSeek = 0;
        uint64_t PendingGeneration = 0;
        uint64_t CompletedSeek = 0;
        int64_t SeekTimestampUs = 0;
        GLuint ExternalTexture = 0;
        GLuint OutputTexture = 0;
        GLuint Framebuffer = 0;
        GLuint Program = 0;
        GLuint LinearProgram = 0;
        GLuint LinearTextures[3]{};
        GLuint VertexArray = 0;
        GLint TransformLocation = -1;
        GLsync FrameFence = nullptr;
        GLsync RetirementFence = nullptr;
        jobject Bridge = nullptr; // 仅在创建它的渲染上下文更新与释放。
        ANativeWindow* Window = nullptr; // 在线程结束后归还引用。
        std::vector<uint8_t> LinearPixels; // 重用单帧 YUV 上传缓冲，大小只与画面尺寸有关。
        int ColorFormat = 0;
        int Stride = 0;
        int SliceHeight = 0;
        int CropLeft = 0;
        int CropTop = 0;
        std::chrono::steady_clock::time_point PendingSince;

        Session(const char* path, int width, int height, int matrix,
            int64_t frames, uint32_t numerator, uint32_t denominator)
            : FilePath(path), Width(width), Height(height), Matrix(matrix), Frames(frames),
              Numerator(numerator), Denominator(denominator) {}

        ~Session()
        {
            Closing.store(true);
            Wake.notify_all();
            if (Worker.joinable()) Worker.join();
        }

        void Fail(int result, int stage)
        {
            // 调用者必须持有会话锁；驱动错误保留原始状态码供 C# 报告。
            Result = result < 0 ? result : FAILURE;
            Stage = stage;
            if (!Closing.load())
            {
                if (!Linear && stage != 8 && stage != 11 && stage != 12 && stage != 13)
                {
                    FallbackRequested = true;
                    State = TextureVersion > 0 ? READY : OPENING;
                }
                else State = FAULTED;
            }
            __android_log_print(ANDROID_LOG_ERROR, "Pulse", "Native failure: stage=%d result=%d", Stage, Result);
            Wake.notify_all();
        }
    };

    std::unordered_map<uint64_t, std::shared_ptr<Session>> m_sessions; // 关闭后由渲染线程完成退役。

    // 所有解码器对象均随工作线程栈退出，失败路径也会归还文件与编解码器资源。
    struct Decoder
    {
        int File = -1;
        AMediaExtractor* Extractor = nullptr;
        AMediaCodec* Codec = nullptr;
        bool Started = false;

        ~Decoder()
        {
            if (Codec != nullptr)
            {
                if (Started) AMediaCodec_stop(Codec);
                AMediaCodec_delete(Codec);
            }
            if (Extractor != nullptr) AMediaExtractor_delete(Extractor);
            if (File >= 0) close(File);
        }
    };

    int OpenFileRange(const char* path, int64_t& offset, int64_t& length)
    {
        offset = 0;
        length = 0;
        if (path == nullptr) return -1;
        if (std::strncmp(path, "jar:", 4) == 0)
        {
            const char* assetPath = std::strstr(path, "!/assets/");
            if (assetPath == nullptr || m_assetManager == nullptr) return -1;
            AAsset* asset = AAssetManager_open(m_assetManager, assetPath + 9, AASSET_MODE_RANDOM);
            if (asset == nullptr) return -1;
            off64_t start = 0;
            off64_t size = 0;
            int descriptor = AAsset_openFileDescriptor64(asset, &start, &size);
            AAsset_close(asset);
            if (descriptor < 0)
                __android_log_print(ANDROID_LOG_ERROR, "Pulse",
                    "APK asset must be stored without compression: %s", path);
            offset = start;
            length = size;
            return descriptor;
        }
        int descriptor = open(path, O_RDONLY | O_CLOEXEC);
        struct stat fileInfo{};
        if (descriptor < 0) return -1;
        if (fstat(descriptor, &fileInfo) != 0 || fileInfo.st_size <= 0)
        {
            close(descriptor);
            return -1;
        }
        length = fileInfo.st_size;
        return descriptor;
    }

    bool OpenDecoder(Session& session, Decoder& decoder)
    {
        int64_t offset = 0;
        int64_t length = 0;
        decoder.File = OpenFileRange(session.FilePath.c_str(), offset, length);
        if (decoder.File < 0 || length <= 0) return false;
        decoder.Extractor = AMediaExtractor_new();
        if (decoder.Extractor == nullptr) return false;
        media_status_t status = AMediaExtractor_setDataSourceFd(
            decoder.Extractor, decoder.File, offset, length);
        if (status != AMEDIA_OK) return false;

        // Pulse v1 只允许单路 H.264 视频；忽略独立音轨之外的非视频轨。
        AMediaFormat* videoFormat = nullptr;
        size_t videoTrack = 0;
        for (size_t index = 0; index < AMediaExtractor_getTrackCount(decoder.Extractor); ++index)
        {
            AMediaFormat* format = AMediaExtractor_getTrackFormat(decoder.Extractor, index);
            const char* mime = nullptr;
            if (format != nullptr && AMediaFormat_getString(format, AMEDIAFORMAT_KEY_MIME, &mime) &&
                std::strncmp(mime, "video/", 6) == 0)
            {
                if (videoFormat != nullptr || std::strcmp(mime, "video/avc") != 0)
                {
                    AMediaFormat_delete(format);
                    if (videoFormat != nullptr) AMediaFormat_delete(videoFormat);
                    return false;
                }
                videoFormat = format;
                videoTrack = index;
            }
            else if (format != nullptr) AMediaFormat_delete(format);
        }
        if (videoFormat == nullptr) return false;
        int32_t width = 0;
        int32_t height = 0;
        int32_t rotation = 0;
        AMediaFormat_getInt32(videoFormat, "rotation-degrees", &rotation);
        bool valid = AMediaFormat_getInt32(videoFormat, AMEDIAFORMAT_KEY_WIDTH, &width) &&
            AMediaFormat_getInt32(videoFormat, AMEDIAFORMAT_KEY_HEIGHT, &height) &&
            width == session.Width && height == session.Height && rotation == 0;
        if (!valid)
        {
            AMediaFormat_delete(videoFormat);
            return false;
        }

        // 系统 Surface 路径负责 YUV 转换，默认色彩信息与离线导出的矩阵保持一致。
        AMediaFormat_setInt32(videoFormat, "color-standard", session.Matrix == 1 ? 1 : 2);
        AMediaFormat_setInt32(videoFormat, "color-range", 2);
        AMediaFormat_setInt32(videoFormat, "color-transfer", 3);
        if (session.Linear) AMediaFormat_setInt32(videoFormat, AMEDIAFORMAT_KEY_COLOR_FORMAT, 19);
        decoder.Codec = AMediaCodec_createDecoderByType("video/avc");
        if (decoder.Codec == nullptr)
        {
            AMediaFormat_delete(videoFormat);
            return false;
        }
        status = AMediaExtractor_selectTrack(decoder.Extractor, videoTrack);
        if (status == AMEDIA_OK)
            status = AMediaCodec_configure(
                decoder.Codec, videoFormat, session.Linear ? nullptr : session.Window, nullptr, 0);
        AMediaFormat_delete(videoFormat);
        if (status != AMEDIA_OK) return false;
        status = AMediaCodec_start(decoder.Codec);
        decoder.Started = status == AMEDIA_OK;
        // 回退重开仍从当前目标关键帧追帧，目标与新 Seek 在循环切点读取。
        int64_t recoveryTarget = 0;
        int64_t sourceTimestamp = 0;
        {
            std::lock_guard<std::mutex> lock(session.Mutex);
            recoveryTarget = session.TargetFrame;
            sourceTimestamp = session.FirstTimestampUs;
        }
        if (decoder.Started && recoveryTarget > 0)
        {
            int64_t targetUs = static_cast<int64_t>(static_cast<long double>(recoveryTarget) *
                1000000.0L * session.Denominator / session.Numerator);
            status = AMediaExtractor_seekTo(decoder.Extractor,
                sourceTimestamp + targetUs, AMEDIAEXTRACTOR_SEEK_PREVIOUS_SYNC);
            if (status != AMEDIA_OK) return false;
        }
        return decoder.Started;
    }

    int FeedInput(Decoder& decoder, bool& inputEnded)
    {
        if (inputEnded) return AMEDIA_OK;
        ssize_t inputIndex = AMediaCodec_dequeueInputBuffer(decoder.Codec, 2000);
        if (inputIndex == AMEDIACODEC_INFO_TRY_AGAIN_LATER) return AMEDIA_OK;
        if (inputIndex < 0) return static_cast<int>(inputIndex);
        size_t capacity = 0;
        uint8_t* buffer = AMediaCodec_getInputBuffer(decoder.Codec, inputIndex, &capacity);
        if (buffer == nullptr) return FAILURE;
        int64_t sampleTime = AMediaExtractor_getSampleTime(decoder.Extractor);
        ssize_t sampleSize = AMediaExtractor_readSampleData(decoder.Extractor, buffer, capacity);
        if (sampleSize < 0)
        {
            inputEnded = true;
            return AMediaCodec_queueInputBuffer(
                decoder.Codec, inputIndex, 0, 0, 0, AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM);
        }
        if (static_cast<size_t>(sampleSize) > capacity || sampleTime < 0) return FAILURE;
        media_status_t status = AMediaCodec_queueInputBuffer(
            decoder.Codec, inputIndex, 0, sampleSize, sampleTime, 0);
        if (status == AMEDIA_OK) AMediaExtractor_advance(decoder.Extractor);
        return status;
    }

    bool ValidateOutputFormat(Session& session, Decoder& decoder)
    {
        AMediaFormat* format = AMediaCodec_getOutputFormat(decoder.Codec);
        if (format == nullptr) return false;
        int32_t width = 0;
        int32_t height = 0;
        int32_t left = 0;
        int32_t top = 0;
        AMediaFormat_getInt32(format, AMEDIAFORMAT_KEY_WIDTH, &width);
        AMediaFormat_getInt32(format, AMEDIAFORMAT_KEY_HEIGHT, &height);
        int32_t right = width - 1;
        int32_t bottom = height - 1;
        AMediaFormat_getInt32(format, "crop-left", &left);
        AMediaFormat_getInt32(format, "crop-top", &top);
        AMediaFormat_getInt32(format, "crop-right", &right);
        AMediaFormat_getInt32(format, "crop-bottom", &bottom);
        bool valid = right - left + 1 == session.Width && bottom - top + 1 == session.Height;
        if (session.Linear)
        {
            // flexible/vendor 格式不能仅凭名称猜测布局，只接受可确定的 I420 与 NV12。
            int32_t colorFormat = 0;
            int32_t stride = width;
            int32_t sliceHeight = height;
            AMediaFormat_getInt32(format, AMEDIAFORMAT_KEY_COLOR_FORMAT, &colorFormat);
            AMediaFormat_getInt32(format, "stride", &stride);
            AMediaFormat_getInt32(format, "slice-height", &sliceHeight);
            if (stride <= 0) stride = width;
            if (sliceHeight <= 0) sliceHeight = height;
            valid = valid && (colorFormat == 19 || colorFormat == 21) && left >= 0 && top >= 0 &&
                left % 2 == 0 && top % 2 == 0 && stride % 2 == 0 && sliceHeight % 2 == 0 &&
                stride >= right + 1 && sliceHeight >= bottom + 1;
            session.ColorFormat = colorFormat;
            session.Stride = stride;
            session.SliceHeight = sliceHeight;
            session.CropLeft = left;
            session.CropTop = top;
            __android_log_print(ANDROID_LOG_INFO, "Pulse", "Linear format=%d stride=%d slice=%d",
                colorFormat, stride, sliceHeight);
        }
        AMediaFormat_delete(format);
        return valid;
    }

    bool CopyLinearFrame(Session& session, Decoder& decoder, size_t index, const AMediaCodecBufferInfo& info)
    {
        size_t capacity = 0;
        uint8_t* buffer = AMediaCodec_getOutputBuffer(decoder.Codec, index, &capacity);
        if (buffer == nullptr || info.offset < 0 || info.size <= 0 ||
            static_cast<size_t>(info.offset) > capacity ||
            static_cast<size_t>(info.size) > capacity - info.offset || session.Stride <= 0 ||
            session.SliceHeight <= 0 || (session.ColorFormat != 19 && session.ColorFormat != 21)) return false;
        size_t stride = session.Stride;
        size_t sliceHeight = session.SliceHeight;
        size_t luminanceSize = stride * sliceHeight;
        size_t sourceSize = luminanceSize + luminanceSize / 2;
        if (sourceSize > static_cast<size_t>(info.size)) return false;
        const uint8_t* source = buffer + info.offset;
        size_t outputSize = static_cast<size_t>(session.Width) * session.Height;
        session.LinearPixels.resize(outputSize + outputSize / 2);
        uint8_t* output = session.LinearPixels.data();
        for (int row = 0; row < session.Height; ++row)
            std::memcpy(output + static_cast<size_t>(row) * session.Width,
                source + (row + session.CropTop) * stride + session.CropLeft, session.Width);
        size_t chromaStride = stride / 2;
        for (int row = 0; row < session.Height / 2; ++row)
        {
            size_t outputRow = static_cast<size_t>(row) * (session.Width / 2);
            size_t sourceRow = static_cast<size_t>(row + session.CropTop / 2);
            for (int column = 0; column < session.Width / 2; ++column)
            {
                size_t chromaColumn = column + session.CropLeft / 2;
                size_t uPosition = session.ColorFormat == 19
                    ? luminanceSize + sourceRow * chromaStride + chromaColumn
                    : luminanceSize + sourceRow * stride + chromaColumn * 2;
                size_t vPosition = session.ColorFormat == 19
                    ? uPosition + luminanceSize / 4 : uPosition + 1;
                output[outputSize + outputRow + column] = source[uPosition];
                output[outputSize + outputSize / 4 + outputRow + column] = source[vPosition];
            }
        }
        return true;
    }

    bool CanDecode(const Session& session, bool outputEnded)
    {
        return !session.Pending &&
            (session.SeekGeneration != session.AppliedSeek ||
             (!outputEnded && (session.PresentedFrame < 0 || session.Playing ||
              session.CompletedSeek != session.SeekGeneration || session.PresentedFrame == session.Frames - 1)));
    }

    void DecodeLoop(Session& session)
    {
        Decoder decoder;
        if (!OpenDecoder(session, decoder))
        {
            std::lock_guard<std::mutex> lock(session.Mutex);
            session.Fail(FAILURE, 2);
            return;
        }
        bool inputEnded = false;
        bool outputEnded = false;
        int64_t lastDecodedFrame = -1;
        uint64_t generation = 0;
        {
            std::lock_guard<std::mutex> lock(session.Mutex);
            generation = session.AppliedSeek;
        }

        while (!session.Closing.load())
        {
            bool seekNeeded = false;
            int64_t seekTimestamp = 0;
            {
                std::unique_lock<std::mutex> lock(session.Mutex);
                // 单帧槽直到 GPU 围栏完成才归还，限制 Surface 队列深度并支持暂停。
                while (!session.Closing.load() && session.State != FAULTED && !session.FallbackRequested &&
                    !CanDecode(session, outputEnded)) session.Wake.wait(lock);
                if (session.Closing.load() || session.State == FAULTED || session.FallbackRequested) return;
                if (session.SeekGeneration != session.AppliedSeek)
                {
                    // 不持有未消费的 Surface 帧时才允许 flush，防止旧通知冒充新 Seek。
                    seekTimestamp = session.FirstTimestampUs + session.SeekTimestampUs;
                    session.AppliedSeek = session.SeekGeneration;
                    generation = session.AppliedSeek;
                    seekNeeded = true;
                }
            }
            if (seekNeeded)
            {
                // 驱动刷新与定位可能耗时，不能持有主线程读取快照所需的会话锁。
                media_status_t status = AMediaCodec_flush(decoder.Codec);
                if (status == AMEDIA_OK)
                    status = AMediaExtractor_seekTo(
                        decoder.Extractor, seekTimestamp, AMEDIAEXTRACTOR_SEEK_PREVIOUS_SYNC);
                if (status != AMEDIA_OK)
                {
                    std::lock_guard<std::mutex> lock(session.Mutex);
                    session.Fail(status, 13);
                    return;
                }
                inputEnded = false;
                outputEnded = false;
                lastDecodedFrame = -1;
            }

            int inputResult = FeedInput(decoder, inputEnded);
            if (inputResult != AMEDIA_OK)
            {
                std::lock_guard<std::mutex> lock(session.Mutex);
                session.Fail(inputResult, 10);
                return;
            }
            AMediaCodecBufferInfo info{};
            ssize_t outputIndex = AMediaCodec_dequeueOutputBuffer(decoder.Codec, &info, 2000);
            if (outputIndex == AMEDIACODEC_INFO_TRY_AGAIN_LATER ||
                outputIndex == AMEDIACODEC_INFO_OUTPUT_BUFFERS_CHANGED) continue;
            if (outputIndex == AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED)
            {
                if (!ValidateOutputFormat(session, decoder))
                {
                    std::lock_guard<std::mutex> lock(session.Mutex);
                    session.Fail(FAILURE, 12);
                    return;
                }
                continue;
            }
            if (outputIndex < 0)
            {
                std::lock_guard<std::mutex> lock(session.Mutex);
                session.Fail(static_cast<int>(outputIndex), 4);
                return;
            }

            std::unique_lock<std::mutex> lock(session.Mutex);
            // 新请求在当前解码调用期间到达时，释放旧输出并立即转入下一轮定位。
            bool stale = generation != session.SeekGeneration || session.Closing.load();
            bool hasImage = info.size > 0 && (info.flags & AMEDIACODEC_BUFFER_FLAG_CODEC_CONFIG) == 0;
            if (!stale && hasImage)
            {
                if (!session.HasFirstTimestamp)
                {
                    session.FirstTimestampUs = info.presentationTimeUs;
                    session.HasFirstTimestamp = true;
                }
                long double position = static_cast<long double>(info.presentationTimeUs - session.FirstTimestampUs) *
                    session.Numerator / (1000000.0L * session.Denominator);
                int64_t frame = static_cast<int64_t>(std::llround(position));
                if (frame < 0 || frame >= session.Frames || std::fabs(position - frame) > 0.1L ||
                    (lastDecodedFrame >= 0 && frame != lastDecodedFrame + 1))
                {
                    AMediaCodec_releaseOutputBuffer(decoder.Codec, outputIndex, false);
                    session.Fail(FAILURE, 11);
                    return;
                }
                lastDecodedFrame = frame;
                bool seeking = session.CompletedSeek != generation;
                if ((session.PresentedFrame < 0 || seeking) && frame > session.TargetFrame)
                {
                    AMediaCodec_releaseOutputBuffer(decoder.Codec, outputIndex, false);
                    session.Fail(FAILURE, 13);
                    return;
                }
                // 先持有未来帧，游戏时钟到达该帧才呈现，不能按渲染帧率提前播放视频。
                while (!session.Closing.load() && session.SeekGeneration == generation &&
                    !session.FallbackRequested && session.State != FAULTED &&
                    (frame > session.TargetFrame ||
                     (!session.Playing && session.PresentedFrame >= 0 && !seeking)))
                {
                    session.Wake.wait(lock);
                }
                if (session.Closing.load() || session.FallbackRequested || session.State == FAULTED)
                {
                    AMediaCodec_releaseOutputBuffer(decoder.Codec, outputIndex, false);
                    return;
                }
                if (session.SeekGeneration != generation)
                {
                    AMediaCodec_releaseOutputBuffer(decoder.Codec, outputIndex, false);
                    continue;
                }
                bool present = (session.PresentedFrame < 0 || session.Playing || seeking) &&
                    frame >= session.TargetFrame;
                if (present)
                {
                    session.PendingFrame = frame;
                    session.PendingTimestamp = (info.presentationTimeUs - session.FirstTimestampUs) * 10;
                    session.PendingGeneration = generation;
                    if (session.Linear && !CopyLinearFrame(session, decoder, outputIndex, info))
                    {
                        AMediaCodec_releaseOutputBuffer(decoder.Codec, outputIndex, false);
                        session.Fail(FAILURE, 11);
                        return;
                    }
                    session.Pending = true;
                    session.PendingSince = std::chrono::steady_clock::now();
                    media_status_t status = AMediaCodec_releaseOutputBuffer(
                        decoder.Codec, outputIndex, !session.Linear);
                    if (status != AMEDIA_OK)
                    {
                        session.Pending = false;
                        session.Fail(status, 5);
                        return;
                    }
                }
                else
                {
                    AMediaCodec_releaseOutputBuffer(decoder.Codec, outputIndex, false);
                    ++session.DroppedFrames;
                }
            }
            else AMediaCodec_releaseOutputBuffer(decoder.Codec, outputIndex, false);

            if (!stale && (info.flags & AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM) != 0)
            {
                outputEnded = true;
                if (lastDecodedFrame != session.Frames - 1)
                {
                    session.Fail(FAILURE, 10);
                    return;
                }
                session.EndOfStream = true;
            }
        }
    }

    void WorkerMain(Session* session)
    {
        // C++ 异常不得跨线程或 C ABI 边界传播；所有失败仍由托管层收敛到 Faulted。
        for (;;)
        {
            try { DecodeLoop(*session); }
            catch (...)
            {
                std::lock_guard<std::mutex> lock(session->Mutex);
                session->Fail(FAILURE, 4);
            }
            std::lock_guard<std::mutex> lock(session->Mutex);
            if (session->Closing.load() || session->Linear || !session->FallbackRequested) break;
            // 只回退一次；旧 Surface 不再消费，新解码器以 ByteBuffer 输出重新追至游戏目标。
            session->Linear = true;
            session->FallbackRequested = false;
            session->Pending = false;
            session->EndOfStream = false;
            session->Result = 0;
            session->State = session->TextureVersion > 0 ? READY : OPENING;
            __android_log_print(ANDROID_LOG_WARN, "Pulse", "Surface unavailable; falling back to linear I420/NV12");
        }
        session->WorkerDone.store(true);
    }

    bool InitializeGraphics(Session& session, JNIEnv* environment)
    {
        session.Stage = 5;
        GLint maxSize = 0;
        glGetIntegerv(GL_MAX_TEXTURE_SIZE, &maxSize);
        if (session.Width > maxSize || session.Height > maxSize) return false;
        session.LinearProgram = CreateProgram(true);
        if (session.LinearProgram == 0) return false;
        session.Program = CreateProgram(false);
        if (session.Program == 0) session.Linear = true;
        if (session.Program != 0)
            session.TransformLocation = glGetUniformLocation(session.Program, "sourceTransform");
        glGenVertexArrays(1, &session.VertexArray);
        glGenTextures(1, &session.ExternalTexture);
        glActiveTexture(GL_TEXTURE0);
        glBindSampler(0, 0);
        glBindTexture(GL_TEXTURE_EXTERNAL_OES, session.ExternalTexture);
        glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        glGenTextures(1, &session.OutputTexture);
        glBindTexture(GL_TEXTURE_2D, session.OutputTexture);
        glBindBuffer(GL_PIXEL_UNPACK_BUFFER, 0);
        glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
        glPixelStorei(GL_UNPACK_ROW_LENGTH, 0);
        glPixelStorei(GL_UNPACK_SKIP_ROWS, 0);
        glPixelStorei(GL_UNPACK_SKIP_PIXELS, 0);
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, session.Width, session.Height, 0, GL_RGBA, GL_UNSIGNED_BYTE, nullptr);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        glGenFramebuffers(1, &session.Framebuffer);
        glBindFramebuffer(GL_FRAMEBUFFER, session.Framebuffer);
        glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, session.OutputTexture, 0);
        if (glCheckFramebufferStatus(GL_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE) return false;

        // 线性路径同样复用一组平面纹理，不在播放循环重新创建 GL 对象。
        glGenTextures(3, session.LinearTextures);
        for (int plane = 0; plane < 3; ++plane)
        {
            int divisor = plane == 0 ? 1 : 2;
            glBindTexture(GL_TEXTURE_2D, session.LinearTextures[plane]);
            glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, session.Width / divisor, session.Height / divisor,
                0, GL_RED, GL_UNSIGNED_BYTE, nullptr);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        }

        if (!session.Linear)
        {
            jobject bridge = environment->NewObject(
                m_bridgeClass, m_constructor, session.ExternalTexture, session.Width, session.Height);
            bool failed = ClearJavaException(environment);
            if (!failed && bridge != nullptr)
            {
                session.Bridge = environment->NewGlobalRef(bridge);
                jobject surface = environment->CallObjectMethod(bridge, m_getSurface);
                failed = ClearJavaException(environment);
                if (!failed && surface != nullptr) session.Window = ANativeWindow_fromSurface(environment, surface);
                if (surface != nullptr) environment->DeleteLocalRef(surface);
            }
            if (bridge != nullptr) environment->DeleteLocalRef(bridge);
            if (session.Bridge == nullptr || session.Window == nullptr) session.Linear = true;
        }
        session.Initialized = true;
        session.Worker = std::thread(WorkerMain, &session);
        return true;
    }

    bool FenceCompleted(GLsync fence, Session& session)
    {
        GLenum result = glClientWaitSync(fence, 0, 0);
        if (result == GL_WAIT_FAILED)
        {
            session.Fail(FAILURE, 8);
            return false;
        }
        return result == GL_ALREADY_SIGNALED || result == GL_CONDITION_SATISFIED;
    }

    void PollFrame(Session& session)
    {
        if (session.FrameFence == nullptr || !FenceCompleted(session.FrameFence, session)) return;
        glDeleteSync(session.FrameFence);
        session.FrameFence = nullptr;
        if (session.PendingGeneration == session.SeekGeneration && !session.Closing.load())
        {
            session.PresentedFrame = session.PendingFrame;
            session.PresentedTimestamp = session.PendingTimestamp;
            session.CompletedSeek = session.PendingGeneration;
            ++session.TextureVersion;
            session.State = READY;
        }
        session.Pending = false;
        session.Wake.notify_all();
    }

    void SubmitFrame(Session& session, JNIEnv* environment)
    {
        if (!session.Pending || session.FrameFence != nullptr || session.FallbackRequested) return;
        GLfloat matrix[16]{};
        if (!session.Linear)
        {
            auto transform = static_cast<jfloatArray>(environment->CallObjectMethod(session.Bridge, m_updateFrame));
            if (ClearJavaException(environment))
            {
                session.Fail(FAILURE, 5);
                return;
            }
            if (transform == nullptr)
            {
                // Surface 不再到帧时先走线性回退，避免一直等到托管首帧/Seek 超时。
                if (std::chrono::steady_clock::now() - session.PendingSince > std::chrono::seconds(2))
                    session.Fail(FAILURE, 5);
                return;
            }
            environment->GetFloatArrayRegion(transform, 0, 16, matrix);
            environment->DeleteLocalRef(transform);
            if (ClearJavaException(environment))
            {
                session.Fail(FAILURE, 5);
                return;
            }
        }
        if (session.PendingGeneration != session.SeekGeneration)
        {
            // 仍须消费旧 Surface 帧，下一次 Seek 才能安全复用同一通知通道。
            session.Pending = false;
            session.Wake.notify_all();
            return;
        }

        session.Stage = 7;
        glBindFramebuffer(GL_FRAMEBUFFER, session.Framebuffer);
        glViewport(0, 0, session.Width, session.Height);
        glDisable(GL_DEPTH_TEST);
        glDisable(GL_BLEND);
        glDisable(GL_SCISSOR_TEST);
        glDisable(GL_CULL_FACE);
        glDisable(GL_STENCIL_TEST);
        glDisable(GL_RASTERIZER_DISCARD);
        glColorMask(GL_TRUE, GL_TRUE, GL_TRUE, GL_TRUE);
        GLuint program = session.Linear ? session.LinearProgram : session.Program;
        glUseProgram(program);
        glBindVertexArray(session.VertexArray);
        glActiveTexture(GL_TEXTURE0);
        glBindSampler(0, 0);
        glUniform1i(glGetUniformLocation(program, "sourceTexture"), 0);
        if (session.Linear)
        {
            glBindBuffer(GL_PIXEL_UNPACK_BUFFER, 0);
            glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
            glPixelStorei(GL_UNPACK_ROW_LENGTH, 0);
            glPixelStorei(GL_UNPACK_SKIP_ROWS, 0);
            glPixelStorei(GL_UNPACK_SKIP_PIXELS, 0);
            size_t planeOffset = 0;
            for (int plane = 0; plane < 3; ++plane)
            {
                int divisor = plane == 0 ? 1 : 2;
                int width = session.Width / divisor;
                int height = session.Height / divisor;
                glActiveTexture(GL_TEXTURE0 + plane);
                glBindSampler(plane, 0);
                glBindTexture(GL_TEXTURE_2D, session.LinearTextures[plane]);
                glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, width, height, GL_RED, GL_UNSIGNED_BYTE,
                    session.LinearPixels.data() + planeOffset);
                planeOffset += static_cast<size_t>(width) * height;
            }
            glUniform1i(glGetUniformLocation(program, "sourceU"), 1);
            glUniform1i(glGetUniformLocation(program, "sourceV"), 2);
            glUniform1i(glGetUniformLocation(program, "colorMatrix"), session.Matrix);
        }
        else
        {
            glBindTexture(GL_TEXTURE_EXTERNAL_OES, session.ExternalTexture);
            glUniformMatrix4fv(session.TransformLocation, 1, GL_FALSE, matrix);
        }
        glDrawArrays(GL_TRIANGLES, 0, 3);
        session.FrameFence = glFenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
        glFlush();
        if (session.FrameFence == nullptr) session.Fail(FAILURE, 8);
        else if (session.State != READY) session.State = SUBMITTED;
    }

    void ReleaseGraphics(Session& session, JNIEnv* environment, bool hasContext)
    {
        // 解码器已退出，Surface 与 GL 输出不再被任何线程写入。
        if (session.Worker.joinable()) session.Worker.join();
        if (session.Window != nullptr)
        {
            ANativeWindow_release(session.Window);
            session.Window = nullptr;
        }
        if (session.Bridge != nullptr && environment != nullptr)
        {
            environment->CallVoidMethod(session.Bridge, m_releaseBridge);
            ClearJavaException(environment);
            environment->DeleteGlobalRef(session.Bridge);
            session.Bridge = nullptr;
        }
        if (!hasContext) return;
        if (session.FrameFence != nullptr) glDeleteSync(session.FrameFence);
        if (session.RetirementFence != nullptr) glDeleteSync(session.RetirementFence);
        glDeleteFramebuffers(1, &session.Framebuffer);
        glDeleteTextures(1, &session.ExternalTexture);
        glDeleteTextures(1, &session.OutputTexture);
        glDeleteTextures(3, session.LinearTextures);
        glDeleteVertexArrays(1, &session.VertexArray);
        if (session.Program != 0) glDeleteProgram(session.Program);
        if (session.LinearProgram != 0) glDeleteProgram(session.LinearProgram);
    }

    void UNITY_INTERFACE_API RenderEvent(int eventID, void*)
    {
        if (!m_deviceReady.load()) return;
        std::lock_guard<std::mutex> registryLock(m_registryMutex);
        JavaEnvironment java;
        JNIEnv* environment = java.Get();
        if (environment == nullptr) return;
        GraphicsState graphicsState;
        for (auto iterator = m_sessions.begin(); iterator != m_sessions.end();)
        {
            Session& session = *iterator->second;
            std::unique_lock<std::mutex> sessionLock(session.Mutex);
            if (session.Closing.load())
            {
                if ((!session.Worker.joinable() || session.WorkerDone.load()) && eventID == 1)
                {
                    if (session.RetirementFence == nullptr)
                    {
                        // 帧末插入围栏，覆盖请求关闭之前 Unity 对外部纹理的最后一次采样。
                        session.RetirementFence = glFenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
                        glFlush();
                        if (session.RetirementFence == nullptr) glFinish();
                    }
                    else if (!FenceCompleted(session.RetirementFence, session))
                    {
                        ++iterator;
                        continue;
                    }
                    else
                    {
                        ReleaseGraphics(session, environment, true);
                        sessionLock.unlock();
                        iterator = m_sessions.erase(iterator);
                        continue;
                    }
                    if (session.RetirementFence == nullptr)
                    {
                        ReleaseGraphics(session, environment, true);
                        sessionLock.unlock();
                        iterator = m_sessions.erase(iterator);
                        continue;
                    }
                }
            }
            else if (session.State != FAULTED)
            {
                try
                {
                    if (!session.Initialized && eventID == 1 && !InitializeGraphics(session, environment))
                    {
                        session.Linear = true;
                        session.Fail(FAILURE, 5);
                    }
                    if (session.Initialized && session.State != FAULTED)
                    {
                        PollFrame(session);
                        if (eventID == 1 && session.State != FAULTED) SubmitFrame(session, environment);
                    }
                }
                catch (...) { session.Fail(FAILURE, 5); }
            }
            ++iterator;
        }
    }

    void StopAll(bool hasContext)
    {
        std::lock_guard<std::mutex> registryLock(m_registryMutex);
        JavaEnvironment java;
        for (auto& entry : m_sessions)
        {
            entry.second->Closing.store(true);
            entry.second->Wake.notify_all();
        }
        // 设备销毁时没有后续 PlayerLoop；有界 dequeue 等待结束后同步归还所有资源。
        for (auto& entry : m_sessions)
        {
            Session& session = *entry.second;
            if (session.Worker.joinable()) session.Worker.join();
            if (hasContext) glFinish();
            ReleaseGraphics(session, java.Get(), hasContext);
        }
        m_sessions.clear();
    }

    void UNITY_INTERFACE_API DeviceEvent(UnityGfxDeviceEventType eventType)
    {
        if (eventType == kUnityGfxDeviceEventInitialize || eventType == kUnityGfxDeviceEventAfterReset)
            m_deviceReady.store(m_graphics != nullptr && m_graphics->GetRenderer() == kUnityGfxRendererOpenGLES30);
        else if (eventType == kUnityGfxDeviceEventShutdown || eventType == kUnityGfxDeviceEventBeforeReset)
        {
            m_deviceReady.store(false);
            StopAll(eglGetCurrentContext() != EGL_NO_CONTEXT);
        }
    }
}

extern "C" JNIEXPORT void JNICALL
Java_com_pulse_videoplayer_PulseSurfaceBridge_NativeInitialize(
    JNIEnv* environment, jclass bridgeClass, jobject assetManager)
{
    std::lock_guard<std::mutex> registryLock(m_registryMutex);
    if (m_bridgeClass != nullptr) return;
    environment->GetJavaVM(&m_javaVM);
    jmethodID constructor = environment->GetMethodID(bridgeClass, "<init>", "(III)V");
    if (environment->ExceptionCheck() || constructor == nullptr) return;
    jmethodID getSurface = environment->GetMethodID(bridgeClass, "GetSurface", "()Landroid/view/Surface;");
    if (environment->ExceptionCheck() || getSurface == nullptr) return;
    jmethodID updateFrame = environment->GetMethodID(bridgeClass, "UpdateFrame", "()[F");
    if (environment->ExceptionCheck() || updateFrame == nullptr) return;
    jmethodID release = environment->GetMethodID(bridgeClass, "Release", "()V");
    // 保留 JNI 异常，让 C# 初始化立即失败，不能启动缺少桥接方法的半初始化后端。
    if (environment->ExceptionCheck() || constructor == nullptr || getSurface == nullptr ||
        updateFrame == nullptr || release == nullptr) return;
    m_constructor = constructor;
    m_getSurface = getSurface;
    m_updateFrame = updateFrame;
    m_releaseBridge = release;
    m_bridgeClass = static_cast<jclass>(environment->NewGlobalRef(bridgeClass));
    m_assetManagerReference = environment->NewGlobalRef(assetManager);
    m_assetManager = AAssetManager_fromJava(environment, assetManager);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces)
{
    m_graphics = interfaces->Get<IUnityGraphics>();
    if (m_graphics == nullptr) return;
    m_graphics->RegisterDeviceEventCallback(DeviceEvent);
    DeviceEvent(kUnityGfxDeviceEventInitialize);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    if (m_graphics != nullptr) m_graphics->UnregisterDeviceEventCallback(DeviceEvent);
    m_deviceReady.store(false);
    StopAll(eglGetCurrentContext() != EGL_NO_CONTEXT);
    JavaEnvironment java;
    if (java.Get() != nullptr && m_bridgeClass != nullptr) java.Get()->DeleteGlobalRef(m_bridgeClass);
    if (java.Get() != nullptr && m_assetManagerReference != nullptr)
        java.Get()->DeleteGlobalRef(m_assetManagerReference);
    m_bridgeClass = nullptr;
    m_assetManagerReference = nullptr;
    m_assetManager = nullptr;
    m_graphics = nullptr;
}

#define PULSEAPI extern "C" __attribute__((visibility("default")))

PULSEAPI int Pulse_GetApiVersion() { return 3; }

PULSEAPI UnityRenderingEventAndData Pulse_GetRenderEvent() { return RenderEvent; }

PULSEAPI int Pulse_IsDeviceReady()
{
    std::lock_guard<std::mutex> registryLock(m_registryMutex);
    return m_deviceReady.load() && m_bridgeClass != nullptr ? 1 : 0;
}

PULSEAPI int Pulse_CreateSession(const char* filePath, int width, int height, int matrix,
    int64_t frames, uint32_t numerator, uint32_t denominator, uint64_t* handle)
{
    if (handle == nullptr) return FAILURE;
    *handle = 0;
    if (filePath == nullptr || *filePath == '\0' || width <= 0 || height <= 0 ||
        width % 2 != 0 || height % 2 != 0 || frames <= 0 ||
        numerator == 0 || denominator == 0 || (matrix != 0 && matrix != 1)) return FAILURE;
    try
    {
        std::lock_guard<std::mutex> registryLock(m_registryMutex);
        if (!m_deviceReady.load() || m_bridgeClass == nullptr) return FAILURE;
        auto session = std::make_shared<Session>(filePath, width, height, matrix, frames, numerator, denominator);
        uint64_t identifier = m_nextHandle.fetch_add(1);
        m_sessions.emplace(identifier, std::move(session));
        *handle = identifier;
        return 0;
    }
    catch (...) { return FAILURE; }
}

PULSEAPI int Pulse_GetSnapshot(uint64_t handle, SessionSnapshot* snapshot)
{
    if (snapshot == nullptr) return 0;
    std::lock_guard<std::mutex> registryLock(m_registryMutex);
    auto iterator = m_sessions.find(handle);
    if (iterator == m_sessions.end()) return 0;
    Session& session = *iterator->second;
    std::lock_guard<std::mutex> sessionLock(session.Mutex);
    *snapshot = {session.Closing.load() ? CLOSING : session.State, session.Result, session.Stage,
        session.Width, session.Height, reinterpret_cast<void*>(static_cast<uintptr_t>(session.OutputTexture)),
        session.FirstTimestampUs * 10};
    return 1;
}

PULSEAPI int Pulse_GetPlayback(uint64_t handle, PlaybackSnapshot* playback)
{
    if (playback == nullptr) return 0;
    std::lock_guard<std::mutex> registryLock(m_registryMutex);
    auto iterator = m_sessions.find(handle);
    if (iterator == m_sessions.end()) return 0;
    Session& session = *iterator->second;
    std::lock_guard<std::mutex> sessionLock(session.Mutex);
    *playback = {session.PresentedFrame, session.PresentedTimestamp, session.TextureVersion,
        session.DroppedFrames, session.EndOfStream ? 1 : 0, session.Pending ? 1 : 0, session.CompletedSeek};
    return 1;
}

PULSEAPI int Pulse_SetTargetFrame(uint64_t handle, int64_t frame, int playing)
{
    std::lock_guard<std::mutex> registryLock(m_registryMutex);
    auto iterator = m_sessions.find(handle);
    if (iterator == m_sessions.end()) return 0;
    Session& session = *iterator->second;
    std::lock_guard<std::mutex> sessionLock(session.Mutex);
    if (session.Closing.load() || session.State == FAULTED || frame < 0 || frame >= session.Frames) return 0;
    session.TargetFrame = frame;
    session.Playing = playing != 0;
    session.Wake.notify_all();
    return 1;
}

PULSEAPI uint64_t Pulse_Seek(uint64_t handle, int64_t frame, int64_t timestamp)
{
    std::lock_guard<std::mutex> registryLock(m_registryMutex);
    auto iterator = m_sessions.find(handle);
    if (iterator == m_sessions.end()) return 0;
    Session& session = *iterator->second;
    std::lock_guard<std::mutex> sessionLock(session.Mutex);
    if (session.Closing.load() || session.State != READY || frame < 0 || frame >= session.Frames ||
        timestamp < 0 || !session.HasFirstTimestamp) return 0;
    session.TargetFrame = frame;
    session.SeekTimestampUs = timestamp / 10;
    session.EndOfStream = false;
    ++session.SeekGeneration;
    session.Wake.notify_all();
    return session.SeekGeneration;
}

PULSEAPI void Pulse_CloseSession(uint64_t handle)
{
    std::lock_guard<std::mutex> registryLock(m_registryMutex);
    auto iterator = m_sessions.find(handle);
    if (iterator == m_sessions.end()) return;
    iterator->second->Closing.store(true);
    iterator->second->Wake.notify_all();
}

PULSEAPI void Pulse_CloseAll()
{
    std::lock_guard<std::mutex> registryLock(m_registryMutex);
    for (auto& entry : m_sessions)
    {
        entry.second->Closing.store(true);
        entry.second->Wake.notify_all();
    }
}

PULSEAPI int Pulse_GetResources(ResourceSnapshot* resources)
{
    if (resources == nullptr) return 0;
    *resources = {};
    std::lock_guard<std::mutex> registryLock(m_registryMutex);
    for (auto& entry : m_sessions)
    {
        Session& session = *entry.second;
        std::lock_guard<std::mutex> sessionLock(session.Mutex);
        ++resources->Sessions;
        if (session.Closing.load()) ++resources->ClosingSessions;
        if (session.Worker.joinable()) ++resources->WorkerHandles;
        if (session.OutputTexture != 0)
        {
            ++resources->OutputTextures;
            resources->EstimatedOutputBytes += static_cast<int64_t>(session.Width) * session.Height * 4;
        }
        if (session.Window != nullptr) ++resources->DecoderSurfaces;
    }
    return 1;
}

PULSEAPI int Pulse_OpenFile(const char* path, int64_t* offset, int64_t* length)
{
    if (offset == nullptr || length == nullptr) return -1;
    return OpenFileRange(path, *offset, *length);
}

PULSEAPI int Pulse_ReadFile(int descriptor, int64_t offset, uint8_t* buffer, int bufferOffset, int count)
{
    if (descriptor < 0 || offset < 0 || buffer == nullptr || bufferOffset < 0 || count < 0) return -1;
    ssize_t result;
    do { result = pread(descriptor, buffer + bufferOffset, count, offset); }
    while (result < 0 && errno == EINTR);
    return static_cast<int>(result);
}

PULSEAPI void Pulse_CloseFile(int descriptor)
{
    if (descriptor >= 0) close(descriptor);
}
