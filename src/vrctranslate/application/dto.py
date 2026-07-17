from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


CONFIG_VERSION = 2


@dataclass(slots=True)
class TranslationProfile:
    id: str = "test"
    name: str = "测试回显"
    provider: str = "test"
    base_url: str = ""
    api_key: str = ""
    model: str = ""
    region: str = ""
    timeout_seconds: float = 20.0
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
    romaji_to_kana: bool = True


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
            timeout_seconds=4.0,
            queue_limit=8,
            task_ttl_seconds=4.0,
        )
    )

    def profile(self, profile_id: str) -> TranslationProfile:
        for profile in self.profiles:
            if profile.id == profile_id:
                return profile
        raise KeyError(profile_id)

    def profile_for_purpose(self, purpose: str) -> TranslationProfile:
        route = self.ocr_route if purpose == "ocr" else self.self_route
        return self.profile(route.profile_id)

    def ensure_routes(self) -> None:
        ids = {profile.id for profile in self.profiles}
        if not ids:
            self.profiles.append(TranslationProfile())
            ids.add("test")
        fallback = self.profiles[0].id
        if self.self_route.profile_id not in ids:
            self.self_route.profile_id = fallback
        if self.ocr_route.profile_id not in ids:
            self.ocr_route.profile_id = fallback

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
    interval_ms: int = 900
    confidence: float = 0.65
    change_threshold: float = 2.0
    duplicate_seconds: float = 8.0
    region_x: int = 0
    region_y: int = 0
    region_width: int = 0
    region_height: int = 0
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
    ocr_overlay_font_size: int = 16
    ocr_overlay_max_items: int = 5
    ocr_overlay_display_seconds: float = 12.0
    main_width: int = 820
    main_height: int = 600
    main_x: int = -1
    main_y: int = -1
    language: str = "zh_CN"


@dataclass(slots=True)
class AppSettings:
    version: int = CONFIG_VERSION
    translation: TranslationSettings = field(default_factory=TranslationSettings)
    osc: OscSettings = field(default_factory=OscSettings)
    ocr: OcrSettings = field(default_factory=OcrSettings)
    ui: UiSettings = field(default_factory=UiSettings)
