# zHeight KB Services - In-Depth Architecture Analysis

## Executive Summary

The **zHeight KB Services** is a distributed, cloud-native microservices platform built on Google Cloud Platform (GCP) designed to power an architectural intelligence knowledge base. It processes architectural design files (DWG, PDF, images), extracts semantic information using AI, validates quality, stores embeddings for similarity search, and provides a RAG (Retrieval-Augmented Generation) API for layout generation and search.

The system is composed of three primary microservices that communicate asynchronously via Google Cloud Pub/Sub, with shared data contracts and database layers. Everything runs on Cloud Run with PostgreSQL backed storage and Google Cloud AI models.

---

## System Architecture Overview

### High-Level Data Flow

```
┌─────────────────┐
│  File Uploads   │
│  (DWG/PDF/IMG)  │
└────────┬────────┘
         │ (GCS Bucket)
         ▼
┌─────────────────────────┐
│  INGESTION SERVICE      │  • Download file from GCS
│  (Async Processing)     │  • Parse (DWG→geometry, PDF→text, IMG→vision)
│  ┌─────────────────┐    │  • AI extraction (Vertex AI + Gemini)
│  │ DWG Parser      │    │  • Generate 3-layer embeddings
│  │ PDF Parser      │    │  • Database write + Pub/Sub notify
│  │ Image Parser    │    │
│  │ AI Extractor    │    │
│  │ Multi-Embedder  │    │
│  └─────────────────┘    │
└────────┬────────────────┘
         │ (Pub/Sub: layout-processed)
         ▼
┌─────────────────────────┐
│ QUALITY GATE SERVICE    │  • Check extraction confidence
│ (Auto-Approval Gate)    │  • Validate space count + metadata
│ ┌─────────────────┐     │  • Auto-approve if confidence ≥ 0.75
│ │ Approval Logic  │     │  • Flag low-confidence for manual review
│ │ DB Update       │     │
│ └─────────────────┘     │
└────────┬────────────────┘
         │ (Approved Projects → KB)
         ▼
┌─────────────────────────┐
│ RAG API SERVICE v3.1    │  • Search: hybrid multi-weight retrieval
│ (Query Interface)       │  • Generate: KB-informed layout proposals
│ ┌─────────────────┐     │  • Feedback: architect corrections tracking
│ │ Search Router   │     │  • Orchestrate: end-to-end workflows
│ │ Generate Router │     │  • Upload: handle new files
│ │ Feedback Router │     │
│ │ Retriever Core  │     │
│ └─────────────────┘     │
└────────┬────────────────┘
         │
         ▼
    ┌─────────────────┐
    │  Clients        │
    │  • AutoCAD      │
    │  • Web UI       │
    │  • Third-party  │
    └─────────────────┘
```

---

## Core Infrastructure

### GCP Services Used

| Service | Purpose | Usage |
|---------|---------|-------|
| **Cloud Run** | Containerized microservices hosting | Ingestion, Quality Gate, RAG API |
| **Cloud SQL (PostgreSQL)** | Primary relational database | Project metadata, spaces, relationships, embeddings |
| **Pub/Sub** | Asynchronous event messaging | Ingestion→QGate, QGate→RAG API workflows |
| **Cloud Storage (GCS)** | File storage | Raw uploads, processed JSON, embeddings |
| **Vertex AI / Gemini** | AI/ML models | Document parsing, space extraction, layout generation |
| **Cloud Build** | CI/CD pipeline | Docker build & push for services |
| **Cloud SQL Auth Proxy** | Secure database connections | Socket-based auth from Cloud Run to SQL |
| **Redis** | Session/cache store | RAG API caching, feedback deduplication |
| **Secret Manager** | Credential storage | DB passwords, API keys, service account tokens |

---

## Service Deep-Dive

### 1. Ingestion Service

**Purpose**: Ingest architectural files (DWG, PDF, images), parse them, extract metadata using AI, generate embeddings, and persist to database.

**Technology Stack**:
- FastAPI 0.111.0 (async web framework)
- SQLAlchemy 2.0.30 + asyncpg (async database driver)
- Google Cloud Storage, Pub/Sub, Document AI, Vision API
- ezdxf 1.3.4 (DWG/DXF parsing)
- PyMuPDF 1.24.3 (PDF text extraction)
- Pydantic 2.7.1 (data validation)
- structlog 24.1.0 (structured logging)

#### Ingestion Pipeline (Step-by-Step)

**Phase 1: Pub/Sub Event Trigger**
```python
# Cloud Storage publishes events to Pub/Sub when files are uploaded to gs://zheight-uploads/
# Message structure:
{
  "bucket": "zheight-uploads",
  "name": "projects/acme-office-tower/floor-01.dwg",
  "messageId": "9876543210"
}
```

**Phase 2: Download & Validation**
```
• Retrieve file from GCS bucket
• Extract file extension (dwg, dxf, pdf, png, jpg, jpeg, tiff)
• Calculate SHA-256 hash of raw bytes for idempotency
• Check if file already processed (query projects.raw_file_paths)
  ↳ If duplicate: skip & return (Pub/Sub retry-safe)
```

**Phase 3: File Parsing**

- **DWG Parser** (ezdxf):
  - Parses AutoCAD DWG/DXF binary format into entities
  - Extracts geometries: walls, doors, windows, columns, stairs
  - Preserves layer information for classification
  - Generates normalized vertex arrays (millimeters)
  
- **PDF Parser** (PyMuPDF):
  - Extracts text via OCR if scanned
  - Preserves layout structure (headings, paragraphs, tables)
  - Detects embedded images (floor plans, elevations)
  - Enriches text with Vertex AI Document AI for structured data extraction
  
- **Image Parser** (Google Cloud Vision):
  - Detects architectural elements: walls, doors, windows, dimensions
  - Extracts text annotations (room labels, dimensions)
  - Generates 2D bounding boxes for element localization
  - Uses Vertex AI's document understanding for blueprint interpretation

**Phase 4: AI Extraction (Vertex AI + Gemini)**

After parsing, raw geometry/text is sent to AI extractors:

