using VrcTranslate.Application.Subtitles;
using VrcTranslate.Core.Speech;
using VrcTranslate.Desktop.Pages;

namespace VrcTranslate.Desktop;

/// <summary>
/// The desktop effects behind the 他人语音 → 字幕 master switch: the
/// application-scoped recognition session and the caption surface owned by
/// <see cref="OverlayWindowHost"/>. Both are applied through
/// <see cref="OtherPlayerCaptionToggle"/>, which is the one serialized path, so
/// the window can never be hidden while recognition keeps running.
/// </summary>
internal sealed class OtherPlayerCaptionEndpoint : IOtherPlayerCaptionEndpoint
{
    private readonly AppState _state;

    public OtherPlayerCaptionEndpoint(AppState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public bool IsRunning => _state.SubtitleVoice.IsRunning;

    public bool IsSurfaceVisible => OverlayWindowHost.IsSubtitleVisible;

    public bool IsModelReady => _state.LocalSpeech.GetModelStatus().State == LocalSpeechModelState.Ready;

    public Task StartAsync(CancellationToken cancellationToken = default) => _state.SubtitleVoice.StartAsync();

    public Task StopAsync(CancellationToken cancellationToken = default) => _state.SubtitleVoice.StopAsync();

    public void ShowSurface() => OverlayWindowHost.ShowSubtitle();

    public void HideSurface() => OverlayWindowHost.HideSubtitle();
}
