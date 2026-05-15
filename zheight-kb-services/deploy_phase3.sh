#!/usr/bin/env bash
# Phase 3 deploy script.
# Fixes vs Phase 3 doc:
#   - cpu=2 (not 4) — stays within Cloud Run quota
#   - max-instances=10 (not 50) — stays within quota
#   - Uses rag-api-key secret (not plugin-api-key which doesn't exist)
#   - Uses GEMINI_API_KEY env var from gemini-api-key secret
#   - GENERATION_MODEL=gemini-2.5-flash (proven working)
set -euo pipefail

PROJECT_ID="zheight-ai-kb"
REGION="us-central1"
REPO="${REGION}-docker.pkg.dev/${PROJECT_ID}/zheight-services"
CLOUD_SQL_CONN="${PROJECT_ID}:${REGION}:${PROJECT_ID}-pg"
CONNECTOR="projects/${PROJECT_ID}/locations/${REGION}/connectors/${PROJECT_ID}-vpc-connector"
SA_INGESTION="sa-ingestion"
SA_SERVING="sa-serving"
DB_NAME="zheight_kb"
DB_USER="kb_admin"

echo "=== Phase 3 Deploy ==="
echo "Project: ${PROJECT_ID}"
echo "Region : ${REGION}"
echo ""

# ── Step 1: Run feedback table migration ─────────────────────────────────────
echo "--- Applying Phase 3 DB migration ---"
echo "Run this SQL manually in Cloud SQL Studio or via Cloud SQL Auth Proxy:"
echo "  psql -h 127.0.0.1 -p 5433 -U kb_admin -d zheight_kb -f migrations/phase3_feedback_table.sql"
echo ""

# ── Step 2: Docker auth ───────────────────────────────────────────────────────
gcloud auth configure-docker "${REGION}-docker.pkg.dev" --quiet

# ── Step 3: Build and push RAG API v3.1 ──────────────────────────────────────
echo "--- Building rag-api:v3.1 ---"
cd services/rag-api
docker build -t "${REPO}/rag-api:v3.1" .
docker push "${REPO}/rag-api:v3.1"
cd ../..

# ── Step 4: Get Redis host/port ───────────────────────────────────────────────
REDIS_HOST=$(gcloud redis instances describe "${PROJECT_ID}-cache" \
  --region="${REGION}" --format="value(host)")
REDIS_PORT=$(gcloud redis instances describe "${PROJECT_ID}-cache" \
  --region="${REGION}" --format="value(port)")

echo "Redis: ${REDIS_HOST}:${REDIS_PORT}"

# ── Step 5: Deploy RAG API ────────────────────────────────────────────────────
echo "--- Deploying rag-api ---"
gcloud run deploy rag-api \
  --image="${REPO}/rag-api:v3.1" \
  --region="${REGION}" \
  --service-account="${SA_SERVING}@${PROJECT_ID}.iam.gserviceaccount.com" \
  --set-env-vars="GCP_PROJECT=${PROJECT_ID},GCP_REGION=${REGION},\
REDIS_HOST=${REDIS_HOST},REDIS_PORT=${REDIS_PORT},\
GENERATION_MODEL=gemini-2.5-flash,API_VERSION=v1,\
CORRECTION_THRESHOLD=50,\
CLOUD_SQL_CONNECTION_NAME=${CLOUD_SQL_CONN},\
DB_USER=${DB_USER},DB_NAME=${DB_NAME}" \
  --set-secrets="DB_PASSWORD=db-password:latest,\
REDIS_AUTH=redis-auth-string:latest,\
RAG_API_KEY=rag-api-key:latest,\
GEMINI_API_KEY=gemini-api-key:latest" \
  --set-cloudsql-instances="${CLOUD_SQL_CONN}" \
  --vpc-connector="${CONNECTOR}" \
  --vpc-egress=private-ranges-only \
  --ingress=all \
  --allow-unauthenticated \
  --memory=4Gi \
  --cpu=2 \
  --timeout=180 \
  --concurrency=40 \
  --min-instances=1 \
  --max-instances=10

RAG_API_URL=$(gcloud run services describe rag-api \
  --region="${REGION}" --format="value(status.url)")

echo ""
echo "=== Deployment complete ==="
echo "RAG API URL: ${RAG_API_URL}"
echo ""

# ── Step 6: Smoke tests ───────────────────────────────────────────────────────
API_KEY=$(gcloud secrets versions access latest --secret="rag-api-key")

echo "--- Health check ---"
curl -sf "${RAG_API_URL}/health" | python3 -m json.tool

echo ""
echo "--- v1/orchestrate test (residential) ---"
curl -sf -X POST "${RAG_API_URL}/v1/orchestrate" \
  -H "Content-Type: application/json" \
  -H "X-API-Key: ${API_KEY}" \
  -H "User-Agent: zHeightPlugin/3.1" \
  -d '{
    "prompt": "3-bedroom house, 2800 sqft, open plan kitchen dining living, primary suite with walk-in closet, 2-car garage, suburban US",
    "project_category": "residential",
    "total_area_sqm": 260,
    "floor_count": 1,
    "site_context": {"plot_area_sqm": 650, "north_direction": "north"},
    "design_intent": {"style": "contemporary_american"},
    "autocad_units": "mm",
    "plugin_version": "3.1"
  }' | python3 -c "
import json, sys
d = json.load(sys.stdin)
print(f'request_id  : {d[\"request_id\"]}')
print(f'category    : {d[\"project_category\"]}')
print(f'api_version : {d[\"api_version\"]}')
print(f'variations  : {len(d[\"variations\"])}')
for v in d['variations']:
    acts = len(v['actions'])
    warns = len(v['warnings'])
    print(f'  V{v[\"variation_id\"]}: {v[\"variation_name\"]} — {acts} actions, {warns} warnings')
"

echo ""
echo "--- Auth failure test (should return 401) ---"
STATUS=$(curl -s -o /dev/null -w "%{http_code}" \
  -X POST "${RAG_API_URL}/v1/orchestrate" \
  -H "Content-Type: application/json" \
  -H "X-API-Key: wrong-key" \
  -d '{"prompt":"test"}')
echo "Response with wrong key: ${STATUS} (expected 401)"

echo ""
echo "=== Phase 3 smoke tests complete ==="