```python
# Extraction request structure:
{
  "parsed_geometry": {...},          # From parser
  "text_content": "...",              # Extracted text
  "domain_hints": ["office", "highrise"],
  "model": "gemini-2.5-flash",
  "system_prompt": "Extract architectural metadata..."
}

# AI Response structure:
{
  "project_name": "ACME Office Tower",
  "project_category": "commercial",
  "project_sub_type": "office",
  "domain_tags": ["office", "highrise", "sustainable"],
  "total_built_area_sqm": 45000,
  "total_built_area_sqft": 484375,
  "floor_count": 30,
  "basement_levels": 2,
  "units_count": 120,
  "site_context": {
    "plot_area_sqm": 5000,
    "site_constraints": "corner_site, high_visibility",
    "access_points": 3,
    "traffic_pattern": "heavy_congestion"
  },
  "regulatory_constraints": {
    "far": 12,
    "setbacks": {"north": 20, "south": 15, "east": 10, "west": 10},
    "max_height_m": 120,
    "building_code": "IBC 2021",
    "zoning": "B7"
  },
  "environmental_factors": {
    "sun_path": "south_dominant",
    "prevailing_wind": "northwest",
    "seismic_zone": 3,
    "flood_risk": "low"
  },
  "design_intent": {
    "sustainability_target": "LEED Platinum",
    "priority_spaces": ["conference_halls", "cafeteria"],
    "desired_aesthetics": "minimalist_glass_curtain_wall"
  },
  "spaces": [
    {
      "space_name": "Conference Room A",
      "space_type": "meeting",
      "space_category": "commercial",
      "floor_number": 5,
      "area_sqm": 85,
      "area_sqft": 915,
      "width_m": 10,
      "length_m": 8.5,
      "height_m": 3.5,
      "facing_direction": "south",
      "has_natural_light": true,
      "has_cross_vent": true,
      "has_direct_access_to": ["lobby", "reception"],
      "is_critical_space": false,
      "special_requirements": ["AV_capable", "flexible_partitions"]
    }
  ],
  "spatial_relationships": [
    {
      "space_a": "Conference Room A",
      "space_b": "Reception",
      "relationship_type": "adjacent_open_plan",
      "relationship_reason": "Professional visibility + accessibility",
      "priority": "preferred"
    }
  ],
  "extraction_confidence": 0.87,
  "ai_extraction_model": "gemini-2.5-flash",
  "ai_extraction_version": "phase3-v2"
}
```

**Phase 5: Three-Layer Embedding Generation**

All projects get embedded in three semantic dimensions for hybrid search:

1. **Spatial Embedding** (768-dim, text-embedding-004):
   - Captures spatial organization, adjacency rules, flow patterns
   - Source: Concatenated `spaces` + `spatial_relationships` descriptions
   - Example: "Conference rooms adjacent to reception with ADA access..."
   
2. **Site/Context Embedding** (768-dim):
   - Captures site conditions, regulatory, environmental factors
   - Source: `site_context` + `regulatory_constraints` + `environmental_factors`
   - Example: "Corner site, high visibility, LEED Platinum target, seismic zone 3..."
   
3. **Design Intent Embedding** (768-dim):
   - Captures architectural vision, use-case, aesthetics
   - Source: `design_intent` + `project_category` + domain tags
   - Example: "Minimalist office, sustainability-focused, collaborative workspace..."

All three are stored in PostgreSQL `project_embeddings` table with HNSW (Hierarchical Navigable Small World) vector indexes for fast ANN (Approximate Nearest Neighbor) search.

**Phase 6: Database Transaction (Atomic Write)**

All project data is written in a **single database transaction**:

```sql
BEGIN;
  -- Insert or upsert projects table
  INSERT INTO projects (
    id, project_name, project_category, project_sub_type, domain_tags,
    total_built_area_sqm, total_built_area_sqft, floor_count, basement_levels,
    units_count, site_context, regulatory_constraints, program_requirements,
    environmental_factors, design_intent, ai_extraction_model, ai_extraction_version,
    extraction_confidence, extraction_warnings, raw_file_paths, processed_json_path,
    file_types_uploaded, approved, quality_score, schema_version, created_at
  ) VALUES (...);

  -- Insert spaces for this project
  INSERT INTO spaces (
    project_id, space_name, space_type, space_category, floor_number, area_sqm, area_sqft,
    width_m, length_m, height_m, facing_direction, has_natural_light, has_cross_vent,
    has_direct_access_to, is_critical_space, special_requirements, position_x_norm, position_y_norm
  ) VALUES (...) × N;

  -- Insert spatial relationships
  INSERT INTO spatial_relationships (
    project_id, space_a, space_b, relationship_type, relationship_reason, priority, is_ai_extracted
  ) VALUES (...) × M;

  -- Insert three embeddings
  INSERT INTO project_embeddings (
    project_id, embedding_type, embedding, embedding_text, model_version
  ) VALUES 
    (project_id, 'spatial', embedding_768d_1, spatial_text, 'text-embedding-004'),
    (project_id, 'site', embedding_768d_2, site_text, 'text-embedding-004'),
    (project_id, 'design_intent', embedding_768d_3, intent_text, 'text-embedding-004');

COMMIT;
```

**Benefits**: All-or-nothing atomicity ensures no orphaned data if extraction/embedding fails mid-process.

**Phase 7: Pub/Sub Notification**

After successful DB write, service publishes event to `layout-processed` topic:

```json
{
  "event": "layout_processed",
  "project_id": "uuid-here",
  "confidence": 0.87,
  "category": "commercial",
  "sub_type": "office",
  "spaces_count": 45,
  "timestamp": "2024-01-15T10:23:45Z"
}
```

Quality Gate service subscribes to this topic.

**Phase 8: Error Handling & Resilience**

- **Parse Failures**: Caught and logged; extraction falls back to generic schema
- **AI Extraction Failures**: Partial fallback; uses basic category classification
- **Embedding Failures**: Logs warning; project stored without embeddings (searchable via metadata)
- **DB Write Failures**: Full rollback; message remains in Pub/Sub queue for retry
- **Pub/Sub Retry Logic**: Exponential backoff (1s → 2s → 4s → 8s..., max 30s) via `async_retry` decorator

**Health Check Endpoint**:
```
GET /health
{
  "service": "ingestion",
  "status": "ok" | "degraded",
  "db": "ok" | "error: ...",
  "vertexai": "ok" | "error: ..."
}
```

---

### 2. Quality Gate Service

**Purpose**: Act as an automated approval gate. Consumes layout-processed events, evaluates extraction quality & completeness, auto-approves high-confidence projects, and flags low-confidence for manual architect review.

**Technology Stack**:
- FastAPI 0.111.0
- SQLAlchemy 2.0.30 + asyncpg
- structlog 24.1.0

#### Quality Gate Logic

**Trigger**: Pub/Sub message from ingestion service

**Input Event**:
```json
{
  "project_id": "uuid-abc123",
  "confidence": 0.87,
  "category": "commercial",
  "sub_type": "office",
  "spaces_count": 45
}
```

