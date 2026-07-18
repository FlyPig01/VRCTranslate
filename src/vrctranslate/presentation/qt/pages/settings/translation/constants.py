from __future__ import annotations


PROVIDERS = [
    ("provider.test", "test"),
    ("provider.deepl", "deepl"),
    ("provider.google_cloud", "google_cloud"),
    ("provider.google_free", "google_free"),
    ("provider.tencent", "tencent"),
    ("provider.openai", "openai_compatible"),
    ("provider.multimodal", "multimodal_openai"),
]

OVERFLOW_POLICIES = [
    ("overflow.split", "split"),
    ("overflow.truncate", "truncate"),
    ("overflow.reject", "reject"),
]
