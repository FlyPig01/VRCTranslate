from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


CONFIG_VERSION = 12
ROMAJI_MODES = frozenset({"off", "auto", "force"})
MIN_PROFILE_TIMEOUT_SECONDS = 8.0
SUPPORTED_TRANSLATION_PROVIDERS = frozenset(
    {
        "test",
        "deepl",
        "google_cloud",
        "google_free",
        "aliyun",
        "tencent",
        "openai_compatible",
        "multimodal_openai",
    }
)
SUPPORTED_SPEECH_RECOGNITION_PROVIDERS = frozenset(
    {
        "tencent_realtime",
        "aliyun_nls_realtime",
    }
)
LEGACY_SPEECH_RECOGNITION_PROVIDERS = frozenset(
    {
        "openai_transcription",
        "dashscope_realtime",
        "audio_chat_completions",
    }
)
REALTIME_SPEECH_PROVIDERS = SUPPORTED_SPEECH_RECOGNITION_PROVIDERS


@dataclass(slots=True)
class TranslationProfile:
    id: str = "test"
    name: str = "测试回显"
    provider: str = "test"
    base_url: str = ""
    api_key: str = ""
    model: str = ""
    region: str = ""
    timeout_seconds: float = 8.0
    options: dict[str, Any] = field(default_factory=dict)


@dataclass(slots=True)
class TranslationRouteSettings:
    profile_id: str = "test"
    source_language: str = "auto"
    target_language: str = "zh-CN"
    message_format: str = "translation_only"
    overflow_policy: str = "split"
    timeout_seconds: float = 8.0
    queue_limit: int = 8
    task_ttl_seconds: float = 4.0
    romaji_mode: str = "auto"
    glossary_enabled: bool = True
    translation_strategy: str = "text_profile"


@dataclass(slots=True)
class TranslationSettings:
    profiles: list[TranslationProfile] = field(
        default_factory=lambda: [TranslationProfile()]
    )
    self_route: TranslationRouteSettings = field(
        default_factory=TranslationRouteSettings
    )
    ocr_route: TranslationRouteSettings = field(
        default_factory=lambda: TranslationRouteSettings(
            source_language="ja",
            timeout_seconds=4.0,
            queue_limit=8,
            task_ttl_seconds=4.0,
            romaji_mode="off",
        )
    )
    voice_route: TranslationRouteSettings = field(
        default_factory=lambda: TranslationRouteSettings(
            source_language="auto",
            target_language="zh-CN",
            timeout_seconds=8.0,
            queue_limit=2,
            task_ttl_seconds=20.0,
            romaji_mode="auto",
        )
    )

    def profile(self, profile_id: str) -> TranslationProfile:
        for profile in self.profiles:
            if profile.id == profile_id:
                return profile
        raise KeyError(profile_id)

    def profile_for_purpose(self, purpose: str) -> TranslationProfile:
        if purpose == "ocr":
            route = self.ocr_route
        elif purpose == "voice":
            route = self.voice_route
        else:
            route = self.self_route
        return self.profile(route.profile_id)

    def ensure_routes(self) -> None:
        self.profiles = [
            profile
            for profile in self.profiles
            if profile.provider in SUPPORTED_TRANSLATION_PROVIDERS
        ]
        for profile in self.profiles:
            if (
                profile.provider == "multimodal_openai"
                and profile.timeout_seconds < MIN_PROFILE_TIMEOUT_SECONDS
            ):
                profile.timeout_seconds = MIN_PROFILE_TIMEOUT_SECONDS
        ids = {profile.id for profile in self.profiles}
        if not ids:
            self.profiles.append(TranslationProfile())
            ids.add("test")
        text_profiles = [
            profile for profile in self.profiles
            if profile.provider != "multimodal_openai"
        ]
        if not text_profiles:
            fallback_profile = TranslationProfile()
            self.profiles.append(fallback_profile)
            ids.add(fallback_profile.id)
            text_profiles.append(fallback_profile)
        fallback = self.profiles[0].id
        text_ids = {profile.id for profile in text_profiles}
        if self.self_route.profile_id not in text_ids:
            self.self_route.profile_id = text_profiles[0].id
        if self.ocr_route.profile_id not in ids:
            self.ocr_route.profile_id = fallback
        if self.voice_route.profile_id not in text_ids:
            self.voice_route.profile_id = text_profiles[0].id


