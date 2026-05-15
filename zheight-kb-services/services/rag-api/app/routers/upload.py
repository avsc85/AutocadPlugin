"""
/upload — upload files with architect brief attached as GCS metadata.
Supports DWG, DXF, PDF, images, IFC, text briefs.
Triggers ingestion pipeline automatically via GCS → Pub/Sub → Cloud Run.
"""
from __future__ import annotations
import os
import structlog
from fastapi import APIRouter, File, Form, HTTPException, UploadFile
from google.cloud import storage

log = structlog.get_logger()
router = APIRouter(prefix="/upload", tags=["upload"])
storage_client = storage.Client()
KB_BUCKET = os.environ.get("KB_BUCKET", "").replace("gs://", "")

FOLDER_MAP = {
    "dwg": "dwg", "dxf": "dwg",
    "pdf": "pdf",
    "png": "images", "jpg": "images", "jpeg": "images", "tif": "images", "tiff": "images",
    "rvt": "revit", "rfa": "revit",
    "ifc": "ifc",
    "txt": "briefs", "md": "briefs",
}
MAX_FILE_SIZE = 100 * 1024 * 1024  # 100 MB


@router.post("")
async def upload(
    file: UploadFile = File(...),
    project_brief: str = Form(default=""),
    project_category: str = Form(default=""),
    architect_notes: str = Form(default=""),
):
    filename = (file.filename or "unnamed").replace("..", "").replace("/", "_")
    ext = filename.rsplit(".", 1)[-1].lower() if "." in filename else ""
    folder = FOLDER_MAP.get(ext, "other")
    gcs_path = f"{folder}/{filename}"

    contents = await file.read()
    if len(contents) > MAX_FILE_SIZE:
        raise HTTPException(413, f"File exceeds 100 MB limit ({len(contents)//1048576} MB)")
    if len(contents) == 0:
        raise HTTPException(400, "Empty file")

    try:
        bucket = storage_client.bucket(KB_BUCKET)
        blob = bucket.blob(gcs_path)
        blob.metadata = {
            "x-project-brief": project_brief[:2000],
            "x-project-category": project_category[:100],
            "x-architect-notes": architect_notes[:500],
        }
        blob.upload_from_string(
            contents,
            content_type=file.content_type or "application/octet-stream",
        )
        log.info("file_uploaded", filename=filename, folder=folder,
                 size_mb=round(len(contents)/1048576, 2), has_brief=bool(project_brief))
        return {
            "status": "uploaded",
            "gcs_path": f"gs://{KB_BUCKET}/{gcs_path}",
            "filename": filename,
            "size_bytes": len(contents),
            "brief_attached": bool(project_brief),
            "message": "File queued for ingestion. Processing typically takes 60–120 seconds.",
        }
    except Exception as exc:
        log.error("upload_error", filename=filename, error=str(exc))
        raise HTTPException(500, str(exc))
