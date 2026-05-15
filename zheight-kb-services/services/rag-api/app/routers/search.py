"""
/search — knowledge base search endpoint.
Returns similar projects without generation — useful for the AutoCAD plugin
"show me similar projects" sidebar and for architects building intuition.
"""
from __future__ import annotations
import structlog
from fastapi import APIRouter
from pydantic import BaseModel

from ..core.retriever import retrieve

log = structlog.get_logger()
router = APIRouter(prefix="/search", tags=["search"])


class SearchRequest(BaseModel):
    prompt: str
    project_category: str | None = None
    domain_tags: list[str] = []
    min_area_sqm: float | None = None
    max_area_sqm: float | None = None
    spatial_weight: float = 0.5
    site_weight: float = 0.3
    intent_weight: float = 0.2
    top_k: int = 10


@router.post("")
async def search(req: SearchRequest):
    log.info("search_request", prompt=req.prompt[:80], category=req.project_category)
    result = await retrieve(
        prompt=req.prompt,
        project_category=req.project_category,
        domain_tags=req.domain_tags or None,
        min_area_sqm=req.min_area_sqm,
        max_area_sqm=req.max_area_sqm,
        spatial_weight=req.spatial_weight,
        site_weight=req.site_weight,
        intent_weight=req.intent_weight,
        top_k=req.top_k,
    )
    return result
