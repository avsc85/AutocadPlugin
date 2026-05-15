-- Phase 2 Universal Schema
-- Run after Phase 1. Adds new tables; does NOT drop Phase 1 tables.

CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Universal projects table
CREATE TABLE IF NOT EXISTS projects (
  id                      UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_name            TEXT,
  project_category        TEXT,
  project_sub_type        TEXT,
  domain_tags             TEXT[],
  total_built_area_sqm    NUMERIC(12,2),
  total_built_area_sqft   NUMERIC(12,2),
  floor_count             INT DEFAULT 1,
  basement_levels         INT DEFAULT 0,
  units_count             INT,
  site_context            JSONB,
  regulatory_constraints  JSONB,
  program_requirements    JSONB,
  environmental_factors   JSONB,
  design_intent           JSONB,
  ai_extraction_model     TEXT,
  ai_extraction_version   TEXT,
  extraction_confidence   NUMERIC(4,3),
  extraction_warnings     TEXT[],
  raw_file_paths          TEXT[],
  processed_json_path     TEXT,
  file_types_uploaded     TEXT[],
  approved                BOOLEAN DEFAULT FALSE,
  quality_score           NUMERIC(4,2),
  architect_id            TEXT,
  company_id              TEXT,
  schema_version          INT DEFAULT 2,
  created_at              TIMESTAMPTZ DEFAULT NOW(),
  updated_at              TIMESTAMPTZ DEFAULT NOW(),
  approved_at             TIMESTAMPTZ
);

-- Spaces
CREATE TABLE IF NOT EXISTS spaces (
  id                UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_id        UUID REFERENCES projects(id) ON DELETE CASCADE,
  space_name        TEXT NOT NULL,
  space_type        TEXT NOT NULL,
  space_category    TEXT,
  floor_number      INT DEFAULT 1,
  area_sqm          NUMERIC(8,2),
  area_sqft         NUMERIC(8,2),
  width_m           NUMERIC(6,2),
  length_m          NUMERIC(6,2),
  height_m          NUMERIC(5,2),
  facing_direction  TEXT,
  has_natural_light BOOLEAN DEFAULT TRUE,
  has_cross_vent    BOOLEAN DEFAULT FALSE,
  has_direct_access_to TEXT[],
  is_critical_space BOOLEAN DEFAULT FALSE,
  special_requirements TEXT[],
  position_x_norm   NUMERIC(5,2),
  position_y_norm   NUMERIC(5,2),
  created_at        TIMESTAMPTZ DEFAULT NOW()
);

-- Spatial relationships
CREATE TABLE IF NOT EXISTS spatial_relationships (
  id                UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_id        UUID REFERENCES projects(id) ON DELETE CASCADE,
  space_a           TEXT NOT NULL,
  space_b           TEXT NOT NULL,
  relationship_type TEXT NOT NULL,
  relationship_reason TEXT,
  priority          TEXT DEFAULT 'preferred',
  is_ai_extracted   BOOLEAN DEFAULT TRUE,
  is_architect_confirmed BOOLEAN DEFAULT FALSE,
  created_at        TIMESTAMPTZ DEFAULT NOW()
);

-- Three-layer embeddings (HNSW — better than IVFFlat for updates and recall)
CREATE TABLE IF NOT EXISTS project_embeddings (
  id                UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_id        UUID REFERENCES projects(id) ON DELETE CASCADE,
  embedding_type    TEXT NOT NULL,
  embedding         VECTOR(768),
  embedding_text    TEXT,
  model_version     TEXT DEFAULT 'text-embedding-004',
  created_at        TIMESTAMPTZ DEFAULT NOW(),
  UNIQUE(project_id, embedding_type)
);

-- HNSW indexes — one per embedding type for fast ANN search
CREATE INDEX IF NOT EXISTS idx_embed_spatial_hnsw
  ON project_embeddings USING hnsw (embedding vector_cosine_ops)
  WHERE embedding_type = 'spatial';

CREATE INDEX IF NOT EXISTS idx_embed_site_hnsw
  ON project_embeddings USING hnsw (embedding vector_cosine_ops)
  WHERE embedding_type = 'site';

CREATE INDEX IF NOT EXISTS idx_embed_intent_hnsw
  ON project_embeddings USING hnsw (embedding vector_cosine_ops)
  WHERE embedding_type = 'intent';

-- Regulations
CREATE TABLE IF NOT EXISTS regulations (
  id                UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  jurisdiction      TEXT NOT NULL,
  code_body         TEXT,
  code_version      TEXT NOT NULL,
  project_category  TEXT,
  rule_category     TEXT NOT NULL,
  rule_name         TEXT NOT NULL,
  rule_description  TEXT NOT NULL,
  rule_value        JSONB,
  applies_to_zones  TEXT[],
  effective_date    DATE,
  expiry_date       DATE,
  is_active         BOOLEAN DEFAULT TRUE,
  source_document   TEXT,
  created_at        TIMESTAMPTZ DEFAULT NOW()
);

-- Architect corrections
CREATE TABLE IF NOT EXISTS architect_corrections (
  id                    UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  source_project_id     UUID REFERENCES projects(id),
  architect_id          TEXT,
  correction_type       TEXT NOT NULL,
  original_generation   JSONB,
  corrected_output      JSONB,
  correction_notes      TEXT,
  severity              TEXT DEFAULT 'minor',
  used_in_training      BOOLEAN DEFAULT FALSE,
  training_run_id       TEXT,
  created_at            TIMESTAMPTZ DEFAULT NOW()
);

-- Training registry
CREATE TABLE IF NOT EXISTS training_runs (
  id                UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  run_name          TEXT NOT NULL,
  trigger_type      TEXT,
  dataset_gcs_path  TEXT,
  project_count     INT,
  correction_count  INT,
  project_categories TEXT[],
  vertex_job_id     TEXT,
  vertex_model_id   TEXT,
  vertex_endpoint_id TEXT,
  eval_scores       JSONB,
  is_production     BOOLEAN DEFAULT FALSE,
  created_at        TIMESTAMPTZ DEFAULT NOW(),
  completed_at      TIMESTAMPTZ
);

-- Standard indexes
CREATE INDEX IF NOT EXISTS idx_projects_category    ON projects(project_category);
CREATE INDEX IF NOT EXISTS idx_projects_tags        ON projects USING gin(domain_tags);
CREATE INDEX IF NOT EXISTS idx_projects_approved    ON projects(approved);
CREATE INDEX IF NOT EXISTS idx_projects_company     ON projects(company_id);
CREATE INDEX IF NOT EXISTS idx_spaces_project       ON spaces(project_id);
CREATE INDEX IF NOT EXISTS idx_spaces_type          ON spaces(space_type);
CREATE INDEX IF NOT EXISTS idx_rels_project         ON spatial_relationships(project_id);
CREATE INDEX IF NOT EXISTS idx_regs_jurisdiction    ON regulations(jurisdiction, is_active);
CREATE INDEX IF NOT EXISTS idx_corrections_training ON architect_corrections(used_in_training);
CREATE INDEX IF NOT EXISTS idx_projects_fts ON projects
  USING gin(to_tsvector('english',
    coalesce(project_name,'') || ' ' ||
    coalesce(project_category,'') || ' ' ||
    coalesce(project_sub_type,'')));

-- updated_at trigger
CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER AS $$ BEGIN NEW.updated_at = NOW(); RETURN NEW; END; $$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_projects_updated ON projects;
CREATE TRIGGER trg_projects_updated
  BEFORE UPDATE ON projects
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();
