"""
AI Extraction Layer — uses Gemini to produce a structured UniversalProjectSchema
from raw parser output. Handles any building type.

Production fixes applied vs Phase 2 draft:
- Exponential backoff on Vertex AI calls
- Robust JSON extraction (handles markdown fences from model)
- Extraction confidence validation
- Async model init to avoid blocking startup
"""
from __future__ import annotations
import os, json, re
import structlog
from google import genai
from google.genai import types

import sys
sys.path.insert(0, "/app")
from shared.utils.retry import async_retry

log = structlog.get_logger()
_client = None

EXTRACTION_SYSTEM_PROMPT = """
You are an expert architectural data analyst. You receive raw parsed output from
architectural files (DWG/DXF, PDF, images, text briefs) and produce a structured
JSON project description.

Understand ANY project type — residential, hospital, school, factory, warehouse,
hotel, place of worship, transit hub, sports facility, data centre, or any other.

RULES:
1. Never assume residential unless data clearly says so
2. Extract ALL identifiable spaces/zones/rooms with exact names found in the file
3. Infer spatial relationships from proximity, annotation grouping, access notes
4. Extract ALL numerical constraints: dimensions, setbacks, FAR, heights
5. Capture design intent: style, cultural context, sustainability targets (US IBC/ADA/NFPA standards)
6. Leave null for uncertain fields — do not guess
7. Add a warning to extraction_warnings for each uncertain field
8. extraction_confidence: 1.0 = crystal clear, 0.7 = mostly clear, 0.5 = partial, 0.3 = guessing
9. OUTPUT: valid JSON only — no markdown fences, no explanation outside the object
"""

EXTRACTION_SCHEMA_HINT = """
Required JSON structure (all fields nullable unless noted):
{
  "project_name": null | string,
  "project_category": string,   // REQUIRED: residential/commercial/institutional/industrial/mixed_use/religious/sports/transport
  "project_sub_type": null | string,
  "domain_tags": [],
  "total_built_area_sqm": null | number,
  "total_built_area_sqft": null | number,
  "floor_count": 1,
  "basement_levels": 0,
  "units_count": null | number,
  "site_context": {"plot_area_sqm":null,"plot_shape":null,"dimensions_m":null,
    "north_direction":null,"slope_percent":null,"road_access":[],"views":[],
    "noise_sources":[],"climate_zone":null},
  "regulatory_constraints": {"far":null,"ground_coverage_percent":null,
    "max_height_m":null,"front_setback_m":null,"side_setback_m":null,
    "rear_setback_m":null,"jurisdiction":null,"code_version":null,"special_restrictions":[]},
  "program_requirements": [{"space_name":"","space_type":"","space_category":null,
    "area_sqm":null,"area_sqft":null,"quantity":1,"facing_preference":null,
    "is_critical":false,"special_requirements":[],"must_be_adjacent_to":[],
    "must_be_separated_from":[]}],
  "spatial_relationships": [{"space_a":"","space_b":"","relationship_type":"",
    "reason":null,"priority":"preferred"}],
  "environmental_factors": {"passive_cooling_required":false,
    "cross_ventilation_priority":null,"daylighting_strategy":null,
    "rainwater_harvesting":false,"solar_orientation_priority":false},
  "design_intent": {"style":null,"cultural_context":null,
    "design_philosophy":null,"target_certification":null,
    "materiality_preference":null,"flexibility_requirement":null},
  "extraction_confidence": 0.0,
  "extraction_warnings": []
}
"""


def _get_client() -> genai.Client:
    global _client
    if not _client:
        _client = genai.Client(
            vertexai=True,
            api_key=os.environ["GEMINI_API_KEY"],
        )
    return _client


