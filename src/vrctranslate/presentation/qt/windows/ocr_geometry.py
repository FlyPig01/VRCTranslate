from __future__ import annotations

from PySide6.QtCore import QPoint, QRect
from PySide6.QtGui import QGuiApplication

from vrctranslate.domain.ocr import CaptureRegion, WindowInfo


def logical_rect_for_region(target: WindowInfo, region: CaptureRegion) -> QRect:
    x, y, ratio = logical_point(target.left + region.x, target.top + region.y)
    return QRect(
        x,
        y,
        max(1, round(region.width / ratio)),
        max(1, round(region.height / ratio)),
    )


def logical_point(physical_x: int, physical_y: int) -> tuple[int, int, float]:
    for screen in QGuiApplication.screens():
        geo = screen.geometry()
        ratio = screen.devicePixelRatio()
        physical_left = round(geo.x() * ratio)
        physical_top = round(geo.y() * ratio)
        physical_width = round(geo.width() * ratio)
        physical_height = round(geo.height() * ratio)
        if (
            physical_left <= physical_x < physical_left + physical_width
            and physical_top <= physical_y < physical_top + physical_height
        ):
            return (
                geo.x() + round((physical_x - physical_left) / ratio),
                geo.y() + round((physical_y - physical_top) / ratio),
                ratio,
            )
    return physical_x, physical_y, 1.0


def physical_point(logical: QPoint) -> tuple[int, int, float]:
    screen = QGuiApplication.screenAt(logical) or QGuiApplication.primaryScreen()
    if screen is None:
        return logical.x(), logical.y(), 1.0
    geo = screen.geometry()
    ratio = screen.devicePixelRatio()
    physical_left = round(geo.x() * ratio)
    physical_top = round(geo.y() * ratio)
    return (
        physical_left + round((logical.x() - geo.x()) * ratio),
        physical_top + round((logical.y() - geo.y()) * ratio),
        ratio,
    )
