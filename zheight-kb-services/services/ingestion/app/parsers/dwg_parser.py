"""
DXF/DWG parser using ezdxf.

DWG note: ezdxf reads DXF natively. DWG is Autodesk's proprietary binary format.
We handle DWG by attempting conversion via LibreCAD CLI (if installed) or by
returning raw byte analysis as a fallback. The AI extractor handles partial data.

For production at scale, consider the Autodesk Platform Services (APS/Forge) API
for DWG parsing — it handles all DWG versions including R2024+.
"""
from __future__ import annotations
import io, subprocess, tempfile, os, re
import structlog

log = structlog.get_logger()


class DWGParser:
    def parse(self, file_bytes: bytes, filename: str) -> dict:
        ext = filename.rsplit(".", 1)[-1].lower()
        if ext == "dxf":
            return self._parse_dxf(file_bytes, filename)
        # DWG: attempt LibreCAD conversion then DXF parse
        dxf_bytes = self._convert_dwg_to_dxf(file_bytes, filename)
        if dxf_bytes:
            return self._parse_dxf(dxf_bytes, filename)
        return self._dwg_fallback(file_bytes, filename)

    def _parse_dxf(self, file_bytes: bytes, filename: str) -> dict:
        try:
            import ezdxf
            doc = ezdxf.read(io.StringIO(file_bytes.decode("utf-8", errors="ignore")))
            msp = doc.modelspace()

            annotations, walls, doors, windows, circles, dimensions = [], [], [], [], [], []
            layers = list(doc.layers.entries.keys())

            for entity in msp:
                etype = entity.dxftype()
                if etype in ("TEXT", "MTEXT"):
                    txt = (entity.dxf.text if etype == "TEXT"
                           else entity.text).strip()
                    if txt:
                        annotations.append({
                            "text": txt[:200],
                            "x": round(float(entity.dxf.insert.x), 2) if hasattr(entity.dxf, "insert") else None,
                            "y": round(float(entity.dxf.insert.y), 2) if hasattr(entity.dxf, "insert") else None,
                        })
                elif etype in ("LINE", "LWPOLYLINE", "POLYLINE"):
                    walls.append({"type": etype, "layer": entity.dxf.layer})
                elif etype == "INSERT":
                    block_name = entity.dxf.name.lower()
                    if any(k in block_name for k in ("door", "dr", "d-")):
                        doors.append({"block": entity.dxf.name, "layer": entity.dxf.layer})
                    elif any(k in block_name for k in ("win", "window", "w-")):
                        windows.append({"block": entity.dxf.name, "layer": entity.dxf.layer})
                elif etype == "CIRCLE":
                    circles.append({"radius": round(entity.dxf.radius, 2), "layer": entity.dxf.layer})
                elif etype in ("DIMENSION", "LEADER"):
                    dimensions.append({"type": etype, "layer": entity.dxf.layer})

            # Extract numeric dimensions from annotation text (e.g. "3600" "4200x3000")
            dim_values = []
            for ann in annotations:
                nums = re.findall(r'\b\d{3,5}\b', ann["text"])
                dim_values.extend([int(n) for n in nums])

            log.info("dxf_parsed", filename=filename, annotations=len(annotations),
                     walls=len(walls), layers=len(layers))

            return {
                "format": "dxf",
                "filename": filename,
                "layers": layers[:50],
                "annotations": annotations[:100],
                "walls": walls[:200],
                "doors": doors[:50],
                "windows": windows[:50],
                "circles": circles[:20],
                "dimensions": dimensions[:50],
                "dimension_values_mm": sorted(set(dim_values)),
                "entity_count": len(list(msp)),
            }
        except Exception as exc:
            log.error("dxf_parse_error", filename=filename, error=str(exc))
            return {"format": "dxf", "filename": filename, "error": str(exc),
                    "annotations": [], "walls": [], "layers": []}

    def _convert_dwg_to_dxf(self, dwg_bytes: bytes, filename: str) -> bytes | None:
        """Attempt DWG→DXF conversion using LibreCAD if available."""
        try:
            with tempfile.NamedTemporaryFile(suffix=".dwg", delete=False) as tmp_in:
                tmp_in.write(dwg_bytes)
                tmp_in_path = tmp_in.name

            out_dir = tempfile.mkdtemp()
            result = subprocess.run(
                ["libreoffice", "--headless", "--convert-to", "dxf",
                 "--outdir", out_dir, tmp_in_path],
                timeout=60, capture_output=True
            )
            dxf_path = os.path.join(out_dir, filename.replace(".dwg", ".dxf"))
            if os.path.exists(dxf_path):
                with open(dxf_path, "rb") as f:
                    return f.read()
        except Exception as exc:
            log.warning("dwg_conversion_unavailable", error=str(exc)[:100])
        return None

    def _dwg_fallback(self, file_bytes: bytes, filename: str) -> dict:
        """Extract readable ASCII strings from DWG binary as last resort."""
        text = file_bytes.decode("latin-1", errors="ignore")
        # Extract printable ASCII sequences of length 4+
        strings = re.findall(r'[A-Za-z0-9 \-_./,()]{4,80}', text)
        unique = list(dict.fromkeys(s.strip() for s in strings if s.strip()))[:100]
        log.warning("dwg_fallback_used", filename=filename, strings_found=len(unique))
        return {
            "format": "dwg_fallback",
            "filename": filename,
            "raw_strings": unique,
            "annotations": [{"text": s, "x": None, "y": None} for s in unique[:50]],
            "walls": [], "doors": [], "windows": [], "layers": [],
            "note": "DWG binary; only ASCII strings extracted. Brief strongly recommended."
        }
