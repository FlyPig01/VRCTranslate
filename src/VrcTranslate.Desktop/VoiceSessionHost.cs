using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Captions;
using VrcTranslate.Application.Speech;
using VrcTranslate.Core.Speech;
using VrcTranslate.Core.Translation;
using VrcTranslate.Desktop.Pages;
using VrcTranslate.Infrastructure.Speech;

namespace VrcTranslate.Desktop;

/// <summary>Severity of a user-facing speech pipeline notification.</summary>
public enum SpeechNotificationKind
{
    Success,
    Warning,
    Error
}

public sealed record SpeechNotificationEventArgs(
    SpeechNotificationKind Kind,
    string Title,
    string Message);

/// <summary>
/// One start's capture together with the background monitor that must run with
/// it. Own voice has no monitor; other-player captions pair the adaptive
/// loopback coordinator with the VRChat process watcher that steers it.
/// </summary>
public sealed record VoiceCapturePlan(IAudioCapture Capture, VrchatProcessWatcher? Monitor);

/// <summary>
/// Application-lifetime owner of one speech recognition session. Pages appear
/// and disappear as the user navigates, so the capture session and the
/// translate/OSC pipeline attached to its results must outlive any single
/// page instance. Visible pages only mirror state and subscribe to events;
/// the session stops when the user asks for it or when the application shuts
/// down, never because a page unloaded.
/// </summary>
public abstract class VoiceSessionHost
{
    private readonly LocalSpeechService _speech;
    private readonly IAudioCaptureFactory _captures;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private LocalSpeechCaptureSession? _session;
    private VrchatProcessWatcher? _monitor;
    private volatile bool _running;

    protected VoiceSessionHost(LocalSpeechService speech, IAudioCaptureFactory captures)
    {
        _speech = speech;
        _captures = captures;
    }

    public bool IsRunning => _running;

    /// <summary>Raised after start or stop completed so visible pages can refresh their visuals.</summary>
    public event EventHandler? RunningChanged;

    /// <summary>Raised on the capture thread for audible-input indication; subscribers must marshal to UI.</summary>
    public event EventHandler<AudioLevelEventArgs>? LevelChanged;

    /// <summary>Raised with a toast-worthy pipeline outcome; may arrive from a worker thread.</summary>
    public event EventHandler<SpeechNotificationEventArgs>? Notified;

    /// <summary>Derives raise the shared notification from their own pipeline events.</summary>
    protected void RaiseNotified(SpeechNotificationEventArgs args) => Notified?.Invoke(this, args);

    /// <summary>
    /// Raised when the capture source changes, so a visible page can label where
    /// the audio comes from. May arrive from a capture thread; subscribers must
    /// marshal to the UI thread.
    /// </summary>
    public event EventHandler<AudioSourceChangedEventArgs>? SourceChanged;

    /// <summary>Source the running session is fed from; system loopback while stopped.</summary>
    public AudioSourceState SourceState => _session?.SourceState ?? AudioSourceState.SystemLoopback;

    /// <summary>
    /// True when a visible page should show its one short compatibility notice
    /// for this run. The latch lives in the session, so navigating away and back
    /// cannot repeat the hint.
    /// </summary>
    public bool ConsumeCaptureFallbackNotice() => _session?.ConsumeFallbackNotice() ?? false;

    /// <summary>Microphone device id for the next start; ignored by loopback sessions.</summary>
    public string? MicrophoneId { get; set; }

    /// <summary>Recognition language tag for the next start; normalized by the speech contract.</summary>
    public string SourceLanguage { get; set; } = LocalSpeechLanguages.SelfChinese;

    protected abstract AudioCaptureMode CaptureMode { get; }

    /// <summary>
    /// How this session processes one audio segment. Other-player sessions split
    /// at speaker changes so alternating voices become separate captions; the
    /// default of none is the own-voice behaviour. Read once per start.
    /// </summary>
    protected virtual SpeechCaptureProcessingOptions CaptureProcessing => SpeechCaptureProcessingOptions.None;

    /// <summary>
    /// Capture for the next start together with the monitor that must run with
    /// it. 自身语音直接向平台工厂要麦克风，来源类型是 Microphone；他人语音交给
    /// 自适应回环协调器，由 VRChat 进程监视器把当前目标推给它。
    /// </summary>
    protected virtual VoiceCapturePlan CreateCapturePlan()
    {
        if (CaptureMode == AudioCaptureMode.Microphone)
        {
            return new VoiceCapturePlan(
                _captures.Create(AudioCaptureRequest.Microphone(MicrophoneId)),
                Monitor: null);
        }

        var loopback = new AdaptiveLoopbackAudioCapture(_captures);
        return new VoiceCapturePlan(
            loopback,
            new VrchatProcessWatcher(new VrchatProcessResolver(), loopback.ApplyTargetAsync));
    }

