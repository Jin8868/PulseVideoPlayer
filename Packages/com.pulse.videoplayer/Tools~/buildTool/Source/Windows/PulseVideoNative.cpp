#include <Windows.h>
#include <d3d11.h>
#include <d3d10.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <wrl/client.h>
#include <atomic>
#include <condition_variable>
#include <chrono>
#include <cstdint>
#include <limits>
#include <memory>
#include <list>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>
#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"

using Microsoft::WRL::ComPtr;

namespace
{
    enum class ESessionState : int32_t
    {
        Opening = 1, Decoded = 2, Submitted = 3, Ready = 4, Closing = 5, Faulted = 6
    };

    enum class ESessionStage : int32_t
    {
        Starting = 0, DeviceManager = 1, OpeningFile = 2, SelectingNV12 = 3,
        Decoding = 4, AcquiringSurface = 5, VideoProcessor = 6, Converting = 7, Fence = 8,
        InspectingSourceType = 9, ReadingSample = 10, InspectingDecodedType = 11, ValidatingDecodedSize = 12,
        Seeking = 13
    };

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
    static_assert(sizeof(SessionSnapshot) == 40, "Managed snapshot layout must match Windows x64");

    struct ResourceSnapshot
    {
        int32_t Sessions;
        int32_t ClosingSessions;
        int32_t WorkerHandles;
        int32_t OutputTextures;
        int32_t DecoderSurfaces;
        int64_t EstimatedOutputBytes;
    };
    static_assert(sizeof(ResourceSnapshot) == 32, "Managed resource layout must match Windows x64");

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
    static_assert(sizeof(PlaybackSnapshot) == 48, "Managed playback layout must match Windows x64");

    IUnityInterfaces* m_unityInterfaces = nullptr; // Unity 提供的接口表。
    IUnityGraphics* m_unityGraphics = nullptr; // 图形设备事件接口。
    ComPtr<ID3D11Device> m_device; // 仅由渲染线程建立和清理的 Unity 设备引用。
    std::mutex m_globalMutex; // 设备和会话索引的互斥锁。
    std::atomic<uint64_t> m_nextHandle{1}; // 单调递增会话标识，禁止旧回调命中新会话。
    std::atomic<bool> m_deviceInvalid{false}; // 图形设备关闭后拒绝继续提交。
    std::once_flag m_foundationOnce; // Media Foundation 仅初始化一次。
    HRESULT m_foundationResult = E_FAIL; // Media Foundation 初始化结果。

    void InitializeFoundation()
    {
        m_foundationResult = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    }

    class Session
    {
    private:
        std::wstring m_filePath; // 本地文件路径，不依赖文件扩展名识别。
        int m_width; // 元数据要求的图像宽度。
        int m_height; // 元数据要求的图像高度。
        int m_matrix; // 未声明矩阵时使用导出器默认的 BT.601。
        int64_t m_expectedFrames; // 容器声明的固定帧率视频总帧数。
        UINT m_frameRateNumerator; // 视频处理器使用的帧率分子。
        UINT m_frameRateDenominator; // 视频处理器使用的帧率分母。
        ComPtr<ID3D11Device> m_sessionDevice; // 会话固定持有创建时的设备。
        std::mutex m_mutex; // 单帧租约、纹理与查询的保护锁。
        std::condition_variable m_slotAvailable; // 消费或关闭时唤醒解码线程。
        std::atomic<ESessionState> m_state{ESessionState::Opening}; // 可供主线程读取的状态。
        std::atomic<ESessionStage> m_stage{ESessionStage::Starting}; // 当前原生步骤。
        std::atomic<HRESULT> m_result{S_OK}; // 原生错误码。
        std::atomic<UINT32> m_detectedWidth{0}; // 出错时报告最近一次读取的媒体类型宽度。
        std::atomic<UINT32> m_detectedHeight{0}; // 出错时报告最近一次读取的媒体类型高度。
        std::atomic<bool> m_closing{false}; // 异步关闭请求。
        std::atomic<bool> m_workerDone{false}; // 解码线程已停止访问会话。
        std::atomic<bool> m_playing{false}; // 主线程请求的播放开关。
        std::atomic<int64_t> m_targetFrame{0}; // 游戏时钟当前要求的目标帧。
        std::atomic<bool> m_endOfStream{false}; // 完整读取并校验帧数后的结束标记。
        std::atomic<int64_t> m_droppedFrames{0}; // 为追赶游戏时钟丢弃的过时帧数量。
        std::atomic<int64_t> m_firstSourceTimestamp{0}; // 首个有效样本的原始时间戳。
        ComPtr<IMFSample> m_sample; // 保留解码表面直到 GPU 使用结束。
        ComPtr<ID3D11Texture2D> m_inputTexture; // 解码器提供的 NV12 表面。
        UINT m_subresource = 0; // 解码表面所在的纹理数组子资源。
        int64_t m_timestamp = 0; // 待提交帧相对于首帧的时间戳，单位为 100ns。
        int64_t m_pendingFrameIndex = -1; // 当前单帧租约的序号。
        int64_t m_displayedFrameIndex = -1; // 已通过 GPU 围栏的帧序号。
        int64_t m_displayedTimestamp = 0; // 已通过 GPU 围栏的帧相对时间戳。
        int64_t m_textureVersion = 0; // 每次完成 GPU 写入后递增的纹理版本。
        bool m_hasPendingFrame = false; // 单帧租约已由解码线程发布。
        bool m_gpuInFlight = false; // 单帧租约正在被 GPU 使用。
        bool m_firstReady = false; // 首帧已就绪，后续提交不改变托管纹理地址。
        ComPtr<ID3D11Texture2D> m_outputTexture; // 交给 Unity 包装的 BGRA 输出纹理。
        ComPtr<ID3D11VideoProcessorEnumerator> m_enumerator; // 视频转换能力与尺寸描述。
        ComPtr<ID3D11VideoProcessor> m_processor; // NV12 到 BGRA 的 GPU 视频处理器。
        ComPtr<ID3D11VideoProcessorInputView> m_inputView; // 输入纹理视图。
        ComPtr<ID3D11VideoProcessorOutputView> m_outputView; // 输出纹理视图。
        ComPtr<ID3D11Query> m_query; // 当前 GPU 工作的完成查询。
        bool m_retirementFence = false; // 已在关闭后插入最后一次 GPU 围栏。
        uint64_t m_seekGeneration = 0; // 最新跳转请求代次，由会话锁保护。
        uint64_t m_pendingGeneration = 0; // 当前解码表面所属跳转代次。
        uint64_t m_completedSeek = 0; // 最新完成 GPU 提交的跳转代次。
        int64_t m_seekTimestamp = 0; // 跳转所需的源时间戳，单位为 100ns。
        bool m_seekPending = false; // 工作线程尚未执行的定位请求。
        bool m_seeking = false; // 允许暂停时解码并提交目标帧。

