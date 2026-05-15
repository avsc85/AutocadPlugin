# GCP Phase 1 — Architectural Intelligence System Setup

> Paste each section into Claude Code (VS Code terminal) in order.
> Set your project values in **Step 0** before running anything else.

---

## Step 0 — Configure your project variables

```bash
# ── SET THESE BEFORE RUNNING ANYTHING ──────────────────────────────────────
export PROJECT_ID="zheight-ai-kb"          # Your GCP project ID
export PROJECT_NUMBER="zheight-ai-kb"                   # Fill after project is created
export REGION="us-central1"               # Primary region
export ZONE="us-central1-a"              # Primary zone
export BILLING_ACCOUNT=""                 # Your billing account ID (gcloud billing accounts list)

# Derived names (do not change)
export KB_BUCKET="gs://${PROJECT_ID}-kb-raw"
export PROCESSED_BUCKET="gs://${PROJECT_ID}-kb-processed"
export MODELS_BUCKET="gs://${PROJECT_ID}-models"
export BACKUPS_BUCKET="gs://${PROJECT_ID}-backups"
export DB_INSTANCE="${PROJECT_ID}-pg"
export DB_NAME="zheight_kb"
export DB_USER="kb_admin"
export DB_PASSWORD=""                      # Set a strong password here
export REDIS_INSTANCE="${PROJECT_ID}-cache"
export SA_INGESTION="sa-ingestion"
export SA_SERVING="sa-serving"
export SA_TRAINING="sa-training"
export NETWORK_NAME="${PROJECT_ID}-vpc"
```

---

## Step 1 — Authenticate and create project

```bash
# Authenticate
gcloud auth login
gcloud auth application-default login

# Create project
gcloud projects create ${PROJECT_ID} --name="zHeight AI Knowledge Base"

# Set project
gcloud config set project ${PROJECT_ID}
gcloud config set compute/region ${REGION}
gcloud config set compute/zone ${ZONE}

# Link billing
gcloud billing projects link ${PROJECT_ID} --billing-account=${BILLING_ACCOUNT}

# Store project number
export PROJECT_NUMBER=$(gcloud projects describe ${PROJECT_ID} --format="value(projectNumber)")
echo "Project number: ${PROJECT_NUMBER}"
```

---

## Step 2 — Enable all required GCP APIs

```bash
gcloud services enable \
  storage.googleapis.com \
  storage-component.googleapis.com \
  sqladmin.googleapis.com \
  sql-component.googleapis.com \
  redis.googleapis.com \
  pubsub.googleapis.com \
  eventarc.googleapis.com \
  run.googleapis.com \
  cloudbuild.googleapis.com \
  cloudscheduler.googleapis.com \
  cloudfunctions.googleapis.com \
  compute.googleapis.com \
  servicenetworking.googleapis.com \
  vpcaccess.googleapis.com \
  aiplatform.googleapis.com \
  ml.googleapis.com \
  documentai.googleapis.com \
  vision.googleapis.com \
  bigquery.googleapis.com \
  bigquerystorage.googleapis.com \
  monitoring.googleapis.com \
  logging.googleapis.com \
  cloudtrace.googleapis.com \
  secretmanager.googleapis.com \
  iam.googleapis.com \
  iamcredentials.googleapis.com \
  artifactregistry.googleapis.com \
  container.googleapis.com \
  containerregistry.googleapis.com \
  dataflow.googleapis.com \
  workflows.googleapis.com \
  looker.googleapis.com

echo "✓ All APIs enabled"
```

---

## Step 3 — Create VPC network and subnets

