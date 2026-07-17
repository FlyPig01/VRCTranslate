from __future__ import annotations

from vrctranslate.application.ports.local_models import LocalTranslationModel
from vrctranslate.application.use_cases.manage_local_models import ManageLocalModels
from vrctranslate.domain.errors import TranslationError


class ModelManagerFake:
    component_available = True
    model_directory = "memory://models"

    def __init__(self, available=None, failure: TranslationError | None = None):
        self.available = available or []
        self.failure = failure

    def installed_models(self):
        return [LocalTranslationModel("en", "zh")]

    def available_models(self, refresh=False):
        if self.failure:
            raise self.failure
        return self.available

    def disk_usage(self):
        return 1024

    def install(self, source_language, target_language, package_version=""):
        return None

    def remove(self, source_language, target_language):
        return None


def test_catalog_distinguishes_missing_index_empty_and_ready() -> None:
    missing = ManageLocalModels(
        ModelManagerFake(failure=TranslationError("index_missing", "no index"))
    ).load_catalog()
    assert missing.state == "index_missing"

    empty = ManageLocalModels(ModelManagerFake()).load_catalog()
    assert empty.state == "empty"

    ready = ManageLocalModels(
        ModelManagerFake([LocalTranslationModel("ja", "zh", "1.0")])
    ).load_catalog()
    assert ready.state == "ready"
    assert ready.available[0].package_version == "1.0"


def test_catalog_distinguishes_missing_component() -> None:
    manager = ModelManagerFake()
    manager.component_available = False
    catalog = ManageLocalModels(manager).load_catalog()
    assert catalog.state == "component_missing"


def test_refresh_failure_keeps_last_cached_catalog() -> None:
    model = LocalTranslationModel("en", "ja", "1.2")

    class RefreshFailingManager(ModelManagerFake):
        def available_models(self, refresh=False):
            if refresh:
                raise TranslationError("network", "refresh failed")
            return [model]

    catalog = ManageLocalModels(RefreshFailingManager()).load_catalog(refresh=True)
    assert catalog.state == "error"
    assert catalog.available == (model,)
