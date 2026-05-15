# GCP Phase 2 (Redesigned) — Universal Architectural Intelligence Platform

> This file supersedes the previous Phase 2 draft.
> The core change: the system is no longer opinionated about project type.
> It understands any building, any scale, any context.
>
> Prerequisite: Phase 1 GCP infrastructure fully deployed and verified.

---

## Architecture decisions made in this redesign

| Previous assumption | Corrected design |
|---|---|
| `layout_type` ENUM ('1BHK','2BHK'...) | Open `project_category` TEXT + `domain_tags[]` |
| Room list only | Full `program_requirements` JSONB — any space type |
| No site geometry | `site_context` JSONB — dimensions, shape, north, slope |
| No constraint storage | `regulatory_constraints` + `environmental_factors` JSONB |
| Single embedding per project | Three embeddings: spatial + site + design intent |
| Hardcoded adjacency rules | AI-extracted relational graph per project |
| BHK-based retrieval | Multi-vector hybrid search across all three embedding types |
| Generation from template | Constraint-aware generation from project brief |

---

## Step 0 — Re-export variables

```bash
export PROJECT_ID="zheight-ai-kb"
export PROJECT_NUMBER=$(gcloud projects describe ${PROJECT_ID} \
  --format="value(projectNumber)")
export REGION="us-central1"
export NETWORK_NAME="${PROJECT_ID}-vpc"
export DB_INSTANCE="${PROJECT_ID}-pg"
export DB_NAME="zheight_kb"
export DB_USER="kb_admin"
export DB_PASSWORD=""                      # from Phase 1
export KB_BUCKET="gs://${PROJECT_ID}-kb-raw"
export PROCESSED_BUCKET="gs://${PROJECT_ID}-kb-processed"
export SA_INGESTION="sa-ingestion"
export SA_SERVING="sa-serving"
export REPO="${REGION}-docker.pkg.dev/${PROJECT_ID}/zheight-services"
export CONNECTOR="projects/${PROJECT_ID}/locations/${REGION}/connectors/${NETWORK_NAME}-connector"
export CLOUD_SQL_CONN="${PROJECT_ID}:${REGION}:${DB_INSTANCE}"
export REDIS_HOST=$(gcloud redis instances describe "${PROJECT_ID}-cache" \
  --region=${REGION} --format="value(host)")
export REDIS_PORT=$(gcloud redis instances describe "${PROJECT_ID}-cache" \
  --region=${REGION} --format="value(port)")
```

---

## Step 1 — Drop and rebuild the database schema

> This replaces the Phase 1 schema with the universal open schema.
> Run this before deploying any services.

