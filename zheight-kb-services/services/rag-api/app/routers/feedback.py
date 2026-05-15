"""
/v1/feedback — receives architect corrections after drawing is placed.
Stores in DB for future fine-tuning trigger (after 50 corrections → Pub/Sub).
"""
from __future__ import annotations
import json, os
import structlog
from fastapi import APIRouter
from pydantic import BaseModel
from sqlalchemy import text

import sys
sys.path.insert(0, "/app")
from shared.db.client import get_db

log = structlog.get_logger()
router = APIRouter(prefix="/feedback", tags=["feedback"])

CORRECTION_THRESHOLD = int(os.environ.get("CORRECTION_THRESHOLD", "50"))


class FeedbackPayload(BaseModel):
    request_id:          str
    architect_id:        str | None = None
    selected_variation:  int
    correction_type:     str = "accepted"
    corrected_dwg_notes: str = ""
    severity:            str = "minor"


@router.post("")
async def receive_feedback(payload: FeedbackPayload):
    try:
        async with get_db() as session:
            await session.execute(text("""
                INSERT INTO architect_feedback (
                    request_id, architect_id, selected_variation,
                    correction_type, corrected_dwg_notes, severity
                ) VALUES (
                    :rid, :arch, :var, :ctype, :notes, :sev
                )
            """), {
                "rid":   payload.request_id,
                "arch":  payload.architect_id,
                "var":   payload.selected_variation,
                "ctype": payload.correction_type,
                "notes": payload.corrected_dwg_notes,
                "sev":   payload.severity,
            })

            # Check if correction threshold reached for retraining trigger
            row = await session.execute(
                text("SELECT COUNT(*) FROM architect_feedback WHERE correction_type != 'accepted'")
            )
            count = row.scalar() or 0

        log.info("feedback_stored",
                 request_id=payload.request_id,
                 correction_type=payload.correction_type,
                 total_corrections=count)

        result = {"status": "ok", "total_corrections": count}

        if count > 0 and count % CORRECTION_THRESHOLD == 0:
            log.info("correction_threshold_reached", count=count)
            result["retraining_triggered"] = True

        return result

    except Exception as exc:
        log.warning("feedback_store_failed", error=str(exc))
        return {"status": "accepted", "note": "stored async"}
