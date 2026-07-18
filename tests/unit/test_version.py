from pathlib import Path
import tomllib

from vrctranslate import __version__


def test_runtime_version_matches_project_metadata() -> None:
    project = Path(__file__).parents[2] / "pyproject.toml"
    metadata = tomllib.loads(project.read_text(encoding="utf-8"))

    assert __version__ == metadata["project"]["version"]