```bash
# Create custom VPC
gcloud compute networks create ${NETWORK_NAME} \
  --subnet-mode=custom \
  --bgp-routing-mode=regional

# Main subnet for Cloud Run / services
gcloud compute subnets create ${NETWORK_NAME}-subnet-main \
  --network=${NETWORK_NAME} \
  --region=${REGION} \
  --range=10.0.0.0/20

# Reserved subnet for VPC connector
gcloud compute subnets create ${NETWORK_NAME}-subnet-connector \
  --network=${NETWORK_NAME} \
  --region=${REGION} \
  --range=10.8.0.0/28

# Reserved IP range for private services (Cloud SQL, Redis)
gcloud compute addresses create google-managed-services-${NETWORK_NAME} \
  --global \
  --purpose=VPC_PEERING \
  --prefix-length=16 \
  --network=${NETWORK_NAME}

# Peer with Google services
gcloud services vpc-peerings connect \
  --service=servicenetworking.googleapis.com \
  --ranges=google-managed-services-${NETWORK_NAME} \
  --network=${NETWORK_NAME}

# Firewall: allow internal traffic
gcloud compute firewall-rules create ${NETWORK_NAME}-allow-internal \
  --network=${NETWORK_NAME} \
  --allow=tcp,udp,icmp \
  --source-ranges=10.0.0.0/8

# Firewall: allow health checks
gcloud compute firewall-rules create ${NETWORK_NAME}-allow-health-checks \
  --network=${NETWORK_NAME} \
  --allow=tcp \
  --source-ranges=35.191.0.0/16,130.211.0.0/22

# VPC serverless connector (Cloud Run → private services)
gcloud compute networks vpc-access connectors create ${NETWORK_NAME}-connector \
  --region=${REGION} \
  --subnet=${NETWORK_NAME}-subnet-connector \
  --subnet-project=${PROJECT_ID} \
  --min-instances=2 \
  --max-instances=10

echo "✓ VPC network ready"
```

---

## Step 4 — Create GCS storage buckets

```bash
# Raw uploads: DWG, PDF, images, Revit files
gcloud storage buckets create ${KB_BUCKET} \
  --location=${REGION} \
  --uniform-bucket-level-access \
  --versioning

# Processed outputs: parsed JSON, embeddings
gcloud storage buckets create ${PROCESSED_BUCKET} \
  --location=${REGION} \
  --uniform-bucket-level-access \
  --versioning

# Model artifacts: fine-tuned model checkpoints
gcloud storage buckets create ${MODELS_BUCKET} \
  --location=${REGION} \
  --uniform-bucket-level-access \
  --versioning

# Backups: DB dumps, KB snapshots
gcloud storage buckets create ${BACKUPS_BUCKET} \
  --location=${REGION} \
  --uniform-bucket-level-access

# Lifecycle policy: move raw files to Nearline after 90 days
cat > /tmp/lifecycle_raw.json << 'EOF'
{
  "rule": [
    {
      "action": { "type": "SetStorageClass", "storageClass": "NEARLINE" },
      "condition": { "age": 90 }
    },
    {
      "action": { "type": "SetStorageClass", "storageClass": "COLDLINE" },
      "condition": { "age": 365 }
    }
  ]
}
EOF

gcloud storage buckets update ${KB_BUCKET} \
  --lifecycle-file=/tmp/lifecycle_raw.json

# Create folder structure in raw bucket
echo "" | gcloud storage cp - ${KB_BUCKET}/dwg/.keep
echo "" | gcloud storage cp - ${KB_BUCKET}/pdf/.keep
echo "" | gcloud storage cp - ${KB_BUCKET}/images/.keep
echo "" | gcloud storage cp - ${KB_BUCKET}/revit/.keep
echo "" | gcloud storage cp - ${KB_BUCKET}/regulations/.keep
echo "" | gcloud storage cp - ${KB_BUCKET}/standards/.keep
echo "" | gcloud storage cp - ${KB_BUCKET}/training_data/.keep

echo "✓ GCS buckets created"
```

---

## Step 5 — Create Cloud SQL (PostgreSQL + pgvector)

