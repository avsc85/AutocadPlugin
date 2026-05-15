#!/usr/bin/env bash
# upload_projects.sh
# Uploads project files (DWG, PDF, or text briefs) to the zHeight KB bucket.
#
# Fix #13: API key loaded from Secret Manager — never hardcoded in this file.
# Run from the repo root:
#   bash scripts/upload/upload_projects.sh

set -euo pipefail

PROJECT_ID="zheight-ai-kb"
BUCKET="gs://${PROJECT_ID}-kb-raw"

# Load API key from Secret Manager — do NOT hardcode here
API_KEY=$(gcloud secrets versions access latest \
  --secret="rag-api-key" \
  --project="${PROJECT_ID}")

RAG_API_URL=$(gcloud run services describe rag-api \
  --region=us-central1 \
  --project="${PROJECT_ID}" \
  --format="value(status.url)")

echo "Uploading to : ${BUCKET}"
echo "API endpoint : ${RAG_API_URL}"
echo ""

# ── Helper: upload file with brief metadata ───────────────────────────────────
upload_with_brief() {
  local file="$1"
  local brief="$2"
  local folder="$3"   # dwg | pdf | briefs

  if [ ! -f "$file" ]; then
    echo "SKIP (not found): $file"
    return
  fi

  gcloud storage cp "$file" \
    "${BUCKET}/${folder}/$(basename "$file")" \
    --metadata="x-project-brief=${brief}"

  echo "✓ $(basename "$file")"
}

# ── Helper: upload plain text brief only (no DWG/PDF) ────────────────────────
upload_brief_only() {
  local brief_file="$1"

  if [ ! -f "$brief_file" ]; then
    echo "SKIP (not found): $brief_file"
    return
  fi

  gcloud storage cp "$brief_file" \
    "${BUCKET}/briefs/$(basename "$brief_file")"

  echo "✓ brief: $(basename "$brief_file")"
}

# ── Add your projects below ───────────────────────────────────────────────────
# Brief format: one paragraph covering type, area, programme, adjacencies,
# site constraints, regulatory requirements, and what worked in practice.
# The richer the brief, the higher the extraction_confidence score.

# Example residential DWG (US suburban):
# upload_with_brief \
#   "your_projects/house_001.dwg" \
#   "3-bedroom suburban house 2800sqft single floor open-plan kitchen dining living \
#    primary suite walk-in closet ensuite bath 2 secondary bedrooms shared bath \
#    powder room mudroom 2-car garage laundry room IBC2021 suburban setbacks" \
#   "dwg"

# Example commercial PDF (US office):
# upload_with_brief \
#   "your_projects/office_hq.pdf" \
#   "Corporate office suburban US 1200sqm single floor 80-person open plan \
#    6 conference rooms 1 boardroom reception server room 2 break rooms ADA compliant" \
#   "pdf"

# Example brief-only text file:
# upload_brief_only "your_projects/hospital_brief.txt"

echo ""
echo "Upload complete."
echo "Review pending entries at: ${RAG_API_URL}/v1/quality-gate/pending"
echo "Approve entries with confidence >= 0.6 to activate KB retrieval."
