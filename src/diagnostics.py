"""Bounded local diagnostics containing operational metadata only."""

from __future__ import annotations

from contextvars import ContextVar, Token
from datetime import datetime, timezone
import json
import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path
from threading import Lock


_correlation: ContextVar[tuple[str, str | None, int | None] | None] = ContextVar(
    "msguide_diagnostic_correlation", default=None
)
_logger = logging.getLogger("msguide.diagnostics")
_logger.propagate = False
_lock = Lock()


def configure(path: str | None) -> None:
    with _lock:
        for handler in list(_logger.handlers):
            handler.close()
            _logger.removeHandler(handler)
        if not path:
            return
        destination = Path(path).expanduser().resolve()
        destination.parent.mkdir(parents=True, exist_ok=True)
        handler = RotatingFileHandler(
            destination,
            maxBytes=1_000_000,
            backupCount=2,
            encoding="utf-8",
        )
        handler.setFormatter(logging.Formatter("%(message)s"))
        _logger.addHandler(handler)
        _logger.setLevel(logging.INFO)


def bind(correlation_id: str, task_id: str | None = None, step: int | None = None) -> Token:
    return _correlation.set((correlation_id, task_id, step))


def reset(token: Token) -> None:
    _correlation.reset(token)


def record(event: str, **fields: str | int | float | bool | None) -> None:
    if not _logger.handlers:
        return
    payload = {
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "event": event,
    }
    context = _correlation.get()
    if context is not None:
        payload["correlationId"] = context[0]
        if context[1] is not None:
            payload["taskId"], payload["step"] = context[1], context[2]
    payload.update(fields)
    _logger.info(json.dumps(payload, separators=(",", ":"), ensure_ascii=True))