```bash
# Create PostgreSQL 15 instance
gcloud sql instances create ${DB_INSTANCE} \
  --database-version=POSTGRES_15 \
  --tier=db-custom-4-16384 \
  --region=${REGION} \
  --network=${NETWORK_NAME} \
  --no-assign-ip \
  --enable-google-private-path \
  --storage-type=SSD \
  --storage-size=100GB \
  --storage-auto-increase \
  --storage-auto-increase-limit=500 \
  --backup-start-time=02:00 \
  --enable-point-in-time-recovery \
  --retained-backups-count=14 \
  --retained-transaction-log-days=7 \
  --maintenance-window-day=SUN \
  --maintenance-window-hour=3 \
  --database-flags=max_connections=200,shared_buffers=4096MB,work_mem=64MB

# Set admin password
gcloud sql users set-password postgres \
  --instance=${DB_INSTANCE} \
  --password=${DB_PASSWORD}

# Create application user
gcloud sql users create ${DB_USER} \
  --instance=${DB_INSTANCE} \
  --password=${DB_PASSWORD}

# Create knowledge base database
gcloud sql databases create ${DB_NAME} \
  --instance=${DB_INSTANCE} \
  --charset=UTF8

# Create read replica (for serving layer — no write load on primary)
gcloud sql instances create ${DB_INSTANCE}-replica \
  --master-instance-name=${DB_INSTANCE} \
  --region=${REGION} \
  --tier=db-custom-2-8192

echo "✓ Cloud SQL instance created — run schema setup in Step 10"
```

---

## Step 6 — Create Memorystore Redis (cache layer)

```bash
gcloud redis instances create ${REDIS_INSTANCE} \
  --size=2 \
  --region=${REGION} \
  --network=projects/${PROJECT_ID}/global/networks/${NETWORK_NAME} \
  --tier=standard \
  --redis-version=redis_7_0 \
  --enable-auth \
  --transit-encryption-mode=SERVER_AUTHENTICATION

# Get Redis host (needed for app config)
export REDIS_HOST=$(gcloud redis instances describe ${REDIS_INSTANCE} \
  --region=${REGION} \
  --format="value(host)")
export REDIS_PORT=$(gcloud redis instances describe ${REDIS_INSTANCE} \
  --region=${REGION} \
  --format="value(port)")

echo "Redis host: ${REDIS_HOST}:${REDIS_PORT}"
echo "✓ Redis cache ready"
```

---

## Step 7 — Create Pub/Sub topics and subscriptions

```bash
# Topic: new raw file uploaded
gcloud pubsub topics create layout-raw-uploaded

# Topic: file parsed and ready for embedding
gcloud pubsub topics create layout-parsed

# Topic: embedding complete, ready for KB write
gcloud pubsub topics create layout-embedded

# Topic: architect corrections from plugin
gcloud pubsub topics create layout-corrections

# Topic: retraining trigger
gcloud pubsub topics create training-trigger

# Topic: dead-letter queue for failed messages
gcloud pubsub topics create layout-ingestion-dlq

# Subscriptions
gcloud pubsub subscriptions create sub-raw-uploaded \
  --topic=layout-raw-uploaded \
  --ack-deadline=300 \
  --dead-letter-topic=layout-ingestion-dlq \
  --max-delivery-attempts=5

gcloud pubsub subscriptions create sub-parsed \
  --topic=layout-parsed \
  --ack-deadline=300 \
  --dead-letter-topic=layout-ingestion-dlq \
  --max-delivery-attempts=5

gcloud pubsub subscriptions create sub-embedded \
  --topic=layout-embedded \
  --ack-deadline=120 \
  --dead-letter-topic=layout-ingestion-dlq \
  --max-delivery-attempts=5

gcloud pubsub subscriptions create sub-corrections \
  --topic=layout-corrections \
  --ack-deadline=300 \
  --dead-letter-topic=layout-ingestion-dlq \
  --max-delivery-attempts=5

gcloud pubsub subscriptions create sub-training-trigger \
  --topic=training-trigger \
  --ack-deadline=60

echo "✓ Pub/Sub topics and subscriptions created"
```

---

## Step 8 — Set up Eventarc triggers (GCS → Pub/Sub)