        void Fail(HRESULT result)
        {
            m_result.store(result);
            if (!m_closing.load()) m_state.store(ESessionState::Faulted);
            m_slotAvailable.notify_all();
        }

        HRESULT FinishStream(int64_t decodedFrames)
        {
            if (decodedFrames != m_expectedFrames) return MF_E_INVALIDMEDIATYPE;
            m_endOfStream.store(true);
            return S_OK;
        }

        HRESULT DecodeLoop()
        {
            m_stage.store(ESessionStage::DeviceManager);
            UINT resetToken = 0;
            ComPtr<IMFDXGIDeviceManager> manager;
            HRESULT result = MFCreateDXGIDeviceManager(&resetToken, &manager);
            if (FAILED(result)) return result;
            result = manager->ResetDevice(m_sessionDevice.Get(), resetToken);
            if (FAILED(result)) return result;

            ComPtr<IMFAttributes> attributes;
            result = MFCreateAttributes(&attributes, 4);
            if (FAILED(result)) return result;
            result = attributes->SetUnknown(MF_SOURCE_READER_D3D_MANAGER, manager.Get());
            if (FAILED(result)) return result;
            result = attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
            if (FAILED(result)) return result;

            // 直接按字节流识别 MP4，避免系统将 .pulse 当成未知扩展名。
            m_stage.store(ESessionStage::OpeningFile);
            ComPtr<IMFByteStream> byteStream;
            result = MFCreateFile(MF_ACCESSMODE_READ, MF_OPENMODE_FAIL_IF_NOT_EXIST,
                MF_FILEFLAGS_NONE, m_filePath.c_str(), &byteStream);
            if (FAILED(result)) return result;
            ComPtr<IMFAttributes> streamAttributes;
            if (SUCCEEDED(byteStream.As(&streamAttributes)))
            {
                streamAttributes->SetString(MF_BYTESTREAM_CONTENT_TYPE, L"video/mp4");
            }
            ComPtr<IMFSourceReader> reader;
            result = MFCreateSourceReaderFromByteStream(byteStream.Get(), attributes.Get(), &reader);
            if (FAILED(result)) return result;
            result = reader->SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, FALSE);
            if (FAILED(result)) return result;
            result = reader->SetStreamSelection(MF_SOURCE_READER_FIRST_VIDEO_STREAM, TRUE);
            if (FAILED(result)) return result;

