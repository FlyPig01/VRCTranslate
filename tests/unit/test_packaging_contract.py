from pathlib import Path
import tomllib
from urllib.parse import quote


ROOT = Path(__file__).parents[2]
REPORTS = (
    "多语言翻译质量测试报告.md",
    "多语言全链路质量与性能测试报告.md",
)


def test_portable_bundle_excludes_developer_benchmark_reports() -> None:
    spec = (ROOT / "VRCTranslate.spec").read_text(encoding="utf-8")

    assert 'ROOT / "README.md"' in spec
    assert 'ROOT / "使用说明.md"' in spec
    assert 'ROOT / "THIRD_PARTY_NOTICES.md"' in spec
    for report in REPORTS:
        assert report not in spec


def test_readme_links_to_versioned_online_benchmark_reports() -> None:
    metadata = tomllib.loads((ROOT / "pyproject.toml").read_text(encoding="utf-8"))
    version = metadata["project"]["version"]
    readme = (ROOT / "README.md").read_text(encoding="utf-8")

    for report in REPORTS:
        expected = (
            "https://github.com/FlyPig01/VRCTranslate/"
            f"blob/v{version}/{quote(report)}"
        )
        assert expected in readme