**Approval Decision Logic**:

```python
AUTO_APPROVE_THRESHOLD = 0.75  # Environment variable, tunable
MIN_SPACES = 3                  # Minimum spaces to consider "complete"

# Count total spaces in database
spaces_count = DB.query("SELECT COUNT(*) FROM spaces WHERE project_id = ?")

# Approval criteria (ALL must be true):
auto_approve = (
    confidence >= 0.75                          # ✓ High confidence extraction
    AND spaces_count >= 3                       # ✓ Minimum spatial data
    AND category != "unknown"                   # ✓ Category identified
)

# Rejection reasons (if not auto-approved):
rejection_reasons = []
if confidence < 0.75:
    rejection_reasons.append("low_confidence(0.65 < 0.75)")
if spaces_count < 3:
    rejection_reasons.append("insufficient_spaces(1 < 3)")
if category == "unknown":
    rejection_reasons.append("unknown_category")
```

**Database Update**:

```sql
UPDATE projects
SET 
  approved = TRUE|FALSE,
  approved_at = NOW() (if approved, else NULL),
  quality_score = {confidence}
WHERE id = {project_id}
```

**Response to Pub/Sub**:

```json
{
  "project_id": "uuid-abc123",
  "approved": true,
  "space_count": 45,
  "confidence": 0.87,
  "reason": "auto_approved"  // or array of rejection reasons
}
```

**Manual Review Path**: Unapproved projects are flagged in the admin UI. Architects can manually review and approve via Admin Dashboard (not in scope of this service).

**Health Check Endpoint**:
```
GET /health
{
  "service": "quality-gate",
  "status": "ok"
}
```

---

### 3. RAG API Service v3.1

**Purpose**: Serve as the primary query interface for architects & AutoCAD plugin. Provides three core capabilities:
1. **Search**: Find similar projects from KB by spatial/site/intent characteristics
2. **Generate**: AI-powered layout generation informed by KB precedents
3. **Feedback**: Capture architect corrections for continuous model improvement

**Technology Stack**:
- FastAPI 0.111.0
- SQLAlchemy 2.0.30 + asyncpg
- Redis 5.0.4 (session cache + feedback deduplication)
- Google Cloud Pub/Sub, Storage, Gemini
- structlog 24.1.0
- Pydantic 2.7.1

#### RAG API Architecture

**API Structure**:

```
GET  /health                      (no auth)
GET  /v1/health                   (no auth)
POST /v1/search                   (with API key)
POST /v1/generate                 (with API key)
POST /v1/feedback                 (with API key)
POST /v1/orchestrate              (with API key)
POST /v1/upload                   (with API key)
```

**Authentication**:

All endpoints (except health) require `X-API-Key` header:

```
X-API-Key: {RAG_API_KEY}  # Retrieved from Secret Manager at startup
```

Routes check: `if key != RAG_API_KEY: return 401 Unauthorized`

**Request ID Propagation**:

Every request gets a unique `X-Request-ID` (UUID4 if not provided):
```
X-Request-ID: 550e8400-e29b-41d4-a716-446655440000
```

ID is threaded through all service logs via structlog contextvars for distributed tracing.

---

#### 3.1 Search Router

**Endpoint**: `POST /v1/search`

**Purpose**: Find similar approved projects from KB without generating new layout.

**Use Cases**:
- AutoCAD plugin: "Show me similar office projects"
- Architect intuition: "What are precedents for high-rise residential with courtyard?"
- Quick inspiration: Search without generation overhead

**Request Schema**:

```python
class SearchRequest(BaseModel):
    prompt: str                      # Natural language query
    project_category: str | None = None   # Filter by category (commercial, residential, etc.)
    domain_tags: list[str] = []    # Additional filter tags
    min_area_sqm: float | None = None
    max_area_sqm: float | None = None
    spatial_weight: float = 0.5    # How much to weight spatial similarity (0-1)
    site_weight: float = 0.3       # Site/regulatory similarity weight
    intent_weight: float = 0.2     # Design intent similarity weight
    top_k: int = 10                # How many results to return
```

**Retrieval Process**:

1. **Embed Query**:
   ```
   query_embedding = embed(prompt)  # Using text-embedding-004 model
   dimension: 768
   ```

2. **Hybrid Multi-Weight Search** (in core/retriever.py):
   
   ```python
   # Search across three embedding types simultaneously
   spatial_results = vector_search(
       embedding_table,
       embedding_type='spatial',
       query_vector=query_embedding,
       distance_metric='cosine',
       top_k=top_k * 1.5  # Over-fetch for re-ranking
   )
   
   site_results = vector_search(
       embedding_type='site',
       query_vector=query_embedding,
       top_k=top_k * 1.5
   )
   
   intent_results = vector_search(
       embedding_type='design_intent',
       query_vector=query_embedding,
       top_k=top_k * 1.5
   )
   
   # Linear combination of relevance scores
   combined_score[project_id] = (
       0.5 * spatial_score[project_id] +
       0.3 * site_score[project_id] +
       0.2 * intent_score[project_id]
   )
   
   ranked_results = sort_by(combined_score)[:top_k]
   ```

3. **Metadata Filter** (optional):
   ```sql
   SELECT p.id, p.project_name, p.project_category, p.domain_tags,
          p.total_built_area_sqm, p.floor_count, p.extraction_confidence,
          ARRAY_AGG(DISTINCT s.space_type) as space_types,
          ARRAY_AGG(DISTINCT sr.relationship_type) as relationships
   FROM projects p
   LEFT JOIN spaces s ON p.id = s.project_id
   LEFT JOIN spatial_relationships sr ON p.id = sr.project_id
   WHERE p.approved = TRUE
     AND (:category IS NULL OR p.project_category = :category)
     AND (:tags IS NULL OR p.domain_tags && :tags)
     AND (:min_area IS NULL OR p.total_built_area_sqm >= :min_area)
     AND (:max_area IS NULL OR p.total_built_area_sqm <= :max_area)
   GROUP BY p.id
   ORDER BY relevance_score DESC
   LIMIT :top_k;
   ```

4. **Response Format**:

```json
{
  "query": "Modern office with open plan",
  "results": [
    {
      "project_id": "uuid-1",
      "project_name": "Silicon Valley Campus A",
      "category": "commercial",
      "sub_type": "office",
      "total_area_sqm": 35000,
      "floor_count": 8,
      "domain_tags": ["office", "tech", "open_plan"],
      "confidence": 0.89,
      "match_score": 0.92,
      "space_types": ["open_office", "collaboration", "cafeteria", "meeting"],
      "key_relationships": ["adjacent_open_plan", "visual_connection"],
      "site_context": {...},
      "regulatory_constraints": {...},
      "design_intent": {...}
    },
    // ... more results
  ],
  "total_found": 47,
  "query_time_ms": 234
}
```

