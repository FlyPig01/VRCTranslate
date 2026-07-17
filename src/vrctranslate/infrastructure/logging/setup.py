from __future__ import annotations

import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path


def configure_logging(base_directory: Path) -> logging.Logger:
    log_directory = base_directory / "logs"
    log_directory.mkdir(parents=True, exist_ok=True)
    logger = logging.getLogger("vrctranslate")
    logger.setLevel(logging.INFO)
    if logger.handlers:
        return logger
    handler = RotatingFileHandler(
        log_directory / "app.log",
        maxBytes=512_000,
        backupCount=3,
        encoding="utf-8",
    )
    handler.setFormatter(
        logging.Formatter("%(asctime)s %(levelname)s %(name)s %(message)s")
    )
    logger.addHandler(handler)
    return logger


def clear_application_logs(logger: logging.Logger) -> None:
    """Clear active rotating logs without deleting an open Windows file."""
    for handler in logger.handlers:
        if not isinstance(handler, RotatingFileHandler) or handler.stream is None:
            continue
        handler.acquire()
        try:
            handler.stream.seek(0)
            handler.stream.truncate(0)
            handler.stream.flush()
        finally:
            handler.release()
        active_path = Path(handler.baseFilename)
        for backup in active_path.parent.glob(f"{active_path.name}.*"):
            if backup.is_file():
                backup.unlink(missing_ok=True)