            m_stage.store(ESessionStage::SelectingNV12);
            // 先校验压缩流的有效尺寸；解码表面可能包含额外的对齐填充。
            m_stage.store(ESessionStage::InspectingSourceType);
            ComPtr<IMFMediaType> sourceType;
            result = reader->GetNativeMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, &sourceType);
            if (FAILED(result)) return result;
            UINT32 sourceWidth = 0;
            UINT32 sourceHeight = 0;
            result = MFGetAttributeSize(sourceType.Get(), MF_MT_FRAME_SIZE, &sourceWidth, &sourceHeight);
            if (FAILED(result)) return result;
            m_detectedWidth.store(sourceWidth);
            m_detectedHeight.store(sourceHeight);
            if (sourceWidth != static_cast<UINT32>(m_width) || sourceHeight != static_cast<UINT32>(m_height))
            {
                return MF_E_INVALIDMEDIATYPE;
            }
            m_stage.store(ESessionStage::SelectingNV12);
            ComPtr<IMFMediaType> requestedType;
            result = MFCreateMediaType(&requestedType);
            if (FAILED(result)) return result;
            result = requestedType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            if (FAILED(result)) return result;
            result = requestedType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
            if (FAILED(result)) return result;
            result = reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, requestedType.Get());
            if (FAILED(result)) return result;

            int64_t frameIndex = 0;
            int64_t originTimestamp = 0;
            int64_t previousTimestamp = 0;
            int emptySamples = 0;
            uint64_t decodeGeneration = 0;
            bool streamEnded = false;
            bool hasPreviousTimestamp = false;
            while (!m_closing.load())
            {
                bool reposition = false;
                int64_t seekTimestamp = 0;
                // 定位与读取只在本线程执行；等待旧 GPU 租约完成后才允许刷新解码器。
                {
                    std::unique_lock<std::mutex> lock(m_mutex);
                    while (!m_closing.load() && m_state.load() != ESessionState::Faulted &&
                        (m_gpuInFlight || (!m_seekPending && (m_hasPendingFrame || streamEnded ||
                        (m_firstReady && !m_playing.load() && !m_seeking)))))
                    {
                        // 主线程命令只改原子量；超时重查避免通知恰好发生在入睡之前而丢失。
                        m_slotAvailable.wait_for(lock, std::chrono::milliseconds(50));
                    }
                    if (m_closing.load() || m_state.load() == ESessionState::Faulted) return S_FALSE;
                    if (m_seekPending)
                    {
                        m_sample.Reset();
                        m_inputView.Reset();
                        m_inputTexture.Reset();
                        m_hasPendingFrame = false;
                        decodeGeneration = m_seekGeneration;
                        seekTimestamp = m_seekTimestamp;
                        m_seekPending = false;
                        reposition = true;
                    }
                }
                if (reposition)
                {
                    m_stage.store(ESessionStage::Seeking);
                    PROPVARIANT position{};
                    position.vt = VT_I8;
                    position.hVal.QuadPart = seekTimestamp;
                    result = reader->SetCurrentPosition(GUID_NULL, position);
                    if (FAILED(result)) return result;
                    // Seek 返回此前的关键帧，随后依据 PTS 丢弃样本直到目标帧。
                    streamEnded = false;
                    frameIndex = 0;
                    hasPreviousTimestamp = false;
                    emptySamples = 0;
                    m_endOfStream.store(false);
                }
                DWORD streamIndex = 0;
                DWORD flags = 0;
                LONGLONG timestamp = 0;
                ComPtr<IMFSample> sample;
                m_stage.store(ESessionStage::ReadingSample);
                result = reader->ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                    0, &streamIndex, &flags, &timestamp, &sample);
                if (FAILED(result)) return result;
                if ((flags & MF_SOURCE_READERF_ERROR) != 0) return E_FAIL;
                if (!sample)
                {
                    if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                    {
                        result = FinishStream(frameIndex);
                        if (FAILED(result)) return result;
                        streamEnded = true;
                        continue;
                    }
                    if (++emptySamples > 256) return E_FAIL;
                    continue;
                }
                emptySamples = 0;
                if (frameIndex >= m_expectedFrames) return MF_E_INVALIDMEDIATYPE;
                if (decodeGeneration == 0 && frameIndex == 0)
                {
                    originTimestamp = timestamp;
                    m_firstSourceTimestamp.store(timestamp);
                }
                else if (hasPreviousTimestamp && timestamp < previousTimestamp) return MF_E_INVALID_TIMESTAMP;
                previousTimestamp = timestamp;
                hasPreviousTimestamp = true;
                if (originTimestamp < 0 && timestamp > std::numeric_limits<int64_t>::max() + originTimestamp)
                {
                    return MF_E_INVALID_TIMESTAMP;
                }
                int64_t relativeTimestamp = timestamp - originTimestamp;
                if (relativeTimestamp < 0) return MF_E_INVALID_TIMESTAMP;
                if (decodeGeneration != 0)
                {
                    long double samplePosition = static_cast<long double>(relativeTimestamp) *
                        m_frameRateNumerator / (10000000.0L * m_frameRateDenominator);
                    if (samplePosition >= static_cast<long double>(m_expectedFrames) - 0.5L)
                        return MF_E_INVALIDMEDIATYPE;
                    frameIndex = static_cast<int64_t>(samplePosition + 0.5L);
                }
                int64_t sampleFrame = frameIndex++;

                // 可以在工作线程跳过过时样本，无需为其创建纹理视图或向 GPU 提交。
                bool discard = false;
                {
                    std::lock_guard<std::mutex> guard(m_mutex);
                    discard = decodeGeneration != m_seekGeneration ||
                        ((m_seeking || m_playing.load()) && sampleFrame < m_targetFrame.load());
                }
                if (discard)
                {
                    m_droppedFrames.fetch_add(1);
                    if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                    {
                        result = FinishStream(frameIndex);
                        if (FAILED(result)) return result;
                        streamEnded = true;
                    }
                    continue;
                }
                ComPtr<IMFMediaType> actualType;
                m_stage.store(ESessionStage::InspectingDecodedType);
                result = reader->GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, &actualType);
                if (FAILED(result)) return result;
                UINT32 width = 0;
                UINT32 height = 0;
                result = MFGetAttributeSize(actualType.Get(), MF_MT_FRAME_SIZE, &width, &height);
                if (FAILED(result)) return result;
                m_detectedWidth.store(width);
                m_detectedHeight.store(height);
                m_stage.store(ESessionStage::ValidatingDecodedSize);
                // 原始尺寸已经核对，只拒绝无法容纳有效画面的解码输出；GPU 转换裁剪填充。
                if (width < static_cast<UINT32>(m_width) || height < static_cast<UINT32>(m_height))
                {
                    return MF_E_INVALIDMEDIATYPE;
                }
                UINT32 matrix = 0;
                if (SUCCEEDED(actualType->GetUINT32(MF_MT_YUV_MATRIX, &matrix)))
                {
                    if (matrix == MFVideoTransferMatrix_BT709) m_matrix = 1;
                    if (matrix == MFVideoTransferMatrix_BT601) m_matrix = 0;
                }

                m_stage.store(ESessionStage::AcquiringSurface);
                ComPtr<IMFMediaBuffer> buffer;
                result = sample->GetBufferByIndex(0, &buffer);
                if (FAILED(result)) return result;
                ComPtr<IMFDXGIBuffer> dxgiBuffer;
                result = buffer.As(&dxgiBuffer);
                if (FAILED(result)) return result; // 本版不引入 CPU 解码缓冲兜底。
                ComPtr<ID3D11Texture2D> texture;
                result = dxgiBuffer->GetResource(IID_PPV_ARGS(&texture));
                if (FAILED(result)) return result;
                UINT subresource = 0;
                result = dxgiBuffer->GetSubresourceIndex(&subresource);
                if (FAILED(result)) return result;
                D3D11_TEXTURE2D_DESC description{};
                texture->GetDesc(&description);
                if (description.Width < static_cast<UINT>(m_width) ||
                    description.Height < static_cast<UINT>(m_height)) return MF_E_INVALIDMEDIATYPE;
                if (description.Format != DXGI_FORMAT_NV12) return MF_E_INVALIDMEDIATYPE;
                // 顺序读取的首个有效视频样本即为首帧，容器时间轴允许非零起点。

                std::lock_guard<std::mutex> guard(m_mutex);
                if (m_closing.load()) return S_FALSE;
                if (decodeGeneration != m_seekGeneration) continue;
                m_sample = sample;
                m_inputTexture = texture;
                m_subresource = subresource;
                m_timestamp = relativeTimestamp;
                m_pendingFrameIndex = sampleFrame;
                m_pendingGeneration = decodeGeneration;
                m_hasPendingFrame = true;
                if (!m_firstReady) m_state.store(ESessionState::Decoded);
                if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    result = FinishStream(frameIndex);
                    if (FAILED(result)) return result;
                    streamEnded = true;
                }
            }
            return S_FALSE;
        }

        HRESULT SubmitFrame(ID3D11DeviceContext* context)
        {
            m_stage.store(ESessionStage::VideoProcessor);
            ComPtr<ID3D11VideoDevice> videoDevice;
            ComPtr<ID3D11VideoContext> videoContext;
            HRESULT result = m_sessionDevice.As(&videoDevice);
            if (FAILED(result)) return result;
            result = context->QueryInterface(IID_PPV_ARGS(&videoContext));
            if (FAILED(result)) return result;
            if (!m_outputTexture)
            {
                D3D11_TEXTURE2D_DESC decodedTexture{};
                m_inputTexture->GetDesc(&decodedTexture);
                D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
                content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
                content.InputWidth = decodedTexture.Width;
                content.InputHeight = decodedTexture.Height;
                content.OutputWidth = m_width;
                content.OutputHeight = m_height;
                content.InputFrameRate = {m_frameRateNumerator, m_frameRateDenominator};
                content.OutputFrameRate = {m_frameRateNumerator, m_frameRateDenominator};
                content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
                result = videoDevice->CreateVideoProcessorEnumerator(&content, &m_enumerator);
                if (FAILED(result)) return result;
                result = videoDevice->CreateVideoProcessor(m_enumerator.Get(), 0, &m_processor);
                if (FAILED(result)) return result;

                D3D11_TEXTURE2D_DESC output{};
                output.Width = m_width;
                output.Height = m_height;
                output.MipLevels = 1;
                output.ArraySize = 1;
                output.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                output.SampleDesc.Count = 1;
                output.Usage = D3D11_USAGE_DEFAULT;
                output.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
                result = m_sessionDevice->CreateTexture2D(&output, nullptr, &m_outputTexture);
                if (FAILED(result)) return result;
                D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputView{};
                outputView.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
                result = videoDevice->CreateVideoProcessorOutputView(
                    m_outputTexture.Get(), m_enumerator.Get(), &outputView, &m_outputView);
                if (FAILED(result)) return result;
            }
            D3D11_TEXTURE2D_DESC input{};
            m_inputTexture->GetDesc(&input);
            D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputView{};
            inputView.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
            inputView.Texture2D.MipSlice = m_subresource % input.MipLevels;
            inputView.Texture2D.ArraySlice = m_subresource / input.MipLevels;
            result = videoDevice->CreateVideoProcessorInputView(
                m_inputTexture.Get(), m_enumerator.Get(), &inputView, &m_inputView);
            if (FAILED(result)) return result;

            RECT rect{0, 0, m_width, m_height};
            videoContext->VideoProcessorSetStreamFrameFormat(
                m_processor.Get(), 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
            videoContext->VideoProcessorSetStreamSourceRect(m_processor.Get(), 0, TRUE, &rect);
            videoContext->VideoProcessorSetStreamDestRect(m_processor.Get(), 0, TRUE, &rect);
            videoContext->VideoProcessorSetOutputTargetRect(m_processor.Get(), TRUE, &rect);
            videoContext->VideoProcessorSetStreamAutoProcessingMode(m_processor.Get(), 0, FALSE);
            D3D11_VIDEO_PROCESSOR_COLOR_SPACE inputColor{};
            inputColor.YCbCr_Matrix = m_matrix != 0 ? 1 : 0;
            inputColor.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
            videoContext->VideoProcessorSetStreamColorSpace(m_processor.Get(), 0, &inputColor);
            D3D11_VIDEO_PROCESSOR_COLOR_SPACE outputColor{};
            videoContext->VideoProcessorSetOutputColorSpace(m_processor.Get(), &outputColor);
            D3D11_VIDEO_PROCESSOR_STREAM stream{};
            stream.Enable = TRUE;
            stream.InputFrameOrField = static_cast<UINT>(m_pendingFrameIndex);
            stream.pInputSurface = m_inputView.Get();
            m_stage.store(ESessionStage::Converting);
            result = videoContext->VideoProcessorBlt(
                m_processor.Get(), m_outputView.Get(), static_cast<UINT>(m_pendingFrameIndex), 1, &stream);
            if (FAILED(result)) return result;
            D3D11_QUERY_DESC queryDescription{D3D11_QUERY_EVENT, 0};
            if (!m_query)
            {
                result = m_sessionDevice->CreateQuery(&queryDescription, &m_query);
                if (FAILED(result)) return result;
            }
            context->End(m_query.Get());
            m_stage.store(ESessionStage::Fence);
            m_hasPendingFrame = false;
            m_gpuInFlight = true;
            if (!m_firstReady)
            {
                ESessionState expected = ESessionState::Decoded;
                m_state.compare_exchange_strong(expected, ESessionState::Submitted);
            }
            return S_OK;
        }

    public:
        Session(std::wstring filePath, int width, int height, int matrix, int64_t frames,
            UINT numerator, UINT denominator, ID3D11Device* device)
            : m_filePath(std::move(filePath)), m_width(width), m_height(height), m_matrix(matrix),
              m_expectedFrames(frames), m_frameRateNumerator(numerator), m_frameRateDenominator(denominator),
              m_sessionDevice(device)
        {
        }

        static void Run(std::shared_ptr<Session> session)
        {
            HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            if (SUCCEEDED(initialized))
            {
                std::call_once(m_foundationOnce, InitializeFoundation);
                HRESULT result = m_foundationResult;
                if (SUCCEEDED(result)) result = session->DecodeLoop();
                if (FAILED(result)) session->Fail(result);
                CoUninitialize();
            }
            else session->Fail(initialized);
            session->m_workerDone.store(true);
        }

        void Close()
        {
            m_closing.store(true);
            m_slotAvailable.notify_all();
        }

        bool SetTargetFrame(int64_t frameIndex, bool playing)
        {
            if (frameIndex < 0 || frameIndex >= m_expectedFrames || m_closing.load() ||
                m_state.load() == ESessionState::Faulted) return false;
            m_targetFrame.store(frameIndex);
            bool previous = m_playing.exchange(playing);
            if (previous != playing) m_slotAvailable.notify_all();
            return true;
        }

        uint64_t Seek(int64_t frameIndex, int64_t relativeTimestamp)
        {
            std::lock_guard<std::mutex> guard(m_mutex);
            if (!m_firstReady || m_closing.load() || m_state.load() == ESessionState::Faulted ||
                frameIndex < 0 || frameIndex >= m_expectedFrames || relativeTimestamp < 0) return 0;
            int64_t origin = m_firstSourceTimestamp.load();
            if (origin > 0 && relativeTimestamp > std::numeric_limits<int64_t>::max() - origin) return 0;
            m_seekTimestamp = origin + relativeTimestamp;
            m_targetFrame.store(frameIndex);
            m_playing.store(false);
            m_seekPending = true;
            m_seeking = true;
            m_endOfStream.store(false);
            ++m_seekGeneration;
            m_slotAvailable.notify_all();
            return m_seekGeneration;
        }

        bool Tick(bool allowSubmit)
        {
            std::lock_guard<std::mutex> guard(m_mutex);
            ComPtr<ID3D11DeviceContext> context;
            m_sessionDevice->GetImmediateContext(&context);
            if (m_deviceInvalid.load())
            {
                Close();
                return m_workerDone.load();
            }
            if (m_closing.load())
            {
                m_state.store(ESessionState::Closing);
                if (!m_workerDone.load()) return false;
                // 帧末插入关闭围栏，等待此前的转换和 UI 采样全部结束再销毁纹理。
                if (!m_retirementFence)
                {
                    if (!allowSubmit) return false;
                    D3D11_QUERY_DESC queryDescription{D3D11_QUERY_EVENT, 0};
                    m_query.Reset();
                    HRESULT result = m_sessionDevice->CreateQuery(&queryDescription, &m_query);
                    if (FAILED(result)) return FAILED(m_sessionDevice->GetDeviceRemovedReason());
                    context->End(m_query.Get());
                    m_retirementFence = true;
                    return false;
                }
                BOOL finished = FALSE;
                HRESULT result = context->GetData(m_query.Get(), &finished, sizeof(finished), 0);
                return (result == S_OK && finished) ||
                    (FAILED(result) && FAILED(m_sessionDevice->GetDeviceRemovedReason()));
            }
            if (m_state.load() == ESessionState::Faulted) return false;
            if (m_gpuInFlight)
            {
                BOOL finished = FALSE;
                HRESULT result = context->GetData(m_query.Get(), &finished, sizeof(finished), 0);
                if (FAILED(result)) Fail(result);
                else if (result == S_OK && finished)
                {
                    m_sample.Reset();
                    m_inputView.Reset();
                    m_inputTexture.Reset();
                    m_gpuInFlight = false;
                    m_displayedFrameIndex = m_pendingFrameIndex;
                    m_displayedTimestamp = m_timestamp;
                    m_textureVersion++;
                    // 旧请求的 GPU 工作可以完成，但不能宣告新定位请求已经就绪。
                    if (m_seeking && m_pendingGeneration == m_seekGeneration && !m_seekPending)
                    {
                        m_completedSeek = m_pendingGeneration;
                        m_seeking = false;
                    }
                    if (!m_firstReady)
                    {
                        ESessionState expected = ESessionState::Submitted;
                        m_firstReady = m_state.compare_exchange_strong(expected, ESessionState::Ready);
                    }
                    m_slotAvailable.notify_all();
                }
            }
            // 帧末在 UI 采样之后写入同一张纹理，不替换托管包装和材质。
            if (allowSubmit && m_state.load() != ESessionState::Faulted &&
                !m_gpuInFlight && m_hasPendingFrame && !m_seekPending &&
                m_pendingGeneration == m_seekGeneration && (!m_firstReady || m_playing.load() || m_seeking) &&
                (m_seeking || m_pendingFrameIndex <= m_targetFrame.load()))
            {
                if (m_firstReady && m_pendingFrameIndex < m_targetFrame.load())
                {
                    m_sample.Reset();
                    m_inputTexture.Reset();
                    m_hasPendingFrame = false;
                    m_droppedFrames.fetch_add(1);
                    m_slotAvailable.notify_all();
                }
                else
                {
                    HRESULT result = SubmitFrame(context.Get());
                    if (FAILED(result)) Fail(result);
                }
            }
            return false;
        }

        void CollectResources(ResourceSnapshot& snapshot)
        {
            // 仅统计已登记会话持有的资源引用，不能代表驱动或系统解码器的完整显存占用。
            std::lock_guard<std::mutex> guard(m_mutex);
            if (m_closing.load()) snapshot.ClosingSessions++;
            if (m_inputTexture) snapshot.DecoderSurfaces++;
            if (m_outputTexture)
            {
                snapshot.OutputTextures++;
                snapshot.EstimatedOutputBytes += static_cast<int64_t>(m_width) * m_height * 4;
            }
        }

        PlaybackSnapshot Playback()
        {
            std::lock_guard<std::mutex> guard(m_mutex);
            PlaybackSnapshot snapshot{};
            snapshot.FrameIndex = m_displayedFrameIndex;
            snapshot.Timestamp = m_displayedTimestamp;
            snapshot.TextureVersion = m_textureVersion;
            snapshot.DroppedFrames = m_droppedFrames.load();
            snapshot.EndOfStream = m_endOfStream.load() ? 1 : 0;
            snapshot.InFlight = m_gpuInFlight ? 1 : 0;
            snapshot.CompletedSeek = m_completedSeek;
            return snapshot;
        }

        SessionSnapshot Snapshot()
        {
            std::lock_guard<std::mutex> guard(m_mutex);
            SessionSnapshot snapshot{};
            snapshot.State = static_cast<int32_t>(m_state.load());
            snapshot.Result = m_result.load();
            snapshot.Stage = static_cast<int32_t>(m_stage.load());
            snapshot.Width = m_width;
            snapshot.Height = m_height;
            if (m_state.load() == ESessionState::Faulted)
            {
                snapshot.Width = static_cast<int32_t>(m_detectedWidth.load());
                snapshot.Height = static_cast<int32_t>(m_detectedHeight.load());
            }
            snapshot.Texture = snapshot.State == static_cast<int32_t>(ESessionState::Ready)
                ? m_outputTexture.Get() : nullptr;
            snapshot.Timestamp = m_firstSourceTimestamp.load();
            return snapshot;
        }
    };

    std::unordered_map<uint64_t, std::shared_ptr<Session>> m_sessions; // 会话与异步退役对象。

    struct Worker
    {
        std::thread Thread; // 保留线程句柄，保证 DLL 卸载前线程真正退出。

        explicit Worker(const std::shared_ptr<Session>& session) : Thread(Session::Run, session) { }
    };
    std::list<Worker> m_workers; // 渲染线程仅回收已退出的线程，正常关闭不等待。

    void UNITY_INTERFACE_API OnDeviceEvent(UnityGfxDeviceEventType eventType)
    {
        if (eventType == kUnityGfxDeviceEventShutdown || eventType == kUnityGfxDeviceEventBeforeReset)
        {
            m_deviceInvalid.store(true);
            std::lock_guard<std::mutex> guard(m_globalMutex);
            for (auto& entry : m_sessions) entry.second->Close();
            m_device.Reset();
        }
        else if (eventType == kUnityGfxDeviceEventInitialize || eventType == kUnityGfxDeviceEventAfterReset)
        {
            m_deviceInvalid.store(false);
        }
    }

    void UNITY_INTERFACE_API OnRenderEvent(int eventID, void*)
    {
        std::vector<std::pair<uint64_t, std::shared_ptr<Session>>> sessions;
        {
            std::lock_guard<std::mutex> guard(m_globalMutex);
            if (!m_device && !m_deviceInvalid.load() && m_unityGraphics &&
                m_unityGraphics->GetRenderer() == kUnityGfxRendererD3D11)
            {
                auto graphics = m_unityInterfaces->Get<IUnityGraphicsD3D11>();
                ID3D11Device* device = graphics ? graphics->GetDevice() : nullptr;
                if (device)
                {
                    ComPtr<ID3D10Multithread> multithread;
                    if (SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&multithread))))
                    {
                        multithread->SetMultithreadProtected(TRUE);
                        m_device = device;
                    }
                }
            }
            for (auto& entry : m_sessions) sessions.push_back(entry);
        }
        for (auto& entry : sessions)
        {
            if (entry.second->Tick(eventID == 1))
            {
                std::lock_guard<std::mutex> guard(m_globalMutex);
                m_sessions.erase(entry.first);
            }
        }
        std::lock_guard<std::mutex> guard(m_globalMutex);
        for (auto worker = m_workers.begin(); worker != m_workers.end();)
        {
            if (WaitForSingleObject(worker->Thread.native_handle(), 0) == WAIT_OBJECT_0)
            {
                worker->Thread.join();
                worker = m_workers.erase(worker);
            }
            else ++worker;
        }
    }
}

extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces)
{
    m_unityInterfaces = interfaces;
    m_unityGraphics = interfaces->Get<IUnityGraphics>();
    m_unityGraphics->RegisterDeviceEventCallback(OnDeviceEvent);
}

extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginUnload()
{
    OnDeviceEvent(kUnityGfxDeviceEventShutdown);
    // 卸载 DLL 前必须停止执行其中的工作线程；普通 Close 从不在主线程等待。
    std::list<Worker> workers;
    {
        std::lock_guard<std::mutex> guard(m_globalMutex);
        workers.splice(workers.end(), m_workers);
    }
    for (auto& worker : workers) worker.Thread.join();
    {
        std::lock_guard<std::mutex> guard(m_globalMutex);
        m_sessions.clear();
    }
    if (SUCCEEDED(m_foundationResult)) MFShutdown();
    if (m_unityGraphics) m_unityGraphics->UnregisterDeviceEventCallback(OnDeviceEvent);
    m_unityGraphics = nullptr;
    m_unityInterfaces = nullptr;
}

extern "C" __declspec(dllexport) void* __cdecl Pulse_GetRenderEvent()
{
    return reinterpret_cast<void*>(OnRenderEvent);
}

extern "C" __declspec(dllexport) int __cdecl Pulse_IsDeviceReady()
{
    std::lock_guard<std::mutex> guard(m_globalMutex);
    return m_device && !m_deviceInvalid.load() ? 1 : 0;
}

