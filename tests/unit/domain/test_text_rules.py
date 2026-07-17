from vrctranslate.domain.chatbox import MessageFormat
from vrctranslate.domain.text_rules import (
    TextDeduplicator,
    format_chatbox_message,
    frame_signature_changed,
    split_utf16,
    truncate_utf16,
    utf16_units,
)


def test_utf16_units_counts_non_bmp_as_two_units() -> None:
    assert utf16_units("A中😀") == 4


def test_format_chatbox_message() -> None:
    assert (
        format_chatbox_message(
            "hello", "你好", MessageFormat.ORIGINAL_THEN_TRANSLATION
        )
        == "hello / 你好"
    )


def test_truncate_utf16_does_not_split_emoji() -> None:
    assert truncate_utf16("ab😀c", 4) == "ab😀"


def test_split_utf16_respects_limit() -> None:
    chunks = split_utf16("one two three four five", 8)
    assert "".join(chunks).replace(" ", "") == "onetwothreefourfive"
    assert all(utf16_units(chunk) <= 8 for chunk in chunks)


def test_deduplicator_rejects_near_duplicates_and_expires() -> None:
    deduplicator = TextDeduplicator(window_seconds=5, similarity=0.85)
    assert deduplicator.accept("Hello, world!", now=1)
    assert not deduplicator.accept("hello world", now=2)
    assert not deduplicator.accept("Hello wor1d", now=3)
    assert deduplicator.accept("Hello world", now=7)


def test_frame_signature_change_is_framework_independent() -> None:
    assert frame_signature_changed(None, b"\x00\x00", 2)
    assert not frame_signature_changed(b"\x00\x00", b"\x00\x00", 2)
    assert frame_signature_changed(b"\x00\x00", b"\x0a\x0a", 2)

