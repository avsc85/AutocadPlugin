"""Exponential backoff retry decorator for external API calls (Vertex AI, GCS)."""
from __future__ import annotations
import asyncio, functools, random, structlog

log = structlog.get_logger()


def async_retry(max_attempts: int = 3, base_delay: float = 1.0, max_delay: float = 30.0):
    def decorator(fn):
        @functools.wraps(fn)
        async def wrapper(*args, **kwargs):
            for attempt in range(1, max_attempts + 1):
                try:
                    return await fn(*args, **kwargs)
                except Exception as exc:
                    if attempt == max_attempts:
                        raise
                    delay = min(base_delay * (2 ** (attempt - 1)) + random.uniform(0, 1), max_delay)
                    log.warning("retry_backoff", fn=fn.__name__, attempt=attempt,
                                delay=round(delay, 2), error=str(exc)[:120])
                    await asyncio.sleep(delay)
        return wrapper
    return decorator
