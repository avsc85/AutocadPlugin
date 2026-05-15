"""
RAG API — production main app.
Phase 3: API versioning under /v1/, orchestrate + feedback endpoints,
request ID propagation, backward-compat routes preserved.

Auth uses RAG_API_KEY env var (secret: rag-api-key in Secret Manager).
"""
from __future__ import annotations
import os, sys, uuid, time
import structlog
from fastapi import FastAPI, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

sys.path.insert(0, "/app")
from shared.db.client import get_db

from .routers import generate, search, upload
from .routers.orchestrate import router as orchestrate_router
from .routers.feedback    import router as feedback_router

log = structlog.get_logger()

API_KEY = os.environ.get("RAG_API_KEY", "")

# E-02: Redis is optional infrastructure — cache state to avoid hammering on startup
_redis_available: bool | None = None   # None = not yet checked
_redis_last_check: float      = 0.0    # epoch seconds of last probe
_REDIS_RECHECK_INTERVAL       = 60.0   # re-probe at most once per minute


async def _check_redis() -> bool:
    global _redis_available, _redis_last_check
    now = time.monotonic()
    if _redis_available is not None and (now - _redis_last_check) < _REDIS_RECHECK_INTERVAL:
        return _redis_available
    try:
        import redis.asyncio as aioredis
        from redis.asyncio.connection import SSLConnection
        pool = aioredis.ConnectionPool(
            connection_class=SSLConnection,
            host=os.environ.get("REDIS_HOST", ""),
            port=int(os.environ.get("REDIS_PORT", 6379)),
            password=os.environ.get("REDIS_AUTH", ""),
            ssl_cert_reqs="none",
            ssl_check_hostname=False,
            socket_connect_timeout=3,
        )
        r = aioredis.Redis(connection_pool=pool)
        await r.ping()
        await r.aclose()
        if _redis_available is not True:
            log.info("redis_available")
        _redis_available = True
    except Exception as exc:
        if _redis_available is not False:
            log.warning("redis_unavailable", error=str(exc)[:80])
        _redis_available = False
    _redis_last_check = now
    return _redis_available

app = FastAPI(
    title="zHeight Architectural Intelligence API",
    version="3.1.0",
    description="AI-powered architectural design knowledge base and generation API.",
    docs_url="/v1/docs",
    openapi_url="/v1/openapi.json",
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=os.environ.get("CORS_ORIGINS", "*").split(","),
    allow_credentials=True,
    allow_methods=["GET", "POST"],
    allow_headers=["*"],
)


@app.middleware("http")
async def auth_and_request_id(request: Request, call_next):
    # Attach request ID for distributed tracing
    request_id = request.headers.get("X-Request-ID", str(uuid.uuid4()))
    structlog.contextvars.bind_contextvars(request_id=request_id)

    path = request.url.path
    public_paths = ("/health", "/v1/health", "/", "/docs", "/redoc",
                    "/openapi.json", "/v1/docs", "/v1/openapi.json")
    if path not in public_paths:
        if API_KEY:
            key = request.headers.get("X-API-Key", "")
            if key != API_KEY:
                return JSONResponse(
                    status_code=401,
                    content={"detail": "Invalid or missing API key"},
                )

    response = await call_next(request)
    response.headers["X-Request-ID"] = request_id
    return response


# ── Health (no auth, both paths) ──────────────────────────────────────────────
@app.get("/health")
@app.get("/v1/health")
async def health():
    from sqlalchemy import text

    checks = {"service": "rag-api", "status": "ok", "version": "3.1.0"}

    try:
        async with get_db() as session:
            row = await session.execute(
                text("SELECT COUNT(*) FROM projects WHERE approved = TRUE"))
            checks["approved_projects"] = row.scalar()
        checks["db"] = "ok"
    except Exception as exc:
        checks["db"] = f"error: {str(exc)[:80]}"
        checks["status"] = "degraded"

    checks["redis"] = "ok" if await _check_redis() else "degraded"

    return checks


# ── v1 versioned routes ────────────────────────────────────────────────────────
app.include_router(orchestrate_router, prefix="/v1")
app.include_router(feedback_router,    prefix="/v1")
app.include_router(search.router,      prefix="/v1")
app.include_router(upload.router,      prefix="/v1")

# ── Legacy unversioned routes (backward compat for existing integrations) ──────
app.include_router(generate.router)
app.include_router(search.router)
app.include_router(upload.router)