    /// <summary>
    /// Publishes one recognized sentence before it is queued behind an in-flight
    /// translation, and returns the id that identifies that message for the rest
    /// of the run. Recognition is roughly six times faster than translation, so
    /// showing the sentence now - instead of after its translation - is what keeps
    /// two sentences from arriving on screen in the same moment. The default
    /// session publishes nothing.
    /// </summary>
    protected virtual long PublishRecognized(SpeechRecognitionResult result) => 0;

    /// <summary>
    /// Runs the recognized sentence through translation and output. The whole
    /// result is passed so a session can also use the speaker label, and
    /// <paramref name="recognizedId"/> is the id that
    /// <see cref="PublishRecognized"/> returned for this sentence (0 when the
    /// session publishes nothing). The default is a no-op; the own-voice session
    /// owns the default flow through <see cref="DispatchRecognizedAsync"/>.
    /// </summary>
    protected virtual Task ProcessRecognizedAsync(SpeechRecognitionResult result, long recognizedId) =>
        Task.CompletedTask;

    /// <summary>
    /// How one recognized sentence continues after it was published: the default
    /// serializes translation and output behind one gate with a toast on failure.
    /// A session that owns its own pipeline (other-player captions) replaces this
    /// entirely - results must never be dropped just because the previous one is
    /// still translating (D29).
    /// </summary>
    protected virtual Task DispatchRecognizedAsync(SpeechRecognitionResult result, long recognizedId) =>
        ProcessPipelinedAsync(result, recognizedId);

    /// <summary>Session-scoped setup after the capture started; runs under the lifecycle gate.</summary>
    protected virtual Task OnSessionStartedAsync() => Task.CompletedTask;

    /// <summary>Session-scoped teardown before the capture stops; runs under the lifecycle gate.</summary>
    protected virtual Task OnSessionStoppingAsync() => Task.CompletedTask;

    /// <summary>
    /// A source boundary switched the audio mid-run (for example VRChat exited).
    /// Work that belonged to the previous source must not surface afterwards.
    /// </summary>
    protected virtual Task OnCaptureSourceBoundaryAsync() => Task.CompletedTask;

    protected abstract string CaptureFaultMessage { get; }

