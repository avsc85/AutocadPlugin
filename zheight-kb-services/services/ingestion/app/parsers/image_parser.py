"""
Image parser using Google Document AI for OCR on floor plan images.
Falls back to Cloud Vision API if Document AI processor is unavailable.
Handles PNG, JPEG, TIFF floor plan scans.
"""
from __future__ import annotations
import base64, os
import structlog

log = structlog.get_logger()

DOCAI_PROCESSOR_ID = os.environ.get(
    "DOCAI_PROCESSOR_ID", "c20903a88e9bed06"  # set in Phase 1
)
DOCAI_LOCATION = "us"


class ImageParser:
    def parse(self, file_bytes: bytes, filename: str, project_id: str) -> dict:
        ext = filename.rsplit(".", 1)[-1].lower()
        mime_map = {
            "png": "image/png", "jpg": "image/jpeg",
            "jpeg": "image/jpeg", "tiff": "image/tiff", "tif": "image/tiff",
        }
        mime_type = mime_map.get(ext, "image/jpeg")

        try:
            return self._parse_docai(file_bytes, filename, mime_type, project_id)
        except Exception as exc:
            log.warning("docai_failed_trying_vision", error=str(exc)[:100])
            try:
                return self._parse_vision(file_bytes, filename, mime_type)
            except Exception as exc2:
                log.error("image_parse_failed", filename=filename, error=str(exc2))
                return self._empty(filename, mime_type)

    def _parse_docai(self, file_bytes: bytes, filename: str,
                     mime_type: str, project_id: str) -> dict:
        from google.cloud import documentai_v1 as docai

        client = docai.DocumentProcessorServiceClient()
        name = client.processor_path(project_id, DOCAI_LOCATION, DOCAI_PROCESSOR_ID)

        request = docai.ProcessRequest(
            name=name,
            raw_document=docai.RawDocument(content=file_bytes, mime_type=mime_type),
        )
        result = client.process_document(request=request)
        doc = result.document

        full_text = doc.text or ""
        # Extract text blocks with confidence
        blocks = []
        for page in doc.pages:
            for block in page.blocks:
                if block.layout.confidence and block.layout.confidence > 0.6:
                    start = block.layout.text_anchor.text_segments[0].start_index if block.layout.text_anchor.text_segments else 0
                    end = block.layout.text_anchor.text_segments[0].end_index if block.layout.text_anchor.text_segments else 0
                    text = full_text[int(start):int(end)].strip()
                    if text:
                        blocks.append({"text": text[:200], "confidence": round(block.layout.confidence, 2)})

        import re
        annotations = [{"text": b["text"], "x": None, "y": None} for b in blocks[:60]]
        areas = re.findall(r'(\d{2,6}(?:\.\d{1,2})?)\s*(?:sqft|sq\.ft|m2|sqm)', full_text, re.I)

        log.info("docai_ocr_complete", filename=filename, text_length=len(full_text),
                 blocks=len(blocks))

        return {
            "format": "image_docai",
            "filename": filename,
            "mime_type": mime_type,
            "raw_text_preview": full_text[:3000],
            "text_blocks": blocks[:60],
            "annotations": annotations,
            "areas_sqft": [float(a) for a in areas[:20]],
        }

    def _parse_vision(self, file_bytes: bytes, filename: str, mime_type: str) -> dict:
        from google.cloud import vision
        client = vision.ImageAnnotatorClient()
        image = vision.Image(content=file_bytes)
        response = client.text_detection(image=image)
        texts = response.text_annotations
        full_text = texts[0].description if texts else ""
        annotations = [
            {"text": t.description[:200], "x": None, "y": None}
            for t in texts[1:61]
        ]
        log.info("vision_ocr_complete", filename=filename, annotations=len(annotations))
        return {
            "format": "image_vision",
            "filename": filename,
            "mime_type": mime_type,
            "raw_text_preview": full_text[:3000],
            "annotations": annotations,
            "areas_sqft": [],
        }

    def _empty(self, filename: str, mime_type: str) -> dict:
        return {
            "format": "image_unreadable",
            "filename": filename,
            "mime_type": mime_type,
            "raw_text_preview": "",
            "annotations": [],
            "areas_sqft": [],
        }
