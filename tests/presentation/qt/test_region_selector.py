from PySide6.QtCore import QPoint, QRect
import pytest

from vrctranslate.domain.ocr import WindowInfo
from vrctranslate.presentation.qt.dialogs.region_selector import RegionSelector


def test_selector_draws_a_high_contrast_custom_crosshair(qtbot) -> None:
    selector = RegionSelector(WindowInfo(1, "VRChat", 0, 0, 320, 180))
    qtbot.addWidget(selector)
    selector.resize(320, 180)
    selector.show()
    qtbot.waitExposed(selector)
    centre = QPoint(170, 96)
    selector._cursor_position = centre
    selector.update()
    qtbot.wait(20)

    color = selector.grab().toImage().pixelColor(centre)

    assert color.alpha() == 255
    assert color.green() >= 180
    assert color.blue() >= 220


def test_selector_maps_qt_logical_coordinates_to_win32_client_space(qtbot) -> None:
    window = WindowInfo(1, "VRChat", 0, 0, 1500, 900)
    selector = RegionSelector(window)
    qtbot.addWidget(selector)
    # Simulate Qt presenting the selector at 150% DPI in logical coordinates.
    selector.resize(1000, 600)

    mapped = selector.client_rect_from_widget(QRect(100, 50, 400, 200))

    assert mapped == QRect(150, 75, 600, 300)


def test_selector_mapping_is_identity_without_dpi_scaling(qtbot) -> None:
    window = WindowInfo(1, "VRChat", 0, 0, 1280, 720)
    selector = RegionSelector(window)
    qtbot.addWidget(selector)
    selector.resize(1280, 720)

    assert selector.client_rect_from_widget(QRect(20, 30, 500, 200)) == QRect(
        20, 30, 500, 200
    )


@pytest.mark.parametrize("scale", [1.0, 1.25, 1.5, 1.75, 2.0])
def test_selector_mapping_supports_common_windows_scale_factors(
    qtbot, scale: float
) -> None:
    client_width, client_height = 4200, 2100
    logical_width = round(client_width / scale)
    logical_height = round(client_height / scale)
    window = WindowInfo(
        1, "VRChat", -logical_width, 0, client_width, client_height
    )
    selector = RegionSelector(window)
    qtbot.addWidget(selector)
    selector.resize(logical_width, logical_height)

    logical = QRect(
        logical_width // 10,
        logical_height // 10,
        logical_width // 2,
        logical_height // 2,
    )
    mapped = selector.client_rect_from_widget(logical)

    assert abs(mapped.x() - 420) <= 1
    assert abs(mapped.y() - 210) <= 1
    assert abs(mapped.width() - 2100) <= 1
    assert abs(mapped.height() - 1050) <= 1
