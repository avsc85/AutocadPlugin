"""
/v1/orchestrate — the single endpoint called by the AutoCAD plugin.

Flow:
1. Validate API key (middleware)
2. Extract intent with Gemini Flash (fast, low cost)
3. Three-vector KB retrieval
4. Generate spatial variations with Gemini Flash
5. Validate and compile to DrawActionPlan
6. Return to plugin

Fixed vs Phase 3 draft:
- Uses google-genai SDK (vertexai=True + api_key) — NOT vertexai SDK
- thinking_budget=0 to prevent JSON truncation
- GENERATION_MODEL defaults to gemini-2.5-flash (already proven)
- Auth uses RAG_API_KEY (matches existing rag-api-key secret)
"""
from __future__ import annotations
import asyncio, json, math, os, re, time, uuid
import structlog
from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel
from google import genai
from google.genai import types

from ..core.retriever import retrieve
from ..core.action_compiler import compile_to_plan

log = structlog.get_logger()
router = APIRouter(prefix="/orchestrate", tags=["orchestrate"])

# VALIDATION-FIX: CHECK-E05 — in-process per-IP rate limiter (10 req/min)
# Uses token-bucket approximation stored in module-level dict; safe for single-instance Cloud Run.
# For multi-instance: replace with Redis-backed counter (key: f"ratelimit:{ip}", TTL 60s).
_rate_buckets: dict[str, list[float]] = {}
_RATE_LIMIT      = 10   # requests
_RATE_WINDOW_SEC = 60.0 # per minute


def _check_rate_limit(ip: str) -> None:
    now = time.monotonic()
    window = _rate_buckets.get(ip, [])
    window = [t for t in window if now - t < _RATE_WINDOW_SEC]
    if len(window) >= _RATE_LIMIT:
        raise HTTPException(
            status_code=429,
            detail=f"Rate limit exceeded: {_RATE_LIMIT} requests per minute per IP",
        )
    window.append(now)
    _rate_buckets[ip] = window

_client = None
_client_lock = asyncio.Lock()

INTENT_SYSTEM = """
You are an architectural programme parser.
Extract structured parameters from an architect's description.
Output JSON only. No markdown. No explanation.
Be inclusive: accept any building type, any language mix.
IMPORTANT: Always output total_area_sqm in SQUARE METRES (sqm).
  Convert from sqft: 1 sqft = 0.0929 sqm (e.g. 1200 sqft = 111.5 sqm).
  Convert from sqm: use as-is.
IMPORTANT: Output total_area_sqm as the HOUSE FLOOR AREA, not the site/lot area.
  Site area goes into site_context.plot_area_sqm.
"""