```bash
# Start Cloud SQL Auth Proxy
curl -o cloud-sql-proxy \
  https://storage.googleapis.com/cloud-sql-connectors/cloud-sql-proxy/v2.8.0/cloud-sql-proxy.linux.amd64
chmod +x cloud-sql-proxy
./cloud-sql-proxy ${CLOUD_SQL_CONN} --port=5433 &
PROXY_PID=$!
sleep 5

PGPASSWORD=${DB_PASSWORD} psql \
  -h 127.0.0.1 -p 5433 \
  -U ${DB_USER} -d ${DB_NAME} << 'SCHEMA'

-- Extensions
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- ─────────────────────────────────────────────────────────────────────────────
-- CORE: Universal project table
-- No fixed enums. Everything is open text or JSONB.
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS projects (
  id                      UUID PRIMARY KEY DEFAULT uuid_generate_v4(),

  -- Identity (open, not enumerated)
  project_name            TEXT,
  project_category        TEXT,           -- 'residential','commercial','institutional','industrial','mixed_use'
  project_sub_type        TEXT,           -- 'school','hospital','warehouse','villa' — free text, no enum
  domain_tags             TEXT[],         -- ['healthcare','critical_care','ICU'] or ['retail','F&B','QSR']

  -- Scale
  total_built_area_sqm    NUMERIC(12,2),
  total_built_area_sqft   NUMERIC(12,2),
  floor_count             INT,
  basement_levels         INT DEFAULT 0,
  units_count             INT,            -- apartments/rooms in a multi-unit project

  -- Site context (full geometry + environment)
  site_context            JSONB,
  -- {
  --   "plot_area_sqm": 500,
  --   "plot_shape": "irregular_L",         -- rectangle, L-shape, triangle, irregular
  --   "dimensions_m": {"frontage": 20, "depth": 25},
  --   "north_direction": "top_left",        -- which corner/edge faces north
  --   "slope_percent": 5,
  --   "slope_direction": "south",
  --   "road_access": ["north_side","east_side"],
  --   "neighboring_buildings": {"north":"high_rise","south":"open"},
  --   "views": ["sea_east","park_north"],
  --   "noise_sources": ["road_north"],
  --   "climate_zone": "hot_dry",
  --   "sun_path_notes": "strong western exposure"
  -- }

  -- Regulatory constraints
  regulatory_constraints  JSONB,
  -- {
  --   "far": 2.5,
  --   "fsi": 1.8,
  --   "ground_coverage_percent": 40,
  --   "max_height_m": 18,
  --   "front_setback_m": 3,
  --   "side_setback_m": 1.5,
  --   "rear_setback_m": 3,
  --   "parking_ratio": "1_per_unit",
  --   "jurisdiction": "BBMP Bengaluru",
  --   "code_version": "BBMP 2023",
  --   "special_restrictions": ["heritage_zone","flood_plain"]
  -- }

  -- Program requirements (what spaces the project needs)
  program_requirements    JSONB,
  -- [
  --   {"space_name":"ICU","space_type":"clinical","area_sqm":200,"quantity":1,
  --    "requirements":["negative_pressure","24hr_access","adjacent_to:nurse_station"]},
  --   {"space_name":"OPD Waiting","space_type":"waiting","area_sqm":80,"quantity":2}
  -- ]

  -- Environmental + design intent
  environmental_factors   JSONB,
  -- {
  --   "passive_cooling_required": true,
  --   "cross_ventilation_priority": "high",
  --   "daylighting_strategy": "courtyard",
  --   "rainwater_harvesting": true,
  --   "green_roof": false,
  --   "solar_orientation_priority": true
  -- }

  design_intent           JSONB,
  -- {
  --   "style": "contemporary_vernacular",
  --   "cultural_context": "South Indian",
  --   "vastu_compliance": true,
  --   "feng_shui": false,
  --   "materiality_preference": "exposed_brick_glass",
  --   "privacy_levels": {"street":"high","garden":"medium"},
  --   "flexibility_requirement": "open_plan_convertible",
  --   "design_philosophy": "biophilic",
  --   "target_certification": "LEED_Gold"
  -- }

  -- AI extraction metadata
  ai_extraction_model     TEXT,           -- which model did the extraction
  ai_extraction_version   TEXT,
  extraction_confidence   NUMERIC(4,3),   -- 0.0 – 1.0
  extraction_warnings     TEXT[],         -- fields the AI was uncertain about

  -- Storage
  raw_file_paths          TEXT[],         -- GCS paths, multiple files per project
  processed_json_path     TEXT,
  file_types_uploaded     TEXT[],         -- ['dwg','pdf','text_brief']

  -- Lifecycle
  approved                BOOLEAN DEFAULT FALSE,
  quality_score           NUMERIC(4,2),
  architect_id            TEXT,
  company_id              TEXT,
  schema_version          INT DEFAULT 2,
  created_at              TIMESTAMPTZ DEFAULT NOW(),
  updated_at              TIMESTAMPTZ DEFAULT NOW(),
  approved_at             TIMESTAMPTZ
);

-- ─────────────────────────────────────────────────────────────────────────────
-- SPACES: replaces the rigid 'rooms' table
-- Works for rooms, zones, floors, clusters — any spatial unit
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS spaces (
  id                UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_id        UUID REFERENCES projects(id) ON DELETE CASCADE,

  space_name        TEXT NOT NULL,
  space_type        TEXT NOT NULL,         -- 'bedroom','OPD','trading_floor','prayer_hall'
  space_category    TEXT,                  -- 'private','semi_public','public','service','circulation'
  floor_number      INT DEFAULT 1,
  area_sqm          NUMERIC(8,2),
  area_sqft         NUMERIC(8,2),
  width_m           NUMERIC(6,2),
  length_m          NUMERIC(6,2),
  height_m          NUMERIC(5,2),

  -- Spatial properties
  facing_direction  TEXT,                  -- 'north','south','east','west','courtyard'
  has_natural_light BOOLEAN DEFAULT TRUE,
  has_cross_vent    BOOLEAN DEFAULT FALSE,
  has_direct_access_to TEXT[],             -- space names it connects to directly
  is_critical_space BOOLEAN DEFAULT FALSE, -- ICU, server room, prayer hall etc
  special_requirements TEXT[],            -- ['negative_pressure','24hr_access','acoustic_isolation']

  -- Approximate position in layout (relative, 0-100 normalized)
  position_x_norm   NUMERIC(5,2),
  position_y_norm   NUMERIC(5,2),

  created_at        TIMESTAMPTZ DEFAULT NOW()
);

-- ─────────────────────────────────────────────────────────────────────────────
-- SPATIAL RELATIONSHIPS: replaces fixed adjacency table
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS spatial_relationships (
  id                UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_id        UUID REFERENCES projects(id) ON DELETE CASCADE,

  space_a           TEXT NOT NULL,
  space_b           TEXT NOT NULL,
  relationship_type TEXT NOT NULL,
  -- 'must_be_adjacent','should_be_adjacent','must_be_separated',
  -- 'connected_by_corridor','visual_connection','acoustic_buffer_required',
  -- 'shared_services','back_of_house_connection','emergency_access'

  relationship_reason TEXT,               -- why this relationship exists
  priority          TEXT DEFAULT 'preferred',  -- 'required','preferred','avoid'
  is_ai_extracted   BOOLEAN DEFAULT TRUE,
  is_architect_confirmed BOOLEAN DEFAULT FALSE,

  created_at        TIMESTAMPTZ DEFAULT NOW()
);

-- ─────────────────────────────────────────────────────────────────────────────
-- MULTI-LAYER EMBEDDINGS: three vectors per project
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS project_embeddings (
  id                UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_id        UUID REFERENCES projects(id) ON DELETE CASCADE,

  embedding_type    TEXT NOT NULL,
  -- 'spatial'  — encodes space list, areas, relationships, circulation
  -- 'site'     — encodes site dimensions, orientation, climate, constraints
  -- 'intent'   — encodes style, culture, sustainability, design philosophy

  embedding         VECTOR(1536),
  embedding_text    TEXT,                  -- the text that was embedded
  model_version     TEXT DEFAULT 'text-embedding-004',
  created_at        TIMESTAMPTZ DEFAULT NOW(),

  UNIQUE(project_id, embedding_type)
);

-- IVFFlat indexes — one per embedding type (rebuild after 500+ rows)
CREATE INDEX IF NOT EXISTS idx_embed_spatial
  ON project_embeddings USING ivfflat (embedding vector_cosine_ops)
  WITH (lists = 100)
  WHERE embedding_type = 'spatial';

CREATE INDEX IF NOT EXISTS idx_embed_site
  ON project_embeddings USING ivfflat (embedding vector_cosine_ops)
  WITH (lists = 100)
  WHERE embedding_type = 'site';

CREATE INDEX IF NOT EXISTS idx_embed_intent
  ON project_embeddings USING ivfflat (embedding vector_cosine_ops)
  WITH (lists = 100)
  WHERE embedding_type = 'intent';

-- ─────────────────────────────────────────────────────────────────────────────
-- BUILDING CODES / REGULATIONS: jurisdiction-aware, versioned
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS regulations (
  id                UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  jurisdiction      TEXT NOT NULL,
  code_body         TEXT,                  -- 'BBMP','BIS','NBC','RERA','local_authority'
  code_version      TEXT NOT NULL,
  project_category  TEXT,                  -- null = applies to all
  rule_category     TEXT NOT NULL,
  -- 'setback','far','fsi','height','parking','fire_egress',
  -- 'accessibility','green_norms','structural','electrical','plumbing'
  rule_name         TEXT NOT NULL,
  rule_description  TEXT NOT NULL,
  rule_value        JSONB,                 -- {"min_m": 3, "unit": "metres"}
  applies_to_zones  TEXT[],
  effective_date    DATE,
  expiry_date       DATE,
  is_active         BOOLEAN DEFAULT TRUE,
  source_document   TEXT,
  created_at        TIMESTAMPTZ DEFAULT NOW()
);

-- ─────────────────────────────────────────────────────────────────────────────
-- ARCHITECT CORRECTIONS: the primary training signal
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS architect_corrections (
  id                    UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  source_project_id     UUID REFERENCES projects(id),
  architect_id          TEXT,
  correction_type       TEXT NOT NULL,
  -- 'space_resize','space_relocation','relationship_change',
  -- 'program_addition','constraint_override','intent_clarification',
  -- 'regulation_correction','full_regeneration'
  original_generation   JSONB,
  corrected_output      JSONB,
  correction_notes      TEXT,
  severity              TEXT DEFAULT 'minor',   -- 'minor','major','fundamental'
  used_in_training      BOOLEAN DEFAULT FALSE,
  training_run_id       TEXT,
  created_at            TIMESTAMPTZ DEFAULT NOW()
);

-- ─────────────────────────────────────────────────────────────────────────────
-- TRAINING REGISTRY
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS training_runs (
  id                UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  run_name          TEXT NOT NULL,
  trigger_type      TEXT,                  -- 'scheduled','correction_threshold','manual'
  dataset_gcs_path  TEXT,
  project_count     INT,
  correction_count  INT,
  project_categories TEXT[],              -- what categories this run covers
  vertex_job_id     TEXT,
  vertex_model_id   TEXT,
  vertex_endpoint_id TEXT,
  eval_scores       JSONB,
  is_production     BOOLEAN DEFAULT FALSE,
  created_at        TIMESTAMPTZ DEFAULT NOW(),
  completed_at      TIMESTAMPTZ
);

-- ─────────────────────────────────────────────────────────────────────────────
-- INDEXES
-- ─────────────────────────────────────────────────────────────────────────────
CREATE INDEX IF NOT EXISTS idx_projects_category   ON projects(project_category);
CREATE INDEX IF NOT EXISTS idx_projects_tags       ON projects USING gin(domain_tags);
CREATE INDEX IF NOT EXISTS idx_projects_approved   ON projects(approved);
CREATE INDEX IF NOT EXISTS idx_projects_company    ON projects(company_id);
CREATE INDEX IF NOT EXISTS idx_spaces_project      ON spaces(project_id);
CREATE INDEX IF NOT EXISTS idx_spaces_type         ON spaces(space_type);
CREATE INDEX IF NOT EXISTS idx_relationships_proj  ON spatial_relationships(project_id);
CREATE INDEX IF NOT EXISTS idx_regs_jurisdiction   ON regulations(jurisdiction, is_active);
CREATE INDEX IF NOT EXISTS idx_corrections_training ON architect_corrections(used_in_training);

-- Full-text search on project names and types
CREATE INDEX IF NOT EXISTS idx_projects_fts ON projects
  USING gin(to_tsvector('english',
    coalesce(project_name,'') || ' ' ||
    coalesce(project_category,'') || ' ' ||
    coalesce(project_sub_type,'')));

-- Updated-at trigger
CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER AS $$ BEGIN NEW.updated_at = NOW(); RETURN NEW; END; $$ LANGUAGE plpgsql;

CREATE TRIGGER trg_projects_updated
  BEFORE UPDATE ON projects
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

SCHEMA

kill ${PROXY_PID}
echo "✓ Universal schema deployed"
```

---

## Step 2 — Create project directory structure

```bash
mkdir -p zheight-kb-services && cd zheight-kb-services

mkdir -p services/ingestion/app/{parsers,ai_extractor,embedder}
mkdir -p services/rag-api/app/{core,routers}
mkdir -p services/quality-gate/app
mkdir -p shared/{db,models,utils}
mkdir -p scripts/{upload,seed,test}

echo "✓ Directory structure ready"
```

---

## Step 3 — Universal Project Schema (shared model)