    /// <summary>Toast shown after a sentence went through the full pipeline; null means stay quiet.</summary>
    protected virtual SpeechNotificationEventArgs? SuccessNotification => null;

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_running && _session?.IsStarted == true) return;

            var plan = CreateCapturePlan();
            var session = new LocalSpeechCaptureSession(
                plan.Capture,
                _speech,
                SourceLanguage,
                // Silero VAD when its bundled model is present, adaptive
                // energy gate otherwise.
                LocalSpeechSegmenterFactory.CreateDefault(),
                CaptureProcessing);
            session.ResultReady += OnResultReady;
            session.Faulted += OnSessionFaulted;
            session.LevelChanged += OnLevelChanged;
            session.SourceChanged += OnSessionSourceChanged;
            try
            {
                await session.StartAsync().ConfigureAwait(false);
                // Session-scoped pipelines exist before the first result can
                // arrive; if this fails the capture is torn down again.
                await OnSessionStartedAsync().ConfigureAwait(false);
                // The session is published before the monitor starts pushing
                // targets, so a source change that immediately falls back to the
                // compatibility mode already finds the page's data source.
                _session = session;
                _monitor = plan.Monitor;
                _running = true;
                // 监视器在采集就绪后才开始推送目标，切换请求不会落在未启动的协调器上。
                plan.Monitor?.Start();
                RunningChanged?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                session.ResultReady -= OnResultReady;
                session.Faulted -= OnSessionFaulted;
                session.LevelChanged -= OnLevelChanged;
                session.SourceChanged -= OnSessionSourceChanged;
                await session.DisposeAsync().ConfigureAwait(false);
                if (plan.Monitor is not null) await plan.Monitor.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _running = false;
            // Pipelines stop before the capture does: work of this run must be
            // cancelled and awaited while the events are still wired up.
            await OnSessionStoppingAsync().ConfigureAwait(false);
            var monitor = _monitor;
            _monitor = null;
            // 先停监视器再释放采集：循环结束后不会再有扫描任务调用协调器。
            if (monitor is not null) await monitor.DisposeAsync().ConfigureAwait(false);
            var session = _session;
            _session = null;
            if (session is not null)
            {
                session.ResultReady -= OnResultReady;
                session.Faulted -= OnSessionFaulted;
                session.LevelChanged -= OnLevelChanged;
                session.SourceChanged -= OnSessionSourceChanged;
                await session.DisposeAsync().ConfigureAwait(false);
            }

            RunningChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Direct recognition hook kept for the validation scripts; bypasses capture.</summary>
    public async Task RecognizeSamplesAsync(
        ReadOnlyMemory<float> samples,
        string sourceLanguage,
        CancellationToken cancellationToken = default)
    {
        if (!_running) return;
        var result = await _speech.RecognizeAsync(
            new SpeechRecognitionRequest(samples, 16_000, sourceLanguage),
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Text)) return;
        var recognizedId = PublishRecognized(result);
        await DispatchRecognizedAsync(result, recognizedId).ConfigureAwait(false);
    }

    private void OnResultReady(object? sender, SpeechRecognitionResult result)
    {
        if (!_running || string.IsNullOrWhiteSpace(result.Text)) return;
        // The sentence is published before the pipeline queues it, and dispatch
        // never drops a result because the previous one is still translating.
        var recognizedId = PublishRecognized(result);
        _ = DispatchRecognizedAsync(result, recognizedId);
    }

    private async Task ProcessPipelinedAsync(SpeechRecognitionResult result, long recognizedId)
    {
        await _processingGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ProcessRecognizedAsync(result, recognizedId).ConfigureAwait(false);
            if (SuccessNotification is { } success)
            {
                Notified?.Invoke(this, success);
            }
        }
        catch (Exception exception)
        {
            Notified?.Invoke(this, DescribeFailure(exception));
        }
        finally
        {
            _processingGate.Release();
        }
    }

    protected abstract SpeechNotificationEventArgs DescribeFailure(Exception exception);

    private void OnSessionFaulted(object? sender, Exception exception)
    {
        if (!_running) return;
        Notified?.Invoke(this, new SpeechNotificationEventArgs(
            SpeechNotificationKind.Warning, "语音采集已停止", CaptureFaultMessage));
    }

    private void OnLevelChanged(object? sender, AudioLevelEventArgs args) =>
        LevelChanged?.Invoke(this, args);

    private void OnSessionSourceChanged(object? sender, AudioSourceChangedEventArgs args)
    {
        SourceChanged?.Invoke(this, args);
        if (args.IsBoundary && _running)
        {
            _ = RunSourceBoundaryAsync();
        }
    }

    private async Task RunSourceBoundaryAsync()
    {
        try
        {
            await OnCaptureSourceBoundaryAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Notified?.Invoke(this, DescribeFailure(exception));
        }
    }
}

/// <summary>Other-player caption session: VRChat loopback audio to subtitle output.</summary>
public sealed class SubtitleSpeechSession : VoiceSessionHost
{
    private readonly AppState _state;
    private OtherPlayerCaptionPipeline? _pipeline;

    public SubtitleSpeechSession(AppState state) : base(state.LocalSpeech, state.AudioCapture)
    {
        _state = state;
        SourceLanguage = "auto";
    }

    /// <summary>
    /// Created on first use: this session is constructed while <see cref="AppState"/>
    /// is still assigning its translation service, so the pipeline must not capture
    /// it eagerly in the constructor.
    /// </summary>
    private OtherPlayerCaptionPipeline Pipeline
    {
        get
        {
            var pipeline = _pipeline;
            if (pipeline is not null) return pipeline;
            pipeline = new OtherPlayerCaptionPipeline(
                _state.Translator,
                new OverlaySubtitleSurface(),
                new AppStateChatboxOutput(_state));
            pipeline.Diagnostics += (_, args) => RaiseNotified(new SpeechNotificationEventArgs(
                SpeechNotificationKind.Warning, "他人语音字幕", args.Message));
            _pipeline = pipeline;
            return pipeline;
        }
    }

    // 回环族：具体采系统混音还是 VRChat 进程音频由自适应协调器决定。
    protected override AudioCaptureMode CaptureMode => AudioCaptureMode.SystemLoopback;

    protected override SpeechCaptureProcessingOptions CaptureProcessing => new()
    {
        // 换人切分对多人对话是能力而不是装饰（D29）：不跟说话人标签开关绑定；
        // 标签开关只控制是否给句子标注说话人名字。自身语音两者全关（默认）。
        SplitAtSpeakerChanges = true,
        AttachSpeakerLabels = _state.LocalSpeech.SpeakerLabelsEnabled,
    };