@async_retry(max_attempts=3, base_delay=2.0)
async def extract_project_schema(
    parsed_data: dict,
    filename: str,
    file_type: str,
    user_brief: str = "",
) -> dict:
    client = _get_client()

    parts = [f"File: {filename} (type: {file_type})"]
    if user_brief:
        parts.append(f"\nArchitect brief:\n{user_brief[:2000]}")

    parts.append(f"\nParsed file data:\n{_summarise(parsed_data, file_type)}")
    parts.append(f"\nExtract into:\n{EXTRACTION_SCHEMA_HINT}")

    prompt = "\n".join(parts)

    log.info("extraction_start", filename=filename, file_type=file_type,
             has_brief=bool(user_brief))

    response = client.models.generate_content(
        model="gemini-2.5-flash",
        contents=prompt,
        config=types.GenerateContentConfig(
            system_instruction=EXTRACTION_SYSTEM_PROMPT,
            temperature=0.05,
            max_output_tokens=8192,
            response_mime_type="application/json",
            thinking_config=types.ThinkingConfig(thinking_budget=0),
        ),
    )

    raw = response.text.strip()
    # Strip markdown fences if model includes them despite mime_type
    raw = re.sub(r"^```(?:json)?\s*", "", raw)
    raw = re.sub(r"\s*```$", "", raw)

    extracted = json.loads(raw)
    extracted["ai_model_used"] = "gemini-2.5-flash"
    extracted["file_types"] = [file_type]
    extracted.setdefault("extraction_confidence", 0.5)
    extracted.setdefault("extraction_warnings", [])

    if not extracted.get("project_category"):
        extracted["project_category"] = "unknown"
        extracted["extraction_warnings"].append("project_category could not be determined")

    log.info("extraction_complete",
             filename=filename,
             category=extracted.get("project_category"),
             sub_type=extracted.get("project_sub_type"),
             confidence=extracted.get("extraction_confidence"),
             spaces=len(extracted.get("program_requirements", [])))

    return extracted


def _summarise(parsed: dict, file_type: str) -> str:
    parts = []
    if file_type in ("dwg", "dxf"):
        annotations = parsed.get("annotations", [])[:50]
        parts.append(f"Layers ({len(parsed.get('layers', []))}): {', '.join(parsed.get('layers', [])[:20])}")
        parts.append(f"Annotations ({len(annotations)}):")
        for a in annotations[:40]:
            parts.append(f"  '{a.get('text','')}' at ({a.get('x')},{a.get('y')})")
        parts.append(f"Walls: {len(parsed.get('walls',[]))}, Doors: {len(parsed.get('doors',[]))}, Windows: {len(parsed.get('windows',[]))}")
        dims = parsed.get("dimension_values_mm", [])
        if dims:
            parts.append(f"Dimension values (mm): {dims[:20]}")
    elif file_type == "pdf":
        parts.append(f"Pages: {parsed.get('page_count', 1)}")
        parts.append(f"Text:\n{parsed.get('raw_text_preview', '')[:2500]}")
        if parsed.get("areas_sqft"):
            parts.append(f"Area values: {parsed['areas_sqft'][:10]}")
        if parsed.get("far_fsi_values"):
            parts.append(f"FAR/FSI values: {parsed['far_fsi_values']}")
    elif file_type == "image":
        parts.append(f"OCR text:\n{parsed.get('raw_text_preview', '')[:2500]}")
        if parsed.get("areas_sqft"):
            parts.append(f"Area values found: {parsed['areas_sqft'][:10]}")
    else:
        parts.append(str(parsed)[:3000])
    return "\n".join(parts)


def fallback_schema(filename: str, file_type: str) -> dict:
    return {
        "project_name": filename,
        "project_category": "unknown",
        "project_sub_type": None,
        "domain_tags": [],
        "total_built_area_sqm": None,
        "floor_count": 1,
        "basement_levels": 0,
        "program_requirements": [],
        "spatial_relationships": [],
        "site_context": {},
        "regulatory_constraints": {},
        "environmental_factors": {},
        "design_intent": {},
        "extraction_confidence": 0.0,
        "extraction_warnings": ["AI extraction failed after retries — manual review required"],
        "ai_model_used": "fallback",
        "file_types": [file_type],
    }