```bash
cat > shared/models/project_schema.py << 'PYEOF'
"""
UniversalProjectSchema — the single data contract used by all services.
No fixed enums. The AI extracts whatever the project actually is.
"""
from __future__ import annotations
from typing import Any
from pydantic import BaseModel, Field


class SiteContext(BaseModel):
    plot_area_sqm: float | None = None
    plot_shape: str | None = None          # 'rectangle','L_shape','triangle','irregular'
    dimensions_m: dict | None = None       # {"frontage": 20, "depth": 25}
    north_direction: str | None = None     # which edge/corner faces north
    slope_percent: float | None = None
    slope_direction: str | None = None
    road_access: list[str] = []
    neighboring_context: dict = {}
    views: list[str] = []
    noise_sources: list[str] = []
    climate_zone: str | None = None
    sun_path_notes: str | None = None
    special_site_conditions: list[str] = []


class RegulatoryConstraints(BaseModel):
    far: float | None = None
    fsi: float | None = None
    ground_coverage_percent: float | None = None
    max_height_m: float | None = None
    front_setback_m: float | None = None
    side_setback_m: float | None = None
    rear_setback_m: float | None = None
    parking_ratio: str | None = None
    jurisdiction: str | None = None
    code_version: str | None = None
    special_restrictions: list[str] = []
    additional_rules: dict = {}


class SpaceRequirement(BaseModel):
    space_name: str
    space_type: str                        # any string — 'bedroom','ICU','prayer_hall'
    space_category: str | None = None     # 'private','public','service','circulation'
    area_sqm: float | None = None
    area_sqft: float | None = None
    quantity: int = 1
    facing_preference: str | None = None
    is_critical: bool = False
    special_requirements: list[str] = []
    must_be_adjacent_to: list[str] = []
    must_be_separated_from: list[str] = []
    notes: str | None = None


class SpatialRelationship(BaseModel):
    space_a: str
    space_b: str
    relationship_type: str
    reason: str | None = None
    priority: str = "preferred"            # 'required','preferred','avoid'


class EnvironmentalFactors(BaseModel):
    passive_cooling_required: bool = False
    cross_ventilation_priority: str | None = None   # 'low','medium','high'
    daylighting_strategy: str | None = None
    rainwater_harvesting: bool = False
    solar_orientation_priority: bool = False
    green_roof: bool = False
    additional_strategies: list[str] = []


class DesignIntent(BaseModel):
    style: str | None = None               # 'contemporary','vernacular','modernist' etc
    cultural_context: str | None = None
    vastu_compliance: bool = False
    feng_shui: bool = False
    materiality_preference: str | None = None
    privacy_strategy: dict = {}
    flexibility_requirement: str | None = None
    design_philosophy: str | None = None
    target_certification: str | None = None  # 'LEED_Gold','GRIHA_4Star'
    circulation_concept: str | None = None
    additional_notes: str | None = None


class UniversalProjectSchema(BaseModel):
    """
    The universal contract. Every project — residential, hospital, school,
    warehouse, temple — gets represented through this schema.
    Fields are nullable. The AI fills what it can extract.
    """
    # Identity
    project_name: str | None = None
    project_category: str              # 'residential','commercial','institutional' etc
    project_sub_type: str | None = None
    domain_tags: list[str] = []

    # Scale
    total_built_area_sqm: float | None = None
    total_built_area_sqft: float | None = None
    floor_count: int = 1
    basement_levels: int = 0
    units_count: int | None = None

    # Core data
    site_context: SiteContext = Field(default_factory=SiteContext)
    regulatory_constraints: RegulatoryConstraints = Field(default_factory=RegulatoryConstraints)
    program_requirements: list[SpaceRequirement] = []
    spatial_relationships: list[SpatialRelationship] = []
    environmental_factors: EnvironmentalFactors = Field(default_factory=EnvironmentalFactors)
    design_intent: DesignIntent = Field(default_factory=DesignIntent)

    # AI extraction metadata
    extraction_confidence: float = 0.0
    extraction_warnings: list[str] = []
    ai_model_used: str = ""

    # Raw source data
    raw_parsed_data: dict = {}
    file_types: list[str] = []
PYEOF
echo "✓ UniversalProjectSchema written"
```

---

## Step 4 — AI extraction layer (the critical new component)

```bash
cat > services/ingestion/app/ai_extractor/extractor.py << 'PYEOF'
"""
AI Extraction Layer — the core of the universal pipeline.

Takes raw parsed output from any file type and uses Gemini to produce
a structured UniversalProjectSchema. This is what replaces the old
hardcoded type-inference logic.

The model reads the raw text, entities, dimensions, annotations —
whatever the parser could extract — and understands what the project
actually is, filling the schema accordingly.
"""
import os
import json
import structlog
import vertexai
from vertexai.generative_models import GenerativeModel, GenerationConfig

log = structlog.get_logger()
_model = None

EXTRACTION_SYSTEM_PROMPT = """
You are an expert architectural data analyst. You will receive raw parsed
output from architectural files (DWG, PDF, images, text briefs) and must
extract a structured JSON project description.

Your job is to understand the project regardless of type:
- Residential: apartments, villas, row houses, dormitories
- Commercial: offices, retail, hotels, restaurants, banks, data centres
- Institutional: hospitals, schools, colleges, government buildings, courts
- Industrial: factories, warehouses, logistics hubs
- Mixed-use, transit hubs, sports facilities, places of worship
- Any other building type

EXTRACTION RULES:
1. Never assume a project is residential unless the data clearly indicates it
2. Extract ALL spaces/rooms/zones you can identify — use exact names found
3. Identify spatial relationships from proximity, annotation grouping, notes
4. Extract ALL numerical constraints: dimensions, setbacks, FAR, heights
5. Capture design intent: style notes, cultural references, sustainability goals
6. If a field is unclear, leave it null — do not guess
7. Add a warning to extraction_warnings for any field you are uncertain about
8. extraction_confidence: 0.9+ = very clear, 0.7 = partial, 0.5 = uncertain

OUTPUT: Valid JSON only. No markdown. No explanation outside the JSON.
Respond with exactly the UniversalProjectSchema structure.
"""

EXTRACTION_SCHEMA = """
{
  "project_name": null or string,
  "project_category": string,        // 'residential','commercial','institutional','industrial','mixed_use','religious','sports','transport'
  "project_sub_type": null or string, // 'hospital','school','warehouse','villa','restaurant' etc - exact type
  "domain_tags": [],                 // specific tags: ['healthcare','critical_care'] or ['retail','F&B']
  "total_built_area_sqm": null or number,
  "total_built_area_sqft": null or number,
  "floor_count": 1,
  "basement_levels": 0,
  "units_count": null,
  "site_context": {
    "plot_area_sqm": null, "plot_shape": null,
    "dimensions_m": null, "north_direction": null,
    "slope_percent": null, "road_access": [],
    "views": [], "noise_sources": [], "climate_zone": null
  },
  "regulatory_constraints": {
    "far": null, "fsi": null, "ground_coverage_percent": null,
    "max_height_m": null, "front_setback_m": null,
    "side_setback_m": null, "rear_setback_m": null,
    "jurisdiction": null, "code_version": null,
    "special_restrictions": []
  },
  "program_requirements": [
    {
      "space_name": "string",
      "space_type": "string",
      "space_category": null,
      "area_sqm": null, "area_sqft": null,
      "quantity": 1, "facing_preference": null,
      "is_critical": false,
      "special_requirements": [],
      "must_be_adjacent_to": [],
      "must_be_separated_from": []
    }
  ],
  "spatial_relationships": [
    {"space_a": "string", "space_b": "string",
     "relationship_type": "string", "reason": null, "priority": "preferred"}
  ],
  "environmental_factors": {
    "passive_cooling_required": false,
    "cross_ventilation_priority": null,
    "daylighting_strategy": null
  },
  "design_intent": {
    "style": null, "cultural_context": null,
    "vastu_compliance": false, "feng_shui": false,
    "design_philosophy": null, "target_certification": null
  },
  "extraction_confidence": 0.0,
  "extraction_warnings": []
}
"""


def _get_model() -> GenerativeModel:
    global _model
    if not _model:
        vertexai.init(
            project=os.environ["GCP_PROJECT"],
            location=os.environ.get("GCP_REGION", "us-central1")
        )
        _model = GenerativeModel(
            model_name="gemini-1.5-pro",
            system_instruction=EXTRACTION_SYSTEM_PROMPT,
        )
    return _model


async def extract_project_schema(
    parsed_data: dict,
    filename: str,
    file_type: str,
    user_brief: str = "",
) -> dict:
    """
    Core extraction function. Takes raw parsed output, returns UniversalProjectSchema dict.

    parsed_data: output from the format-specific parser (ezdxf, PyMuPDF etc)
    user_brief: optional text brief the architect provided at upload time
    """
    model = _get_model()

    # Build the extraction prompt from available data
    prompt_parts = [
        f"File type: {file_type}",
        f"Filename: {filename}",
    ]

    if user_brief:
        prompt_parts.append(f"\nArchitect's project brief:\n{user_brief}")

    # Include what the parser found — trimmed to fit context
    parse_summary = _summarise_parse(parsed_data, file_type)
    prompt_parts.append(f"\nParsed file data:\n{parse_summary}")

    prompt_parts.append(f"\nExtract into this JSON schema:\n{EXTRACTION_SCHEMA}")

    full_prompt = "\n".join(prompt_parts)

    log.info("ai_extraction_start", filename=filename, file_type=file_type,
             brief_provided=bool(user_brief))

    try:
        response = model.generate_content(
            full_prompt,
            generation_config=GenerationConfig(
                temperature=0.1,      # low temperature for structured extraction
                max_output_tokens=4096,
                response_mime_type="application/json",
            ),
        )
        extracted = json.loads(response.text)
        extracted["ai_model_used"] = "gemini-1.5-pro"
        extracted["raw_parsed_data"] = {"summary": parse_summary[:500]}
        extracted["file_types"] = [file_type]

        log.info("ai_extraction_complete",
                 filename=filename,
                 category=extracted.get("project_category"),
                 sub_type=extracted.get("project_sub_type"),
                 confidence=extracted.get("extraction_confidence"),
                 spaces_found=len(extracted.get("program_requirements", [])))

        return extracted

    except json.JSONDecodeError as e:
        log.error("extraction_json_error", filename=filename, error=str(e))
        return _fallback_schema(filename, file_type, parsed_data)
    except Exception as e:
        log.error("extraction_error", filename=filename, error=str(e))
        return _fallback_schema(filename, file_type, parsed_data)


def _summarise_parse(parsed: dict, file_type: str) -> str:
    """Converts raw parse output to a compact string for the extraction prompt."""
    parts = []

    if file_type in ("dwg", "dxf"):
        annotations = parsed.get("annotations", [])[:50]
        walls = parsed.get("walls", [])
        doors = parsed.get("doors", [])
        windows = parsed.get("windows", [])
        layers = parsed.get("layers", [])[:30]

        parts.append(f"Layers: {', '.join(layers)}")
        parts.append(f"Text annotations found ({len(annotations)}):")
        for ann in annotations[:40]:
            parts.append(f"  '{ann.get('text','')}'  at ({ann.get('x')},{ann.get('y')})")
        parts.append(f"Wall polylines: {len(walls)}")
        parts.append(f"Door blocks: {len(doors)}")
        parts.append(f"Window blocks: {len(windows)}")

    elif file_type == "pdf":
        parts.append(f"Page count: {parsed.get('page_count', 1)}")
        parts.append("Text extracted:")
        parts.append(parsed.get("raw_text_preview", "")[:2000])
        dims = parsed.get("dimensions_found", [])[:20]
        if dims:
            parts.append(f"Dimensions found: {dims}")
        areas = parsed.get("areas_sqft", [])[:10]
        if areas:
            parts.append(f"Area values (sqft): {areas}")

    elif file_type == "image":
        parts.append("Image file — awaiting Document AI OCR result")
        parts.append(f"MIME type: {parsed.get('mime_type','')}")

    else:
        # Text brief or unknown
        parts.append(str(parsed)[:3000])

    return "\n".join(parts)


def _fallback_schema(filename: str, file_type: str, parsed: dict) -> dict:
    """Minimal fallback when AI extraction fails."""
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
        "extraction_warnings": ["AI extraction failed — manual review required"],
        "ai_model_used": "fallback",
        "raw_parsed_data": {},
        "file_types": [file_type],
    }
PYEOF
echo "✓ AI extraction layer written"
```

