-- Phase 3: architect_feedback table for correction tracking and retraining trigger.
-- Run once against zheight_kb database.

CREATE TABLE IF NOT EXISTS architect_feedback (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    request_id          TEXT NOT NULL,
    architect_id        TEXT,
    selected_variation  INTEGER NOT NULL DEFAULT 1,
    correction_type     TEXT NOT NULL DEFAULT 'accepted',
    corrected_dwg_notes TEXT DEFAULT '',
    severity            TEXT NOT NULL DEFAULT 'minor',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_feedback_request_id  ON architect_feedback (request_id);
CREATE INDEX IF NOT EXISTS idx_feedback_created_at  ON architect_feedback (created_at);
CREATE INDEX IF NOT EXISTS idx_feedback_correction   ON architect_feedback (correction_type);

COMMENT ON TABLE architect_feedback IS
    'Stores architect selections and corrections after AI layout generation. '
    'After CORRECTION_THRESHOLD non-accepted corrections a retraining signal is emitted.';