```bash
# Grant Eventarc the GCS service account permission to publish
gcloud projects add-iam-policy-binding ${PROJECT_ID} \
  --member="serviceAccount:service-${PROJECT_NUMBER}@gs-project-accounts.iam.gserviceaccount.com" \
  --role="roles/pubsub.publisher"

# Grant Eventarc service account
gcloud projects add-iam-policy-binding ${PROJECT_ID} \
  --member="serviceAccount:service-${PROJECT_NUMBER}@gcp-sa-eventarc.iam.gserviceaccount.com" \
  --role="roles/eventarc.serviceAgent"

echo "✓ Eventarc permissions set — triggers will be created when Cloud Run services are deployed"
```

---

## Step 9 — Create service accounts with least-privilege IAM

```bash
# ── Ingestion service account ───────────────────────────────────────────────
gcloud iam service-accounts create ${SA_INGESTION} \
  --display-name="KB Ingestion Service"

# Permissions: read raw bucket, write processed bucket, publish Pub/Sub, write SQL
for ROLE in \
  roles/storage.objectViewer \
  roles/storage.objectCreator \
  roles/pubsub.publisher \
  roles/pubsub.subscriber \
  roles/cloudsql.client \
  roles/aiplatform.user \
  roles/documentai.apiUser; do
  gcloud projects add-iam-policy-binding ${PROJECT_ID} \
    --member="serviceAccount:${SA_INGESTION}@${PROJECT_ID}.iam.gserviceaccount.com" \
    --role="${ROLE}"
done

# ── Serving service account ─────────────────────────────────────────────────
gcloud iam service-accounts create ${SA_SERVING} \
  --display-name="KB Serving / RAG API"

for ROLE in \
  roles/storage.objectViewer \
  roles/pubsub.publisher \
  roles/cloudsql.client \
  roles/datastore.user \
  roles/aiplatform.user \
  roles/redis.viewer; do
  gcloud projects add-iam-policy-binding ${PROJECT_ID} \
    --member="serviceAccount:${SA_SERVING}@${PROJECT_ID}.iam.gserviceaccount.com" \
    --role="${ROLE}"
done

# ── Training service account ────────────────────────────────────────────────
gcloud iam service-accounts create ${SA_TRAINING} \
  --display-name="KB AI Training"

for ROLE in \
  roles/storage.objectAdmin \
  roles/aiplatform.user \
  roles/bigquery.dataEditor \
  roles/cloudsql.client \
  roles/logging.logWriter \
  roles/monitoring.metricWriter; do
  gcloud projects add-iam-policy-binding ${PROJECT_ID} \
    --member="serviceAccount:${SA_TRAINING}@${PROJECT_ID}.iam.gserviceaccount.com" \
    --role="${ROLE}"
done

echo "✓ Service accounts created"
```

---

## Step 10 — Store secrets in Secret Manager

```bash
# Cloud SQL connection string
echo -n "postgresql+psycopg2://${DB_USER}:${DB_PASSWORD}@/${DB_NAME}?host=/cloudsql/${PROJECT_ID}:${REGION}:${DB_INSTANCE}" \
  | gcloud secrets create db-connection-string --data-file=-

# DB password alone (for Cloud SQL Auth Proxy use)
echo -n "${DB_PASSWORD}" \
  | gcloud secrets create db-password --data-file=-

# Redis auth string
export REDIS_AUTH=$(gcloud redis instances get-auth-string ${REDIS_INSTANCE} \
  --region=${REGION} --format="value(authString)")
echo -n "${REDIS_AUTH}" \
  | gcloud secrets create redis-auth-string --data-file=-

# Grant service accounts access to secrets
for SA in ${SA_INGESTION} ${SA_SERVING} ${SA_TRAINING}; do
  gcloud secrets add-iam-policy-binding db-connection-string \
    --member="serviceAccount:${SA}@${PROJECT_ID}.iam.gserviceaccount.com" \
    --role="roles/secretmanager.secretAccessor"
  gcloud secrets add-iam-policy-binding db-password \
    --member="serviceAccount:${SA}@${PROJECT_ID}.iam.gserviceaccount.com" \
    --role="roles/secretmanager.secretAccessor"
done

gcloud secrets add-iam-policy-binding redis-auth-string \
  --member="serviceAccount:${SA_SERVING}@${PROJECT_ID}.iam.gserviceaccount.com" \
  --role="roles/secretmanager.secretAccessor"

echo "✓ Secrets stored"
```