---

## Step 5 — Multi-layer embedder

```bash
cat > services/ingestion/app/embedder/multi_embedder.py << 'PYEOF'
"""
Multi-layer embedder — generates three separate embedding vectors per project.

spatial  — what spaces exist, how large, how they relate
site     — where it sits, what constraints it faces
intent   — what it should feel like, cultural/sustainability goals

Three separate vectors means retrieval can weight them independently:
- "find similar hospital layouts" → weight spatial heavily
- "find projects on awkward L-shaped plots" → weight site heavily
- "find biophilic Vastu-compliant designs" → weight intent heavily
"""
import os
import structlog
import vertexai
from vertexai.language_models import TextEmbeddingModel

log = structlog.get_logger()
_model = None


def _get_model() -> TextEmbeddingModel:
    global _model
    if not _model:
        vertexai.init(
            project=os.environ["GCP_PROJECT"],
            location=os.environ.get("GCP_REGION", "us-central1")
        )
        _model = TextEmbeddingModel.from_pretrained("text-embedding-004")
    return _model


def build_spatial_text(schema: dict) -> str:
    """Encodes the spatial programme: what spaces, sizes, relationships."""
    category = schema.get("project_category", "")
    sub_type = schema.get("project_sub_type", "")
    tags = " ".join(schema.get("domain_tags", []))
    floors = schema.get("floor_count", 1)
    area_sqm = schema.get("total_built_area_sqm", "")
    units = schema.get("units_count", "")

    spaces = schema.get("program_requirements", [])
    space_lines = []
    for s in spaces:
        line = f"{s.get('quantity',1)}x {s.get('space_name','')} ({s.get('space_type','')})"
        if s.get("area_sqm"):
            line += f" {s['area_sqm']}sqm"
        if s.get("is_critical"):
            line += " [critical]"
        if s.get("special_requirements"):
            line += f" [{', '.join(s['special_requirements'][:3])}]"
        space_lines.append(line)

    rels = schema.get("spatial_relationships", [])
    rel_lines = [
        f"{r.get('space_a')} {r.get('relationship_type')} {r.get('space_b')}"
        for r in rels[:15]
    ]

    return (
        f"Project: {category} {sub_type} {tags}. "
        f"Floors: {floors}. Area: {area_sqm}sqm. Units: {units}. "
        f"Spaces: {'; '.join(space_lines[:30])}. "
        f"Relationships: {'; '.join(rel_lines)}."
    ).strip()


def build_site_text(schema: dict) -> str:
    """Encodes site and regulatory context."""
    site = schema.get("site_context", {})
    reg = schema.get("regulatory_constraints", {})
    env = schema.get("environmental_factors", {})

    return (
        f"Site: {site.get('plot_area_sqm','')}sqm {site.get('plot_shape','')} plot. "
        f"Frontage: {site.get('dimensions_m',{}).get('frontage','')}m. "
        f"North facing: {site.get('north_direction','')}. "
        f"Slope: {site.get('slope_percent','')}% {site.get('slope_direction','')}. "
        f"Road access: {', '.join(site.get('road_access',[]))}. "
        f"Views: {', '.join(site.get('views',[]))}. "
        f"Climate: {site.get('climate_zone','')}. "
        f"FAR: {reg.get('far','')}. FSI: {reg.get('fsi','')}. "
        f"Height limit: {reg.get('max_height_m','')}m. "
        f"Setbacks F/S/R: {reg.get('front_setback_m','')}/{reg.get('side_setback_m','')}/{reg.get('rear_setback_m','')}m. "
        f"Jurisdiction: {reg.get('jurisdiction','')} {reg.get('code_version','')}. "
        f"Special: {', '.join(reg.get('special_restrictions',[]))}. "
        f"Cross ventilation: {env.get('cross_ventilation_priority','')}. "
        f"Daylighting: {env.get('daylighting_strategy','')}."
    ).strip()


def build_intent_text(schema: dict) -> str:
    """Encodes design philosophy, culture, sustainability goals."""
    intent = schema.get("design_intent", {})
    env = schema.get("environmental_factors", {})
    tags = " ".join(schema.get("domain_tags", []))

    return (
        f"Style: {intent.get('style','')}. "
        f"Cultural context: {intent.get('cultural_context','')}. "
        f"Vastu: {intent.get('vastu_compliance',False)}. "
        f"Feng Shui: {intent.get('feng_shui',False)}. "
        f"Philosophy: {intent.get('design_philosophy','')}. "
        f"Materiality: {intent.get('materiality_preference','')}. "
        f"Flexibility: {intent.get('flexibility_requirement','')}. "
        f"Certification: {intent.get('target_certification','')}. "
        f"Passive cooling: {env.get('passive_cooling_required',False)}. "
        f"Solar priority: {env.get('solar_orientation_priority',False)}. "
        f"Green roof: {env.get('green_roof',False)}. "
        f"Domain: {tags}. "
        f"Notes: {intent.get('additional_notes','')}."
    ).strip()


def generate_embeddings(texts: list[str]) -> list[list[float]]:
    model = _get_model()
    results = model.get_embeddings(texts)
    return [r.values for r in results]


async def embed_project(schema: dict) -> dict[str, dict]:
    """
    Returns three named embedding results.
    Called once per project after AI extraction.
    """
    spatial_text = build_spatial_text(schema)
    site_text = build_site_text(schema)
    intent_text = build_intent_text(schema)

    log.info("multi_embed_start",
             spatial_preview=spatial_text[:80],
             site_preview=site_text[:80],
             intent_preview=intent_text[:80])

    vectors = generate_embeddings([spatial_text, site_text, intent_text])

    return {
        "spatial":  {"text": spatial_text,  "vector": vectors[0]},
        "site":     {"text": site_text,      "vector": vectors[1]},
        "intent":   {"text": intent_text,    "vector": vectors[2]},
    }
PYEOF
echo "✓ Multi-layer embedder written"
```