extern "C" __declspec(dllexport) HRESULT __cdecl Pulse_CreateSession(
    const wchar_t* filePath, int width, int height, int matrix, int64_t frames,
    UINT numerator, UINT denominator, uint64_t* handle)
{
    if (!filePath || !handle || width <= 0 || height <= 0 || frames <= 0 ||
        numerator == 0 || denominator == 0) return E_INVALIDARG;
    *handle = 0;
    ComPtr<ID3D11Device> device;
    {
        std::lock_guard<std::mutex> guard(m_globalMutex);
        if (!m_device || m_deviceInvalid.load()) return E_UNEXPECTED;
        device = m_device;
    }
    // 低于 11 的功能级别不能用事件查询保证视频处理器租约已完成。
    if (device->GetFeatureLevel() < D3D_FEATURE_LEVEL_11_0) return DXGI_ERROR_UNSUPPORTED;
    uint64_t sessionHandle = m_nextHandle.fetch_add(1);
    try
    {
        auto session = std::make_shared<Session>(
            filePath, width, height, matrix, frames, numerator, denominator, device.Get());
        {
            std::lock_guard<std::mutex> guard(m_globalMutex);
            if (m_deviceInvalid.load() || m_device.Get() != device.Get()) return E_UNEXPECTED;
            m_sessions.emplace(sessionHandle, session);
            try { m_workers.emplace_back(session); }
            catch (...)
            {
                m_sessions.erase(sessionHandle);
                throw;
            }
        }
        *handle = sessionHandle;
        return S_OK;
    }
    catch (...) { return E_FAIL; }
}