@dataclass(slots=True)
class SpeechRecognitionProfile:
    id: str = ""
    name: str = ""
    provider: str = "tencent_realtime"
    base_url: str = ""
    api_key: str = ""
    model: str = "16k_zh"
    timeout_seconds: float = 8.0
    options: dict[str, Any] = field(
        default_factory=lambda: {"validation_state": "pending"}
    )


@dataclass(slots=True)
class VoiceSegmentSettings:
    energy_threshold: int = 350
    silence_ms: int = 650
    minimum_speech_ms: int = 300
    maximum_segment_seconds: float = 12.0


@dataclass(slots=True)
class VoiceOverlaySettings:
    topmost: bool = True
    show_original: bool = True
    display_mode: str = "both"
    x: int = -1
    y: int = -1
    width: int = 560
    height: int = 210
    opacity: float = 0.9
    font_size: int = 18
    max_items: int = 3


@dataclass(slots=True)
class VoiceTranslationSettings:
    target_process_name: str = "VRChat.exe"
    target_window_title: str = ""
    asr_profile_id: str = ""
    asr_profiles: list[SpeechRecognitionProfile] = field(default_factory=list)
    segment: VoiceSegmentSettings = field(default_factory=VoiceSegmentSettings)
    overlay: VoiceOverlaySettings = field(default_factory=VoiceOverlaySettings)

    def ensure_profiles(self) -> None:
        self.asr_profiles = [
            profile
            for profile in self.asr_profiles
            if profile.provider in SUPPORTED_SPEECH_RECOGNITION_PROVIDERS
        ]
        if not self.asr_profiles:
            self.asr_profile_id = ""
            return
        ids = {profile.id for profile in self.asr_profiles}
        if self.asr_profile_id not in ids:
            self.asr_profile_id = self.asr_profiles[0].id

    def asr_profile(self) -> SpeechRecognitionProfile:
        self.ensure_profiles()
        if not self.asr_profiles:
            raise ValueError("尚未配置实时语音识别档案")
        for profile in self.asr_profiles:
            if profile.id == self.asr_profile_id:
                return profile
        raise KeyError(self.asr_profile_id)

@dataclass(slots=True)
class GlossarySettings:
    enabled: bool = True
    builtin_enabled: bool = True


@dataclass(slots=True)
class OscSettings:
    host: str = "127.0.0.1"
    port: int = 9000
    min_interval_seconds: float = 1.5
    play_sound: bool = True
    chatbox_max_units: int = 144


@dataclass(slots=True)
class OcrSettings:
    capture_backend: str = "auto"
    recognition_mode: str = "continuous"
    interval_ms: int = 900
    confidence: float = 0.68
    change_threshold: float = 0.0
    multimodal_interval_ms: int = 3000
    multimodal_max_image_side: int = 1600
    multimodal_image_quality: int = 85
    region_x: int = 0
    region_y: int = 0
    region_width: int = 0
    region_height: int = 0
    region_coordinate_space: str = "window"
    window_title: str = "VRChat"


@dataclass(slots=True)
class UiSettings:
    input_topmost: bool = True
    ocr_topmost: bool = True
    ocr_mouse_passthrough: bool = False
    input_x: int = -1
    input_y: int = -1
    input_width: int = 480
    ocr_overlay_x: int = -1
    ocr_overlay_y: int = -1
    ocr_overlay_width: int = 420
    ocr_overlay_height: int = 220
    ocr_overlay_opacity: float = 0.88
    ocr_overlay_show_original: bool = True
    ocr_overlay_font_size: int = 16
    ocr_overlay_max_items: int = 5
    ocr_display_mode: str = "overlay"
    ocr_inline_opacity: float = 0.9
    ocr_inline_auto_contrast: bool = True
    ocr_orb_topmost: bool = True
    ocr_orb_x: int = -1
    ocr_orb_y: int = -1
    main_width: int = 960
    main_height: int = 660
    main_x: int = -1
    main_y: int = -1
    language: str = "zh_CN"


@dataclass(slots=True)
class AppSettings:
    version: int = CONFIG_VERSION
    translation: TranslationSettings = field(default_factory=TranslationSettings)
    glossary: GlossarySettings = field(default_factory=GlossarySettings)
    osc: OscSettings = field(default_factory=OscSettings)
    ocr: OcrSettings = field(default_factory=OcrSettings)
    ui: UiSettings = field(default_factory=UiSettings)
    voice: VoiceTranslationSettings = field(default_factory=VoiceTranslationSettings)
