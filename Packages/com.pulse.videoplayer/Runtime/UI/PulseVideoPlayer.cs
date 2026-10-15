using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace PulseVideoPlayer.UI
{
    /// <summary>可挂载到项目自有预制体的视频组件，显示控件通过 Inspector 引用绑定。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("PulseVideoPlayer")]
    public sealed class PulseVideoPlayer : MonoBehaviour, IPulseClock, IPulsePlayerEvents
    {
        private static readonly int m_rgbRectID = Shader.PropertyToID("_RGBRect"); // RGB 采样区域。
        private static readonly int m_alphaRectID = Shader.PropertyToID("_AlphaRect"); // Alpha 采样区域。
        private static readonly int m_hasAlphaID = Shader.PropertyToID("_HasAlpha"); // 是否存在透明通道。
        private static readonly int m_unpremultiplyID = Shader.PropertyToID("_Unpremultiply"); // 是否去除源预乘。

        [Header("显示与进度引用")]
        [Tooltip("必填：视频显示区域，层级和名称不限。")]
        [SerializeField] private RawImage m_videoImage; // 视频画面引用。
        [Tooltip("可选：设置为 Filled / Radial 360。直接使用 fillAmount 显示零至一的加载进度，不旋转图像。")]
        [SerializeField] private Image m_loadingProgressImage; // 加载进度引用。
        [Tooltip("可选：加载百分比文字，使用 Unity UI Text。")]
        [SerializeField] private Text m_loadingPercentageText; // 加载百分比引用。
        [Tooltip("可选：显示播放进度，支持拖动预览并在松手后跳转。")]
        [SerializeField] private Slider m_playbackProgressSlider; // 播放进度引用。
        [SerializeField] private Text m_playbackTimeText; // 当前时间与总时长文字引用。
        [SerializeField] private Button m_playButton; // 播放、恢复和重播按钮引用。
        [SerializeField] private Button m_pauseButton; // 暂停按钮引用。
        [Tooltip("可选：现有比例适配器；不填写时尝试使用 RawImage 上的适配器。")]
        [SerializeField] private AspectRatioFitter m_aspectRatioFitter; // 项目已有的显示比例适配器。

        [Header("打开与显示配置")]
        [SerializeField] private string m_sourcePath = string.Empty; // Inspector 配置的视频路径。
        [SerializeField] private bool m_relativeToStreamingAssets = true; // 相对路径是否从 StreamingAssets 开始。
        [SerializeField] private bool m_openOnEnable; // 启用时是否打开配置的视频。
        [SerializeField] private bool m_playWhenReady = true; // 首帧准备完成后是否自动播放。
        [SerializeField] private bool m_preserveAspectRatio = true; // 是否更新已有适配器的比例。
        [SerializeField] private string m_audioEventKey = string.Empty; // 宿主音频事件，空值为无音轨。
        [SerializeField] private EPulseLoopMode m_loopMode = EPulseLoopMode.Once; // 总播放次数策略。
        [Min(1)] [SerializeField] private int m_playCount = 1; // 指定次数模式的总播放次数。

        private PulsePlayer m_player; // 本组件拥有的底层播放器。
        private CancellationTokenSource m_openCancellation; // 当前打开任务的取消源，由任务收尾释放。
        private int m_generation; // 打开、关闭请求代次，避免旧任务重新绑定画面。
        private Material m_runtimeMaterial; // 当前视频独占的运行时材质。
        private RawImage m_boundImage; // 本次绑定的画面引用，避免运行中更换 Inspector 引用导致旧图遗留。
        private Texture m_originalTexture; // 绑定前的项目纹理。
        private Material m_originalMaterial; // 绑定前的项目材质。
        private bool m_originalImageEnabled; // 绑定前的显示开关。
        private AspectRatioFitter m_boundFitter; // 本次修改的比例适配器。
        private float m_originalAspectRatio; // 绑定前的项目显示比例。
        private float m_loadingProgress; // 当前加载进度，范围零至一。
        private bool m_isLoading; // 是否正在显示加载提示。
        private int m_displayedPercentage = -1; // 缓存整数百分比，避免重复生成文字。
        private string m_lastOpenedPath; // 最近一次有效打开路径，用于重新播放。
        private Exception m_lastError; // 最近一次打开或播放失败。
        private Button m_boundPlayButton; // 已注册回调的播放按钮。
        private Button m_boundPauseButton; // 已注册回调的暂停按钮。
        private long m_displayedSeconds = -1; // 已显示的当前秒数，避免逐帧生成文字。
        private long m_displayedDuration = -1; // 已显示的总时长秒数。
        private Slider m_boundSlider; // 已注册进度回调的滑条。
        private PulseSeekSlider m_sliderInput; // 接收滑条自身的指针事件。
        private bool m_isDragging; // 用户拖动期间停止自动覆盖滑条。
        private float m_previewProgress; // 拖动中的预览位置，范围零至一。
        private IPulsePlaybackDriver m_playbackDriver; // 宿主注入的资源和音画协调实现。
        private bool m_hasPendingSliderSeek; // 非拖动的值变化延迟到指针事件处理完后再提交。

        /// <summary>Inspector 绑定的视频画面。</summary>
        public RawImage VideoImage => m_videoImage;
        /// <summary>Inspector 绑定的可选加载进度图像。</summary>
        public Image LoadingProgressImage => m_loadingProgressImage;
        /// <summary>Inspector 绑定的可选加载百分比文字。</summary>
        public Text LoadingPercentageText => m_loadingPercentageText;
        /// <summary>Inspector 绑定的可选播放进度条。</summary>
        public Slider PlaybackProgressSlider => m_playbackProgressSlider;
        /// <summary>Inspector 绑定的当前时间与总时长文字。</summary>
        public Text PlaybackTimeText => m_playbackTimeText;
        /// <summary>Inspector 绑定的播放按钮。</summary>
        public Button PlayButton => m_playButton;
        /// <summary>Inspector 绑定的暂停按钮。</summary>
        public Button PauseButton => m_pauseButton;
        /// <summary>当前播放状态，失败后可读取 LastError。</summary>
        public EPulsePlayerState State => m_player?.State ??
            (m_lastError == null ? EPulsePlayerState.Closed : EPulsePlayerState.Faulted);
        /// <summary>当前加载进度，范围零至一。</summary>
        public float LoadingProgress => m_loadingProgress;
        /// <summary>是否正在显示加载提示。</summary>
        public bool IsLoading => m_isLoading;
        /// <summary>当前视频时长，单位为秒。</summary>
        public double DurationSeconds => m_player?.DurationSeconds ?? 0;
        /// <summary>当前视频元数据，未准备好时为空。</summary>
        public PulseMetadata Metadata => m_player?.Metadata;
        /// <summary>最近一次错误，下一次有效打开时清空。</summary>
        public Exception LastError => m_lastError;
        /// <summary>是否正在等待最新跳转请求的目标画面。</summary>
        public bool IsSeeking => m_player?.IsSeeking ?? false;
        /// <summary>宿主是否正在准备资源或协调跳转。</summary>
        public bool IsPlaybackBusy => m_playbackDriver?.IsBusy ?? false;
        /// <summary>Inspector 配置的资源地址或本地路径。</summary>
        public string ConfiguredVideoLocation => ResolveConfiguredPath();
        /// <summary>Inspector 配置的音频事件。</summary>
        public string AudioEventKey => m_audioEventKey;
        /// <summary>Inspector 配置的循环模式。</summary>
        public EPulseLoopMode LoopMode => m_loopMode;
        /// <summary>Inspector 配置的总播放次数。</summary>
        public int PlayCount => m_playCount;

        /// <summary>首帧与显示材质绑定完成，可以开始播放。</summary>
        public event Action Ready;
        /// <summary>播放器开始推进时钟。</summary>
        public event Action Started;
        /// <summary>播放时钟已暂停。</summary>
        public event Action Paused;
        /// <summary>播放时钟已恢复。</summary>
        public event Action Resumed;
        /// <summary>画面已解除绑定并请求关闭。</summary>
        public event Action Stopped;
        /// <summary>视频播放到末尾，保留最后一帧。</summary>
        public event Action Completed;
        /// <summary>打开或运行中的播放失败。</summary>
        public event Action<Exception> Failed;
        /// <summary>加载进度发生变化，参数范围零至一。</summary>
        public event Action<float> LoadingProgressChanged;

        private void OnEnable()
        {
            if (!Application.isPlaying) return;
            BindButtons();
            BindSlider();
            ConfigureSlider();
            SetLoadingVisible(false);
            RefreshButtons();
            RefreshTimeText();
            if (m_openOnEnable) Play();
        }

        private void Update()
        {
            m_playbackDriver?.Tick();
            RefreshButtons();
            RefreshTimeText();
            // 拖动期间由用户控制滑条，避免播放时钟每帧把滑块拉回。
            if (m_playbackProgressSlider == null || m_isDragging) return;
            double duration = DurationSeconds;
            float progress = duration > 0 ? (float)(GetPlayTimeMs() / (duration * 1000)) : 0;
            m_playbackProgressSlider.SetValueWithoutNotify(Mathf.Clamp01(progress));
        }

        private void LateUpdate()
        {
            if (!m_hasPendingSliderSeek) return;
            m_hasPendingSliderSeek = false;
            // Slider 的值变化可能先于指针按下转发；等待本帧事件分发结束，避免误把按下当成 Seek。
            if (!m_isDragging && CanSeek()) SeekFromSlider(m_previewProgress);
        }

        /// <summary>注入宿主资源和音频实现；传空值恢复组件本地播放模式。</summary>
        /// <param name="driver">宿主实现，不要求继承 MonoBehaviour。</param>
        /// <exception cref="InvalidOperationException">组件仍持有打开的播放器。</exception>
        public void SetPlaybackDriver(IPulsePlaybackDriver driver)
        {
            if (m_player != null || m_openCancellation != null || IsPlaybackBusy)
                throw new InvalidOperationException("请先等待 CloseAsync 完成再替换播放适配器。");
            m_playbackDriver = driver;
        }

        /// <summary>加载资源和可选音轨并开始播放，空音频事件表示无音轨。</summary>
        /// <param name="videoLocation">资源地址、本地路径或 CDN URL。</param>
        /// <param name="audioEventKey">宿主音频事件 Key。</param>
        /// <param name="loopMode">循环模式。</param>
        /// <param name="playCount">Count 模式下的总播放次数。</param>
        /// <param name="cancellationToken">取消本次播放生命周期。</param>
        /// <returns>准备完成并开始播放的任务，不等待播放结束。</returns>
        /// <exception cref="NotSupportedException">未安装处理资源、音频和循环的宿主适配器。</exception>
        public UniTask PlayAsync(string videoLocation, string audioEventKey = null,
            EPulseLoopMode loopMode = EPulseLoopMode.Once, int playCount = 1,
            CancellationToken cancellationToken = default)
        {
            if (m_playbackDriver != null)
                return m_playbackDriver.PlayAsync(videoLocation, audioEventKey, loopMode, playCount, cancellationToken);
            if (!string.IsNullOrWhiteSpace(audioEventKey) || loopMode != EPulseLoopMode.Once)
                throw new NotSupportedException("音轨与循环需要先注入宿主播放适配器。");
            return OpenCoreAsync(videoLocation, true, cancellationToken);
        }

        /// <summary>通过回调式入口加载并播放，错误通过 Failed 和 Unity 日志报告。</summary>
        /// <param name="videoLocation">资源地址、本地路径或 CDN URL。</param>
        /// <param name="audioEventKey">宿主音频事件 Key。</param>
        /// <param name="loopMode">循环模式。</param>
        /// <param name="playCount">总播放次数。</param>
        public void Play(string videoLocation, string audioEventKey = null,
            EPulseLoopMode loopMode = EPulseLoopMode.Once, int playCount = 1)
        {
            try { PlayAsync(videoLocation, audioEventKey, loopMode, playCount).Forget(LogUnhandled); }
            catch (Exception exception) { ReportPlaybackFailure(exception); }
        }

        /// <summary>宿主准备本地首帧，不自动开始播放。</summary>
        /// <param name="filePath">已经交付的真实文件路径。</param>
        /// <param name="cancellationToken">本次准备令牌。</param>
        /// <returns>首帧绑定完成的任务。</returns>
        public UniTask PrepareVideoAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return OpenCoreAsync(filePath, false, cancellationToken);
        }

        /// <summary>宿主启动已经准备好的视频时间轴。</summary>
        /// <exception cref="InvalidOperationException">视频尚未准备好。</exception>
        public void StartPreparedVideo() { RequirePlayer().Play(); }

        /// <summary>宿主暂停视频时间轴，音轨由宿主同步暂停。</summary>
        /// <exception cref="InvalidOperationException">视频未就绪或当前状态不能暂停。</exception>
        public void PauseVideo() { RequirePlayer().Pause(); }

        /// <summary>宿主恢复视频时间轴，音轨由宿主同步恢复。</summary>
        /// <exception cref="InvalidOperationException">视频未就绪或当前状态不能恢复。</exception>
        public void ResumeVideo() { RequirePlayer().Resume(); }

        /// <summary>宿主定位视频后端，不再次进入宿主协调逻辑。</summary>
        /// <param name="timeSeconds">目标秒数。</param>
        /// <param name="cancellationToken">本次等待令牌。</param>
        /// <returns>目标画面就绪任务。</returns>
        public UniTask SeekVideoAsync(double timeSeconds, CancellationToken cancellationToken = default)
        {
            return RequirePlayer().SeekAsync(timeSeconds, cancellationToken);
        }

        /// <summary>显示无确定百分比的加载提示，供宿主预加载音频等步骤使用。</summary>
        public void ShowPreparing() { SetLoadingProgress(0); }

        /// <summary>报告宿主失败，恢复界面并通知业务。</summary>
        /// <param name="exception">失败原因。</param>
        public void ReportPlaybackFailure(Exception exception)
        {
            m_lastError = exception;
            SetLoadingVisible(false);
            NotifyFailure(exception, true);
        }

        private void CloseVideo() { DetachPlayer()?.Dispose(); }

        /// <summary>兼容旧接口，业务入口优先使用 Play 或 PlayAsync。</summary>
        [Obsolete("业务播放使用 Play 或 PlayAsync；宿主准备首帧使用 PrepareVideoAsync。")]
        public void Open()
        {
            try
            {
                UniTask operation = m_playbackDriver != null
                    ? PlayAsync(ConfiguredVideoLocation, m_audioEventKey, m_loopMode, m_playCount)
                    : OpenCoreAsync(ResolveConfiguredPath(), m_playWhenReady, default);
                operation.Forget(LogUnhandled);
            }
            catch (Exception exception)
            {
                m_lastError = exception;
                NotifyFailure(exception, false);
                LogUnhandled(exception);
            }
        }

        /// <summary>打开本地视频并绑定画面，准备完成后按配置决定是否自动播放。</summary>
        /// <param name="filePath">视频的真实本地文件路径，不是 YooAsset 地址或下载 URL。</param>
        /// <param name="cancellationToken">用于取消本次打开的令牌。</param>
        /// <returns>首帧显示已准备完成的任务。</returns>
        /// <exception cref="ArgumentException">路径为空。</exception>
        /// <exception cref="InvalidOperationException">组件未启用、缺少画面引用或 Shader 不可用。</exception>
        /// <exception cref="OperationCanceledException">打开被取消或被关闭、新打开取代。</exception>
        [Obsolete("业务播放使用 PlayAsync；宿主准备首帧使用 PrepareVideoAsync。")]
        public UniTask OpenAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (m_playbackDriver != null)
                return PlayAsync(filePath, m_audioEventKey, m_loopMode, m_playCount, cancellationToken);
            return OpenCoreAsync(filePath, m_playWhenReady, cancellationToken);
        }

        /// <summary>按配置加载并播放视频；已暂停时恢复，播放结束后重新播放。</summary>
        /// <exception cref="InvalidOperationException">已准备的播放器状态不允许开始播放。</exception>
        public void Play()
        {
            if (IsPlaybackBusy) return;
            switch (State)
            {
                case EPulsePlayerState.Ready:
                    RequirePlayer().Play();
                    break;
                case EPulsePlayerState.Paused:
                    Resume();
                    break;
                case EPulsePlayerState.Closed:
                case EPulsePlayerState.Faulted:
                case EPulsePlayerState.Ended:
                    if (m_playbackDriver != null) m_playbackDriver.Replay();
                    else Replay();
                    break;
            }
        }

        /// <summary>暂停并保留当前视频画面。</summary>
        /// <exception cref="InvalidOperationException">组件未准备好或当前不是播放状态。</exception>
        public void Pause()
        {
            if (m_playbackDriver != null) { m_playbackDriver.Pause(); return; }
            RequirePlayer().Pause();
        }

        /// <summary>从暂停位置继续播放。</summary>
        /// <exception cref="InvalidOperationException">组件未准备好或当前不是暂停状态。</exception>
        public void Resume()
        {
            if (m_playbackDriver != null) { m_playbackDriver.Resume(); return; }
            RequirePlayer().Resume();
        }

        /// <summary>重新打开最近的视频并从零开始播放，可直接绑定到 Unity Button。</summary>
        public void Replay()
        {
            if (m_playbackDriver != null) { m_playbackDriver.Replay(); return; }
            try
            {
                string filePath = string.IsNullOrWhiteSpace(m_lastOpenedPath)
                    ? ResolveConfiguredPath() : m_lastOpenedPath;
                OpenCoreAsync(filePath, true, default).Forget(LogUnhandled);
            }
            catch (Exception exception)
            {
                m_lastError = exception;
                NotifyFailure(exception, false);
                LogUnhandled(exception);
            }
        }

        /// <summary>请求跳转到指定时间，播放中继续播放，暂停时保持暂停。</summary>
        /// <param name="timeSeconds">目标时间，单位为秒。</param>
        /// <exception cref="InvalidOperationException">视频尚未准备好或原生定位被拒绝。</exception>
        /// <exception cref="ArgumentOutOfRangeException">时间不是有限数值。</exception>
        public void Seek(double timeSeconds)
        {
            if (m_playbackDriver != null)
            {
                m_playbackDriver.SeekAsync(timeSeconds, default).Forget(LogUnhandled);
                return;
            }
            RequirePlayer().Seek(timeSeconds);
            RefreshTimeText();
        }

        /// <summary>跳转并等待目标画面就绪；关闭或新的跳转请求会取消等待。</summary>
        /// <param name="timeSeconds">目标时间，单位为秒。</param>
        /// <param name="cancellationToken">只取消等待，不撤销已提交的定位请求。</param>
        /// <returns>目标画面就绪的任务。</returns>
        /// <exception cref="InvalidOperationException">视频尚未准备好或原生定位被拒绝。</exception>
        /// <exception cref="ArgumentOutOfRangeException">时间不是有限数值。</exception>
        /// <exception cref="OperationCanceledException">等待被取消或被更新请求替代。</exception>
        public UniTask SeekAsync(double timeSeconds, CancellationToken cancellationToken = default)
        {
            if (m_playbackDriver != null) return m_playbackDriver.SeekAsync(timeSeconds, cancellationToken);
            return RequirePlayer().SeekAsync(timeSeconds, cancellationToken);
        }

        /// <summary>读取视频时钟，暂停时保持不变。</summary>
        /// <returns>从视频起点开始的播放时间，单位为毫秒；未打开时为零。</returns>
        public double GetPlayTimeMs()
        {
            return m_player?.GetPlayTimeMs() ?? 0;
        }

        /// <summary>更新加载提示，供外部资源获取或下载流程提供真实进度。</summary>
        /// <param name="progress">加载进度，有限数值会限制在零至一。</param>
        /// <exception cref="ArgumentOutOfRangeException">进度为 NaN 或无穷大。</exception>
        public void SetLoadingProgress(float progress)
        {
            if (float.IsNaN(progress) || float.IsInfinity(progress))
            {
                throw new ArgumentOutOfRangeException(nameof(progress), "进度必须是有限数值。");
            }
            float normalized = Mathf.Clamp01(progress);
            ApplyLoadingProgress(normalized);
            SetLoadingVisible(normalized < 1);
            NotifyProgress(normalized);
        }

        /// <summary>解除画面绑定并请求关闭，不等待原生资源退役。</summary>
        public void Close()
        {
            if (m_playbackDriver != null) m_playbackDriver.CloseAsync().Forget(LogUnhandled);
            else CloseVideo();
        }

        /// <summary>解除画面绑定并等待旧播放器的打开任务、解码与 GPU 资源退役。</summary>
        /// <returns>旧播放器关闭完成的任务，依赖运行中的 Unity PlayerLoop。</returns>
        public async UniTask CloseAsync()
        {
            if (m_playbackDriver != null) { await m_playbackDriver.CloseAsync(); return; }
            await CloseVideoAsync();
        }

        /// <summary>仅关闭视频后端，供宿主在释放资源租约前等待原生读取结束。</summary>
        /// <returns>视频和 GPU 资源退役完成的任务。</returns>
        public async UniTask CloseVideoAsync()
        {
            PulsePlayer player = DetachPlayer();
            if (player == null) return;
            player.Dispose();
            await player.CloseAsync();
        }

        /// <summary>停止播放并关闭，可直接绑定到 Unity Button。</summary>
        public void Stop()
        {
            Close();
        }

        private async UniTask OpenCoreAsync(string filePath, bool playAfterReady, CancellationToken cancellationToken)
        {
            // 验证发生在取消旧会话之前，配置错误不会无故中断仍在播放的视频。
            if (!Application.isPlaying || !isActiveAndEnabled)
                throw new InvalidOperationException("请在 Play 模式中启用组件后打开视频。");
            if (m_videoImage == null) throw new InvalidOperationException("请绑定视频 RawImage。");
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("视频路径为空。", nameof(filePath));
            cancellationToken.ThrowIfCancellationRequested();
            Shader shader = Resources.Load<Shader>("PulseVideoSurface");
            if (shader == null) throw new InvalidOperationException("未找到 PulseVideoSurface Shader。");

            float preparationProgress = m_playbackDriver != null ? m_loadingProgress : 0;
            int previousGeneration = m_generation;
            CloseVideo();
            // 关闭事件可能触发新的打开请求，旧调用不能覆盖订阅者创建的会话。
            if (m_generation != previousGeneration + 1 || !isActiveAndEnabled)
                throw new OperationCanceledException(cancellationToken);
            int generation = ++m_generation;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken openToken = cancellation.Token;
            var player = new PulsePlayer();
            m_player = player;
            m_openCancellation = cancellation;
            m_lastOpenedPath = filePath;
            m_lastError = null;
            SubscribePlayer(player);
            ConfigureSlider();
            try
            {
                // 宿主准备时保留已有进度；本地打开从零开始，不修改图像角度。
                SetLoadingProgress(preparationProgress);
                EnsureCurrent(generation, openToken);
                await player.OpenAsync(filePath, openToken);
                EnsureCurrent(generation, openToken);
                BindDisplay(player, shader);
                if (m_playbackDriver == null) SetLoadingProgress(1);
                EnsureCurrent(generation, openToken);
                Notify(Ready);
                EnsureCurrent(generation, openToken);
                if (playAfterReady && player.State == EPulsePlayerState.Ready) player.Play();
            }
            catch (OperationCanceledException)
            {
                if (generation == m_generation) CloseVideo();
                throw;
            }
            catch (Exception exception)
            {
                if (generation == m_generation)
                {
                    CloseVideo();
                    if (m_generation == generation + 1)
                    {
                        m_lastError = exception;
                        NotifyFailure(exception, false);
                    }
                }
                throw;
            }
            finally
            {
                if (ReferenceEquals(m_openCancellation, cancellation)) m_openCancellation = null;
                cancellation.Dispose();
            }
        }

        private PulsePlayer DetachPlayer()
        {
            m_generation++;
            m_openCancellation?.Cancel();
            m_openCancellation = null;
            PulsePlayer player = m_player;
            m_player = null;
            m_isDragging = false;

            // 必须先解除 UI 采样，再释放外部纹理和材质。
            m_hasPendingSliderSeek = false;
            UnbindDisplay();
            SetLoadingVisible(false);
            ApplyLoadingProgress(0);
            if (m_playbackProgressSlider != null) m_playbackProgressSlider.SetValueWithoutNotify(0);
            RefreshButtons();
            RefreshTimeText();
            if (player == null) return null;
            UnsubscribePlayer(player);
            player.Close();
            Notify(Stopped);
            return player;
        }

        private void BindDisplay(PulsePlayer player, Shader shader)
        {
            PulseMetadata metadata = player.Metadata;
            m_boundImage = m_videoImage;
            m_originalTexture = m_boundImage.texture;
            m_originalMaterial = m_boundImage.material;
            m_originalImageEnabled = m_boundImage.enabled;
            m_runtimeMaterial = new Material(shader);
            m_runtimeMaterial.name = "Pulse Video Material";
            m_runtimeMaterial.SetVector(m_rgbRectID, GetUVRegion(metadata.RGBRegion, metadata));
            m_runtimeMaterial.SetFloat(m_hasAlphaID, metadata.AlphaRegion.HasValue ? 1 : 0);
            m_runtimeMaterial.SetFloat(m_unpremultiplyID,
                metadata.AlphaMode == EPulseAlphaMode.Premultiplied ? 1 : 0);
            if (metadata.AlphaRegion.HasValue)
                m_runtimeMaterial.SetVector(m_alphaRectID, GetUVRegion(metadata.AlphaRegion.Value, metadata));
            m_boundImage.material = m_runtimeMaterial;
            m_boundImage.texture = player.Texture;
            m_boundImage.enabled = true;

            // 仅更新项目已有的比例适配器，不改变层级或自动添加布局组件。
            if (!m_preserveAspectRatio) return;
            m_boundFitter = m_aspectRatioFitter != null
                ? m_aspectRatioFitter : m_boundImage.GetComponent<AspectRatioFitter>();
            if (m_boundFitter == null) return;
            m_originalAspectRatio = m_boundFitter.aspectRatio;
            m_boundFitter.aspectRatio = (float)metadata.DisplayWidth / metadata.DisplayHeight;
        }

        private void UnbindDisplay()
        {
            if (m_boundImage != null)
            {
                m_boundImage.texture = m_originalTexture;
                m_boundImage.material = m_originalMaterial;
                m_boundImage.enabled = m_originalImageEnabled && m_originalTexture != null;
            }
            if (m_boundFitter != null) m_boundFitter.aspectRatio = m_originalAspectRatio;
            if (m_runtimeMaterial != null) Destroy(m_runtimeMaterial);
            m_boundImage = null;
            m_boundFitter = null;
            m_runtimeMaterial = null;
            m_originalTexture = null;
            m_originalMaterial = null;
        }

        private void BindButtons()
        {
            // 只注册本组件的监听，保留项目在按钮上配置的其他行为。
            UnbindButtons();
            m_boundPlayButton = m_playButton;
            m_boundPauseButton = m_pauseButton;
            if (m_boundPlayButton != null) m_boundPlayButton.onClick.AddListener(OnPlayClicked);
            if (m_boundPauseButton != null) m_boundPauseButton.onClick.AddListener(OnPauseClicked);
        }

        private void UnbindButtons()
        {
            if (m_boundPlayButton != null) m_boundPlayButton.onClick.RemoveListener(OnPlayClicked);
            if (m_boundPauseButton != null) m_boundPauseButton.onClick.RemoveListener(OnPauseClicked);
            m_boundPlayButton = null;
            m_boundPauseButton = null;
        }

        private void RefreshButtons()
        {
            EPulsePlayerState state = State;
            bool canOpen = !string.IsNullOrWhiteSpace(m_lastOpenedPath) ||
                !string.IsNullOrWhiteSpace(m_sourcePath);
            bool canPlay = state == EPulsePlayerState.Ready || state == EPulsePlayerState.Paused ||
                state == EPulsePlayerState.Ended ||
                (canOpen && (state == EPulsePlayerState.Closed || state == EPulsePlayerState.Faulted));
            if (m_boundPlayButton != null)
                m_boundPlayButton.interactable = isActiveAndEnabled && !m_isLoading && !IsSeeking &&
                    !IsPlaybackBusy && !m_isDragging && m_videoImage != null && canPlay;
            if (m_boundPauseButton != null)
                m_boundPauseButton.interactable = isActiveAndEnabled && !IsSeeking &&
                    !IsPlaybackBusy && !m_isDragging && state == EPulsePlayerState.Playing;
            if (m_boundSlider != null)
                m_boundSlider.interactable = CanSeek();
        }

        private void OnPlayClicked()
        {
            try
            {
                switch (State)
                {
                    case EPulsePlayerState.Ready: Play(); break;
                    case EPulsePlayerState.Paused: Resume(); break;
                    case EPulsePlayerState.Closed:
                    case EPulsePlayerState.Faulted:
                    case EPulsePlayerState.Ended: Replay(); break;
                }
            }
            catch (Exception exception)
            {
                NotifyFailure(exception, true);
            }
            RefreshButtons();
        }

        private void OnPauseClicked()
        {
            try
            {
                if (State == EPulsePlayerState.Playing) Pause();
            }
            catch (Exception exception)
            {
                NotifyFailure(exception, true);
            }
            RefreshButtons();
        }

        private void RefreshTimeText()
        {
            if (m_playbackTimeText == null) return;
            // 时间只在整秒变化时格式化；暂停时保持当前时间，关闭后归零。
            long duration = (long)Math.Max(0, Math.Ceiling(DurationSeconds));
            double playTime = m_isDragging ? m_previewProgress * DurationSeconds : GetPlayTimeMs() / 1000;
            long seconds = (long)Math.Max(0, Math.Floor(playTime));
            if (!m_isDragging && State == EPulsePlayerState.Ended) seconds = duration;
            seconds = Math.Min(seconds, duration);
            if (seconds == m_displayedSeconds && duration == m_displayedDuration) return;
            m_displayedSeconds = seconds;
            m_displayedDuration = duration;
            m_playbackTimeText.text = FormatTime(seconds) + "/" + FormatTime(duration);
        }

        private static string FormatTime(long seconds)
        {
            // 一小时以内使用 m:ss，较长视频使用 h:mm:ss。
            if (seconds < 3600) return (seconds / 60).ToString() + ":" + (seconds % 60).ToString("00");
            return (seconds / 3600).ToString() + ":" + ((seconds / 60) % 60).ToString("00") +
                ":" + (seconds % 60).ToString("00");
        }

        private void BindSlider()
        {
            UnbindSlider();
            m_boundSlider = m_playbackProgressSlider;
            if (m_boundSlider == null) return;
            m_sliderInput = m_boundSlider.GetComponent<PulseSeekSlider>();
            if (m_sliderInput == null) m_sliderInput = m_boundSlider.gameObject.AddComponent<PulseSeekSlider>();
            m_sliderInput.SetOwner(this);
            m_boundSlider.onValueChanged.AddListener(OnSliderValueChanged);
        }

        private void UnbindSlider()
        {
            if (m_boundSlider != null)
            {
                m_boundSlider.onValueChanged.RemoveListener(OnSliderValueChanged);
                m_boundSlider.interactable = false;
            }
            if (m_sliderInput != null) m_sliderInput.SetOwner(null);
            m_boundSlider = null;
            m_sliderInput = null;
            m_isDragging = false;
            m_hasPendingSliderSeek = false;
        }

        private bool CanSeek()
        {
            EPulsePlayerState state = State;
            return isActiveAndEnabled && !m_isLoading && DurationSeconds > 0 &&
                !IsPlaybackBusy &&
                (state == EPulsePlayerState.Ready || state == EPulsePlayerState.Playing ||
                state == EPulsePlayerState.Paused || state == EPulsePlayerState.Ended);
        }

        internal void BeginProgressDrag()
        {
            if (!CanSeek() || m_boundSlider == null) return;
            m_hasPendingSliderSeek = false;
            m_isDragging = true;
            m_previewProgress = m_boundSlider.value;
        }

        internal void EndProgressDrag()
        {
            if (!m_isDragging) return;
            m_isDragging = false;
            if (CanSeek()) SeekFromSlider(m_previewProgress);
        }

        private void OnSliderValueChanged(float progress)
        {
            if (!CanSeek()) return;
            m_previewProgress = Mathf.Clamp01(progress);
            if (m_isDragging) RefreshTimeText();
            else m_hasPendingSliderSeek = true;
        }

        private void SeekFromSlider(float progress)
        {
            try { Seek(Mathf.Clamp01(progress) * DurationSeconds); }
            catch (Exception exception) { NotifyFailure(exception, true); }
        }

        private void ConfigureSlider()
        {
            if (m_playbackProgressSlider == null) return;
            m_playbackProgressSlider.minValue = 0;
            m_playbackProgressSlider.maxValue = 1;
            m_playbackProgressSlider.wholeNumbers = false;
            m_playbackProgressSlider.interactable = CanSeek();
            m_playbackProgressSlider.SetValueWithoutNotify(0);
        }

        private void ApplyLoadingProgress(float progress)
        {
            m_loadingProgress = progress;
            if (m_loadingProgressImage != null) m_loadingProgressImage.fillAmount = progress;
            int percentage = Mathf.RoundToInt(progress * 100);
            if (m_loadingPercentageText == null || percentage == m_displayedPercentage) return;
            m_displayedPercentage = percentage;
            m_loadingPercentageText.text = percentage.ToString() + "%";
        }

        private void SetLoadingVisible(bool visible)
        {
            // 只控制 Graphic，不禁用对象，避免引用所在对象包含本组件时触发自我关闭。
            m_isLoading = visible;
            if (m_loadingProgressImage != null) m_loadingProgressImage.enabled = visible;
            if (m_loadingPercentageText != null) m_loadingPercentageText.enabled = visible;
        }

        private string ResolveConfiguredPath()
        {
            if (string.IsNullOrWhiteSpace(m_sourcePath)) throw new ArgumentException("请填写 Source Path。");
            if (Uri.TryCreate(m_sourcePath, UriKind.Absolute, out Uri address) &&
                (address.IsFile || address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps))
                return m_sourcePath;
            return m_relativeToStreamingAssets && !Path.IsPathRooted(m_sourcePath)
                ? Path.Combine(Application.streamingAssetsPath, m_sourcePath) : m_sourcePath;
        }

        private PulsePlayer RequirePlayer()
        {
            if (m_player == null) throw new InvalidOperationException("请先打开并准备视频。");
            return m_player;
        }

        private void EnsureCurrent(int generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (this == null || !isActiveAndEnabled || generation != m_generation)
                throw new OperationCanceledException(cancellationToken);
        }

        private static Vector4 GetUVRegion(PulseRegion region, PulseMetadata metadata)
        {
            return new Vector4(
                (float)region.X / metadata.EncodedWidth, (float)region.Y / metadata.EncodedHeight,
                (float)region.Width / metadata.EncodedWidth, (float)region.Height / metadata.EncodedHeight);
        }

        private void SubscribePlayer(PulsePlayer player)
        {
            player.Started += OnStarted;
            player.Paused += OnPaused;
            player.Resumed += OnResumed;
            player.Stopped += OnStopped;
            player.Completed += OnCompleted;
            player.Failed += OnFailed;
        }

        private void UnsubscribePlayer(PulsePlayer player)
        {
            player.Started -= OnStarted;
            player.Paused -= OnPaused;
            player.Resumed -= OnResumed;
            player.Stopped -= OnStopped;
            player.Completed -= OnCompleted;
            player.Failed -= OnFailed;
        }

        private void OnStarted() { Notify(Started); }
        private void OnPaused() { Notify(Paused); }
        private void OnResumed() { Notify(Resumed); }
        private void OnCompleted()
        {
            if (m_playbackDriver == null || m_playbackDriver.OnVideoCompleted()) Notify(Completed);
        }

        private void OnStopped()
        {
            UnbindDisplay();
            SetLoadingVisible(false);
            Notify(Stopped);
        }

        private void OnFailed(Exception exception)
        {
            m_lastError = exception;
            UnbindDisplay();
            SetLoadingVisible(false);
            NotifyFailure(exception, true);
        }

        private static void Notify(Action callback)
        {
            try { callback?.Invoke(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private void NotifyProgress(float progress)
        {
            try { LoadingProgressChanged?.Invoke(progress); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }

        private void NotifyFailure(Exception exception, bool logWhenUnhandled)
        {
            try
            {
                if (Failed != null) Failed.Invoke(exception);
                else if (logWhenUnhandled) Debug.LogException(exception, this);
            }
            catch (Exception listenerException) { Debug.LogException(listenerException, this); }
        }

        private static void LogUnhandled(Exception exception)
        {
            if (!(exception is OperationCanceledException)) Debug.LogException(exception);
        }

        private void OnDisable()
        {
            UnbindButtons();
            UnbindSlider();
            if (Application.isPlaying || m_player != null || m_openCancellation != null) Close();
        }

        private void OnDestroy()
        {
            UnbindButtons();
            UnbindSlider();
            if (m_playbackDriver != null || m_player != null || m_openCancellation != null) Close();
        }
    }
}
