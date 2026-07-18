from __future__ import annotations

from collections.abc import Callable
from threading import Lock

import httpx

from vrctranslate.application.dto import TranslationProfile


_REASONING_KEYS = ("enable_thinking", "reasoning_effort")


def generation_parameters(
    profile: TranslationProfile,
    *,
    default_max_tokens: int,
) -> dict[str, object]:
    """Return conservative generation controls shared by text and vision."""

    options = profile.options
    try:
        temperature = float(options.get("temperature", 0))
    except (TypeError, ValueError):
        temperature = 0.0
    temperature = min(2.0, max(0.0, temperature))
    try:
        max_tokens = int(options.get("max_output_tokens", default_max_tokens))
    except (TypeError, ValueError):
        max_tokens = default_max_tokens
    max_tokens = min(8192, max(32, max_tokens))
    parameters: dict[str, object] = {
        "temperature": temperature,
        "max_tokens": max_tokens,
    }

    reasoning_mode = str(options.get("reasoning_mode", "off"))
    if reasoning_mode != "off":
        return parameters
    model = profile.model.casefold()
    reasoning_api = str(options.get("reasoning_api", "auto"))
    if reasoning_api == "qwen" or (
        reasoning_api == "auto" and "qwen" in model
    ):
        parameters["enable_thinking"] = False
    elif reasoning_api == "openai" or (
        reasoning_api == "auto"
        and model.startswith(("o1", "o3", "o4", "gpt-5"))
    ):
        parameters["reasoning_effort"] = "minimal"
    return parameters


def message_text(content: object) -> str:
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return "".join(
            str(item.get("text", ""))
            for item in content
            if isinstance(item, dict) and item.get("type") in {None, "text"}
        )
    raise ValueError("message content is not text")


class CompatibleRequestSession:
    """Reuse HTTP connections and gracefully drop unsupported tuning fields."""

    def __init__(
        self,
        client_factory: Callable[[float], httpx.Client] | None = None,
    ) -> None:
        self._client_factory = client_factory or (
            lambda timeout: httpx.Client(timeout=timeout)
        )
        self._client: httpx.Client | None = None
        self._lock = Lock()
        self._unsupported: set[tuple[str, str, str]] = set()

    def post(
        self,
        endpoint: str,
        *,
        headers: dict[str, str],
        payload: dict[str, object],
        timeout: float,
    ) -> httpx.Response:
        current = dict(payload)
        model = str(current.get("model", ""))
        with self._lock:
            for parameter in _REASONING_KEYS:
                if (endpoint, model, parameter) in self._unsupported:
                    current.pop(parameter, None)
        response = self._http_client(timeout).post(
            endpoint,
            headers=headers,
            json=current,
            timeout=timeout,
        )
        rejected = self._rejected_reasoning_parameter(response, current)
        if rejected is None:
            return response
        with self._lock:
            self._unsupported.add((endpoint, model, rejected))
        current.pop(rejected, None)
        return self._http_client(timeout).post(
            endpoint,
            headers=headers,
            json=current,
            timeout=timeout,
        )

    def close(self) -> None:
        with self._lock:
            client, self._client = self._client, None
        if client is not None:
            client.close()

    def _http_client(self, timeout: float) -> httpx.Client:
        with self._lock:
            if self._client is None:
                self._client = self._client_factory(timeout)
            return self._client

    @staticmethod
    def _rejected_reasoning_parameter(
        response: httpx.Response,
        payload: dict[str, object],
    ) -> str | None:
        if response.status_code not in {400, 422}:
            return None
        try:
            detail = response.text.casefold()
        except (AttributeError, RuntimeError):
            return None
        rejection_markers = (
            "unknown",
            "unsupported",
            "unrecognized",
            "extra",
            "not permitted",
            "未知",
            "不支持",
        )
        if not any(marker in detail for marker in rejection_markers):
            return None
        return next(
            (
                parameter
                for parameter in _REASONING_KEYS
                if parameter in payload and parameter.casefold() in detail
            ),
            None,
        )
