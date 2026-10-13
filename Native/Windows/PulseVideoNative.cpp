#include <Windows.h>
#include <d3d11.h>
#include <d3d10.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <wrl/client.h>
#include <atomic>
#include <cstdint>
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
        InspectingSourceType = 9, ReadingSample = 10, InspectingDecodedType = 11, ValidatingDecodedSize = 12
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
        ComPtr<ID3D11Device> m_sessionDevice; // 会话固定持有创建时的设备。
        std::mutex m_mutex; // 单帧租约、纹理与查询的保护锁。
        std::atomic<ESessionState> m_state{ESessionState::Opening}; // 可供主线程读取的状态。
        std::atomic<ESessionStage> m_stage{ESessionStage::Starting}; // 当前原生步骤。
        std::atomic<HRESULT> m_result{S_OK}; // 原生错误码。
        std::atomic<UINT32> m_detectedWidth{0}; // 出错时报告最近一次读取的媒体类型宽度。
        std::atomic<UINT32> m_detectedHeight{0}; // 出错时报告最近一次读取的媒体类型高度。
        std::atomic<bool> m_closing{false}; // 异步关闭请求。
        std::atomic<bool> m_workerDone{false}; // 解码线程已停止访问会话。
        ComPtr<IMFSample> m_sample; // 保留解码表面直到 GPU 使用结束。
        ComPtr<ID3D11Texture2D> m_inputTexture; // 解码器提供的 NV12 表面。
        UINT m_subresource = 0; // 解码表面所在的纹理数组子资源。
        int64_t m_timestamp = 0; // 首帧原始 PTS，单位为一千万分之一秒，不要求从零开始。
        ComPtr<ID3D11Texture2D> m_outputTexture; // 交给 Unity 包装的 BGRA 输出纹理。
        ComPtr<ID3D11VideoProcessorEnumerator> m_enumerator; // 视频转换能力与尺寸描述。
        ComPtr<ID3D11VideoProcessor> m_processor; // NV12 到 BGRA 的 GPU 视频处理器。
        ComPtr<ID3D11VideoProcessorInputView> m_inputView; // 输入纹理视图。
        ComPtr<ID3D11VideoProcessorOutputView> m_outputView; // 输出纹理视图。
        ComPtr<ID3D11Query> m_query; // 当前 GPU 工作的完成查询。
        bool m_retirementFence = false; // 已在关闭后插入最后一次 GPU 围栏。

        void Fail(HRESULT result)
        {
            m_result.store(result);
            if (!m_closing.load()) m_state.store(ESessionState::Faulted);
        }

        HRESULT DecodeFirstFrame()
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

            m_stage.store(ESessionStage::Decoding);
            for (int attempt = 0; attempt < 256 && !m_closing.load(); ++attempt)
            {
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
                    if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0) return HRESULT_FROM_WIN32(ERROR_HANDLE_EOF);
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
                m_sample = sample;
                m_inputTexture = texture;
                m_subresource = subresource;
                m_timestamp = timestamp;
                m_state.store(ESessionState::Decoded);
                return S_OK;
            }
            return m_closing.load() ? S_FALSE : E_FAIL;
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
            D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
            content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
            content.InputWidth = m_width;
            content.InputHeight = m_height;
            content.OutputWidth = m_width;
            content.OutputHeight = m_height;
            content.InputFrameRate = {30, 1};
            content.OutputFrameRate = {30, 1};
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
            D3D11_TEXTURE2D_DESC input{};
            m_inputTexture->GetDesc(&input);
            D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputView{};
            inputView.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
            inputView.Texture2D.MipSlice = m_subresource % input.MipLevels;
            inputView.Texture2D.ArraySlice = m_subresource / input.MipLevels;
            result = videoDevice->CreateVideoProcessorInputView(
                m_inputTexture.Get(), m_enumerator.Get(), &inputView, &m_inputView);
            if (FAILED(result)) return result;
            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputView{};
            outputView.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
            result = videoDevice->CreateVideoProcessorOutputView(
                m_outputTexture.Get(), m_enumerator.Get(), &outputView, &m_outputView);
            if (FAILED(result)) return result;

            RECT rect{0, 0, m_width, m_height};
            videoContext->VideoProcessorSetStreamFrameFormat(m_processor.Get(), 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
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
            stream.pInputSurface = m_inputView.Get();
            m_stage.store(ESessionStage::Converting);
            result = videoContext->VideoProcessorBlt(m_processor.Get(), m_outputView.Get(), 0, 1, &stream);
            if (FAILED(result)) return result;
            D3D11_QUERY_DESC queryDescription{D3D11_QUERY_EVENT, 0};
            result = m_sessionDevice->CreateQuery(&queryDescription, &m_query);
            if (FAILED(result)) return result;
            context->End(m_query.Get());
            m_stage.store(ESessionStage::Fence);
            m_state.store(ESessionState::Submitted);
            return S_OK;
        }

    public:
        Session(std::wstring filePath, int width, int height, int matrix, ID3D11Device* device)
            : m_filePath(std::move(filePath)), m_width(width), m_height(height), m_matrix(matrix), m_sessionDevice(device)
        {
        }

        static void Run(std::shared_ptr<Session> session)
        {
            HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            if (SUCCEEDED(initialized))
            {
                std::call_once(m_foundationOnce, InitializeFoundation);
                HRESULT result = m_foundationResult;
                if (SUCCEEDED(result)) result = session->DecodeFirstFrame();
                if (FAILED(result)) session->Fail(result);
                CoUninitialize();
            }
            else session->Fail(initialized);
            session->m_workerDone.store(true);
        }

        void Close()
        {
            m_closing.store(true);
        }

        bool Tick()
        {
            std::lock_guard<std::mutex> guard(m_mutex);
            ComPtr<ID3D11DeviceContext> context;
            m_sessionDevice->GetImmediateContext(&context);
            if (m_deviceInvalid.load())
            {
                m_closing.store(true);
                return m_workerDone.load();
            }
            if (m_closing.load())
            {
                m_state.store(ESessionState::Closing);
                if (!m_workerDone.load()) return false;
                // 帧末插入关闭围栏，等待此前的转换和 UI 采样全部结束再销毁纹理。
                if (!m_retirementFence)
                {
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
            if (m_state.load() == ESessionState::Decoded)
            {
                HRESULT result = SubmitFrame(context.Get());
                if (FAILED(result)) Fail(result);
            }
            else if (m_state.load() == ESessionState::Submitted)
            {
                BOOL finished = FALSE;
                HRESULT result = context->GetData(m_query.Get(), &finished, sizeof(finished), 0);
                if (FAILED(result)) Fail(result);
                else if (result == S_OK && finished)
                {
                    m_sample.Reset();
                    m_inputView.Reset();
                    m_inputTexture.Reset();
                    m_query.Reset();
                    m_state.store(ESessionState::Ready);
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
            snapshot.Timestamp = m_timestamp;
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

    void UNITY_INTERFACE_API OnRenderEvent(int, void*)
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
            if (entry.second->Tick())
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
    const wchar_t* filePath, int width, int height, int matrix, uint64_t* handle)
{
    if (!filePath || !handle || width <= 0 || height <= 0) return E_INVALIDARG;
    *handle = 0;
    ComPtr<ID3D11Device> device;
    {
        std::lock_guard<std::mutex> guard(m_globalMutex);
        if (!m_device || m_deviceInvalid.load()) return E_UNEXPECTED;
        device = m_device;
    }
    uint64_t sessionHandle = m_nextHandle.fetch_add(1);
    try
    {
        auto session = std::make_shared<Session>(filePath, width, height, matrix, device.Get());
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
