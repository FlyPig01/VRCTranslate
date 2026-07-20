from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from time import monotonic
from urllib.parse import urlsplit

import httpx


@dataclass(frozen=True, slots=True)
class DownloadSourceProbe:
    url: str
    bytes_received: int
    elapsed_seconds: float

    @property
    def bytes_per_second(self) -> float:
        return self.bytes_received / max(self.elapsed_seconds, 0.001)


class AdaptiveDownloadSourceSelector:
    """Rank equivalent download sources using a small real transfer."""

    def __init__(
        self,
        *,
        probe_bytes: int = 1024 * 1024,
        probe_timeout_seconds: float = 8.0,
    ) -> None:
        self._probe_bytes = max(64 * 1024, int(probe_bytes))
        self._probe_timeout_seconds = max(1.0, float(probe_timeout_seconds))
        self._origin_preferences: dict[tuple[str, ...], tuple[str, ...]] = {}

    def order(
        self,
        client: httpx.Client,
        urls: tuple[str, ...],
    ) -> tuple[str, ...]:
        candidates = tuple(dict.fromkeys(url for url in urls if url))
        if len(candidates) <= 1:
            return candidates

        origins = tuple(self._origin(url) for url in candidates)
        preference_key = tuple(sorted(set(origins)))
        cached = self._origin_preferences.get(preference_key)
        if cached is not None:
            positions = {origin: index for index, origin in enumerate(cached)}
            return tuple(
                url
                for _index, url in sorted(
                    enumerate(candidates),
                    key=lambda item: (
                        positions.get(self._origin(item[1]), len(positions)),
                        item[0],
                    ),
                )
            )

        probes = self._probe_all(client, candidates)
        declared_positions = {url: index for index, url in enumerate(candidates)}
        successful = sorted(
            probes,
            key=lambda probe: (
                -probe.bytes_per_second,
                declared_positions[probe.url],
            ),
        )
        successful_urls = {probe.url for probe in successful}
        ordered = tuple(probe.url for probe in successful) + tuple(
            url for url in candidates if url not in successful_urls
        )
        self._origin_preferences[preference_key] = tuple(
            dict.fromkeys(self._origin(url) for url in ordered)
        )
        return ordered

    def _probe_all(
        self,
        client: httpx.Client,
        urls: tuple[str, ...],
    ) -> tuple[DownloadSourceProbe, ...]:
        results: list[DownloadSourceProbe] = []
        with ThreadPoolExecutor(
            max_workers=min(4, len(urls)),
            thread_name_prefix="download-source-probe",
        ) as executor:
            futures = {
                executor.submit(self._probe, client, url): url for url in urls
            }
            for future in as_completed(futures):
                try:
                    probe = future.result()
                except Exception:
                    # A probe must never prevent the normal fallback download.
                    probe = None
                if probe is not None:
                    results.append(probe)
        return tuple(results)

    def _probe(
        self,
        client: httpx.Client,
        url: str,
    ) -> DownloadSourceProbe | None:
        started = monotonic()
        received = 0
        timeout = httpx.Timeout(
            self._probe_timeout_seconds,
            connect=min(4.0, self._probe_timeout_seconds),
        )
        headers = {"Range": f"bytes=0-{self._probe_bytes - 1}"}
        try:
            with client.stream(
                "GET",
                url,
                headers=headers,
                timeout=timeout,
            ) as response:
                response.raise_for_status()
                for chunk in response.iter_bytes(64 * 1024):
                    if not chunk:
                        continue
                    received += min(len(chunk), self._probe_bytes - received)
                    if (
                        received >= self._probe_bytes
                        or monotonic() - started >= self._probe_timeout_seconds
                    ):
                        break
        except (httpx.HTTPError, OSError):
            return None
        if received <= 0:
            return None
        return DownloadSourceProbe(
            url=url,
            bytes_received=received,
            elapsed_seconds=monotonic() - started,
        )

    @staticmethod
    def _origin(url: str) -> str:
        parsed = urlsplit(url)
        if parsed.scheme and parsed.netloc:
            return f"{parsed.scheme.casefold()}://{parsed.netloc.casefold()}"
        return url