extern "C" __declspec(dllexport) int __cdecl Pulse_GetSnapshot(uint64_t handle, SessionSnapshot* snapshot)
{
    if (!snapshot) return 0;
    std::shared_ptr<Session> session;
    {
        std::lock_guard<std::mutex> guard(m_globalMutex);
        auto found = m_sessions.find(handle);
        if (found == m_sessions.end()) return 0;
        session = found->second;
    }
    *snapshot = session->Snapshot();
    return 1;
}

extern "C" __declspec(dllexport) void __cdecl Pulse_CloseSession(uint64_t handle)
{
    std::lock_guard<std::mutex> guard(m_globalMutex);
    auto found = m_sessions.find(handle);
    if (found != m_sessions.end()) found->second->Close();
}

extern "C" __declspec(dllexport) void __cdecl Pulse_CloseAll()
{
    std::lock_guard<std::mutex> guard(m_globalMutex);
    for (auto& entry : m_sessions) entry.second->Close();
}

extern "C" __declspec(dllexport) int __cdecl Pulse_GetResources(ResourceSnapshot* snapshot)
{
    if (!snapshot) return 0;
    *snapshot = {};
    std::lock_guard<std::mutex> guard(m_globalMutex);
    snapshot->Sessions = static_cast<int32_t>(m_sessions.size());
    snapshot->WorkerHandles = static_cast<int32_t>(m_workers.size());
    for (auto& entry : m_sessions) entry.second->CollectResources(*snapshot);
    return 1;
}

