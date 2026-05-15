"""
/generate — universal layout generation endpoint.
Works for any building type. Retrieves KB references + Gemini generation.
"""
from __future__ import annotations
import asyncio, json, os, re
import structlog
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel
from google import genai
from google.genai import types

from ..core.retriever import retrieve

log = structlog.get_logger()
router = APIRouter(prefix="/generate", tags=["generation"])
_client = None
_client_lock = asyncio.Lock()

GENERATION_SYSTEM_PROMPT = """
You are an expert architectural design consultant working with professional architects.
Generate detailed spatial layout proposals for ANY building type.

TYPES YOU HANDLE:
Residential (villa, apartment, row house, dormitory), Commercial (office, retail, hotel,
restaurant, data centre), Institutional (hospital, clinic, school, college, court),
Industrial (factory, warehouse, logistics), Mixed-use, Religious, Sports, Transport hubs.

RULES:
1. Output valid JSON ONLY — no markdown, no text outside the JSON object
2. Respect ALL constraints: FAR, setbacks, height limits, plot dimensions
3. Critical spaces (ICU, server room, prayer hall) must honour adjacency rules
4. Generate exactly the requested number of variations with distinct spatial concepts
5. Scale spaces to realistic US standards (IBC 2021, NFPA 101, ADA, local zoning codes)
6. Account for sun path, prevailing winds, and cross-ventilation
7. Flag constraint conflicts in warnings — never silently violate them
"""


async def _get_client() -> genai.Client:
    global _client
    async with _client_lock:
        if not _client:
            _client = genai.Client(
                vertexai=True,
                api_key=os.environ["GEMINI_API_KEY"],
            )
    return _client


class GenerateRequest(BaseModel):
    prompt: str
    project_category: str | None = None
    domain_tags: list[str] = []
    total_area_sqm: float | None = None
    total_area_sqft: float | None = None
    floor_count: int = 1
    site_context: dict = {}
    regulatory_constraints: dict = {}
    environmental_factors: dict = {}
    design_intent: dict = {}
    spatial_weight: float = 0.5
    site_weight: float = 0.3
    intent_weight: float = 0.2
    variations: int = 3


@router.post("")
async def generate(req: GenerateRequest):
    log.info("generate_start", prompt=req.prompt[:80], category=req.project_category)

    retrieval = await retrieve(
        prompt=req.prompt,
        project_category=req.project_category,
        domain_tags=req.domain_tags or None,
        min_area_sqm=req.total_area_sqm * 0.6 if req.total_area_sqm else None,
        max_area_sqm=req.total_area_sqm * 1.4 if req.total_area_sqm else None,
        spatial_weight=req.spatial_weight,
        site_weight=req.site_weight,
        intent_weight=req.intent_weight,
        top_k=3,
    )

    references = retrieval.get("projects", [])
    ref_context = _format_references(references)
    constraint_context = _format_constraints(req)

    full_prompt = f"""
ARCHITECT'S REQUEST:
{req.prompt}

SCALE:
Total area: {req.total_area_sqm or '?'} sqm / {req.total_area_sqft or '?'} sqft
Floors: {req.floor_count}

{constraint_context}

REFERENCE PROJECTS FROM KNOWLEDGE BASE ({len(references)} found):
{ref_context}

Generate {req.variations} layout variations as JSON matching exactly this structure:
{{
  "project_type_understood": "your interpretation",
  "design_standards_applied": ["IBC 2021", "..."],
  "variations": [
    {{
      "variation": 1,
      "concept_name": "name",
      "concept_rationale": "why",
      "total_area_sqm": 0,
      "organisation_strategy": "description",
      "spaces": [
        {{"name": "", "type": "", "area_sqm": 0, "floor": 1,
          "facing": "", "is_critical": false,
          "position_hint": "", "adjacencies": []}}
      ],
      "circulation": "strategy description",
      "constraint_compliance": {{"far_used": 0.0, "height_m": 0, "coverage_percent": 0}},
      "passive_design_notes": "sun/wind strategy",
      "warnings": []
    }}
  ],
  "global_warnings": [],
  "recommended_variation": 1
}}
"""

    client = await _get_client()
    model_name = os.environ.get("GENERATION_MODEL", "gemini-2.5-flash")

    for attempt in range(1, 3):
        try:
            response = await asyncio.to_thread(
                client.models.generate_content,
                model=model_name,
                contents=full_prompt,
                config=types.GenerateContentConfig(
                    system_instruction=GENERATION_SYSTEM_PROMPT,
                    temperature=0.4,
                    max_output_tokens=16384,
                    response_mime_type="application/json",
                    thinking_config=types.ThinkingConfig(thinking_budget=0),
                ),
            )
            raw = response.text.strip()
            raw = re.sub(r"^```(?:json)?\s*", "", raw)
            raw = re.sub(r"\s*```$", "", raw)
            result = json.loads(raw)
            result["reference_projects_used"] = len(references)
            result["retrieval_weights"] = retrieval.get("weights_used")
            log.info("generate_complete", variations=len(result.get("variations", [])))
            return result
        except json.JSONDecodeError as exc:
            if attempt == 2:
                log.error("generation_json_error", error=str(exc))
                raise HTTPException(500, "Generation model returned malformed JSON")
            log.warning("generation_json_retry", attempt=attempt)
        except Exception as exc:
            log.error("generation_error", error=str(exc))
            raise HTTPException(500, str(exc))


def _format_references(references: list) -> str:
    if not references:
        return "No similar projects found in KB — generating from first principles."
    lines = []
    for i, ref in enumerate(references[:3], 1):
        lines.append(
            f"Ref {i} (match {ref.get('composite_score',0):.2f}): "
            f"{ref.get('project_category')} / {ref.get('project_sub_type')} "
            f"— {ref.get('total_built_area_sqm')} sqm, {ref.get('floor_count')} floors "
            f"tags: {ref.get('domain_tags')}"
        )
    return "\n".join(lines)


def _format_constraints(req: GenerateRequest) -> str:
    parts = []
    sc = req.site_context
    if sc:
        parts.append(
            f"Site: {sc.get('plot_area_sqm','')} sqm, {sc.get('plot_shape','')} shape, "
            f"North: {sc.get('north_direction','')}. Roads: {sc.get('road_access',[])}."
        )
    rc = req.regulatory_constraints
    if rc:
        parts.append(
            f"Regulations: FAR {rc.get('far','?')}, Height max {rc.get('max_height_m','?')}m, "
            f"Setbacks F{rc.get('front_setback_m','?')}/S{rc.get('side_setback_m','?')}/R{rc.get('rear_setback_m','?')}m. "
            f"Jurisdiction: {rc.get('jurisdiction','')}."
        )
    di = req.design_intent
    if di:
        parts.append(
            f"Design: {di.get('style','')} style, "
            f"philosophy: {di.get('design_philosophy','')}."
        )
    ef = req.environmental_factors
    if ef:
        parts.append(
            f"Environment: passive cooling={ef.get('passive_cooling_required',False)}, "
            f"ventilation={ef.get('cross_ventilation_priority','')}, "
            f"daylighting={ef.get('daylighting_strategy','')}."
        )
    return "\n".join(parts)