---

## Step 11 — Bootstrap Cloud SQL schema (pgvector + tables)

```bash
# Install Cloud SQL Auth Proxy (Mac/Linux)
curl -o cloud-sql-proxy \
  https://storage.googleapis.com/cloud-sql-connectors/cloud-sql-proxy/v2.8.0/cloud-sql-proxy.linux.amd64
chmod +x cloud-sql-proxy

# Start proxy in background
./cloud-sql-proxy ${PROJECT_ID}:${REGION}:${DB_INSTANCE} --port=5433 &
PROXY_PID=$!
sleep 5

# Run schema bootstrap
PGPASSWORD=${DB_PASSWORD} psql \
  -h 127.0.0.1 -p 5433 \
  -U ${DB_USER} -d ${DB_NAME} << 'SCHEMA'

-- Enable pgvector extension
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- ── Layout projects master table ─────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS layout_projects (
  id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_name    TEXT NOT NULL,
  layout_type     TEXT NOT NULL,     -- '1BHK','2BHK','studio','office','commercial'
  total_area_sqft NUMERIC(10,2),
  floor_count     INT DEFAULT 1,
  style           TEXT,              -- 'modern','traditional','vastu'
  orientation     TEXT,              -- 'north','south','east','west'
  zone_code       TEXT,              -- e.g. 'R-1','C-2'
  building_code_version TEXT,
  city            TEXT,
  state           TEXT,
  architect_id    TEXT,
  approved        BOOLEAN DEFAULT FALSE,
  quality_score   NUMERIC(4,2),
  raw_file_path   TEXT,              -- GCS path to original file
  processed_path  TEXT,              -- GCS path to parsed JSON
  schema_version  INT DEFAULT 1,
  created_at      TIMESTAMPTZ DEFAULT NOW(),
  updated_at      TIMESTAMPTZ DEFAULT NOW(),
  ingested_at     TIMESTAMPTZ
);

-- ── Layout embeddings (vector search) ────────────────────────────────────────
CREATE TABLE IF NOT EXISTS layout_embeddings (
  id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_id      UUID REFERENCES layout_projects(id) ON DELETE CASCADE,
  embedding       VECTOR(1536),      -- text-embedding-004 dimensions
  embedding_text  TEXT,              -- the text that was embedded
  model_version   TEXT DEFAULT 'text-embedding-004',
  created_at      TIMESTAMPTZ DEFAULT NOW()
);

-- IVFFlat index for ANN search (rebuild after 1000+ rows for best performance)
CREATE INDEX IF NOT EXISTS idx_layout_embeddings_ivfflat
  ON layout_embeddings
  USING ivfflat (embedding vector_cosine_ops)
  WITH (lists = 100);

-- ── Rooms table ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS rooms (
  id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_id      UUID REFERENCES layout_projects(id) ON DELETE CASCADE,
  room_name       TEXT NOT NULL,     -- 'master_bedroom','kitchen','living_room'
  room_type       TEXT NOT NULL,
  area_sqft       NUMERIC(8,2),
  width_ft        NUMERIC(6,2),
  length_ft       NUMERIC(6,2),
  facing          TEXT,              -- 'north','south','east','west'
  floor_number    INT DEFAULT 1,
  has_window      BOOLEAN DEFAULT TRUE,
  has_attached_bath BOOLEAN DEFAULT FALSE,
  position_x      NUMERIC(8,2),     -- relative coordinates (feet)
  position_y      NUMERIC(8,2),
  created_at      TIMESTAMPTZ DEFAULT NOW()
);

-- ── Room adjacency table ──────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS room_adjacencies (
  id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_id      UUID REFERENCES layout_projects(id) ON DELETE CASCADE,
  room_a          TEXT NOT NULL,
  room_b          TEXT NOT NULL,
  relationship    TEXT NOT NULL,    -- 'adjacent','separated','connected_by_corridor'
  is_preferred    BOOLEAN DEFAULT TRUE,
  created_at      TIMESTAMPTZ DEFAULT NOW()
);

-- ── Building codes / regulations ─────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS building_codes (
  id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  code_name       TEXT NOT NULL,
  jurisdiction    TEXT NOT NULL,    -- 'Palo Alto','Oakland','California'
  code_version    TEXT NOT NULL,
  category        TEXT NOT NULL,    -- 'setback','height','area','ventilation'
  rule_text       TEXT NOT NULL,
  rule_json       JSONB,
  effective_date  DATE,
  expiry_date     DATE,
  is_active       BOOLEAN DEFAULT TRUE,
  created_at      TIMESTAMPTZ DEFAULT NOW()
);

-- ── Architect corrections (training signal) ──────────────────────────────────
CREATE TABLE IF NOT EXISTS layout_corrections (
  id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  project_id      UUID REFERENCES layout_projects(id),
  architect_id    TEXT,
  original_json   JSONB,            -- what AI generated
  corrected_json  JSONB,            -- what architect produced
  correction_type TEXT,             -- 'room_resize','room_move','adjacency_fix'
  correction_notes TEXT,
  used_in_training BOOLEAN DEFAULT FALSE,
  created_at      TIMESTAMPTZ DEFAULT NOW()
);

-- ── Training datasets registry ───────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS training_datasets (
  id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  dataset_name    TEXT NOT NULL,
  gcs_path        TEXT NOT NULL,    -- path to JSONL in GCS
  record_count    INT,
  layout_types    TEXT[],
  created_at      TIMESTAMPTZ DEFAULT NOW(),
  used_for_run    TEXT             -- Vertex AI training job ID
);

-- ── Model registry mirror ────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS model_versions (
  id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  version_tag     TEXT NOT NULL,   -- 'v1.0.0'
  vertex_model_id TEXT,
  vertex_endpoint_id TEXT,
  eval_validity_score NUMERIC(5,4),
  eval_adjacency_score NUMERIC(5,4),
  eval_compliance_score NUMERIC(5,4),
  eval_architect_score NUMERIC(5,4),
  is_production   BOOLEAN DEFAULT FALSE,
  deployed_at     TIMESTAMPTZ,
  retired_at      TIMESTAMPTZ,
  created_at      TIMESTAMPTZ DEFAULT NOW()
);

-- ── Indexes ──────────────────────────────────────────────────────────────────
CREATE INDEX IF NOT EXISTS idx_projects_type      ON layout_projects(layout_type);
CREATE INDEX IF NOT EXISTS idx_projects_zone      ON layout_projects(zone_code);
CREATE INDEX IF NOT EXISTS idx_projects_approved  ON layout_projects(approved);
CREATE INDEX IF NOT EXISTS idx_rooms_project      ON rooms(project_id);
CREATE INDEX IF NOT EXISTS idx_adjacency_project  ON room_adjacencies(project_id);
CREATE INDEX IF NOT EXISTS idx_codes_jurisdiction ON building_codes(jurisdiction, is_active);
CREATE INDEX IF NOT EXISTS idx_corrections_used   ON layout_corrections(used_in_training);

-- ── Update trigger ───────────────────────────────────────────────────────────
CREATE OR REPLACE FUNCTION update_updated_at()
RETURNS TRIGGER AS $$
BEGIN NEW.updated_at = NOW(); RETURN NEW; END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_projects_updated
  BEFORE UPDATE ON layout_projects
  FOR EACH ROW EXECUTE FUNCTION update_updated_at();

SCHEMA

# Stop proxy
kill ${PROXY_PID}

echo "✓ Database schema bootstrapped"
```