---

## Step 6 — Ingestion service main app (universal pipeline)

```bash
cat > services/ingestion/app/main.py << 'PYEOF'
"""
Universal Ingestion Service.
Pipeline: upload → parse → AI extraction → multi-embed → DB write → quality gate
"""
import base64, json, os
import structlog
from fastapi import FastAPI, Request, HTTPException
from google.cloud import storage, pubsub_v1
from sqlalchemy import text

from .parsers.dwg_parser import DWGParser
from .parsers.pdf_parser import PDFParser
from .parsers.image_parser import ImageParser
from .ai_extractor.extractor import extract_project_schema
from .embedder.multi_embedder import embed_project

import sys
sys.path.insert(0, '/app')
from shared.db.client import get_db

log = structlog.get_logger()
app = FastAPI(title="zHeight Universal Ingestion")

storage_client = storage.Client()
publisher = pubsub_v1.PublisherClient()
PROJECT_ID = os.environ.get("GCP_PROJECT", "")
PROCESSED_BUCKET = os.environ.get("PROCESSED_BUCKET", "")


@app.get("/health")
async def health():
    return {"status": "ok", "service": "ingestion-universal"}


@app.post("/ingest")
async def ingest(request: Request):
    body = await request.json()
    message = body.get("message", {})
    data_b64 = message.get("data", "")

    try:
        data = json.loads(base64.b64decode(data_b64).decode("utf-8"))
    except Exception as e:
        raise HTTPException(status_code=400, detail=f"Bad Pub/Sub message: {e}")

    bucket_name = data.get("bucket", "")
    file_name = data.get("name", "")

    if not bucket_name or not file_name:
        return {"status": "skipped"}
    if file_name.endswith(".keep") or file_name.endswith("/"):
        return {"status": "skipped", "reason": "placeholder"}

    log.info("ingest_start", file=file_name)

    # ── 1. Download ──────────────────────────────────────────────────────────
    bucket = storage_client.bucket(bucket_name)
    blob = bucket.blob(file_name)
    file_bytes = blob.download_as_bytes()
    filename = file_name.split("/")[-1]
    ext = filename.rsplit(".", 1)[-1].lower()

    # ── 2. Parse ─────────────────────────────────────────────────────────────
    if ext in ("dwg", "dxf"):
        parsed = DWGParser().parse(file_bytes, filename)
        file_type = "dwg"
    elif ext == "pdf":
        parsed = PDFParser().parse(file_bytes, filename)
        file_type = "pdf"
    elif ext in ("png", "jpg", "jpeg", "tiff", "tif"):
        parsed = ImageParser().parse(file_bytes, filename, PROJECT_ID)
        file_type = "image"
    elif ext in ("txt", "md"):
        parsed = {"raw_text": file_bytes.decode("utf-8", errors="ignore"), "file_type": "brief"}
        file_type = "brief"
    else:
        log.warning("unsupported_type", ext=ext)
        return {"status": "skipped", "reason": f"unsupported: {ext}"}

    # ── 3. Retrieve optional user brief from GCS metadata ───────────────────
    user_brief = blob.metadata.get("x-project-brief", "") if blob.metadata else ""

    # ── 4. AI extraction (the universal understanding step) ──────────────────
    schema = await extract_project_schema(
        parsed_data=parsed,
        filename=filename,
        file_type=file_type,
        user_brief=user_brief,
    )

    # ── 5. Multi-layer embedding ─────────────────────────────────────────────
    embeddings = await embed_project(schema)

    # ── 6. Save processed JSON to GCS ────────────────────────────────────────
    processed_key = f"parsed/{file_name.replace('/', '_')}.json"
    out_bucket = storage_client.bucket(PROCESSED_BUCKET.replace("gs://", ""))
    out_bucket.blob(processed_key).upload_from_string(
        json.dumps({"schema": schema, "embeddings": {
            k: {"text": v["text"]} for k, v in embeddings.items()
        }}, default=str),
        content_type="application/json",
    )

    # ── 7. Write to Cloud SQL ────────────────────────────────────────────────
    project_db_id = await _write_to_db(
        schema, embeddings,
        raw_path=f"gs://{bucket_name}/{file_name}",
        processed_path=f"gs://{PROCESSED_BUCKET}/{processed_key}",
        file_type=file_type,
    )

    # ── 8. Publish for quality gate notification ─────────────────────────────
    publisher.publish(
        f"projects/{PROJECT_ID}/topics/layout-embedded",
        json.dumps({
            "project_id": project_db_id,
            "file": file_name,
            "category": schema.get("project_category"),
            "sub_type": schema.get("project_sub_type"),
            "confidence": schema.get("extraction_confidence"),
        }).encode("utf-8")
    )

    log.info("ingest_complete",
             project_id=project_db_id,
             category=schema.get("project_category"),
             confidence=schema.get("extraction_confidence"))

    return {
        "status": "ok",
        "project_id": project_db_id,
        "category": schema.get("project_category"),
        "sub_type": schema.get("project_sub_type"),
        "spaces_extracted": len(schema.get("program_requirements", [])),
        "confidence": schema.get("extraction_confidence"),
    }


async def _write_to_db(schema: dict, embeddings: dict,
                        raw_path: str, processed_path: str,
                        file_type: str) -> str:
    import json as _json
    async with get_db() as session:
        result = await session.execute(
            text("""
                INSERT INTO projects (
                    project_name, project_category, project_sub_type, domain_tags,
                    total_built_area_sqm, total_built_area_sqft, floor_count,
                    basement_levels, units_count,
                    site_context, regulatory_constraints, program_requirements,
                    environmental_factors, design_intent,
                    ai_extraction_model, ai_extraction_version, extraction_confidence,
                    extraction_warnings, raw_file_paths, processed_json_path,
                    file_types_uploaded, approved
                ) VALUES (
                    :name, :category, :sub_type, :tags,
                    :area_sqm, :area_sqft, :floors,
                    :basements, :units,
                    :site::jsonb, :reg::jsonb, :program::jsonb,
                    :env::jsonb, :intent::jsonb,
                    :ai_model, '1.0', :confidence,
                    :warnings, ARRAY[:raw_path], :proc_path,
                    ARRAY[:file_type], false
                ) RETURNING id
            """),
            {
                "name":       schema.get("project_name"),
                "category":   schema.get("project_category", "unknown"),
                "sub_type":   schema.get("project_sub_type"),
                "tags":       schema.get("domain_tags", []),
                "area_sqm":   schema.get("total_built_area_sqm"),
                "area_sqft":  schema.get("total_built_area_sqft"),
                "floors":     schema.get("floor_count", 1),
                "basements":  schema.get("basement_levels", 0),
                "units":      schema.get("units_count"),
                "site":       _json.dumps(schema.get("site_context", {})),
                "reg":        _json.dumps(schema.get("regulatory_constraints", {})),
                "program":    _json.dumps(schema.get("program_requirements", [])),
                "env":        _json.dumps(schema.get("environmental_factors", {})),
                "intent":     _json.dumps(schema.get("design_intent", {})),
                "ai_model":   schema.get("ai_model_used", "gemini-1.5-pro"),
                "confidence": schema.get("extraction_confidence", 0.0),
                "warnings":   schema.get("extraction_warnings", []),
                "raw_path":   raw_path,
                "proc_path":  processed_path,
                "file_type":  file_type,
            }
        )
        project_id = str(result.scalar_one())

        # Write spaces
        for space in schema.get("program_requirements", []):
            await session.execute(text("""
                INSERT INTO spaces (project_id, space_name, space_type,
                    space_category, area_sqm, area_sqft, is_critical_space,
                    special_requirements, facing_direction, has_direct_access_to)
                VALUES (:pid, :name, :stype, :scat, :area_sqm, :area_sqft,
                    :critical, :reqs, :facing, :access)
            """), {
                "pid":      project_id,
                "name":     space.get("space_name"),
                "stype":    space.get("space_type"),
                "scat":     space.get("space_category"),
                "area_sqm": space.get("area_sqm"),
                "area_sqft":space.get("area_sqft"),
                "critical": space.get("is_critical", False),
                "reqs":     space.get("special_requirements", []),
                "facing":   space.get("facing_preference"),
                "access":   space.get("must_be_adjacent_to", []),
            })

        # Write spatial relationships
        for rel in schema.get("spatial_relationships", []):
            await session.execute(text("""
                INSERT INTO spatial_relationships
                    (project_id, space_a, space_b, relationship_type,
                     relationship_reason, priority, is_ai_extracted)
                VALUES (:pid, :a, :b, :rtype, :reason, :priority, true)
            """), {
                "pid":    project_id,
                "a":      rel.get("space_a"),
                "b":      rel.get("space_b"),
                "rtype":  rel.get("relationship_type"),
                "reason": rel.get("reason"),
                "priority": rel.get("priority", "preferred"),
            })

        # Write three embeddings
        for embed_type, embed_data in embeddings.items():
            vec_str = "[" + ",".join(map(str, embed_data["vector"])) + "]"
            await session.execute(text("""
                INSERT INTO project_embeddings
                    (project_id, embedding_type, embedding, embedding_text)
                VALUES (:pid, :etype, :vec::vector, :text)
            """), {
                "pid":   project_id,
                "etype": embed_type,
                "vec":   vec_str,
                "text":  embed_data["text"],
            })

        return project_id
PYEOF
echo "✓ Universal ingestion service written"
```

