"""
Query embedder for the RAG API.
Uses google-genai SDK with Vertex AI backend (text-embedding-004).
"""
from __future__ import annotations
import asyncio, os
from google import genai

import sys; sys.path.insert(0, "/app")
from shared.utils.retry import async_retry

_client = None


def _get_client() -> genai.Client:
    global _client
    if not _client:
        _client = genai.Client(
            vertexai=True,
            api_key=os.environ["GEMINI_API_KEY"],
        )
    return _client


def _sync_embed(texts: list[str]) -> list[list[float]]:
    client = _get_client()
    results = []
    for text in texts:
        r = client.models.embed_content(
            model="text-embedding-004",
            contents=text,
        )
        results.append(r.embeddings[0].values)
    return results


@async_retry(max_attempts=3, base_delay=1.5)
async def embed_texts(texts: list[str]) -> list[list[float]]:
    return await asyncio.to_thread(_sync_embed, texts)