extern "C" __declspec(dllexport) int __cdecl Pulse_GetApiVersion()
{
    return 3;
}

extern "C" __declspec(dllexport) int __cdecl Pulse_SetTargetFrame(uint64_t handle, int64_t frameIndex, int playing)
{
    std::shared_ptr<Session> session;
    {
        std::lock_guard<std::mutex> guard(m_globalMutex);
        auto found = m_sessions.find(handle);
        if (found == m_sessions.end()) return 0;
        session = found->second;
    }
    return session->SetTargetFrame(frameIndex, playing != 0) ? 1 : 0;
}

extern "C" __declspec(dllexport) uint64_t __cdecl Pulse_Seek(
    uint64_t handle, int64_t frameIndex, int64_t relativeTimestamp)
{
    std::shared_ptr<Session> session;
    {
        std::lock_guard<std::mutex> guard(m_globalMutex);
        auto found = m_sessions.find(handle);
        if (found == m_sessions.end()) return 0;
        session = found->second;
    }
    return session->Seek(frameIndex, relativeTimestamp);
}

extern "C" __declspec(dllexport) int __cdecl Pulse_GetPlayback(uint64_t handle, PlaybackSnapshot* snapshot)
{
    if (!snapshot) return 0;
    std::shared_ptr<Session> session;
    {
        std::lock_guard<std::mutex> guard(m_globalMutex);
        auto found = m_sessions.find(handle);
        if (found == m_sessions.end()) return 0;
        session = found->second;
    }
    *snapshot = session->Playback();
    return 1;
}