---

## Step 12 — Create Artifact Registry (Docker images)

```bash
# Docker repository for Cloud Run service images
gcloud artifacts repositories create zheight-services \
  --repository-format=docker \
  --location=${REGION} \
  --description="zHeight AI plugin service images"

# Configure Docker auth
gcloud auth configure-docker ${REGION}-docker.pkg.dev

echo "✓ Artifact Registry ready"
echo "  Push images to: ${REGION}-docker.pkg.dev/${PROJECT_ID}/zheight-services/<image>:<tag>"
```

---

## Step 13 — Create Cloud Scheduler jobs

```bash
# Enable App Engine (required by Cloud Scheduler in some regions)
gcloud app create --region=${REGION} 2>/dev/null || true

# Monthly retraining trigger (1st of each month at 01:00 UTC)
gcloud scheduler jobs create pubsub monthly-retrain \
  --location=${REGION} \
  --schedule="0 1 1 * *" \
  --topic=training-trigger \
  --message-body='{"trigger":"monthly_schedule","source":"cloud_scheduler"}' \
  --description="Monthly KB retraining trigger"

# Daily KB health check
gcloud scheduler jobs create http daily-kb-health \
  --location=${REGION} \
  --schedule="0 6 * * *" \
  --uri="https://placeholder.run.app/health" \
  --http-method=GET \
  --description="Daily KB health check — update URI after Cloud Run deploy"

echo "✓ Cloud Scheduler jobs created"
```

