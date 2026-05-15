"""
PDF parser using PyMuPDF (fitz).
Extracts text, tables, dimension values, and floor plan annotations.
Handles architectural PDFs which are often multi-page with mixed text/drawing content.
"""
from __future__ import annotations
import re
import structlog

log = structlog.get_logger()

# Regex patterns for architectural data
DIM_PATTERN = re.compile(r"\b(\d{1,5}(?:\.\d{1,2})?)\s*(?:sqft|sq\.ft|sft|m2|sqm|sq\.m)\b", re.I)
AREA_PATTERN = re.compile(r"(\d{2,6}(?:\.\d{1,2})?)\s*(?:sqft|sq\.ft|sft|m2|sqm)", re.I)
HEIGHT_PATTERN = re.compile(r"(\d{1,3}(?:\.\d{1,2})?)\s*(?:m|ft|feet|metre|meter)(?:\s+height|\s+ht\.?)?", re.I)
SETBACK_PATTERN = re.compile(r"(\d{1,3}(?:\.\d{1,2})?)\s*m\s+(?:front|rear|side|back)\s+setback", re.I)
FAR_PATTERN = re.compile(r"(?:far|floor\s+area\s+ratio)\s*[=:]\s*(\d+(?:\.\d+)?)", re.I)


class PDFParser:
    def parse(self, file_bytes: bytes, filename: str) -> dict:
        try:
            import fitz  # PyMuPDF
            doc = fitz.open(stream=file_bytes, filetype="pdf")
            pages_text, all_text_parts = [], []

            for page_num in range(len(doc)):
                page = doc[page_num]
                text = page.get_text("text")
                pages_text.append({"page": page_num + 1, "text": text[:3000]})
                all_text_parts.append(text)

            full_text = "\n".join(all_text_parts)
            preview = full_text[:4000]

            areas = [float(m.group(1)) for m in AREA_PATTERN.finditer(full_text)]
            heights = [float(m.group(1)) for m in HEIGHT_PATTERN.finditer(full_text)]
            setbacks = re.findall(SETBACK_PATTERN, full_text)
            far_fsi = re.findall(FAR_PATTERN, full_text)

            # Extract dimension-like numbers (3-5 digit patterns common in floor plans)
            dim_numbers = re.findall(r'\b(\d{3,5})\b', full_text)
            dim_ints = sorted(set(int(d) for d in dim_numbers if 100 <= int(d) <= 50000))[:30]

            log.info("pdf_parsed", filename=filename, pages=len(doc),
                     areas_found=len(areas), text_length=len(full_text))

            doc.close()
            return {
                "format": "pdf",
                "filename": filename,
                "page_count": len(doc) if not doc.is_closed else len(pages_text),
                "raw_text_preview": preview,
                "full_text_length": len(full_text),
                "areas_sqft": areas[:20],
                "heights_found": heights[:10],
                "setbacks_found": setbacks[:10],
                "far_fsi_values": far_fsi[:5],
                "dimensions_found": dim_ints,
                "pages": pages_text[:5],
            }
        except ImportError:
            log.error("pymupdf_not_installed")
            return self._text_fallback(file_bytes, filename)
        except Exception as exc:
            log.error("pdf_parse_error", filename=filename, error=str(exc))
            return {"format": "pdf", "filename": filename, "error": str(exc),
                    "raw_text_preview": "", "areas_sqft": [], "page_count": 0}

    def _text_fallback(self, file_bytes: bytes, filename: str) -> dict:
        text = file_bytes.decode("latin-1", errors="ignore")
        readable = re.findall(r'[A-Za-z0-9 \-_.,;:()\n]{5,200}', text)
        preview = " ".join(readable[:100])[:3000]
        return {
            "format": "pdf_fallback", "filename": filename,
            "raw_text_preview": preview, "page_count": 0,
            "areas_sqft": [], "dimensions_found": []
        }
