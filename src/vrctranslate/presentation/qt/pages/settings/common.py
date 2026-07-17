from __future__ import annotations

from PySide6.QtWidgets import QFrame, QLabel, QScrollArea, QVBoxLayout, QWidget


def card(title: str) -> tuple[QFrame, QVBoxLayout]:
    frame = QFrame()
    frame.setObjectName("card")
    layout = QVBoxLayout(frame)
    layout.setContentsMargins(20, 18, 20, 18)
    layout.setSpacing(12)
    label = QLabel(title)
    label.setObjectName("cardTitle")
    layout.addWidget(label)
    return frame, layout


def scroll_page() -> tuple[QScrollArea, QWidget, QVBoxLayout]:
    scroll = QScrollArea()
    scroll.setObjectName("settingsContentScroll")
    scroll.setWidgetResizable(True)
    scroll.setFrameShape(QFrame.Shape.NoFrame)
    content = QWidget()
    layout = QVBoxLayout(content)
    layout.setContentsMargins(2, 14, 8, 14)
    layout.setSpacing(14)
    scroll.setWidget(content)
    return scroll, content, layout