GEN_SYSTEM = """
You are a licensed US residential architect and the PRIMARY SPATIAL INTELLIGENCE ENGINE.
You make ALL architectural decisions. A deterministic CAD geometry executor translates
your spatial intent into drawings — it cannot reason, it can only execute.

YOUR ROLE: architectural planner. The engine's role: geometric executor.

YOU MUST PRODUCE 10 DIMENSIONS OF ARCHITECTURAL INTELLIGENCE:

1. SPATIAL ZONING
   Organise every plan into clear zones:
   PUBLIC   → entry, foyer, living, dining, kitchen, powder_room
   PRIVATE  → bedrooms, bathrooms, walk_in_closet (suite cluster at REAR of lot)
   SERVICE  → laundry, mudroom, garage, pantry
   Note: do NOT include hallway/corridor — the engine generates circulation automatically.

2. CIRCULATION SEQUENCE
   Define movement hierarchy from street to most private.
   Typical CA suburban: Entry → Foyer → Open Living → Dining → Outdoor Connection
                        Entry → Hallway → Bedroom Wing → Primary Suite (at rear)

3. VIEW AXES
   Establish dominant visual axes:
   - Entry focal point (what you see when entering)
   - Kitchen island sightline to living/outdoor
   - Primary bedroom backyard orientation

4. SOLAR ORIENTATION REASONING
   - Living, dining, kitchen → south-facing light (productive all day)
   - Primary bedroom → east-facing (morning light, privacy from street)
   - Service zones → north or west (reduce cooling load)
   - Minimise west glazing in bedrooms (summer heat gain)

5. PRIVACY HIERARCHY — required on every space
   "public"      → entry, living, dining, kitchen
   "semi_private" → guest_bedroom, powder_room
   "private"     → primary_bedroom, primary_bath, walk_in_closet, bathroom
   "service"     → laundry, garage, mudroom, pantry

6. ACOUSTIC BUFFERING
   Bedrooms require separation from garage, foyer, and great room.
   Express as noise_buffer_from list on each bedroom space.

7. OUTDOOR CONNECTIONS
   Living and dining connect to backyard/patio via adjacency.
   Primary bedroom may reference backyard for private outdoor access.

8. ADJACENCY GRAPH — required on every space
   "connected_to"   → rooms directly accessible through a shared door
   "near"           → rooms spatially adjacent but not directly connected
   "separated_from" → rooms requiring acoustic or visual separation

9. ORGANISATION TYPE — select the one that best fits the brief and lot:
   "ranch"             → wide single-storey, 2.5:1 W:D, sequential front-to-back
   "split_wing"        → Y-shaped, open living core, diverging bedroom and service wings
   "open_plan_suburban"→ open core, private wing laterally offset by hallway
   "courtyard"         → rooms around a central outdoor court
   "spine"             → compact, rooms either side of a central corridor
   "compact_urban"     → tight 0.8:1 footprint, efficiency-first

10. WING ORIENTATION — which side the bedroom wing occupies on the X axis:
    "living_left"  → living faces front-left, bedrooms right (default CA suburban)
    "living_right" → living faces front-right (narrow east-facing lots)
    In BOTH cases: bedrooms must be farthest from street entry (rear of lot in Y axis).

11. KITCHEN WORK TRIANGLE:
    - Refrigerator, sink, and cooktop must each be within 2.1m–2.7m of each other.
    - Kitchen minimum internal width: 2700mm (even if area is small).
    - Island kitchens require 1200mm circulation clearance on all four sides.
    - If kitchen area_sqm < 7.5 → "kitchen_type": "galley" (single-wall, max 2.4m wide).
    - If kitchen area_sqm 7.5–12 → "kitchen_type": "L_shape" (preferred_width_m ≥ 2.7).
    - If kitchen area_sqm > 12 → "kitchen_type": "island" or "U_shape".
    - Add "kitchen_type" field to EVERY kitchen space object in every variation.

PRIMARY SUITE STRATEGY (mandatory):
- primary_bedroom placed at REAR of bedroom wing (highest Y — farthest from street)
- Suite cluster = primary_bedroom + primary_bath + walk_in_closet (all in zone "rear")
- connected_to must include: primary_bath, walk_in_closet
- separated_from must include: garage, entry, great_room, foyer

GARAGE PLACEMENT — add as variation-level field "garage_placement":
- "front" → front-loaded driveway, garage near street face (default CA suburban with garage)
- "rear"  → alley-loaded, garage at back of service zone

OUTPUT RULES (non-negotiable):
1. Valid JSON only. No markdown. No explanation. No comments.
2. Generate exactly 3 variations. Each must have a distinct organisation_type.
3. Every space MUST have the correct "type" from the approved list.
4. area_sqm must be realistic (not too small, not too large).
5. Use key "zones" at the variation level — NEVER "spaces" at variation level.
6. Every space must include: privacy_level, adjacency object, has_natural_light.
7. primary_bedroom MUST be in zone_position "rear" (private, rear of lot).
8. powder_room MUST be in zone_position "front" (guest-accessible, near entry).
9. Do NOT include hallway, corridor, backyard, yard, garden — engine-computed.
10. Each variation must differ in organisation_type (not just room sizes).

APPROVED ROOM TYPES (use EXACTLY these strings — the engine matches on type):

BEDROOMS (placed in bedroom wing, paired with bath):
  primary_bedroom   → Primary Suite (largest bedroom, 12'×13'-6", ~18 sqm)
  secondary_bedroom → Bedroom 2, 3, 4 (10'×12', ~11 sqm each)
  guest_bedroom     → Guest Room (10'×12', ~11 sqm)

BATHROOMS (auto-paired with nearest bedroom):
  primary_bath      → Primary En-Suite (6'×8', ~4.5 sqm) — ONE per primary bedroom
  bathroom          → Shared Bath (5'×8', ~3.7 sqm) — one per 2 secondary bedrooms
  powder_room       → Powder Room / Half Bath (3'×6', ~1.7 sqm) — near entry

CLOSETS (placed beside primary bedroom):
  walk_in_closet    → Walk-In Closet (6'×8', ~4.5 sqm)

ENTRY (placed at street-facing front of living wing):
  entry             → Entry / Foyer (~4.5 sqm)

OPEN PLAN (ALWAYS merge kitchen+dining+living into ONE zone):
  kitchen           → Kitchen (10'×12', ~11 sqm)
  dining            → Dining Room (10'×12', ~11 sqm)
  living            → Living Room / Family Room (14'×18', ~23 sqm)
  *** All three MUST be in ONE zone with zone_position "centre" ***
  *** The engine merges them into one open-plan great room ***

ENCLAIR / GLASS ROOM (placed at rear of living wing, opens to backyard):
  enclair           → Enclair / Covered Glass Room (~25 sqm, glass walls, backyard connection)
  sunroom           → Sunroom (glass-walled extension, ~20 sqm)
  covered_porch     → Covered Porch / Veranda (~15 sqm)
  *** Use "enclair" when brief mentions: "Enclair", "covered room", "glass room", "sunroom",
      "veranda", "lanai", "indoor-outdoor living", "covered outdoor room" ***
  *** The engine draws this with glass/glazed walls and sliding doors to the backyard ***

SERVICE (placed at rear of living wing, between open plan and backyard):
  laundry           → Laundry Room (6'×8', ~4.5 sqm)
  mudroom           → Mudroom (6'×8', ~4.5 sqm)
  garage            → Garage (20'×20', ~37 sqm minimum; use area_sqm ≥ 37)
  pantry            → Pantry (4'×8', ~3 sqm)

GARAGE RULES — include ONLY when explicitly requested:
- Add a garage ONLY if the brief mentions "garage", "car", "parking", or "ADU".
- For 1-2 bedroom homes in urban/CA settings: do NOT add garage unless asked.
- If the brief mentions "ADU", "accessory dwelling", "granny flat", or "in-law suite":
  include garage with area_sqm ≥ 55. Engine auto-marks it adu_capable.
- ADU garage does NOT replace bedrooms.

MAXIMIZE BACKYARD:
- When brief says "maximize backyard" or "large backyard": keep total_area_sqm lean
  (do not pad with extra service rooms). Place only what is asked for. The engine
  positions the building near the front setback automatically.

AREA BUDGET — spaces must sum to ≈ total_area_sqm (±10%):
  Studio/1-bed:  ~55 sqm  | 2-bed house: ~90–115 sqm  | 3-bed house: ~150 sqm
  4-bed house: ~200 sqm  | 5-bed house: ~280 sqm
  (note: 1,200 SF = ~111 sqm)

US RESIDENTIAL ZONE ASSIGNMENT:
  zone_position "front"   → entry, powder_room only
  zone_position "centre"  → kitchen + dining + living (ALL THREE, ONE zone) + enclair/sunroom if requested
  zone_position "rear"    → all bedrooms, baths, walk_in_closet
  zone_position "service" → laundry, mudroom, garage, pantry

SCHEMA KEY REQUIREMENT — CRITICAL:
- Use key "zones" at the variation level. NEVER use "spaces" at the variation level.
- "spaces" is only valid INSIDE each zone object (the list of rooms within a zone).
- Any example or training data that shows "spaces" at variation level is WRONG — convert it.

REQUIRED SPACE METADATA — every space object inside zones must include:
  "natural_light": true | false
  "privacy_level": "public" | "semi_private" | "private" | "service"
  "adjacency": ["type_or_zone_name", ...]
  "preferred_width_m": preferred minimum width in metres (architecturally correct; NOT code minimum)
  "preferred_depth_m": preferred minimum depth in metres (architecturally correct)
  "max_width_m": maximum acceptable width before room feels oversized or disproportionate
  — These three dimension fields are MANDATORY on every space. The geometry engine uses them
    to enforce room proportions during placement. Use the standard US residential values:
    primary_bedroom → preferred_width_m: 3.66, preferred_depth_m: 4.12, max_width_m: 6.0
    secondary_bedroom → preferred_width_m: 3.05, preferred_depth_m: 3.66, max_width_m: 5.0
    kitchen (L_shape) → preferred_width_m: 2.7, preferred_depth_m: 3.6, max_width_m: 5.5
    living_room → preferred_width_m: 4.2, preferred_depth_m: 5.5, max_width_m: 9.0
    dining → preferred_width_m: 3.0, preferred_depth_m: 3.6, max_width_m: 6.0
    primary_bath → preferred_width_m: 1.83, preferred_depth_m: 2.44, max_width_m: 3.5
    bathroom → preferred_width_m: 1.52, preferred_depth_m: 2.44, max_width_m: 2.8

ADJACENCY RULES — enforce in every variation:
- kitchen must list dining or living in its adjacency
- primary_bedroom must list primary_bath and walk_in_closet in adjacency
- powder_room must be in zone_position "front", adjacent to entry
- bedroom wing (zone_position "rear") must NOT directly connect to street/entry
- Every zone must be reachable via valid adjacency graph — no disconnected zones
"""

