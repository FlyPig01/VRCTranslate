import logging
from logging.handlers import RotatingFileHandler

from vrctranslate.infrastructure.logging.setup import clear_application_logs


def test_clear_logs_truncates_active_file_and_removes_backups(tmp_path) -> None:
    path = tmp_path / "app.log"
    logger = logging.Logger("clear-test")
    handler = RotatingFileHandler(path, maxBytes=10, backupCount=3, encoding="utf-8")
    logger.addHandler(handler)
    logger.setLevel(logging.INFO)
    logger.info("active content")
    backup = tmp_path / "app.log.1"
    backup.write_text("old content", encoding="utf-8")
    clear_application_logs(logger)
    assert path.read_text(encoding="utf-8") == ""
    assert not backup.exists()
    handler.close()