---

## Step 14 — Set up Cloud Monitoring and alerting

```bash
# Create notification channel (update email before running)
export ALERT_EMAIL="your-email@zheight.com"

gcloud monitoring channels create \
  --display-name="zHeight Alerts" \
  --type=email \
  --channel-labels=email_address=${ALERT_EMAIL}

export CHANNEL_ID=$(gcloud monitoring channels list \
  --filter="displayName='zHeight Alerts'" \
  --format="value(name)")

# Alert: ingestion error rate > 5%
cat > /tmp/alert_ingestion_errors.json << EOF
{
  "displayName": "Ingestion error rate high",
  "conditions": [{
    "displayName": "Error rate > 5%",
    "conditionThreshold": {
      "filter": "resource.type=\"cloud_run_revision\" AND metric.type=\"run.googleapis.com/request_count\" AND metric.labels.response_code_class=\"5xx\"",
      "comparison": "COMPARISON_GT",
      "thresholdValue": 0.05,
      "duration": "300s"
    }
  }],
  "notificationChannels": ["${CHANNEL_ID}"],
  "alertStrategy": { "autoClose": "86400s" }
}
EOF

gcloud monitoring policies create --policy-from-file=/tmp/alert_ingestion_errors.json

# Alert: Cloud SQL storage > 80%
cat > /tmp/alert_db_storage.json << EOF
{
  "displayName": "Cloud SQL storage > 80%",
  "conditions": [{
    "displayName": "Storage utilization high",
    "conditionThreshold": {
      "filter": "resource.type=\"cloudsql_database\" AND metric.type=\"cloudsql.googleapis.com/database/disk/utilization\"",
      "comparison": "COMPARISON_GT",
      "thresholdValue": 0.8,
      "duration": "600s"
    }
  }],
  "notificationChannels": ["${CHANNEL_ID}"]
}
EOF

gcloud monitoring policies create --policy-from-file=/tmp/alert_db_storage.json

echo "✓ Monitoring alerts created"
```

---

## Step 15 — Set up BigQuery (analytics and training data warehouse)

```bash
# Create dataset for KB analytics
bq mk \
  --dataset \
  --location=${REGION} \
  --description="zHeight KB analytics and training data" \
  ${PROJECT_ID}:zheight_analytics

# Create dataset for ML training exports
bq mk \
  --dataset \
  --location=${REGION} \
  --description="Vertex AI training dataset exports" \
  ${PROJECT_ID}:zheight_training

echo "✓ BigQuery datasets created"
```

---

## Step 16 — Create Vertex AI resources

