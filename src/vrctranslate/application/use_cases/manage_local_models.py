from __future__ import annotations

from dataclasses import dataclass

from vrctranslate.application.ports.local_models import (
    LocalModelManager,
    LocalTranslationModel,
)
from vrctranslate.domain.errors import VrcTranslateError


@dataclass(frozen=True, slots=True)
class LocalModelCatalog:
    state: str
    installed: tuple[LocalTranslationModel, ...] = ()
    available: tuple[LocalTranslationModel, ...] = ()
    disk_usage: int = 0
    message: str = ""


class ManageLocalModels:
    """Expose explicit catalog states instead of treating every failure as empty."""

    def __init__(self, manager: LocalModelManager) -> None:
        self._manager = manager

    def load_catalog(self, refresh: bool = False) -> LocalModelCatalog:
        if not self._manager.component_available:
            return LocalModelCatalog(
                "component_missing",
                message="当前环境未安装 Argos Translate 组件。",
            )
        installed: tuple[LocalTranslationModel, ...] = ()
        usage = 0
        try:
            installed = tuple(self._manager.installed_models())
            usage = self._manager.disk_usage()
            available = tuple(self._manager.available_models(refresh=refresh))
        except VrcTranslateError as exc:
            category = getattr(exc, "category", "error")
            state = "index_missing" if category == "index_missing" else "error"
            cached: tuple[LocalTranslationModel, ...] = ()
            if refresh and state == "error":
                try:
                    cached = tuple(self._manager.available_models(refresh=False))
                except Exception:
                    pass
            return LocalModelCatalog(state, installed, cached, usage, exc.user_message)
        except Exception as exc:
            return LocalModelCatalog(
                "error",
                installed,
                (),
                usage,
                f"Argos 模型目录读取失败：{type(exc).__name__}",
            )
        return LocalModelCatalog(
            "ready" if available else "empty",
            installed,
            available,
            usage,
            "模型索引已更新" if refresh else "已读取便携模型索引",
        )
