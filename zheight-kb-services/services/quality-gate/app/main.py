"""
Quality Gate Service.
Consumes layout-embedded Pub/Sub events and decides auto-approval or manual review.
Low-confidence extractions → flagged for review.
High-confidence + complete data → auto-approved for KB inclusion.

Approval threshold: confidence >= 0.75 AND spaces_count >= 3
"""
from __future__ import annotations
import base64, json, os
import structlog
from fastapi import FastAPI, Request, HTTPException
from sqlalchemy import text

import sys
sys.path.insert(0, "/app")
from shared.db.client import get_db

log = structlog.get_logger()
app = FastAPI(title="zHeight Quality Gate Service")

AUTO_APPROVE_THRESHOLD = float(os.environ.get("AUTO_APPROVE_THRESHOLD", "0.75"))
MIN_SPACES = int(os.environ.get("MIN_SPACES_FOR_APPROVAL", "3"))


@app.get("/health")
async def health():
    return {"service": "quality-gate", "status": "ok"}


@app.post("/gate")
async def quality_gate(request: Request):
    body = await request.json()
    message = body.get("message", {})
    data_b64 = message.get("data", "")

    try:
        data = json.loads(base64.b64decode(data_b64).decode("utf-8"))
    except Exception as exc:
        raise HTTPException(400, f"Bad message: {exc}")

    project_id = data.get("project_id")
    if not project_id:
        return {"status": "skipped", "reason": "no project_id"}

    confidence = float(data.get("confidence") or 0)
    category = data.get("category", "unknown")
    sub_type = data.get("sub_type")

    log.info("quality_gate_received", project_id=project_id,
             category=category, confidence=confidence)

    # Count spaces for this project
    async with get_db() as session:
        row = await session.execute(
            text("SELECT COUNT(*) FROM spaces WHERE project_id = :pid"),
            {"pid": project_id}
        )
        space_count = row.scalar() or 0

    # Approval logic
    auto_approve = (
        confidence >= AUTO_APPROVE_THRESHOLD
        and space_count >= MIN_SPACES
        and category != "unknown"
    )

    reason = []
    if confidence < AUTO_APPROVE_THRESHOLD:
        reason.append(f"low_confidence({confidence:.2f}<{AUTO_APPROVE_THRESHOLD})")
    if space_count < MIN_SPACES:
        reason.append(f"insufficient_spaces({space_count}<{MIN_SPACES})")
    if category == "unknown":
        reason.append("unknown_category")

    async with get_db() as session:
        await session.execute(
            text("""
                UPDATE projects
                SET approved = :approved,
                    approved_at = CASE WHEN :approved THEN NOW() ELSE NULL END,
                    quality_score = :confidence
                WHERE id = :pid
            """),
            {"approved": auto_approve, "confidence": confidence, "pid": project_id}
        )

    log.info("quality_gate_decision",
             project_id=project_id,
             approved=auto_approve,
             space_count=space_count,
             reason=reason)

    return {
        "project_id": project_id,
        "approved": auto_approve,
        "space_count": space_count,
        "confidence": confidence,
        "reason": reason if not auto_approve else "auto_approved",
    }