---

#### 3.2 Generate Router

**Endpoint**: `POST /v1/generate`

**Purpose**: Generate 1-3 spatial layout variations for a project, informed by KB precedents.

**Request Schema**:

```python
class GenerateRequest(BaseModel):
    prompt: str
    project_category: str | None = None
    domain_tags: list[str] = []
    total_area_sqm: float | None = None
    total_area_sqft: float | None = None
    floor_count: int = 1
    site_context: dict = {}          # User-provided constraints
    regulatory_constraints: dict = {}
    environmental_factors: dict = {}
    design_intent: dict = {}
    spatial_weight: float = 0.5
    site_weight: float = 0.3
    intent_weight: float = 0.2
    variations: int = 3              # How many layout variations to generate
```

**Generation Pipeline**:

1. **KB Retrieval** (same as search, but with area-based filtering):

   ```python
   # Auto-scale search bounds around provided area
   min_area = total_area_sqm * 0.6
   max_area = total_area_sqm * 1.4
   
   # Retrieve top 10 precedent projects
   kb_results = await retrieve(
       prompt=prompt,
       min_area_sqm=min_area,
       max_area_sqm=max_area,
       top_k=10
   )
   ```

2. **KB Context Compilation**:

   ```python
   context = """
   Precedent Projects:
   
   1. PROJECT: {name}
      Category: {category}
      Area: {area_sqm} sqm
      Spaces: {space_list}
      Key Relationships:
      {relationships_json}
      Site Context: {site_context_json}
      Regulatory: {constraints_json}
   
   [... more projects ...]
   
   Extraction Model Performance: {confidence}%
   """
   ```

3. **AI Generation via Gemini**:

   ```python
   system_prompt = """
   You are an expert architectural design consultant working with professional architects.
   Generate detailed spatial layout proposals for ANY building type.
   
   TYPES YOU HANDLE:
   Residential (villa, apartment, row house, dormitory)
   Commercial (office, retail, hotel, restaurant, data centre)
   Institutional (hospital, clinic, school, college, court)
   Industrial (factory, warehouse, logistics)
   Mixed-use, Religious, Sports, Transport hubs
   
   RULES:
   1. Output valid JSON ONLY — no markdown, no text outside the JSON object
   2. Respect ALL constraints: FAR, setbacks, height limits, plot dimensions
   3. Critical spaces (ICU, server room, prayer hall) must honour adjacency rules
   4. Generate exactly {variations} variations with distinct spatial concepts
   5. Scale spaces to realistic US standards (IBC 2021, NFPA 101, ADA)
   6. Account for sun path, prevailing winds, cross-ventilation
   7. Flag constraint conflicts in warnings — never silently violate them
   """
   
   user_prompt = f"""
   Generate {variations} layout variations for:
   
   Project Brief:
   {prompt}
   
   Constraints:
   • Total Area: {total_area_sqm} sqm
   • Floors: {floor_count}
   • Category: {project_category}
   • Site Context: {json.dumps(site_context)}
   • Regulatory: {json.dumps(regulatory_constraints)}
   • Environmental: {json.dumps(environmental_factors)}
   • Design Intent: {json.dumps(design_intent)}
   
   Precedent KB Context:
   {context}
   
   Each variation must include:
   - Unique spatial organization concept
   - List of spaces with dimensions (in mm for DWG export)
   - Spatial relationships and adjacencies
   - Warnings for constraint conflicts (if any)
   - Estimated match to precedent projects
   """
   
   response = await gemini_client.generate_content(
       model="gemini-2.5-flash",
       system_instruction=system_prompt,
       contents=user_prompt,
       temperature=1.0,  # Controlled randomness for variation
       max_output_tokens=8000
   )
   ```

4. **JSON Parsing & Validation**:

   ```python
   # Extract JSON from response (handles markdown wrappers)
   json_match = re.search(r'\{.*\}', response.text, re.DOTALL)
   layout_json = json.loads(json_match.group(0))
   
   # Validate structure
   assert "variations" in layout_json
   assert len(layout_json["variations"]) == variations
   for var in layout_json["variations"]:
       assert "concept" in var
       assert "spaces" in var
       assert "relationships" in var
   ```

5. **DrawActionPlan Conversion**:

   For each variation, convert semantic layout → DrawActionPlan (executable by AutoCAD plugin):

   ```python
   # DrawActionPlan is the canonical data contract
   # Contains drawing primitives: walls, doors, windows, dimensions, labels, groups
   
   draw_actions = []
   
   # Create layer for this variation
   draw_actions.append(DrawAction(
       action_type=ActionType.CREATE_LAYER,
       layer=f"Variation_{variation_id}",
       group_id="root"
   ))
   
   # Draw walls
   for space in variation["spaces"]:
       for wall in space["walls"]:
           draw_actions.append(DrawAction(
               action_type=ActionType.DRAW_WALL,
               layer=f"Variation_{variation_id}",
               start=Point2D(x=wall.x1, y=wall.y1),
               end=Point2D(x=wall.x2, y=wall.y2),
               thickness_mm=wall.thickness_mm,
               wall_type=wall.type
           ))
   
   # Draw doors
   for door in variation["doors"]:
       draw_actions.append(DrawAction(
           action_type=ActionType.DRAW_DOOR,
           layer=f"Variation_{variation_id}",
           start=Point2D(x=door.x, y=door.y),
           door_width_mm=door.width,
           door_swing=door.swing_direction,
           swing_angle=door.swing_angle
       ))
   
   # ... windows, columns, dimensions, labels, etc.
   
   return draw_actions
   ```

6. **Response Format**:

```json
{
  "request_id": "550e8400-e29b-41d4-a716-446655440000",
  "project_brief": "Modern office with open plan",
  "generated_at": "2024-01-15T10:45:30Z",
  "kb_precedents_used": 5,
  "variations": [
    {
      "variation_id": 1,
      "concept": "Linear spine with connected pods",
      "description": "Single corridor backbone with clustered work pods radiating outward...",
      "spaces": [
        {
          "space_name": "Open Office A",
          "space_type": "workspace",
          "area_sqm": 450,
          "position_x_mm": 0,
          "position_y_mm": 0,
          "width_mm": 15000,
          "length_mm": 30000,
          "height_mm": 3500,
          "adjacent_spaces": ["Collaboration Pod B", "Circulation"]
        }
      ],
      "relationships": [
        {
          "space_a": "Open Office A",
          "space_b": "Collaboration Pod B",
          "relationship_type": "visual_connection_required",
          "priority": "preferred"
        }
      ],
      "constraints_respected": [
        "FAR 12 achieved",
        "Setbacks met: N:20m, S:15m, E:10m, W:10m",
        "Max height: 120m (within limit)",
        "Cross-ventilation: 65% of spaces"
      ],
      "warnings": [],
      "kb_match_score": 0.88,
      "draw_actions": [
        {
          "action_type": "DRAW_WALL",
          "layer": "Variation_1_Walls",
          "start": {"x": 0, "y": 0},
          "end": {"x": 15000, "y": 0},
          "thickness_mm": 200,
          "wall_type": "structural"
        },
        // ... more draw actions
      ]
    },
    // ... more variations
  ],
  "generation_time_ms": 4250,
  "notes": "All layouts respect building code requirements. Some natural light strategies may require mechanical integration."
}
```

---

#### 3.3 Feedback Router

**Endpoint**: `POST /v1/feedback`

**Purpose**: Capture architect feedback on generated layouts for continuous learning & quality tracking.

**Request Schema**:

```python
class FeedbackRequest(BaseModel):
    request_id: str                  # Links back to original generation request
    architect_id: str | None = None  # Who provided feedback
    selected_variation: int          # Which of the 3 variations they chose (1-3)
    correction_type: str = "accepted"   # "accepted", "modified", "rejected"
    corrected_dwg_notes: str = ""    # Architect's notes on modifications
    severity: str = "minor"          # "minor", "moderate", "critical"
```

**Feedback Storage** (Phase 3):

```sql
-- Table: architect_feedback
CREATE TABLE architect_feedback (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    request_id TEXT NOT NULL,
    architect_id TEXT,
    selected_variation INTEGER NOT NULL DEFAULT 1,
    correction_type TEXT NOT NULL DEFAULT 'accepted',
    corrected_dwg_notes TEXT DEFAULT '',
    severity TEXT NOT NULL DEFAULT 'minor',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_feedback_request_id ON architect_feedback (request_id);
CREATE INDEX idx_feedback_created_at ON architect_feedback (created_at);
CREATE INDEX idx_feedback_correction ON architect_feedback (correction_type);
```

**Processing Logic**:

1. **Deduplication** (Redis):
   ```python
   feedback_key = f"feedback:{request_id}:{architect_id}"
   if await redis.exists(feedback_key):
       return {"status": "duplicate", "reason": "Already processed"}
   await redis.setex(feedback_key, 3600, "1")  # TTL: 1 hour
   ```

2. **Store in DB**:
   ```sql
   INSERT INTO architect_feedback (
       request_id, architect_id, selected_variation,
       correction_type, corrected_dwg_notes, severity
   ) VALUES (?, ?, ?, ?, ?, ?)
   ```

3. **Trigger Retraining Signal** (if threshold exceeded):
   ```python
   # Count non-accepted corrections for this project
   critical_count = await db.query(
       """
       SELECT COUNT(*) FROM architect_feedback
       WHERE request_id LIKE :project_id || ':%'
       AND correction_type IN ('modified', 'rejected')
       AND severity IN ('moderate', 'critical')
       """
   )
   
   CORRECTION_THRESHOLD = 5  # After 5 critical corrections, trigger retraining
   
   if critical_count >= CORRECTION_THRESHOLD:
       await pubsub.publish('retraining_signal', {
           "project_id": project_id,
           "feedback_count": critical_count,
           "trigger_reason": "threshold_exceeded"
       })
   ```

4. **Response**:
```json
{
  "status": "recorded",
  "feedback_id": "uuid-feedback-123",
  "variation_selected": 1,
  "correction_type": "modified",
  "retraining_triggered": false
}
```

---

#### 3.4 Orchestrate Router

**Endpoint**: `POST /v1/orchestrate`

**Purpose**: End-to-end workflow that combines search + generate + feedback in one atomic operation.

**Use Case**: AutoCAD plugin: "Show me precedents AND generate 3 variations AND wait for feedback"

**Request**: Combines SearchRequest + GenerateRequest + callback URL

**Response**: Returns both search results AND generated variations with unified request ID.

---

#### 3.5 Upload Router

**Endpoint**: `POST /v1/upload`

**Purpose**: Direct upload endpoint for new project files (alternative to GCS).

**Request**: Multipart form with file + metadata

**Processing**: Internally calls Ingestion Service pipeline via Pub/Sub.

---

### Database Schema (PostgreSQL)

#### projects table

Core project metadata extracted by AI:

```sql
CREATE TABLE projects (
  -- Identity & Classification
  id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_name TEXT,
  project_category TEXT,  -- "commercial", "residential", "institutional", "industrial", etc.
  project_sub_type TEXT,  -- "office", "villa", "hospital", etc.
  domain_tags TEXT[],     -- ["office", "highrise", "sustainable"]
  
  -- Dimensional Data
  total_built_area_sqm NUMERIC(12,2),
  total_built_area_sqft NUMERIC(12,2),
  floor_count INT DEFAULT 1,
  basement_levels INT DEFAULT 0,
  units_count INT,
  
  -- Context & Constraints (stored as JSONB for flexibility)
  site_context JSONB,
    -- {"plot_area_sqm": 5000, "site_constraints": "corner_site", "access_points": 3}
  regulatory_constraints JSONB,
    -- {"far": 12, "setbacks": {...}, "max_height_m": 120, "zoning": "B7"}
  program_requirements JSONB,
    -- {"primary_function": "office", "secondary_functions": [...]}
  environmental_factors JSONB,
    -- {"sun_path": "south", "prevailing_wind": "northwest", "seismic_zone": 3}
  design_intent JSONB,
    -- {"sustainability": "LEED Platinum", "priority_spaces": [...]}
  
  -- AI Extraction Metadata
  ai_extraction_model TEXT,     -- "gemini-2.5-flash"
  ai_extraction_version TEXT,   -- "phase3-v2"
  extraction_confidence NUMERIC(4,3),  -- 0.000 - 1.000
  extraction_warnings TEXT[],
  
  -- File Tracking
  raw_file_paths TEXT[],        -- ["gs://bucket/path/file.dwg", "gs://bucket/path/file.pdf"]
  processed_json_path TEXT,     -- "gs://bucket/processed/project-id.json"
  file_types_uploaded TEXT[],   -- ["dwg", "pdf"]
  
  -- Quality & Approval
  approved BOOLEAN DEFAULT FALSE,
  quality_score NUMERIC(4,2),   -- 0.00 - 100.00
  approved_at TIMESTAMPTZ,
  
  -- Ownership & Metadata
  architect_id TEXT,
  company_id TEXT,
  schema_version INT DEFAULT 2,
  
  -- Timestamps
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_projects_category ON projects (project_category);
CREATE INDEX idx_projects_approved ON projects (approved) WHERE approved = TRUE;
CREATE INDEX idx_projects_created ON projects (created_at DESC);
```