---

## Step 7 — Universal RAG retrieval and generation

```bash
cat > services/rag-api/app/core/retriever.py << 'PYEOF'
"""
Universal retriever — three-vector hybrid search.

Weights can be tuned per request:
- spatial_weight: how much the space programme matters
- site_weight: how much the site/constraints matter
- intent_weight: how much the design philosophy matters

Default: spatial 0.5, site 0.3, intent 0.2
(For a "find hospitals with ICU" query → spatial 0.8, site 0.1, intent 0.1)
(For "biophilic Vastu villa on sloped site" → all three weighted equally)
"""
import os, json, hashlib
import structlog
from sqlalchemy import text
from redis.asyncio import Redis

from .embedder import embed_texts

log = structlog.get_logger()
_redis = None

TTL = 3600


def _get_redis():
    global _redis
    if not _redis:
        _redis = Redis(
            host=os.environ["REDIS_HOST"],
            port=int(os.environ.get("REDIS_PORT", 6379)),
            password=os.environ.get("REDIS_AUTH", ""),
            ssl=True, decode_responses=True
        )
    return _redis


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
    cache_key = "rag:" + hashlib.md5(
        json.dumps([prompt, project_category, domain_tags, min_area_sqm,
                    max_area_sqm, spatial_weight, site_weight, intent_weight, top_k],
                   sort_keys=True, default=str).encode()
    ).hexdigest()

    redis = _get_redis()
    cached = await redis.get(cache_key)
    if cached:
        log.info("cache_hit")
        return json.loads(cached)

    # Generate three query vectors from the prompt
    vectors = await embed_texts([prompt, prompt, prompt])
    spatial_vec, site_vec, intent_vec = vectors[0], vectors[1], vectors[2]

    # Hybrid search across all three embedding types
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
        "weights_used": {
            "spatial": spatial_weight,
            "site": site_weight,
            "intent": intent_weight,
        }
    }

    await redis.setex(cache_key, TTL, json.dumps(result, default=str))
    return result


async def _three_vector_search(
    spatial_vec, site_vec, intent_vec,
    sw, stw, iw,
    category, tags, min_area, max_area, limit
) -> list[dict]:
    from shared.db.client import get_db

    filters = ["p.approved = true"]
    params: dict = {
        "sv": "[" + ",".join(map(str, spatial_vec)) + "]",
        "stv": "[" + ",".join(map(str, site_vec)) + "]",
        "iv": "[" + ",".join(map(str, intent_vec)) + "]",
        "sw": sw, "stw": stw, "iw": iw,
        "limit": limit,
    }

    if category:
        filters.append("p.project_category = :category")
        params["category"] = category
    if tags:
        filters.append("p.domain_tags && :tags")
        params["tags"] = tags
    if min_area:
        filters.append("p.total_built_area_sqm >= :min_area")
        params["min_area"] = min_area
    if max_area:
        filters.append("p.total_built_area_sqm <= :max_area")
        params["max_area"] = max_area

    where = " AND ".join(filters)

    sql = f"""
        WITH spatial_scores AS (
            SELECT project_id,
                   1 - (embedding <=> :sv::vector) AS score
            FROM project_embeddings
            WHERE embedding_type = 'spatial'
        ),
        site_scores AS (
            SELECT project_id,
                   1 - (embedding <=> :stv::vector) AS score
            FROM project_embeddings
            WHERE embedding_type = 'site'
        ),
        intent_scores AS (
            SELECT project_id,
                   1 - (embedding <=> :iv::vector) AS score
            FROM project_embeddings
            WHERE embedding_type = 'intent'
        )
        SELECT
            p.id,
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
            (COALESCE(sp.score,0) * :sw +
             COALESCE(st.score,0) * :stw +
             COALESCE(i.score, 0) * :iw) AS composite_score
        FROM projects p
        LEFT JOIN spatial_scores sp ON sp.project_id = p.id
        LEFT JOIN site_scores    st ON st.project_id = p.id
        LEFT JOIN intent_scores  i  ON i.project_id  = p.id
        WHERE {where}
        ORDER BY composite_score DESC
        LIMIT :limit
    """

    async with get_db() as session:
        result = await session.execute(text(sql), params)
        rows = result.mappings().all()

    return [dict(r) for r in rows]
PYEOF

cat > services/rag-api/app/core/embedder.py << 'PYEOF'
import os
import vertexai
from vertexai.language_models import TextEmbeddingModel

_model = None

def _get_model():
    global _model
    if not _model:
        vertexai.init(project=os.environ["GCP_PROJECT"],
                      location=os.environ.get("GCP_REGION","us-central1"))
        _model = TextEmbeddingModel.from_pretrained("text-embedding-004")
    return _model

async def embed_texts(texts: list[str]) -> list[list[float]]:
    model = _get_model()
    results = model.get_embeddings(texts)
    return [r.values for r in results]
PYEOF
echo "✓ Universal retriever written"
```

---

## Step 8 — Universal generation router

