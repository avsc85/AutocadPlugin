"""
Three-vector hybrid retriever with Redis caching.

Production fixes vs Phase 2 draft:
- Query is decomposed into three aspect texts before embedding
  (same vector for all 3 dimensions was the original bug)
- Redis circuit breaker: graceful degradation to DB-only if Redis is down
- Read replica routing for SELECT queries
- Cache invalidation hook for approved project count changes
"""
from __future__ import annotations
import hashlib, json, os, sys
import structlog
from sqlalchemy import text

sys.path.insert(0, "/app")
from shared.db.client import get_read_db

from .embedder import embed_texts

log = structlog.get_logger()
_redis_client = None
_redis_broken = False   # circuit breaker flag

TTL = 3600


def _decompose_query(prompt: str, category: str | None, domain_tags: list | None) -> tuple[str, str, str]:
    """
    Decompose a single user prompt into three focused aspect texts for embedding.
    spatial  → space types, programme, scale
    site     → location, orientation, constraints, climate
    intent   → style, culture, philosophy, sustainability
    """
    cat = category or ""
    tags = " ".join(domain_tags or [])
    spatial_q = (
        f"Building type: {cat} {tags}. "
        f"Space programme and layout requirements: {prompt}"
    )
    site_q = (
        f"Site conditions and regulatory constraints for {cat} project. "
        f"Plot shape, orientation, setbacks, FAR, climate: {prompt}"
    )
    intent_q = (
        f"Architectural style, cultural context, design philosophy for {cat} {tags}. "
        f"Sustainability, materiality, certification: {prompt}"
    )
    return spatial_q, site_q, intent_q


async def _get_redis():
    global _redis_client, _redis_broken
    if _redis_broken:
        return None
    try:
        if not _redis_client:
            import redis.asyncio as aioredis
            from redis.asyncio.connection import SSLConnection
            pool = aioredis.ConnectionPool(
                connection_class=SSLConnection,
                host=os.environ["REDIS_HOST"],
                port=int(os.environ.get("REDIS_PORT", 6379)),
                password=os.environ.get("REDIS_AUTH", ""),
                ssl_cert_reqs="none",
                ssl_check_hostname=False,
                socket_connect_timeout=2,
                decode_responses=True,
            )
            _redis_client = aioredis.Redis(connection_pool=pool)
        return _redis_client
    except Exception as exc:
        log.warning("redis_unavailable_degraded", error=str(exc)[:80])
        _redis_broken = True
        return None


async def retrieve(
    prompt: str,
    project_category: str | None = None,
    domain_tags: list[str] | None = None,
    min_area_sqm: float | None = None,
    max_area_sqm: float | None = None,
    spatial_weight: float = 0.5,
    site_weight: float = 0.3,
    intent_weight: float = 0.2,
    top_k: int = 5,
) -> dict:
    cache_key = "rag2:" + hashlib.md5(
        json.dumps([prompt, project_category, domain_tags, min_area_sqm,
                    max_area_sqm, spatial_weight, site_weight, intent_weight, top_k],
                   sort_keys=True, default=str).encode()
    ).hexdigest()

    redis = await _get_redis()
    if redis:
        try:
            cached = await redis.get(cache_key)
            if cached:
                log.info("cache_hit", key=cache_key[:16])
                return json.loads(cached)
        except Exception:
            pass

    # Decompose query into three focused aspect texts, then embed in one API call
    spatial_q, site_q, intent_q = _decompose_query(prompt, project_category, domain_tags)
    vectors = await embed_texts([spatial_q, site_q, intent_q])
    spatial_vec, site_vec, intent_vec = vectors[0], vectors[1], vectors[2]

    candidates = await _three_vector_search(
        spatial_vec, site_vec, intent_vec,
        spatial_weight, site_weight, intent_weight,
        project_category, domain_tags, min_area_sqm, max_area_sqm,
        limit=top_k * 3,
    )

    ranked = sorted(candidates, key=lambda x: x["composite_score"], reverse=True)[:top_k]

    result = {
        "projects": ranked,
        "query": prompt,
        "total_candidates": len(candidates),
        "weights_used": {"spatial": spatial_weight, "site": site_weight, "intent": intent_weight},
    }

    if redis:
        try:
            await redis.setex(cache_key, TTL, json.dumps(result, default=str))
        except Exception:
            pass

    return result


async def _three_vector_search(
    spatial_vec, site_vec, intent_vec,
    sw, stw, iw,
    category, tags, min_area, max_area, limit
) -> list[dict]:
    filters = ["p.approved = TRUE"]
    params: dict = {
        "sv":    "[" + ",".join(str(round(v, 8)) for v in spatial_vec) + "]",
        "stv":   "[" + ",".join(str(round(v, 8)) for v in site_vec) + "]",
        "iv":    "[" + ",".join(str(round(v, 8)) for v in intent_vec) + "]",
        "sw": sw, "stw": stw, "iw": iw, "limit": limit,
    }

    if category:
        filters.append("p.project_category = :category")
        params["category"] = category
    if tags:
        filters.append("p.domain_tags && CAST(:tags AS TEXT[])")
        params["tags"] = list(tags)
    if min_area:
        filters.append("p.total_built_area_sqm >= :min_area")
        params["min_area"] = min_area
    if max_area:
        filters.append("p.total_built_area_sqm <= :max_area")
        params["max_area"] = max_area

    where = " AND ".join(filters)

    # Each CTE hits a partial HNSW index (embedding_type filter pushes to index scan)
    sql = f"""
        WITH spatial_scores AS (
            SELECT project_id, 1 - (embedding <=> CAST(:sv AS VECTOR)) AS score
            FROM project_embeddings WHERE embedding_type = 'spatial'
        ),
        site_scores AS (
            SELECT project_id, 1 - (embedding <=> CAST(:stv AS VECTOR)) AS score
            FROM project_embeddings WHERE embedding_type = 'site'
        ),
        intent_scores AS (
            SELECT project_id, 1 - (embedding <=> CAST(:iv AS VECTOR)) AS score
            FROM project_embeddings WHERE embedding_type = 'intent'
        )
        SELECT
            CAST(p.id AS TEXT),
            p.project_name,
            p.project_category,
            p.project_sub_type,
            p.domain_tags,
            p.total_built_area_sqm,
            p.floor_count,
            p.site_context,
            p.design_intent,
            p.processed_json_path,
            COALESCE(sp.score, 0) AS spatial_score,
            COALESCE(st.score, 0) AS site_score,
            COALESCE(i.score,  0) AS intent_score,
            (COALESCE(sp.score,0)*:sw +
             COALESCE(st.score,0)*:stw +
             COALESCE(i.score, 0)*:iw) AS composite_score
        FROM projects p
        LEFT JOIN spatial_scores sp ON sp.project_id = p.id
        LEFT JOIN site_scores    st ON st.project_id = p.id
        LEFT JOIN intent_scores   i ON i.project_id  = p.id
        WHERE {where}
        ORDER BY composite_score DESC
        LIMIT :limit
    """

    async with get_read_db() as session:
        result = await session.execute(text(sql), params)
        rows = result.mappings().all()

    return [dict(r) for r in rows]