INTENT_SCHEMA = """{
  "project_category": "string",
  "project_sub_type": null,
  "domain_tags": [],
  "total_area_sqm": null,
  "floor_count": 1,
  "spaces_requested": [
    {"name": "string", "type": "string", "area_sqm": null,
     "quantity": 1, "facing": null, "is_critical": false, "notes": ""}
  ],
  "site_context": {
    "plot_area_sqm": null, "plot_shape": null,
    "north_direction": null, "climate_zone": null
  },
  "regulatory_constraints": {
    "far": null, "max_height_m": null, "jurisdiction": null,
    "front_setback_m": null, "side_setback_m": null, "rear_setback_m": null
  },
  "design_intent": {
    "style": null, "design_philosophy": null, "target_certification": null
  },
  "spatial_weight": 0.5,
  "site_weight": 0.3,
  "intent_weight": 0.2
}"""


async def _get_client() -> genai.Client:
    global _client
    async with _client_lock:
        if not _client:
            _client = genai.Client(
                vertexai=True,
                api_key=os.environ["GEMINI_API_KEY"],
            )
    return _client


class PluginRequest(BaseModel):
    prompt:                  str
    project_category:        str | None = None
    site_context:            dict = {}
    regulatory_constraints:  dict = {}
    design_intent:           dict = {}
    total_area_sqm:          float | None = None
    floor_count:             int = 1
    plugin_version:          str = "unknown"
    autocad_version:         str = "unknown"
    autocad_units:           str = "mm"