```bash
cat > services/rag-api/app/routers/generate.py << 'PYEOF'
"""
Universal generation endpoint.
Works for any project type — the architect describes what they need,
the system retrieves relevant references and generates structured output.
"""
import os, json
import structlog
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel
import vertexai
from vertexai.generative_models import GenerativeModel, GenerationConfig

from ..core.retriever import retrieve

log = structlog.get_logger()
router = APIRouter(prefix="/generate", tags=["generation"])
_model = None

SYSTEM_PROMPT = """
You are an expert architectural design assistant working with professional
architects and designers. You help generate spatial layout proposals for
ANY type of building project.

You are NOT limited to residential layouts. You can generate:
- Hospitals, clinics, diagnostic centres
- Schools, colleges, training centres
- Offices, co-working, data centres
- Hotels, resorts, serviced apartments
- Retail, showrooms, malls
- Factories, warehouses, logistics
- Places of worship, community centres
- Sports facilities, stadia
- Transit hubs, airports
- Any other building type the architect describes

GENERATION RULES:
1. Output valid JSON only. No explanation outside JSON.
2. Respect ALL stated constraints: site dimensions, setbacks, FAR, height
3. Respect adjacency requirements — critical spaces must be where specified
4. Generate exactly 3 spatial variations with meaningfully different
   organisational strategies (e.g. courtyard vs linear vs clustered)
5. Each variation should have a clear design concept name and rationale
6. Account for site orientation — passive solar, cross ventilation
7. Scale spaces realistically based on typical standards for the project type
8. Flag any constraint conflicts in warnings
9. Never reference BHK or residential conventions for non-residential projects
"""


def _get_model():
    global _model
    if not _model:
        vertexai.init(project=os.environ["GCP_PROJECT"],
                      location=os.environ.get("GCP_REGION","us-central1"))
        _model = GenerativeModel(
            model_name=os.environ.get("GENERATION_MODEL","gemini-1.5-pro"),
            system_instruction=SYSTEM_PROMPT,
        )
    return _model


class GenerateRequest(BaseModel):
    # Project description — free text, no enum
    prompt: str
    project_category: str | None = None   # optional filter for retrieval
    domain_tags: list[str] = []

    # Scale
    total_area_sqm: float | None = None
    total_area_sqft: float | None = None
    floor_count: int = 1

    # Site
    site_context: dict = {}
    regulatory_constraints: dict = {}
    environmental_factors: dict = {}
    design_intent: dict = {}

    # Retrieval weights (architect can tune)
    spatial_weight: float = 0.5
    site_weight: float = 0.3
    intent_weight: float = 0.2

    variations: int = 3


@router.post("")
async def generate(req: GenerateRequest):
    log.info("generate_request",
             prompt=req.prompt[:100],
             category=req.project_category)

    # Retrieve relevant reference projects
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

    # Build reference context
    ref_context = ""
    for i, ref in enumerate(references[:3], 1):
        ref_context += (
            f"\nReference project {i} "
            f"(composite match: {ref.get('composite_score',0):.2f}):\n"
            f"  Type: {ref.get('project_category')} / {ref.get('project_sub_type')}\n"
            f"  Area: {ref.get('total_built_area_sqm')} sqm, "
            f"{ref.get('floor_count')} floors\n"
            f"  Tags: {ref.get('domain_tags')}\n"
        )

    # Build constraint summary
    constraints = ""
    if req.site_context:
        sc = req.site_context
        constraints += (
            f"\nSite: {sc.get('plot_area_sqm','')} sqm, "
            f"{sc.get('plot_shape','')} shape, "
            f"North: {sc.get('north_direction','')}. "
            f"Road access: {sc.get('road_access',[])}.\n"
        )
    if req.regulatory_constraints:
        rc = req.regulatory_constraints
        constraints += (
            f"Regulations: FAR {rc.get('far','?')}, "
            f"Height max {rc.get('max_height_m','?')}m, "
            f"Setbacks F{rc.get('front_setback_m','?')}/"
            f"S{rc.get('side_setback_m','?')}/"
            f"R{rc.get('rear_setback_m','?')}m. "
            f"Jurisdiction: {rc.get('jurisdiction','')}.\n"
        )
    if req.design_intent:
        di = req.design_intent
        constraints += (
            f"Design intent: {di.get('style','')} style, "
            f"{'Vastu ' if di.get('vastu_compliance') else ''}"
            f"{'Feng Shui ' if di.get('feng_shui') else ''}"
            f"philosophy: {di.get('design_philosophy','')}.\n"
        )

    full_prompt = f"""
ARCHITECT'S REQUEST:
{req.prompt}

SCALE:
Total area: {req.total_area_sqm or '?'} sqm / {req.total_area_sqft or '?'} sqft
Floors: {req.floor_count}

{constraints}

REFERENCE PROJECTS FROM KNOWLEDGE BASE:
{ref_context if ref_context else "No similar projects found — generate from first principles using best practices for this building type."}

Generate {req.variations} spatial layout variations as JSON:
{{
  "project_type_understood": "your interpretation of what this project is",
  "design_standards_applied": ["standards/codes you referenced"],
  "variations": [
    {{
      "variation": 1,
      "concept_name": "Courtyard-centred organisation",
      "concept_rationale": "why this spatial strategy",
      "total_area_sqm": 0,
      "organisation_strategy": "description of how spaces are organised",
      "spaces": [
        {{
          "name": "space name",
          "type": "space type",
          "area_sqm": 0,
          "floor": 1,
          "facing": "direction",
          "is_critical": false,
          "position_hint": "where in the plan",
          "adjacencies": ["space names it connects to"]
        }}
      ],
      "circulation": "description of circulation strategy",
      "constraint_compliance": {{
        "far_used": 0.0,
        "height_m": 0,
        "coverage_percent": 0
      }},
      "passive_design_notes": "sun/wind/ventilation strategy",
      "warnings": []
    }}
  ],
  "global_warnings": [],
  "recommended_variation": 1
}}
"""

    try:
        model = _get_model()
        response = model.generate_content(
            full_prompt,
            generation_config=GenerationConfig(
                temperature=0.45,
                max_output_tokens=8192,
                response_mime_type="application/json",
            ),
        )
        result = json.loads(response.text)
        result["reference_projects_used"] = len(references)
        result["retrieval_weights"] = retrieval.get("weights_used")
        return result

    except json.JSONDecodeError as e:
        log.error("generation_json_error", error=str(e))
        raise HTTPException(500, "Generation model returned invalid JSON")
    except Exception as e:
        log.error("generation_error", error=str(e))
        raise HTTPException(500, str(e))
PYEOF
echo "✓ Universal generation router written"
```

---

## Step 9 — Brief upload endpoint (architect provides context at upload time)

```bash
cat > services/rag-api/app/routers/upload.py << 'PYEOF'
"""
/upload endpoint — lets architects attach a text brief when uploading files.
The brief is stored as GCS object metadata so the ingestion pipeline
uses it during AI extraction.
"""
import os
import structlog
from fastapi import APIRouter, UploadFile, File, Form, HTTPException
from google.cloud import storage

log = structlog.get_logger()
router = APIRouter(prefix="/upload", tags=["upload"])
storage_client = storage.Client()
KB_BUCKET = os.environ.get("KB_BUCKET", "").replace("gs://", "")


@router.post("")
async def upload_with_brief(
    file: UploadFile = File(...),
    project_brief: str = Form(default=""),
    project_category: str = Form(default=""),
    architect_notes: str = Form(default=""),
):
    """
    Upload a file to GCS with architect's brief attached as metadata.
    The ingestion pipeline will use this brief to improve AI extraction.
    """
    filename = file.filename or "unnamed"
    ext = filename.rsplit(".", 1)[-1].lower()
    folder_map = {
        "dwg": "dwg", "dxf": "dwg",
        "pdf": "pdf",
        "png": "images", "jpg": "images", "jpeg": "images",
        "rvt": "revit", "rfa": "revit",
        "ifc": "ifc",
        "txt": "briefs", "md": "briefs",
    }
    folder = folder_map.get(ext, "other")
    gcs_path = f"{folder}/{filename}"

    try:
        contents = await file.read()
        bucket = storage_client.bucket(KB_BUCKET)
        blob = bucket.blob(gcs_path)
        blob.metadata = {
            "x-project-brief": project_brief[:2000],
            "x-project-category": project_category,
            "x-architect-notes": architect_notes[:1000],
        }
        blob.upload_from_string(contents,
            content_type=file.content_type or "application/octet-stream")

        log.info("file_uploaded",
                 filename=filename, folder=folder,
                 brief_length=len(project_brief))

        return {
            "status": "uploaded",
            "gcs_path": f"gs://{KB_BUCKET}/{gcs_path}",
            "filename": filename,
            "brief_attached": bool(project_brief),
            "message": "Ingestion pipeline will process this file automatically."
        }
    except Exception as e:
        log.error("upload_error", filename=filename, error=str(e))
        raise HTTPException(500, str(e))
PYEOF
echo "✓ Upload with brief endpoint written"
```

