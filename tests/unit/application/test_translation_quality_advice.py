from vrctranslate.application.dto import TranslationProfile
from vrctranslate.application.translation_quality import translation_quality_advice


def test_quality_advice_never_changes_profile_and_marks_current_candidate() -> None:
    profile = TranslationProfile(
        id="aliyun-professional",
        provider="aliyun",
        options={"aliyun_api": "professional"},
    )

    advice = translation_quality_advice(profile, "zh-CN", "es")

    assert advice.state == "candidate"
    assert advice.candidate_provider == "aliyun"
    assert advice.candidate_variant == "professional"
    assert profile.id == "aliyun-professional"


def test_quality_advice_distinguishes_aliyun_api_variants() -> None:
    profile = TranslationProfile(
        provider="aliyun",
        options={"aliyun_api": "general"},
    )

    advice = translation_quality_advice(profile, "zh-CN", "ja")

    assert advice.state == "alternative"
    assert advice.candidate_variant == "professional"


def test_google_free_is_always_experimental() -> None:
    advice = translation_quality_advice(
        TranslationProfile(provider="google_free"),
        "en",
        "zh-CN",
    )

    assert advice.state == "experimental"


def test_auto_source_has_no_fixed_direction_recommendation() -> None:
    advice = translation_quality_advice(
        TranslationProfile(provider="tencent"),
        "auto",
        "zh-CN",
    )

    assert advice.state == "none"