def _format_rag_precedent(projects: list[dict]) -> str:
    """Serialize retrieved KB projects into rich spatial precedent descriptions."""
    if not projects:
        return "No KB references — generate from first principles."

    lines = ["PRECEDENT SPATIAL PATTERNS (use as reasoning references, NOT templates to copy):"]
    for i, r in enumerate(projects[:3], 1):
        sc = r.get("site_context") or {}
        if isinstance(sc, str):
            try: sc = json.loads(sc)
            except Exception: sc = {}
        di = r.get("design_intent") or {}
        if isinstance(di, str):
            try: di = json.loads(di)
            except Exception: di = {}
        tags = r.get("domain_tags") or []
        if isinstance(tags, str):
            try: tags = json.loads(tags)
            except Exception: tags = [tags]

        lines.append(
            f"\nPRECEDENT {i} — {r.get('project_name', 'Reference Project')} "
            f"(match score: {r.get('composite_score', 0):.2f})"
        )
        lines.append(
            f"  PROJECT SUMMARY: {r.get('project_category','?')}/{r.get('project_sub_type','?')} | "
            f"{r.get('total_built_area_sqm','?')} sqm built | "
            f"{r.get('floor_count', 1)} floor(s) | "
            f"style: {di.get('style') or '?'} | "
            f"tags: {', '.join(str(t) for t in tags) if tags else 'none'}"
        )

        plot_w = sc.get("plot_width_m") or sc.get("plot_w_m")
        plot_d = sc.get("plot_depth_m") or sc.get("plot_d_m")
        plot_area = sc.get("plot_area_sqm")
        north = sc.get("north_direction") or sc.get("north")
        climate = sc.get("climate_zone")
        if plot_w or plot_d or plot_area or north or climate:
            dim_str = (f"{plot_w}m × {plot_d}m" if plot_w and plot_d
                       else f"{plot_area} sqm" if plot_area else "unknown dimensions")
            lines.append(
                f"  LOT CONTEXT: {dim_str} | "
                f"north: {north or 'unknown'} | climate: {climate or 'unknown'}"
            )

        org = (di.get("organisation_type") or di.get("organisation_strategy")
               or r.get("organisation_type"))
        wing = di.get("wing_orientation") or r.get("wing_orientation")
        if org or wing:
            lines.append(f"  ORGANISATION: type={org or '?'} | wing_orientation={wing or '?'}")

        for field, label in [
            ("design_philosophy", "DESIGN INTENT"),
            ("circulation_sequence", "CIRCULATION"),
            ("solar_strategy", "SOLAR STRATEGY"),
            ("entry_experience", "ENTRY EXPERIENCE"),
            ("view_axes", "VIEW AXES"),
            ("privacy_strategy", "PRIVACY STRATEGY"),
            ("spatial_patterns", "SPATIAL PATTERNS"),
        ]:
            val = di.get(field) or r.get(field)
            if val:
                lines.append(f"  {label}: {str(val)[:250]}")

    lines.append(
        "\nINSTRUCTION: Use these precedents to inform spatial reasoning — "
        "organisation_type choice, wing_orientation, circulation hierarchy, "
        "solar strategy, and adjacency decisions. Do NOT copy room areas or "
        "layouts directly. Adapt the spatial intelligence to the current brief."
    )
    return "\n".join(lines)


