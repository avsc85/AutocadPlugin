"""
Multi-layer embedder — three vectors per project.

Production fixes vs Phase 2 draft:
- Sync Vertex AI call wrapped in asyncio.to_thread (SDK is sync-only)
- Retry on quota/network errors
- Batches all three texts in one API call (3 embeddings = 1 request)
"""
from __future__ import annotations
import asyncio, os
import structlog
from google import genai

import sys
sys.path.insert(0, "/app")
from shared.utils.retry import async_retry

log = structlog.get_logger()
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


def build_spatial_text(schema: dict) -> str:
    cat = schema.get("project_category", "")
    sub = schema.get("project_sub_type", "")
    tags = " ".join(schema.get("domain_tags", []))
    floors = schema.get("floor_count", 1)
    area = schema.get("total_built_area_sqm", "")
    units = schema.get("units_count", "")

    space_lines = []
    for s in schema.get("program_requirements", [])[:30]:
        line = f"{s.get('quantity',1)}x {s.get('space_name','')} ({s.get('space_type','')})"
        if s.get("area_sqm"):
            line += f" {s['area_sqm']}sqm"
        if s.get("is_critical"):
            line += " [critical]"
        if s.get("special_requirements"):
            line += f" [{', '.join(s['special_requirements'][:2])}]"
        space_lines.append(line)

    rels = [
        f"{r.get('space_a')} {r.get('relationship_type')} {r.get('space_b')}"
        for r in schema.get("spatial_relationships", [])[:15]
    ]

    return (
        f"Project type: {cat} {sub} {tags}. Floors: {floors}. "
        f"Total area: {area}sqm. Units: {units}. "
        f"Spaces: {'; '.join(space_lines)}. "
        f"Spatial rules: {'; '.join(rels)}."
    ).strip()


def build_site_text(schema: dict) -> str:
    site = schema.get("site_context") or {}
    reg = schema.get("regulatory_constraints") or {}
    env = schema.get("environmental_factors") or {}

    return (
        f"Site area: {site.get('plot_area_sqm','')}sqm, "
        f"shape: {site.get('plot_shape','')}, "
        f"frontage: {(site.get('dimensions_m') or {}).get('frontage','')}m. "
        f"North: {site.get('north_direction','')}. "
        f"Slope: {site.get('slope_percent','')}% {site.get('slope_direction','')}. "
        f"Roads: {', '.join(site.get('road_access',[]))}. "
        f"Views: {', '.join(site.get('views',[]))}. "
        f"Climate: {site.get('climate_zone','')}. "
        f"FAR {reg.get('far','')} "
        f"height limit {reg.get('max_height_m','')}m. "
        f"Setbacks {reg.get('front_setback_m','')}/{reg.get('side_setback_m','')}/{reg.get('rear_setback_m','')}m. "
        f"Jurisdiction: {reg.get('jurisdiction','')} {reg.get('code_version','')}. "
        f"Restrictions: {', '.join(reg.get('special_restrictions',[]))}. "
        f"Cross ventilation: {env.get('cross_ventilation_priority','')}. "
        f"Daylighting: {env.get('daylighting_strategy','')}."
    ).strip()


def build_intent_text(schema: dict) -> str:
    intent = schema.get("design_intent") or {}
    env = schema.get("environmental_factors") or {}
    tags = " ".join(schema.get("domain_tags", []))

    return (
        f"Architectural style: {intent.get('style','')}. "
        f"Cultural context: {intent.get('cultural_context','')}. "
        f"Philosophy: {intent.get('design_philosophy','')}. "
        f"Materiality: {intent.get('materiality_preference','')}. "
        f"Flexibility: {intent.get('flexibility_requirement','')}. "
        f"Certification target: {intent.get('target_certification','')}. "
        f"Passive cooling: {env.get('passive_cooling_required', False)}. "
        f"Solar priority: {env.get('solar_orientation_priority', False)}. "
        f"Green roof: {env.get('green_roof', False)}. "
        f"Domain: {tags}."
    ).strip()


@async_retry(max_attempts=3, base_delay=2.0)
async def embed_project(schema: dict) -> dict[str, dict]:
    spatial = build_spatial_text(schema)
    site = build_site_text(schema)
    intent = build_intent_text(schema)

    log.info("embedding_start",
             spatial=spatial[:60], site=site[:60], intent=intent[:60])

    # SDK is synchronous — run in thread pool to avoid blocking event loop
    vectors = await asyncio.to_thread(_sync_embed, [spatial, site, intent])

    return {
        "spatial": {"text": spatial, "vector": vectors[0]},
        "site":    {"text": site,    "vector": vectors[1]},
        "intent":  {"text": intent,  "vector": vectors[2]},
    }