---

## Step 10 — Build, push, and deploy

```bash
# Build images
gcloud auth configure-docker ${REGION}-docker.pkg.dev

cd services/ingestion
docker build -t ${REPO}/ingestion:v2.0 .
docker push ${REPO}/ingestion:v2.0
cd ../..

cd services/rag-api
docker build -t ${REPO}/rag-api:v2.0 .
docker push ${REPO}/rag-api:v2.0
cd ../..

cd services/quality-gate
docker build -t ${REPO}/quality-gate:v2.0 .
docker push ${REPO}/quality-gate:v2.0
cd ../..

# Deploy ingestion service (internal)
gcloud run deploy ingestion-service \
  --image=${REPO}/ingestion:v2.0 \
  --region=${REGION} \
  --service-account=${SA_INGESTION}@${PROJECT_ID}.iam.gserviceaccount.com \
  --set-env-vars="GCP_PROJECT=${PROJECT_ID},GCP_REGION=${REGION},PROCESSED_BUCKET=${PROCESSED_BUCKET},KB_BUCKET=${KB_BUCKET}" \
  --set-secrets="DB_PASSWORD=db-password:latest" \
  --set-cloudsql-instances=${CLOUD_SQL_CONN} \
  --add-cloudsql-instances=${CLOUD_SQL_CONN} \
  --vpc-connector=${CONNECTOR} \
  --vpc-egress=private-ranges-only \
  --ingress=internal \
  --no-allow-unauthenticated \
  --memory=4Gi \
  --cpu=4 \
  --timeout=540 \
  --concurrency=5 \
  --min-instances=0 \
  --max-instances=20 \
  --set-env-vars="CLOUD_SQL_CONNECTION_NAME=${CLOUD_SQL_CONN},DB_USER=${DB_USER},DB_NAME=${DB_NAME}"

# Deploy RAG API (public)
gcloud run deploy rag-api \
  --image=${REPO}/rag-api:v2.0 \
  --region=${REGION} \
  --service-account=${SA_SERVING}@${PROJECT_ID}.iam.gserviceaccount.com \
  --set-env-vars="GCP_PROJECT=${PROJECT_ID},GCP_REGION=${REGION},\
REDIS_HOST=${REDIS_HOST},REDIS_PORT=${REDIS_PORT},\
GENERATION_MODEL=gemini-1.5-pro,KB_BUCKET=${KB_BUCKET},\
CLOUD_SQL_CONNECTION_NAME=${CLOUD_SQL_CONN},DB_USER=${DB_USER},DB_NAME=${DB_NAME}" \
  --set-secrets="DB_PASSWORD=db-password:latest,REDIS_AUTH=redis-auth-string:latest" \
  --set-cloudsql-instances=${CLOUD_SQL_CONN} \
  --vpc-connector=${CONNECTOR} \
  --vpc-egress=private-ranges-only \
  --ingress=all \
  --allow-unauthenticated \
  --memory=2Gi \
  --cpu=2 \
  --timeout=180 \
  --concurrency=40 \
  --min-instances=1 \
  --max-instances=50

export RAG_API_URL=$(gcloud run services describe rag-api \
  --region=${REGION} --format="value(status.url)")
echo "RAG API: ${RAG_API_URL}"

# Wire Eventarc
gcloud run services add-iam-policy-binding ingestion-service \
  --region=${REGION} \
  --member="serviceAccount:service-${PROJECT_NUMBER}@gcp-sa-pubsub.iam.gserviceaccount.com" \
  --role="roles/run.invoker"

gcloud pubsub subscriptions create push-to-ingestion-v2 \
  --topic=layout-raw-uploaded \
  --push-endpoint="$(gcloud run services describe ingestion-service \
    --region=${REGION} --format='value(status.url)')/ingest" \
  --push-auth-service-account="${SA_INGESTION}@${PROJECT_ID}.iam.gserviceaccount.com" \
  --ack-deadline=540 \
  --dead-letter-topic=layout-ingestion-dlq \
  --max-delivery-attempts=3

echo "✓ All services deployed and wired"
```

---

## Step 11 — End-to-end validation tests

```bash
echo "=== Health check ==="
curl -sf "${RAG_API_URL}/health" | python3 -m json.tool

echo ""
echo "=== Test 1: Residential prompt ==="
curl -sf -X POST "${RAG_API_URL}/generate" \
  -H "Content-Type: application/json" \
  -d '{
    "prompt": "3 bedroom villa with a home office, south India traditional courtyard style, Vastu compliant, 400sqm plot with 8m frontage",
    "project_category": "residential",
    "total_area_sqm": 280,
    "floor_count": 2,
    "site_context": {"plot_area_sqm": 400, "north_direction": "north_wall", "climate_zone": "hot_humid"},
    "design_intent": {"vastu_compliance": true, "cultural_context": "South Indian", "style": "contemporary_vernacular"}
  }' | python3 -m json.tool

echo ""
echo "=== Test 2: Hospital prompt ==="
curl -sf -X POST "${RAG_API_URL}/generate" \
  -H "Content-Type: application/json" \
  -d '{
    "prompt": "50-bed secondary care hospital, ground floor OPD with 8 consultation rooms, emergency, pharmacy, and diagnostics. First floor 30-bed ward with nursing station. ICU on ground floor adjacent to emergency.",
    "project_category": "institutional",
    "domain_tags": ["healthcare","hospital","secondary_care"],
    "total_area_sqm": 2400,
    "floor_count": 2,
    "site_weight": 0.2,
    "spatial_weight": 0.7,
    "intent_weight": 0.1
  }' | python3 -m json.tool

echo ""
echo "=== Test 3: Office prompt ==="
curl -sf -X POST "${RAG_API_URL}/generate" \
  -H "Content-Type: application/json" \
  -d '{
    "prompt": "Co-working space for 120 people: open hot-desking zone, 6 private cabins, 2 large meeting rooms, 1 boardroom, phone booths, café, reception. Biophilic design, natural light priority.",
    "project_category": "commercial",
    "domain_tags": ["office","coworking"],
    "total_area_sqm": 900,
    "floor_count": 1,
    "design_intent": {"design_philosophy": "biophilic", "style": "contemporary"}
  }' | python3 -m json.tool

echo ""
echo "✓ All tests sent — review JSON responses above"
```

---

## API reference

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/generate` | Main generation — any project type |
| `POST` | `/search` | KB search — returns similar projects |
| `POST` | `/upload` | Upload file with text brief |
| `GET`  | `/health` | Health check |

### `/generate` key fields

| Field | Type | Purpose |
|---|---|---|
| `prompt` | string | Free text — describe any project |
| `project_category` | string or null | Optional filter for retrieval |
| `domain_tags` | list | Specific domain tags for filtering |
| `site_context` | dict | Plot, orientation, climate, access |
| `regulatory_constraints` | dict | FAR, setbacks, height, jurisdiction |
| `design_intent` | dict | Style, culture, Vastu, sustainability |
| `spatial_weight` | float 0–1 | How much to weight space-similarity |
| `site_weight` | float 0–1 | How much to weight site-similarity |
| `intent_weight` | float 0–1 | How much to weight design-similarity |

---

## What is now future-proof

| Concern | How it is addressed |
|---|---|
| New project type | No code changes — AI extracts whatever it is |
| New space type | Open `space_type TEXT` field — no enum to update |
| New country / code | `regulations` table is append-only |
| IFC / Revit support | Parser slot exists — add `ifc` ext handler |
| Multilingual briefs | Gemini extracts in any language, stores in English |
| Training data coverage | Schema captures any domain — training set grows naturally |
| Creative generation | Weighted retrieval + constraint-aware prompt = novel outputs |
| Vastu / Feng Shui | First-class fields in `design_intent` |
| Sustainability targets | `environmental_factors` + `target_certification` |

---

## What's next — Phase 3

- Vertex AI Pipeline: export approved projects as training JSONL
- Fine-tuning run: teach a Gemini model on the extracted universal schema
- Evaluation: measure against real architect outputs, not BHK templates
- Plugin integration: AutoCAD/Revit API connector to `/generate`