@router.post("")
async def orchestrate(request: Request, req: PluginRequest):
    # VALIDATION-FIX: CHECK-E05 — enforce rate limit before any AI work
    client_ip = request.client.host if request.client else "unknown"
    _check_rate_limit(client_ip)

    request_id = str(uuid.uuid4())
    t0 = time.perf_counter()

    log.info("orchestrate_start",
             request_id=request_id,
             prompt=req.prompt[:120],
             category=req.project_category or "(from intent)",
             units=req.autocad_units)

    client = await _get_client()
    model_name = os.environ.get("GENERATION_MODEL", "gemini-2.5-flash")

    # ── 1. Intent extraction ──────────────────────────────────────────────────
    try:
        intent_resp = await asyncio.to_thread(
            client.models.generate_content,
            model=model_name,
            contents=f"Parse:\n{req.prompt}\n\nSchema:\n{INTENT_SCHEMA}",
            config=types.GenerateContentConfig(
                system_instruction=INTENT_SYSTEM,
                temperature=0.05,
                max_output_tokens=1024,
                response_mime_type="application/json",
                thinking_config=types.ThinkingConfig(thinking_budget=0),
            ),
        )
        raw = intent_resp.text.strip()
        raw = re.sub(r"^```(?:json)?\s*", "", raw)
        raw = re.sub(r"\s*```$", "", raw)
        intent = json.loads(raw)
    except Exception as e:
        log.warning("intent_parse_fallback", error=str(e))
        intent = {
            "project_category": req.project_category or "residential",
            "spatial_weight": 0.5,
            "site_weight": 0.3,
            "intent_weight": 0.2,
        }

    category = req.project_category or intent.get("project_category", "residential") or "residential"
    area_sqm = req.total_area_sqm or intent.get("total_area_sqm")
    # If Gemini returned area > 2000 it likely kept sqft — convert to sqm
    if area_sqm and float(area_sqm) > 2000:
        area_sqm = round(float(area_sqm) / 10.764, 1)
    site_ctx = {**intent.get("site_context", {}), **req.site_context}
    reg_ctx  = {**intent.get("regulatory_constraints", {}), **req.regulatory_constraints}
    design   = {**intent.get("design_intent", {}), **req.design_intent}
    sw  = float(intent.get("spatial_weight", 0.5))
    stw = float(intent.get("site_weight", 0.3))
    iw  = float(intent.get("intent_weight", 0.2))

    # ── 2. KB retrieval ───────────────────────────────────────────────────────
    retrieval = await retrieve(
        prompt=req.prompt,
        project_category=category,
        domain_tags=intent.get("domain_tags") or None,
        min_area_sqm=area_sqm * 0.6 if area_sqm else None,
        max_area_sqm=area_sqm * 1.4 if area_sqm else None,
        spatial_weight=sw,
        site_weight=stw,
        intent_weight=iw,
        top_k=3,
    )
    references = retrieval.get("projects", [])

    # ── 3. Build generation context ───────────────────────────────────────────
    ref_ctx = _format_rag_precedent(references)

    spaces_ctx = ""
    if intent.get("spaces_requested"):
        spaces_ctx = "Spaces required:\n" + "\n".join(
            f"  {s.get('quantity',1)}x {s['name']} ({s.get('type')}) "
            f"~{s.get('area_sqm','?')}sqm  facing:{s.get('facing','any')}  "
            f"critical:{s.get('is_critical',False)}  notes:{s.get('notes','')}"
            for s in intent.get("spaces_requested", [])
        )

    gen_prompt = f"""
BRIEF: {req.prompt}
BUILDING TYPE: {category}
TOTAL FLOOR AREA: {area_sqm or 150} sqm
FLOORS: {req.floor_count}
{spaces_ctx}

KB REFERENCES:
{ref_ctx}

Generate exactly 3 layout variations. Output this JSON structure exactly:

{{
  "project_type_understood": "3-bedroom suburban house",
  "project_category": "{category}",
  "total_area_sqm": {area_sqm or 150},
  "variations": [
    {{
      "concept_name": "Classic Ranch",
      "concept_rationale": "Single-storey linear plan with open great room facing backyard",
      "total_area_sqm": {area_sqm or 150},
      "organisation_strategy": "residential",
      "organisation_type": "ranch",
      "wing_orientation": "living_left",
      "circulation_sequence": "Entry → Foyer → Open Living/Kitchen → Dining → Backyard",
      "solar_strategy": "South-facing living and kitchen for all-day light; east-facing primary suite for morning privacy",
      "garage_placement": "front",
      "structural_grid_m": 4.0,
      "zones": [
        {{
          "zone_name": "front",
          "zone_position": "front",
          "spaces": [
            {{"name": "Entry", "type": "entry", "area_sqm": 4.5, "floor": 1, "has_natural_light": true, "privacy_level": "public", "adjacency": {{"connected_to": ["living", "foyer"], "near": ["powder_room"], "separated_from": []}}}},
            {{"name": "Powder Room", "type": "powder_room", "area_sqm": 1.7, "floor": 1, "has_natural_light": false, "privacy_level": "semi_private", "adjacency": {{"connected_to": ["entry"], "near": ["living"], "separated_from": ["primary_bedroom"]}}}}
          ]
        }},
        {{
          "zone_name": "centre",
          "zone_position": "centre",
          "spaces": [
            {{"name": "Kitchen", "type": "kitchen", "area_sqm": 11.0, "floor": 1, "has_natural_light": true, "privacy_level": "public", "kitchen_type": "L_shape", "preferred_width_m": 2.7, "preferred_depth_m": 3.6, "max_width_m": 5.5, "adjacency": {{"connected_to": ["dining", "living"], "near": ["laundry"], "separated_from": []}}}},
            {{"name": "Dining Room", "type": "dining", "area_sqm": 11.0, "floor": 1, "has_natural_light": true, "privacy_level": "public", "preferred_width_m": 3.0, "preferred_depth_m": 3.6, "max_width_m": 6.0, "adjacency": {{"connected_to": ["kitchen", "living"], "near": [], "separated_from": []}}}},
            {{"name": "Family Room", "type": "living", "area_sqm": 23.0, "floor": 1, "has_natural_light": true, "privacy_level": "public", "preferred_width_m": 4.2, "preferred_depth_m": 5.5, "max_width_m": 9.0, "adjacency": {{"connected_to": ["kitchen", "dining", "entry"], "near": [], "separated_from": []}}}}
          ]
        }},
        {{
          "zone_name": "rear",
          "zone_position": "rear",
          "spaces": [
            {{"name": "Bedroom 2", "type": "secondary_bedroom", "area_sqm": 11.0, "floor": 1, "has_natural_light": true, "privacy_level": "private", "adjacency": {{"connected_to": ["bathroom"], "near": ["hallway"], "separated_from": ["garage", "entry"]}}}},
            {{"name": "Bedroom 3", "type": "secondary_bedroom", "area_sqm": 11.0, "floor": 1, "has_natural_light": true, "privacy_level": "private", "adjacency": {{"connected_to": ["bathroom"], "near": ["hallway"], "separated_from": ["garage", "entry"]}}}},
            {{"name": "Bath 2", "type": "bathroom", "area_sqm": 3.7, "floor": 1, "has_natural_light": false, "privacy_level": "private", "adjacency": {{"connected_to": ["secondary_bedroom"], "near": [], "separated_from": []}}}},
            {{"name": "Primary Suite", "type": "primary_bedroom", "area_sqm": 18.0, "floor": 1, "has_natural_light": true, "privacy_level": "private", "preferred_width_m": 3.66, "preferred_depth_m": 4.12, "max_width_m": 6.0, "adjacency": {{"connected_to": ["primary_bath", "walk_in_closet"], "near": [], "separated_from": ["garage", "entry", "great_room", "foyer"]}}}},
            {{"name": "Walk-In Closet", "type": "walk_in_closet", "area_sqm": 4.5, "floor": 1, "has_natural_light": false, "privacy_level": "private", "adjacency": {{"connected_to": ["primary_bedroom"], "near": [], "separated_from": []}}}},
            {{"name": "Primary Bath", "type": "primary_bath", "area_sqm": 4.5, "floor": 1, "has_natural_light": false, "privacy_level": "private", "adjacency": {{"connected_to": ["primary_bedroom"], "near": [], "separated_from": []}}}}
          ]
        }},
        {{
          "zone_name": "service",
          "zone_position": "service",
          "spaces": [
            {{"name": "Laundry Room", "type": "laundry", "area_sqm": 4.5, "floor": 1, "has_natural_light": false, "privacy_level": "service", "adjacency": {{"connected_to": [], "near": ["kitchen"], "separated_from": []}}}},
            {{"name": "Mudroom", "type": "mudroom", "area_sqm": 4.5, "floor": 1, "has_natural_light": false, "privacy_level": "service", "adjacency": {{"connected_to": ["garage"], "near": ["entry"], "separated_from": []}}}}
          ]
        }}
      ],
      "constraint_compliance": {{"far_used": 0.25, "height_m": 3.0, "coverage_pct": 30}},
      "passive_notes": "South-facing living area maximizes daylight; east-facing primary suite for morning light",
      "warnings": []
    }}
  ],
  "recommended_variation": 1,
  "global_warnings": []
}}

INSTRUCTIONS:
1. Keep this EXACT zone structure (front / centre / rear / service).
2. Scale areas to match total_area_sqm={area_sqm or 150} sqm — all spaces must sum to ≈ that total.
3. Use ONLY approved type strings: primary_bedroom, secondary_bedroom, guest_bedroom,
   primary_bath, bathroom, powder_room, walk_in_closet, entry, kitchen, dining, living,
   enclair, sunroom, covered_porch, laundry, mudroom, garage, pantry.
4. Kitchen + dining + living MUST be in ONE zone with zone_position "centre".
5. Do NOT add hallway, corridor, backyard, yard, garden, or any outdoor space.
6. Variation 2: use organisation_type "split_wing", organisation_strategy "split_wing" —
   Y-shaped plan: open living core at front (full stem width), bedroom wing and service wing
   diverging at rear. Consider "living_right" wing_orientation if solar context suits it.
7. Variation 3: use organisation_type "compact_urban", organisation_strategy "spine" —
   tight footprint, efficiency-first, compact bedroom wing.
8. Each variation should adjust room count and sizes to reflect the brief.
9. MANDATORY — use key "zones" (NOT "spaces") at the variation level. The layout engine
   will FAIL SILENTLY if you emit "spaces" instead of "zones" at variation level.
10. Each variation MUST include these variation-level fields: organisation_type (one of:
    ranch/split_wing/open_plan_suburban/courtyard/spine/compact_urban), wing_orientation
    ("living_left" or "living_right"), circulation_sequence (movement sequence string),
    solar_strategy (orientation strategy string). Include garage_placement ("front" or
    "rear") when a garage is present.
11. Every space MUST include "privacy_level" ("public"/"semi_private"/"private"/"service")
    and "adjacency" object with "connected_to", "near", "separated_from" lists.
12. In zone "rear": secondary bedrooms FIRST (near street / low Y), primary suite LAST
    (deepest into lot — rear privacy corner). The layout engine places them in list order.
13. Use the precedent patterns above to justify organisation_type and wing_orientation
    choices — adapt spatial intelligence to this brief, do not copy areas or layouts.
14. Every space MUST include "preferred_width_m", "preferred_depth_m", and "max_width_m"
    (float metres). Kitchen spaces MUST also include "kitchen_type" ("galley", "L_shape",
    "island", or "U_shape") per constraint 11. Omitting these fields will break the
    geometry engine's proportion enforcement."""

    # ── 4. Generate ───────────────────────────────────────────────────────────
    generation: dict = {}
    for attempt in range(1, 3):
        try:
            gen_resp = await asyncio.to_thread(
                client.models.generate_content,
                model=model_name,
                contents=gen_prompt,
                config=types.GenerateContentConfig(
                    system_instruction=GEN_SYSTEM,
                    temperature=0.4,
                    max_output_tokens=16384,
                    response_mime_type="application/json",
                    thinking_config=types.ThinkingConfig(thinking_budget=0),
                ),
            )
            raw = gen_resp.text.strip()
            raw = re.sub(r"^```(?:json)?\s*", "", raw)
            raw = re.sub(r"\s*```$", "", raw)
            parsed = json.loads(raw)
            log.debug("generation_raw_type",
                      attempt=attempt, type=type(parsed).__name__,
                      preview=str(raw)[:120])
            # Unwrap list wrapper — Gemini sometimes returns [{...}] instead of {...}
            # Walk up to 3 levels deep to handle nested list wrapping
            for _ in range(3):
                if not isinstance(parsed, list):
                    break
                if parsed and isinstance(parsed[0], dict):
                    parsed = parsed[0]
                elif parsed and isinstance(parsed[0], list):
                    parsed = parsed[0]  # peel one layer, keep looping
                else:
                    raise json.JSONDecodeError(
                        f"Unwrappable list structure (len={len(parsed)})", raw, 0)
            if not isinstance(parsed, dict):
                raise json.JSONDecodeError(
                    f"Expected JSON object, got {type(parsed).__name__}", raw, 0)
            generation = parsed
            break
        except json.JSONDecodeError as exc:
            if attempt == 2:
                log.error("generation_json_error", error=str(exc))
                raise HTTPException(500, "Generation returned invalid JSON")
            log.warning("generation_json_retry", attempt=attempt)
        except Exception as exc:
            log.error("generation_error", error=str(exc))
            raise HTTPException(500, f"Generation failed: {exc}")

    if not generation:
        raise HTTPException(500, "Generation produced empty response after retries")

    generation["total_area_sqm"] = area_sqm or generation.get("total_area_sqm", 100)
    generation["project_category"] = category
    generation["autocad_units"] = req.autocad_units

    # Inject site constraints so the C# layout engine uses the actual lot size
    # Parse site_area from intent (sqft → sqm) and estimate plot dimensions
    site_area_sqm = None
    if site_ctx.get("plot_area_sqm"):
        site_area_sqm = float(site_ctx["plot_area_sqm"])
    elif site_ctx.get("plot_area_sqft"):
        site_area_sqm = float(site_ctx["plot_area_sqft"]) / 10.764
    # Also try parsing from prompt directly — but ONLY if "lot/site/plot" appears nearby,
    # to avoid treating house area ("2,500 sqft house") as lot size.
    if not site_area_sqm:
        # Require "lot", "site", "plot", or "land" within 60 chars of the sqft figure
        m = re.search(
            r'(?:lot|site|plot|land|property)[^.]{0,60}?(\d[\d,]*)\s*(?:sqft|sq\.?\s*ft)'
            r'|(\d[\d,]*)\s*(?:sqft|sq\.?\s*ft)[^.]{0,60}?(?:lot|site|plot|land|property)',
            req.prompt, re.I)
        if m:
            raw_num = (m.group(1) or m.group(2)).replace(",", "")
            site_area_sqm = float(raw_num) / 10.764

    if site_area_sqm and site_area_sqm > 50:
        # Estimate plot dimensions: assume 2:1 depth:width ratio (US suburban standard,
        # e.g. 50ft wide × 100ft deep = 465 sqm typical suburban lot)
        plot_w_m = math.sqrt(site_area_sqm / 2.0)
        plot_d_m = site_area_sqm / plot_w_m
        generation["site_constraints"] = {
            "plot_width_mm":  round(plot_w_m * 1000),
            "plot_depth_mm":  round(plot_d_m * 1000),
            "front_setback_mm": int((reg_ctx.get("front_setback_m") or 7.5) * 1000),
            "side_setback_mm":  int((reg_ctx.get("side_setback_m")  or 1.5) * 1000),
            "rear_setback_mm":  int((reg_ctx.get("rear_setback_m")  or 7.5) * 1000),
        }

    # ── 5. Compile to DrawActionPlan ──────────────────────────────────────────
    plan = compile_to_plan(generation, request_id, req.autocad_units)
    plan.reference_project_ids = [str(r.get("id", "")) for r in references]
    plan.kb_scores             = [float(r.get("composite_score", 0)) for r in references]

    elapsed = round(time.perf_counter() - t0, 2)
    log.info("orchestrate_complete",
             request_id=request_id,
             variations=len(plan.variations),
             refs_used=len(references),
             elapsed_s=elapsed)

    return plan.model_dump()