#### spaces table

Individual spaces within projects:

```sql
CREATE TABLE spaces (
  id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_id UUID REFERENCES projects(id) ON DELETE CASCADE,
  
  -- Identity
  space_name TEXT NOT NULL,
  space_type TEXT NOT NULL,  -- "office", "conference", "cafeteria", etc.
  space_category TEXT,
  floor_number INT DEFAULT 1,
  
  -- Dimensions
  area_sqm NUMERIC(8,2),
  area_sqft NUMERIC(8,2),
  width_m NUMERIC(6,2),
  length_m NUMERIC(6,2),
  height_m NUMERIC(5,2),
  
  -- Environmental & Orientation
  facing_direction TEXT,  -- "north", "south", "east", "west", "northeast", etc.
  has_natural_light BOOLEAN DEFAULT TRUE,
  has_cross_vent BOOLEAN DEFAULT FALSE,
  has_direct_access_to TEXT[],  -- ["lobby", "reception"]
  
  -- Special Properties
  is_critical_space BOOLEAN DEFAULT FALSE,  -- ICU, server room, prayer hall, etc.
  special_requirements TEXT[],   -- ["AV_capable", "fire_rated", "isolated"]
  
  -- Positioning (normalized 0-1)
  position_x_norm NUMERIC(5,2),
  position_y_norm NUMERIC(5,2),
  
  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_spaces_project ON spaces (project_id);
CREATE INDEX idx_spaces_type ON spaces (space_type);
```

#### spatial_relationships table

Adjacency rules, flow patterns, visibility requirements:

```sql
CREATE TABLE spatial_relationships (
  id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_id UUID REFERENCES projects(id) ON DELETE CASCADE,
  
  space_a TEXT NOT NULL,     -- "Conference Room A"
  space_b TEXT NOT NULL,     -- "Reception"
  
  -- Relationship Type
  relationship_type TEXT NOT NULL,
    -- "adjacent_open_plan"
    -- "visually_connected"
    -- "directly_accessible"
    -- "acoustically_isolated"
    -- "require_shared_services"
  
  relationship_reason TEXT,   -- "Professional visibility + accessibility"
  priority TEXT DEFAULT 'preferred',  -- "required", "preferred", "nice_to_have"
  
  -- Data Provenance
  is_ai_extracted BOOLEAN DEFAULT TRUE,
  is_architect_confirmed BOOLEAN DEFAULT FALSE,
  
  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_relationships_project ON spatial_relationships (project_id);
```

#### project_embeddings table

Three semantic embeddings per project for hybrid search (768-dimensional vectors):

```sql
CREATE TABLE project_embeddings (
  id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_id UUID REFERENCES projects(id) ON DELETE CASCADE,
  
  -- Embedding Type (one per project for each type)
  embedding_type TEXT NOT NULL,
    -- "spatial" (organization, adjacency, flow)
    -- "site" (regulatory, environmental, context)
    -- "design_intent" (vision, aesthetics, use-case)
  
  -- Vector Data (pgvector extension)
  embedding VECTOR(768),
  embedding_text TEXT,  -- Source text for debugging/audit
  model_version TEXT DEFAULT 'text-embedding-004',
  
  created_at TIMESTAMPTZ DEFAULT NOW(),
  
  UNIQUE(project_id, embedding_type)
);

-- HNSW indexes for fast approximate nearest neighbor search
CREATE INDEX idx_embed_spatial_hnsw
  ON project_embeddings USING hnsw (embedding vector_cosine_ops)
  WHERE embedding_type = 'spatial';

CREATE INDEX idx_embed_site_hnsw
  ON project_embeddings USING hnsw (embedding vector_cosine_ops)
  WHERE embedding_type = 'site';

CREATE INDEX idx_embed_intent_hnsw
  ON project_embeddings USING hnsw (embedding vector_cosine_ops)
  WHERE embedding_type = 'design_intent';
```

#### architect_feedback table

Phase 3 addition for tracking corrections & retraining signals:

```sql
CREATE TABLE architect_feedback (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  request_id TEXT NOT NULL,
  architect_id TEXT,
  selected_variation INTEGER NOT NULL DEFAULT 1,
  correction_type TEXT NOT NULL DEFAULT 'accepted',
    -- "accepted" (layout is good)
    -- "modified" (layout modified before use)
    -- "rejected" (layout not usable, need to regenerate)
  corrected_dwg_notes TEXT DEFAULT '',
  severity TEXT NOT NULL DEFAULT 'minor',
    -- "minor" (cosmetic)
    -- "moderate" (functional impact)
    -- "critical" (architectural error, safety issue)
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_feedback_request_id ON architect_feedback (request_id);
CREATE INDEX idx_feedback_created_at ON architect_feedback (created_at);
CREATE INDEX idx_feedback_correction ON architect_feedback (correction_type);
```

---

## Data Contracts

### DrawActionPlan (C# ↔ Python Contract)

The canonical format for communicating drawing instructions between GCP backend and AutoCAD plugin.

**File**: [shared/contracts/draw_action_plan.py](shared/contracts/draw_action_plan.py)

