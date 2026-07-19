from __future__ import annotations


PROVIDERS = [
    ("provider.test", "test"),
    ("provider.deepl", "deepl"),
    ("provider.google_cloud", "google_cloud"),
    ("provider.google_free", "google_free"),
    ("provider.aliyun", "aliyun"),
    ("provider.tencent", "tencent"),
    ("provider.openai", "openai_compatible"),
    ("provider.multimodal", "multimodal_openai"),
]

MACHINE_TRANSLATION_PROVIDERS = [
    ("provider.deepl", "deepl"),
    ("provider.google_cloud", "google_cloud"),
    ("provider.google_free", "google_free"),
    ("provider.aliyun", "aliyun"),
    ("provider.tencent", "tencent"),
]

ALIYUN_REGIONS = [
    ("aliyun.region.cn_hangzhou", "cn-hangzhou"),
    ("aliyun.region.cn_shanghai", "cn-shanghai"),
    ("aliyun.region.cn_beijing", "cn-beijing"),
    ("aliyun.region.cn_shenzhen", "cn-shenzhen"),
    ("aliyun.region.cn_zhangjiakou", "cn-zhangjiakou"),
    ("aliyun.region.cn_huhehaote", "cn-huhehaote"),
    ("aliyun.region.cn_chengdu", "cn-chengdu"),
    ("aliyun.region.cn_hongkong", "cn-hongkong"),
    ("aliyun.region.ap_southeast_1", "ap-southeast-1"),
    ("aliyun.region.ap_northeast_1", "ap-northeast-1"),
    ("aliyun.region.us_west_1", "us-west-1"),
    ("aliyun.region.us_east_1", "us-east-1"),
    ("aliyun.region.eu_central_1", "eu-central-1"),
    ("aliyun.region.eu_west_1", "eu-west-1"),
]


def aliyun_endpoint_for_region(region: str) -> str:
    if region == "cn-hangzhou":
        return "mt.cn-hangzhou.aliyuncs.com"
    if region == "ap-southeast-1":
        return "mt.ap-southeast-1.aliyuncs.com"
    if region in {value for _key, value in ALIYUN_REGIONS}:
        return "mt.aliyuncs.com"
    return ""

MODEL_VENDORS = [
    ("model_vendor.deepseek", "deepseek", "https://api.deepseek.com/v1"),
    (
        "model_vendor.qwen",
        "qwen",
        "https://dashscope.aliyuncs.com/compatible-mode/v1",
    ),
    (
        "model_vendor.doubao",
        "doubao",
        "https://ark.cn-beijing.volces.com/api/v3",
    ),
    ("model_vendor.minimax", "minimax", ""),
    ("model_vendor.kimi", "kimi", "https://api.moonshot.cn/v1"),
    (
        "model_vendor.zhipu",
        "zhipu",
        "https://open.bigmodel.cn/api/paas/v4",
    ),
    ("model_vendor.openai", "openai", "https://api.openai.com/v1"),
]

LARGE_MODEL_PROVIDERS = frozenset({"openai_compatible", "multimodal_openai"})


def model_vendor_from_profile(base_url: str, name: str = "") -> str:
    value = f"{base_url} {name}".casefold()
    markers = (
        ("deepseek", ("deepseek",)),
        ("qwen", ("dashscope", "aliyuncs", "qwen", "通义", "千问")),
        ("doubao", ("volces", "volcengine", "豆包", "doubao")),
        ("minimax", ("minimax",)),
        ("kimi", ("moonshot", "kimi")),
        ("zhipu", ("bigmodel", "zhipu", "智谱", "glm")),
        ("openai", ("openai.com",)),
    )
    for vendor, aliases in markers:
        if any(alias in value for alias in aliases):
            return vendor
    return "custom"

OVERFLOW_POLICIES = [
    ("overflow.split", "split"),
    ("overflow.truncate", "truncate"),
    ("overflow.reject", "reject"),
]
