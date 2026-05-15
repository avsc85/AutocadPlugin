"""
Async SQLAlchemy client for Cloud SQL PostgreSQL.
Uses asyncpg driver with connection pooling tuned for Cloud Run concurrency.
Supports both the Cloud SQL Auth Proxy socket path (production) and direct TCP (local dev).
"""
from __future__ import annotations
import os
from contextlib import asynccontextmanager
from urllib.parse import quote
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

_engine = None
_session_factory = None


def _build_url() -> str:
    user = os.environ["DB_USER"]
    password = quote(os.environ["DB_PASSWORD"], safe="")
    db = os.environ["DB_NAME"]
    conn_name = os.environ.get("CLOUD_SQL_CONNECTION_NAME", "")

    if conn_name:
        socket_dir = f"/cloudsql/{conn_name}"
        return (
            f"postgresql+asyncpg://{user}:{password}@/{db}"
            f"?host={socket_dir}"
        )
    host = os.environ.get("DB_HOST", "127.0.0.1")
    port = os.environ.get("DB_PORT", "5433")
    return f"postgresql+asyncpg://{user}:{password}@{host}:{port}/{db}"


def _get_engine():
    global _engine, _session_factory
    if _engine is None:
        url = _build_url()
        is_prod = bool(os.environ.get("CLOUD_SQL_CONNECTION_NAME"))
        engine_kwargs = {"echo": False}
        if is_prod:
            # NullPool in Cloud Run: each request owns its connection,
            # avoids socket starvation under concurrent cold starts.
            engine_kwargs["poolclass"] = NullPool
        else:
            # Local dev: use a small connection pool
            engine_kwargs.update({"pool_size": 5, "max_overflow": 10,
                                   "pool_timeout": 30, "pool_recycle": 1800})

        _engine = create_async_engine(url, **engine_kwargs)
        _session_factory = async_sessionmaker(
            _engine, class_=AsyncSession, expire_on_commit=False
        )
    return _engine, _session_factory


@asynccontextmanager
async def get_db():
    """Usage: async with get_db() as session: ..."""
    _, factory = _get_engine()
    async with factory() as session:
        async with session.begin():
            yield session


@asynccontextmanager
async def get_read_db():
    """Routes to the read replica for SELECT-heavy retrieval queries."""
    user = os.environ["DB_USER"]
    password = os.environ["DB_PASSWORD"]
    db = os.environ["DB_NAME"]
    replica_host = os.environ.get("DB_REPLICA_HOST", "")

    if not replica_host:
        # Fall back to primary if no replica configured
        async with get_db() as session:
            yield session
        return

    url = f"postgresql+asyncpg://{user}:{quote(password, safe='')}@{replica_host}/{db}"
    engine = create_async_engine(url, poolclass=NullPool)
    factory = async_sessionmaker(engine, class_=AsyncSession, expire_on_commit=False)
    async with factory() as session:
        yield session
    await engine.dispose()
