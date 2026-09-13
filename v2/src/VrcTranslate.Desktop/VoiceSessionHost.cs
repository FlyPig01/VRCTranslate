using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;
using VrcTranslate.Core.Speech;
using VrcTranslate.Core.Translation;
using VrcTranslate.Desktop.Pages;

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
    private volatile bool _running;
    private volatile bool _processingResult;

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

    /// <summary>Microphone device id for the next start; ignored by loopback sessions.</summary>
    public string? MicrophoneId { get; set; }

    /// <summary>Recognition language tag for the next start; normalized by the speech contract.</summary>
    public string SourceLanguage { get; set; } = LocalSpeechLanguages.SelfChinese;

    protected abstract AudioCaptureMode CaptureMode { get; }

    protected abstract Task ProcessRecognizedAsync(string text, string? recognizedLanguage);

    protected abstract string CaptureFaultMessage { get; }

    /// <summary>Toast shown after a sentence went through the full pipeline; null means stay quiet.</summary>
    protected virtual SpeechNotificationEventArgs? SuccessNotification => null;

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_running && _session?.IsStarted == true) return;

            var session = new LocalSpeechCaptureSession(
                _captures.Create(CaptureMode, MicrophoneId),
                _speech,
                SourceLanguage);
            session.ResultReady += OnResultReady;
            session.Faulted += OnSessionFaulted;
            session.LevelChanged += OnLevelChanged;
            try
            {
                await session.StartAsync().ConfigureAwait(false);
                _session = session;
                _running = true;
                RunningChanged?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                session.ResultReady -= OnResultReady;
                session.Faulted -= OnSessionFaulted;
                session.LevelChanged -= OnLevelChanged;
                await session.DisposeAsync().ConfigureAwait(false);
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
            var session = _session;
            _session = null;
            if (session is not null)
            {
                session.ResultReady -= OnResultReady;
                session.Faulted -= OnSessionFaulted;
                session.LevelChanged -= OnLevelChanged;
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
        if (!_running || _processingResult) return;
        _processingResult = true;
        try
        {
            var result = await _speech.RecognizeAsync(
                new SpeechRecognitionRequest(samples, 16_000, sourceLanguage),
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                await ProcessRecognizedAsync(result.Text, result.SourceLanguage).ConfigureAwait(false);
            }
        }
        finally
        {
            _processingResult = false;
        }
    }

    private void OnResultReady(object? sender, SpeechRecognitionResult result)
    {
        if (!_running || string.IsNullOrWhiteSpace(result.Text) || _processingResult) return;
        _ = ProcessPipelinedAsync(result);
    }

    private async Task ProcessPipelinedAsync(SpeechRecognitionResult result)
    {
        await _processingGate.WaitAsync().ConfigureAwait(false);
        _processingResult = true;
        try
        {
            await ProcessRecognizedAsync(result.Text, result.SourceLanguage).ConfigureAwait(false);
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
            _processingResult = false;
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
}

/// <summary>Other-player caption session: VRChat loopback audio to subtitle output.</summary>
public sealed class SubtitleSpeechSession : VoiceSessionHost
{
    private readonly AppState _state;

    public SubtitleSpeechSession(AppState state) : base(state.LocalSpeech, state.AudioCapture)
    {
        _state = state;
        SourceLanguage = "auto";
    }

    protected override AudioCaptureMode CaptureMode => AudioCaptureMode.SystemLoopback;

    protected override string CaptureFaultMessage => "无法读取系统音频，请检查音频设备后重试。";

    protected override SpeechNotificationEventArgs DescribeFailure(Exception exception) =>
        new(SpeechNotificationKind.Error, "翻译失败", exception.Message);

    protected override async Task ProcessRecognizedAsync(string text, string? recognizedLanguage)
    {
        var current = _state.CurrentRoute;
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
        var translated = await _state.Translator.TranslateAsync(
            new TextTranslationRequest(text, voiceRoute, TextTranslationSource.SpeechRecognition,
                sourceLanguageHint: sourceMode == SourceLanguageMode.Fixed ? recognizedLanguage : null))
            .ConfigureAwait(false);
        _state.SetTranslationPreview(text, translated.TranslatedText);
        // Other-player captions are translated to Simplified Chinese only;
        // use the shared OSC length guard without adding own-input targets
        // or the original text to this stream.
        await _state.Osc.SendChatboxAsync(TranslationOutputFormatter.TrimForOsc(translated.TranslatedText))
            .ConfigureAwait(false);
        OverlayWindowHost.SetSubtitleFromAnyThread(text, translated.TranslatedText);
    }
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

    protected override async Task ProcessRecognizedAsync(string text, string? recognizedLanguage)
    {
        var result = await _state.TranslateSelfAsync(text, TextTranslationSource.SpeechRecognition)
            .ConfigureAwait(false);
        _state.SetTranslationPreview(
            text,
            result.Primary.TranslatedText,
            result.Secondary?.TranslatedText,
            result.Targets);
        await _state.Osc.SendChatboxAsync(TranslationOutputFormatter.FormatForOsc(result))
            .ConfigureAwait(false);
    }
}