    protected override string CaptureFaultMessage => "无法读取系统音频，请检查音频设备后重试。";

    protected override SpeechNotificationEventArgs DescribeFailure(Exception exception) =>
        new(SpeechNotificationKind.Error, "翻译失败", exception.Message);

    protected override Task OnSessionStartedAsync() => Pipeline.BeginSessionAsync();

    protected override Task OnSessionStoppingAsync() => Pipeline.EndSessionAsync();

    protected override async Task OnCaptureSourceBoundaryAsync()
    {
        // 音源切换后旧源的半句不允许再回填或外发：清空本会话的队列再继续。
        var pipeline = Pipeline;
        await pipeline.EndSessionAsync().ConfigureAwait(false);
        await pipeline.BeginSessionAsync().ConfigureAwait(false);
    }

    protected override async Task DispatchRecognizedAsync(SpeechRecognitionResult result, long recognizedId)
    {
        // 组合根职责（D28.2）：档案恢复完成后才取本句的路由快照，运行中换
        // 档案只影响下一句；翻译、字幕与 OSC 的顺序交给管线。
        await _state.Ready.ConfigureAwait(false);
        var current = _state.CurrentRoute;
        var recognizedLanguage = result.SourceLanguage;
        var sourceMode = string.IsNullOrWhiteSpace(recognizedLanguage) || recognizedLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? SourceLanguageMode.AutoDetect
            : SourceLanguageMode.Fixed;
        var fixedSource = sourceMode == SourceLanguageMode.Fixed ? recognizedLanguage : null;
        var voiceRoute = new TranslationRoute(
            current.RouteId,
            current.DisplayName,
            current.Profile,
            new LanguagePolicy(sourceMode, "zh-CN", fixedSource),
            current.InvariantPolicy,
            current.RetryPolicy);
        Pipeline.TrySubmit(new OtherPlayerCaptionInput(
            result.Text,
            result.SpeakerLabel,
            fixedSource,
            voiceRoute));
    }
}

/// <summary>Bridge between the caption pipeline and the desktop subtitle window.</summary>
internal sealed class OverlaySubtitleSurface : IOtherPlayerSubtitleSurface
{
    public void PublishPending(long captionId, string text, string? speakerLabel) =>
        OverlayWindowHost.AppendRecognizedSubtitleFromAnyThread(captionId, text, speakerLabel);

    public void FillTranslation(long captionId, string? translatedText) =>
        OverlayWindowHost.FillSubtitleTranslationFromAnyThread(captionId, translatedText);
}

/// <summary>Sends through the app's current chatbox client; reads it per send so reloads apply.</summary>
internal sealed class AppStateChatboxOutput(AppState state) : IChatboxOutput
{
    public Task SendChatboxAsync(string message, CancellationToken cancellationToken = default) =>
        state.Osc.SendChatboxAsync(message, cancellationToken);
}

/// <summary>Own-voice session: microphone audio to the self-translation targets.</summary>
public sealed class SelfVoiceSpeechSession : VoiceSessionHost
{
    private readonly AppState _state;

    public SelfVoiceSpeechSession(AppState state) : base(state.LocalSpeech, state.AudioCapture)
    {
        _state = state;
        SourceLanguage = LocalSpeechLanguages.SelfChinese;
    }

    protected override AudioCaptureMode CaptureMode => AudioCaptureMode.Microphone;

    protected override string CaptureFaultMessage => "麦克风暂时不可用，请检查设备后重试。";

    protected override SpeechNotificationEventArgs DescribeFailure(Exception exception) =>
        new(SpeechNotificationKind.Error, "发送失败", exception.Message);

    protected override SpeechNotificationEventArgs? SuccessNotification =>
        new(SpeechNotificationKind.Success, "已发送", "自身语音译文已发送到 VRChat。");

    protected override async Task ProcessRecognizedAsync(SpeechRecognitionResult recognized, long recognizedId)
    {
        var text = recognized.Text;
        var result = await _state.TranslateSelfAsync(text, TextTranslationSource.SpeechRecognition)
            .ConfigureAwait(false);
        _state.SetTranslationPreview(
            text,
            result.Primary.TranslatedText,
            result.Secondary?.TranslatedText,
            result.Targets);
        // The same formatting rule as the manual input: translations, plus the
        // recognized original unless the OSC preference turned it off.
        await _state.Osc.SendChatboxAsync(TranslationOutputFormatter.FormatForChatbox(
                result,
                OscChatboxSettings.ReadIncludeOriginal()))
            .ConfigureAwait(false);
    }
}
