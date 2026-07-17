from __future__ import annotations

from dataclasses import asdict
from typing import Any

from vrctranslate.application.dto import (
    CONFIG_VERSION,
    AppSettings,
    OcrSettings,
    OscSettings,
    TranslationProfile,
    TranslationRouteSettings,
    TranslationSettings,
    UiSettings,
)


def settings_v2_to_dict(settings: AppSettings) -> dict[str, Any]:
    settings.version = CONFIG_VERSION
    settings.translation.ensure_routes()
    return asdict(settings)


def settings_v2_from_dict(raw: dict[str, Any]) -> AppSettings:
    translation = mapping(raw.get("translation"))
    profiles_raw = translation.get("profiles")
    profiles = []
    if isinstance(profiles_raw, list):
        for index, item in enumerate(profiles_raw):
            if isinstance(item, dict):
                profiles.append(profile_from_dict(item, index))
    if not profiles:
        profiles = [TranslationProfile()]
    self_route = route_from_dict(
        mapping(translation.get("self_route")), TranslationRouteSettings()
    )
    ocr_route = route_from_dict(
        mapping(translation.get("ocr_route")),
        TranslationRouteSettings(timeout_seconds=4.0),
    )
    translation_settings = TranslationSettings(profiles, self_route, ocr_route)
    translation_settings.ensure_routes()
    osc = mapping(raw.get("osc"))
    ocr = mapping(raw.get("ocr"))
    ui = mapping(raw.get("ui"))
    return AppSettings(
        version=CONFIG_VERSION,
        translation=translation_settings,
        osc=osc_from_dict(osc),
        ocr=ocr_from_dict(ocr),
        ui=UiSettings(
            input_topmost=bool(ui.get("input_topmost", True)),
            ocr_topmost=bool(ui.get("ocr_topmost", True)),
            ocr_mouse_passthrough=bool(ui.get("ocr_mouse_passthrough", False)),
            input_x=int_in_range(ui.get("input_x"), -1, -100000, 100000),
            input_y=int_in_range(ui.get("input_y"), -1, -100000, 100000),
            input_width=int_in_range(ui.get("input_width"), 480, 320, 1200),
            ocr_overlay_x=int_in_range(ui.get("ocr_overlay_x"), -1, -100000, 100000),
            ocr_overlay_y=int_in_range(ui.get("ocr_overlay_y"), -1, -100000, 100000),
            ocr_overlay_width=int_in_range(ui.get("ocr_overlay_width"), 420, 260, 1600),
            ocr_overlay_height=int_in_range(ui.get("ocr_overlay_height"), 220, 100, 1200),
            ocr_overlay_opacity=float_in_range(ui.get("ocr_overlay_opacity"), 0.88, 0.25, 1.0),
            ocr_overlay_font_size=int_in_range(ui.get("ocr_overlay_font_size"), 16, 10, 40),
            ocr_overlay_max_items=int_in_range(ui.get("ocr_overlay_max_items"), 5, 1, 20),
            ocr_overlay_display_seconds=float_in_range(
                ui.get("ocr_overlay_display_seconds"), 12.0, 2.0, 120.0
            ),
            main_width=int_in_range(ui.get("main_width"), 820, 720, 2400),
            main_height=int_in_range(ui.get("main_height"), 600, 520, 1800),
            main_x=int_in_range(ui.get("main_x"), -1, -100000, 100000),
            main_y=int_in_range(ui.get("main_y"), -1, -100000, 100000),
        ),
    )


def profile_from_dict(raw: dict[str, Any], index: int) -> TranslationProfile:
    profile_id = str(raw.get("id", f"profile-{index + 1}")).strip() or f"profile-{index + 1}"
    provider = str(raw.get("provider", "test")).strip() or "test"
    options = raw.get("options") if isinstance(raw.get("options"), dict) else {}
    return TranslationProfile(
        id=profile_id,
        name=str(raw.get("name", profile_id)),
        provider=provider,
        base_url=str(raw.get("base_url", "")),
        api_key=str(raw.get("api_key", "")),
        model=str(raw.get("model", "")),
        region=str(raw.get("region", "")),
        timeout_seconds=float_in_range(raw.get("timeout_seconds"), 20.0, 1, 120),
        options=dict(options),
    )


def route_from_dict(
    raw: dict[str, Any], default: TranslationRouteSettings
) -> TranslationRouteSettings:
    return TranslationRouteSettings(
        profile_id=str(raw.get("profile_id", default.profile_id)),
        source_language=str(raw.get("source_language", default.source_language)),
        target_language=str(raw.get("target_language", default.target_language)),
        message_format=str(raw.get("message_format", default.message_format)),
        overflow_policy=str(raw.get("overflow_policy", default.overflow_policy)),
        timeout_seconds=float_in_range(
            raw.get("timeout_seconds"), default.timeout_seconds, 0.5, 120
        ),
        queue_limit=int_in_range(raw.get("queue_limit"), default.queue_limit, 1, 100),
        task_ttl_seconds=float_in_range(
            raw.get("task_ttl_seconds"), default.task_ttl_seconds, 0.5, 60
        ),
        romaji_to_kana=bool(raw.get("romaji_to_kana", True)),
    )


def osc_from_dict(raw: dict[str, Any]) -> OscSettings:
    return OscSettings(
        host=str(raw.get("host", "127.0.0.1")),
        port=int_in_range(raw.get("port"), 9000, 1, 65535),
        min_interval_seconds=float_in_range(
            raw.get("min_interval_seconds"), 1.5, 0.1, 60
        ),
        play_sound=bool(raw.get("play_sound", True)),
        chatbox_max_units=int_in_range(raw.get("chatbox_max_units"), 144, 1, 10000),
    )


def ocr_from_dict(raw: dict[str, Any]) -> OcrSettings:
    backend = str(raw.get("capture_backend", "auto"))
    return OcrSettings(
        capture_backend=backend if backend in {"auto", "windows", "screen"} else "auto",
        interval_ms=int_in_range(raw.get("interval_ms"), 900, 250, 10000),
        confidence=float_in_range(raw.get("confidence"), 0.8, 0, 1),
        change_threshold=float_in_range(raw.get("change_threshold"), 0.0, 0, 255),
        duplicate_seconds=float_in_range(raw.get("duplicate_seconds"), 4.0, 0, 300),
        region_x=int_in_range(raw.get("region_x"), 0, 0, 100000),
        region_y=int_in_range(raw.get("region_y"), 0, 0, 100000),
        region_width=int_in_range(raw.get("region_width"), 0, 0, 100000),
        region_height=int_in_range(raw.get("region_height"), 0, 0, 100000),
        window_title=str(raw.get("window_title", "VRChat")),
    )


def mapping(value: Any) -> dict[str, Any]:
    return value if isinstance(value, dict) else {}


def int_in_range(value: Any, default: int, minimum: int, maximum: int) -> int:
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        return default
    return min(maximum, max(minimum, parsed))


def float_in_range(
    value: Any, default: float, minimum: float, maximum: float
) -> float:
    try:
        parsed = float(value)
    except (TypeError, ValueError):
        return default
    return min(maximum, max(minimum, parsed))