```python
class ActionType(str, Enum):
    DRAW_WALL = "DRAW_WALL"
    DRAW_DOOR = "DRAW_DOOR"
    DRAW_WINDOW = "DRAW_WINDOW"
    DRAW_COLUMN = "DRAW_COLUMN"
    DRAW_STAIR = "DRAW_STAIR"
    DRAW_ROOM_LABEL = "DRAW_ROOM_LABEL"
    ADD_DIMENSION = "ADD_DIMENSION"
    ADD_AREA_TAG = "ADD_AREA_TAG"
    ADD_NORTH_ARROW = "ADD_NORTH_ARROW"
    ADD_SCALE_BAR = "ADD_SCALE_BAR"
    ADD_TITLE_BLOCK = "ADD_TITLE_BLOCK"
    ADD_HATCH = "ADD_HATCH"
    CREATE_LAYER = "CREATE_LAYER"
    START_GROUP = "START_GROUP"
    END_GROUP = "END_GROUP"

class Point2D(BaseModel):
    x: float  # millimeters
    y: float  # millimeters

class DrawAction(BaseModel):
    action_type: ActionType
    layer: str                          # AutoCAD layer name
    group_id: str | None = None
    start: Point2D | None = None        # For lines, walls
    end: Point2D | None = None
    vertices: list[Point2D] = []        # For polygons
    center: Point2D | None = None       # For circles
    thickness_mm: float | None = None
    height_mm: float | None = None
    wall_type: str | None = None        # "structural", "curtain_wall", "partition"
    door_width_mm: float | None = None
    door_swing: str = "right"
    swing_angle: float = 90.0
    window_width_mm: float | None = None
    window_height_mm: float | None = None
    window_sill_mm: float | None = None
    # ... 30+ more properties for various geometry types
```

**Contract Guarantee**: All coordinates in **millimeters**. AutoCAD plugin converts to drawing units.

---

## Deployment & DevOps

### CI/CD Pipeline (Cloud Build)

**File**: [cloudbuild.yaml](cloudbuild.yaml)

```yaml
steps:
  # ── Build all three services in parallel ──
  - name: 'gcr.io/cloud-builders/docker'
    id: build-ingestion
    args: ['build', '-f', 'services/ingestion/Dockerfile', 
           '-t', 'us-central1-docker.pkg.dev/$PROJECT_ID/zheight-services/ingestion:$TAG_NAME']
    waitFor: ['-']  # Don't wait for previous steps
  
  - name: 'gcr.io/cloud-builders/docker'
    id: build-rag-api
    args: ['build', '-f', 'services/rag-api/Dockerfile', ...]
    waitFor: ['-']
  
  - name: 'gcr.io/cloud-builders/docker'
    id: build-quality-gate
    args: ['build', '-f', 'services/quality-gate/Dockerfile', ...]
    waitFor: ['-']
  
  # ── Push all images ──
  - name: 'gcr.io/cloud-builders/docker'
    id: push
    args: ['push', '--all-tags', 'us-central1-docker.pkg.dev/$PROJECT_ID/zheight-services/...']
```

**Trigger**: On git tag push (v3.1.0, etc.)

**Output**: Docker images pushed to Artifact Registry

### Deployment Script

**File**: [deploy_phase3.sh](deploy_phase3.sh)

Key deployments:

```bash
#!/usr/bin/env bash

PROJECT_ID="zheight-ai-kb"
REGION="us-central1"
REPO="us-central1-docker.pkg.dev/${PROJECT_ID}/zheight-services"
CLOUD_SQL_CONN="${PROJECT_ID}:${REGION}:${PROJECT_ID}-pg"
CONNECTOR="projects/${PROJECT_ID}/locations/${REGION}/connectors/${PROJECT_ID}-vpc-connector"

# 1. Run database migration (Phase 3 feedback table)
echo "Applying Phase 3 DB migration..."
psql -h 127.0.0.1 -p 5433 -U kb_admin -d zheight_kb -f migrations/phase3_feedback_table.sql

# 2. Build & Push Docker images
echo "Building rag-api:v3.1..."
cd services/rag-api
docker build -t "${REPO}/rag-api:v3.1" .
docker push "${REPO}/rag-api:v3.1"

# 3. Deploy RAG API to Cloud Run
gcloud run deploy rag-api \
  --image="${REPO}/rag-api:v3.1" \
  --region=${REGION} \
  --platform=managed \
  --memory=2Gi \
  --cpu=2 \
  --max-instances=10 \
  --service-account=${SA_SERVING} \
  --vpc-connector=${CONNECTOR} \
  --set-env-vars="\
    GCP_PROJECT=${PROJECT_ID},\
    DB_USER=kb_admin,\
    DB_NAME=zheight_kb,\
    CLOUD_SQL_CONNECTION_NAME=${CLOUD_SQL_CONN},\
    RAG_API_KEY=$(gcloud secrets versions access latest --secret=rag-api-key),\
    GEMINI_API_KEY=$(gcloud secrets versions access latest --secret=gemini-api-key),\
    REDIS_HOST=$(gcloud redis instances describe ${PROJECT_ID}-cache --region=${REGION} --format='value(host)'),\
    REDIS_PORT=$(gcloud redis instances describe ${PROJECT_ID}-cache --region=${REGION} --format='value(port)'),\
    REDIS_AUTH=$(gcloud secrets versions access latest --secret=redis-auth)\
  " \
  --quiet
```

### Infrastructure Requirements

| Component | Spec | Notes |
|-----------|------|-------|
| **Cloud Run (Ingestion)** | 2 CPU, 4GB RAM, max 100 | Handles long parsing/extraction operations |
| **Cloud Run (Quality Gate)** | 1 CPU, 1GB RAM, max 50 | Lightweight decision logic |
| **Cloud Run (RAG API)** | 2 CPU, 2GB RAM, max 10 | Primary user-facing service |
| **Cloud SQL PostgreSQL** | db-custom-4-16GB | Primary storage, backups every 6h |
| **Cloud SQL Read Replica** | db-custom-2-8GB | Offload SELECT queries from primary |
| **Redis (Memorystore)** | 5GB, high-availability | Session cache, feedback dedup |
| **Cloud Storage** | Multi-regional | Upload staging, processed files |
| **Vertex AI / Gemini** | API usage-based | Extract & generate models |

---

## Error Handling & Resilience

### Retry Strategies

**File**: [shared/utils/retry.py](shared/utils/retry.py)

```python
@async_retry(max_attempts=3, base_delay=1.0, max_delay=30.0)
async def call_vertex_ai():
    # Exponential backoff:
    # Attempt 1: fail, wait 1.0 - 2.0 seconds
    # Attempt 2: fail, wait 2.0 - 4.0 seconds
    # Attempt 3: fail, wait 4.0 - 8.0 seconds
    pass
```

### Pub/Sub Message Handling

- **Idempotency**: File hash + DB uniqueness check prevents reprocessing
- **Ack Deadline**: 10-minute deadline allows long operations
- **Dead Letter Queue**: Failed messages sent to DLQ after 5 retries
- **Manual Ack**: Service only acknowledges after successful DB commit

### Transaction Boundaries

- **Ingestion**: Atomic transaction for project + spaces + relationships + embeddings
- **Quality Gate**: Single UPDATE transaction
- **RAG API**: Search/Generate are read-only; Feedback uses single INSERT

### Health Checks