```bash
# Enable Vertex AI in the project region
gcloud ai index-endpoints create \
  --display-name="layout-vector-index-endpoint" \
  --network="projects/${PROJECT_NUMBER}/global/networks/${NETWORK_NAME}" \
  --region=${REGION}

# Create a Vertex AI Dataset placeholder for training
gcloud ai datasets create \
  --display-name="zheight-layout-training-v1" \
  --metadata-schema-uri="gs://google-cloud-aiplatform/schema/dataset/metadata/text_1.0.0.yaml" \
  --region=${REGION}

echo "✓ Vertex AI resources initialized"
```

---

## Step 17 — Create Document AI processor (for PDF/image OCR)

```bash
# Create a general OCR processor
gcloud ai processors create \
  --location=us \
  --display-name="zheight-layout-ocr" \
  --type=OCR_PROCESSOR 2>/dev/null || \
echo "Note: Document AI processors may need to be created via Console at:
  https://console.cloud.google.com/ai/document-ai/processors"
```

---

## Step 18 — Verify full setup

```bash
echo ""
echo "═══════════════════════════════════════════════════"
echo "  Phase 1 Setup Verification"
echo "═══════════════════════════════════════════════════"

echo ""
echo "── Project ─────────────────────────────────────────"
gcloud config get-value project

echo ""
echo "── Storage buckets ─────────────────────────────────"
gcloud storage buckets list --filter="name~${PROJECT_ID}" --format="table(name,location,storageClass)"

echo ""
echo "── Cloud SQL ───────────────────────────────────────"
gcloud sql instances list --format="table(name,region,databaseVersion,state)"

echo ""
echo "── Redis ───────────────────────────────────────────"
gcloud redis instances list --region=${REGION} --format="table(name,host,port,state)"

echo ""
echo "── Pub/Sub topics ──────────────────────────────────"
gcloud pubsub topics list --format="table(name)"

echo ""
echo "── Service accounts ────────────────────────────────"
gcloud iam service-accounts list --filter="email~${PROJECT_ID}" --format="table(email,displayName)"

echo ""
echo "── Secrets ─────────────────────────────────────────"
gcloud secrets list --format="table(name,createTime)"

echo ""
echo "── Artifact Registry ───────────────────────────────"
gcloud artifacts repositories list --location=${REGION} --format="table(name,format,location)"

echo ""
echo "── VPC network ─────────────────────────────────────"
gcloud compute networks list --filter="name=${NETWORK_NAME}" --format="table(name,mode)"

echo ""
echo "═══════════════════════════════════════════════════"
echo "  Phase 1 complete. Next: deploy Cloud Run services"
echo "═══════════════════════════════════════════════════"
```

---

## Reference — Key resource names

| Resource | Name / Path |
|---|---|
| GCP Project | `zheight-ai-kb` |
| Region | `us-central1` |
| Raw file bucket | `gs://zheight-ai-kb-kb-raw/` |
| Processed bucket | `gs://zheight-ai-kb-kb-processed/` |
| Models bucket | `gs://zheight-ai-kb-models/` |
| Cloud SQL instance | `zheight-ai-kb-pg` |
| Database | `zheight_kb` |
| Redis | `zheight-ai-kb-cache` |
| VPC | `zheight-ai-kb-vpc` |
| Ingestion SA | `sa-ingestion@zheight-ai-kb.iam.gserviceaccount.com` |
| Serving SA | `sa-serving@zheight-ai-kb.iam.gserviceaccount.com` |
| Training SA | `sa-training@zheight-ai-kb.iam.gserviceaccount.com` |
| Docker registry | `us-central1-docker.pkg.dev/zheight-ai-kb/zheight-services/` |

---

## What's next — Phase 2

- Deploy Cloud Run ingestion service (parser + embedder)
- Deploy Cloud Run RAG API service
- Set up Eventarc trigger wiring (GCS → Pub/Sub → Cloud Run)
- Upload first batch of historical layouts
- Run end-to-end ingestion test