Every service exposes `/health`:

```json
{
  "service": "ingestion|quality-gate|rag-api",
  "status": "ok|degraded",
  "db": "ok|error: connection refused",
  "vertexai": "ok|error: quota exceeded",
  "redis": "ok|degraded: timeout",
  "approved_projects": 147,
  "version": "3.1.0"
}
```

Cloud Run autoscaler uses these to route traffic away from unhealthy instances.

---

## Logging & Observability

### Structured Logging (structlog)

All services use structlog with JSON output:

```python
import structlog

log = structlog.get_logger()

log.info("ingest_received", file=file_name, message_id=message_id)
log.warning("retry_backoff", fn="call_gemini", attempt=2, delay=2.5)
log.error("db_write_failed", project_id=project_id, error=str(exc))
```

**JSON Output**:
```json
{
  "timestamp": "2024-01-15T10:23:45.123456Z",
  "level": "info",
  "event": "ingest_received",
  "file": "floor-01.dwg",
  "message_id": "12345",
  "request_id": "550e8400-e29b-41d4-a716-446655440000"
}
```

### Request ID Propagation

Every request gets a unique X-Request-ID that flows through:
- HTTP headers
- Pub/Sub message attributes
- structlog context
- Database audit logs

Enables full request tracing across distributed services.

### Metrics (Cloud Logging / Stackdriver)

Automatically collected by Cloud Run:
- Request count, latency, error rate
- CPU, memory, disk usage
- Cold start duration
- Replica count, scaling events

---

## Performance Characteristics

### Search Latency

- **Query Embedding**: ~150ms (Vertex AI API)
- **Vector Search (HNSW)**: ~50-100ms (3 embedding types in parallel)
- **Metadata JOIN**: ~20-50ms
- **Total**: **250-350ms** for top-10 results

### Generation Latency

- **KB Retrieval**: ~250ms (as above)
- **KB Context Compilation**: ~50ms (JSON serialization)
- **Gemini API Call**: ~2-4 seconds (LLM generation)
- **DrawActionPlan Conversion**: ~100-200ms
- **Total**: **2.5-4.5 seconds** for 3 variations

### Ingestion Throughput

- **Typical file**: DWG 10-50MB → parsed + extracted + embedded in **30-90 seconds**
- **Bottleneck**: Vertex AI AI extraction (sequential for safety)
- **Concurrent uploads**: Up to 100 via Cloud Run autoscaling

### Database Performance

- **Project lookup**: <10ms (index on project_id)
- **Vector ANN search**: <100ms (HNSW index on VECTOR(768))
- **Space enumeration**: <50ms (index on project_id)
- **Transaction write**: <100ms (3 tables, atomic)

---

## Security

### Authentication & Authorization

- **API Key**: All endpoints except `/health` require `X-API-Key` header
- **Service Accounts**: Cloud Run uses Workload Identity for GCS/Pub/Sub/Secret Manager
- **Cloud SQL Auth Proxy**: Socket-based authentication (Unix domain socket in production)
- **CORS**: Configurable per environment

### Secrets Management

- **DB Password**: Cloud Secret Manager (rotated every 90 days)
- **API Keys**: Cloud Secret Manager
- **Service Account Keys**: Workload Identity (no key files)

### Data Privacy

- **Encryption at Rest**: Cloud SQL automatic encryption (AES-256)
- **Encryption in Transit**: TLS 1.3 for all connections
- **PII Handling**: Project/architect IDs stored encrypted

---

## Monitoring Checklist

### Critical Alerts

1. **Ingestion Service** 
   - `/health` returns `status: degraded` → DB or Vertex AI down
   - Pub/Sub dead-letter queue > 0 → Processing failures
   - Cold start latency > 30s → Scale up container

2. **Quality Gate Service**
   - Approval backlog > 1000 → Increase concurrency
   - Error rate > 1% → Check DB connection pool

3. **RAG API Service**
   - Search latency p99 > 1s → Vector index degraded
   - Redis connection pool exhausted → Max instances too low
   - GEMINI_API_KEY invalid → Regenerate from Secret Manager

4. **Database**
   - CPU > 80% → Increase instance size
   - Replication lag > 10s → Add read replicas
   - Connection pool exhausted → Increase max connections

---

## Future Enhancements (Roadmap)

1. **Model Fine-Tuning**: Train custom Gemini models on architect feedback data
2. **Real-Time Collaboration**: WebSocket support for multi-architect feedback
3. **Constraint Solver**: Automated feasibility checking for regulatory conflicts
4. **3D Generation**: Export to 3D file formats (3DM, IFC, Revit)
5. **Mobile App**: Mobile interface for project search & feedback
6. **Integration with BIM Tools**: Direct Revit plugin integration
7. **Advanced Analytics**: Dashboard for project trends, most common spatial patterns
8. **Federated Learning**: Privacy-preserving KB sharing across organizations

---

## Troubleshooting Guide

### "Extraction confidence too low (0.45 < 0.75)"

**Cause**: AI model struggled with file format or quality

**Solution**:
1. Check file resolution (images < 150 DPI may be too low)
2. Provide additional reference images for complex projects
3. Manually annotate project metadata via admin UI

### "Vector search returning irrelevant results"

**Cause**: Embedding model weights may be outdated

**Solution**:
1. Regenerate embeddings: `UPDATE project_embeddings SET embedding = NULL WHERE created_at < '2024-01-01'`
2. Re-run embedder pipeline for affected projects
3. Verify HNSW indexes: `REINDEX INDEX idx_embed_spatial_hnsw;`

### "Generation timeout (>5 seconds)"

**Cause**: Gemini API latency or KB retrieval overhead

**Solution**:
1. Increase max-instances in Cloud Run deployment
2. Check Redis connection pool: `REDIS_POOL_SIZE`
3. Profile KB retrieval: add query time logging

### "Database connection pool exhausted"

**Cause**: Too many concurrent requests or connection leaks

**Solution**:
```python
# In client.py, increase pool settings
pool_size=10  # was 5
max_overflow=20  # was 10
pool_timeout=60  # was 30
```

---

## Conclusion

The zHeight KB Services platform is a sophisticated distributed system designed for architectural intelligence at scale. Each microservice has a focused responsibility, asynchronous communication prevents bottlenecks, and the three-layer embedding architecture enables nuanced semantic search. Deployment on Cloud Run with PostgreSQL provides enterprise-grade reliability while remaining cost-efficient through autoscaling.

The system is ready for production use with Phase 3 improvements (feedback tracking, Redis caching, read replicas) deployed and operational.

