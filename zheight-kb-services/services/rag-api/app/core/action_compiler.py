"""
Action compiler — converts AI spatial output to DrawActionPlan.

All coordinates are mm from (0,0) regardless of DWG units.
The C# plugin applies the mm→drawing-units scale factor.
Rooms are placed by zone-aware shelf packing (Python side) and then
re-laid by GridLayoutEngine.cs (C# side) using the zones_json payload
for collision-free, wall-sharing, grid-aligned output.
"""
import json
import math
import uuid
from datetime import datetime, timezone

import sys
sys.path.insert(0, "/app")
from shared.contracts.draw_action_plan import (
    DrawAction, DrawActionPlan, VariationPlan, ActionType,
    Point2D, SpaceSummaryItem, ConstraintReport
)

WALL_THICKNESS = {"external": 230, "structural": 230,
                  "internal": 150, "partition": 100, None: 150}

DOOR_WIDTHS = {
    "bedroom": 900, "master_bedroom": 900, "bathroom": 750,
    "toilet": 750,  "kitchen": 900,        "living": 1000,
    "entry": 1200,  "main_entry": 1500,    "emergency": 1800,
    "icu": 1200,    "opd": 1000,           "meeting_room": 900,
    "default": 900,
}

ROOM_ASPECT = {
    "bedroom": 1.25,    "living": 1.6,  "kitchen": 1.4,
    "bathroom": 1.2,    "office": 1.5,  "meeting_room": 1.8,
    "default": 1.4,
}

AIA_LAYERS = {
    "A-WALL":       {"color": 7,  "linetype": "CONTINUOUS", "lw": 0.50},
    "A-WALL-EXTR":  {"color": 7,  "linetype": "CONTINUOUS", "lw": 0.70},
    "A-WALL-INTR":  {"color": 7,  "linetype": "CONTINUOUS", "lw": 0.35},
    "A-WALL-PRTN":  {"color": 8,  "linetype": "CONTINUOUS", "lw": 0.18},
    "A-WALL-PATT":  {"color": 8,  "linetype": "CONTINUOUS", "lw": 0.18},
    "A-DOOR":       {"color": 4,  "linetype": "CONTINUOUS", "lw": 0.35},
    "A-DOOR-SWNG":  {"color": 4,  "linetype": "CONTINUOUS", "lw": 0.18},
    "A-GLAZ":       {"color": 4,  "linetype": "CONTINUOUS", "lw": 0.25},
    "S-COLS":       {"color": 7,  "linetype": "CONTINUOUS", "lw": 0.70},
    "A-FLOR-STRS":  {"color": 3,  "linetype": "CONTINUOUS", "lw": 0.25},
    "A-ANNO-TEXT":  {"color": 2,  "linetype": "CONTINUOUS", "lw": 0.18},
    "A-ANNO-DIMS":  {"color": 2,  "linetype": "CONTINUOUS", "lw": 0.18},
    "A-ANNO-SYMB":  {"color": 2,  "linetype": "CONTINUOUS", "lw": 0.18},
    "A-ANNO-TTLB":  {"color": 7,  "linetype": "CONTINUOUS", "lw": 0.50},
    "A-AREA-IDEN":  {"color": 6,  "linetype": "CONTINUOUS", "lw": 0.18},
    "C-PROP":       {"color": 1,  "linetype": "CENTER",     "lw": 0.25},
    "ZH-AI-NOTES":  {"color": 150,"linetype": "CONTINUOUS", "lw": 0.18},
}


class ActionCompiler:
    def compile(self, variation: dict, idx: int,
                total_area_sqm: float) -> VariationPlan:
        actions: list[DrawAction] = []
        space_summaries: list[SpaceSummaryItem] = []

        # Canvas in mm
        side_mm  = math.sqrt(max(total_area_sqm, 10)) * 1000
        canvas_w = side_mm * 1.4
        canvas_h = side_mm

        # 1. Create layers
        actions.extend(self._create_layers())

        # 2. Site boundary
        actions.append(self._site_boundary(canvas_w, canvas_h))

        # 3. Place and draw rooms — flatten zones (new schema) or plain spaces (old)
        spaces = self._extract_spaces(variation)
        placed = self._place_rooms(spaces, canvas_w, canvas_h)

        for room in placed:
            actions.extend(self._compile_room(room))
            space_summaries.append(SpaceSummaryItem(
                name=room.get("name", "Room"),
                type=room.get("type", "room"),
                area_sqm=room.get("area_sqm") or round(
                    room["w_mm"] * room["d_mm"] / 1_000_000, 1),
                floor=room.get("floor", 1),
                facing=room.get("facing"),
            ))

        # 4. North arrow
        actions.append(DrawAction(
            action_type=ActionType.ADD_NORTH_ARROW,
            layer="A-ANNO-SYMB",
            center=Point2D(x=canvas_w + 600, y=canvas_h - 300),
            properties={"size_mm": 300},
        ))

        # 5. Scale bar
        actions.append(DrawAction(
            action_type=ActionType.ADD_SCALE_BAR,
            layer="A-ANNO-SYMB",
            start=Point2D(x=0, y=-700),
            properties={"scale": "1:100", "length_mm": 5000},
        ))

        # 6. Title block
        actions.append(DrawAction(
            action_type=ActionType.ADD_TITLE_BLOCK,
            layer="A-ANNO-TTLB",
            start=Point2D(x=0, y=-1400),
            label_text=(f"V{idx+1}: {variation.get('concept_name','Layout')} — "
                        f"{variation.get('concept_rationale','')[:80]}"),
            properties={
                "total_area_sqm": variation.get("total_area_sqm", total_area_sqm),
                "warnings": variation.get("warnings", []),
            },
        ))

        cc = variation.get("constraint_compliance", {})
        constraint_report = ConstraintReport(
            far_used=cc.get("far_used"),
            height_m=cc.get("height_m"),
            coverage_pct=cc.get("coverage_pct"),
        )

        return VariationPlan(
            variation_id=idx + 1,
            variation_name=variation.get("concept_name", f"Variation {idx+1}"),
            concept_rationale=variation.get("concept_rationale", ""),
            total_area_sqm=variation.get("total_area_sqm", total_area_sqm),
            floor_count=variation.get("floor_count", 1),
            actions=actions,
            space_summary=space_summaries,
            constraint_report=constraint_report,
            passive_notes=variation.get("passive_notes", ""),
            warnings=variation.get("warnings", []),
        )

    def _create_layers(self) -> list[DrawAction]:
        return [
            DrawAction(
                action_type=ActionType.CREATE_LAYER,
                layer=name,
                layer_color=props["color"],
                layer_linetype=props["linetype"],
                layer_lineweight=props["lw"],
            )
            for name, props in AIA_LAYERS.items()
        ]

    def _site_boundary(self, w: float, h: float) -> DrawAction:
        return DrawAction(
            action_type=ActionType.DRAW_WALL,
            layer="C-PROP",
            thickness_mm=0,
            vertices=[
                Point2D(x=0, y=0), Point2D(x=w, y=0),
                Point2D(x=w, y=h), Point2D(x=0, y=h),
                Point2D(x=0, y=0),
            ],
            properties={"closed": True, "is_site_boundary": True},
        )

    def _extract_spaces(self, variation: dict) -> list[dict]:
        """Flatten zones schema into a flat space list (preserves zone metadata)."""
        if variation.get("zones"):
            spaces = []
            for zone in variation["zones"]:
                for s in zone.get("spaces", []):
                    spaces.append({
                        **s,
                        "_zone_name":     zone.get("zone_name"),
                        "_zone_position": zone.get("zone_position"),
                    })
            return spaces
        return variation.get("spaces", [])

    def _place_rooms(self, spaces: list[dict],
                     canvas_w: float, canvas_h: float) -> list[dict]:
        """Shelf-pack rooms left-to-right, wrapping rows.

        Position hints from the LLM are intentionally ignored — they cause
        overlap because the model has no collision awareness.  The C# side
        runs GridLayoutEngine which produces the authoritative coordinates;
        these Python coordinates are a safe fallback only.
        """
        placed = []
        cur_x, cur_y = 0.0, 0.0
        row_h, padding = 0.0, 150.0

        for space in spaces:
            area   = max(float(space.get("area_sqm") or 10), 1.0)
            aspect = float(space.get("aspect_ratio") or
                           ROOM_ASPECT.get((space.get("type") or "").lower(),
                                           ROOM_ASPECT["default"]))
            w_mm = math.sqrt(area * aspect) * 1000
            d_mm = math.sqrt(area / aspect) * 1000

            # Honour min dimensions from new schema
            min_w = max(float(space.get("min_width_m") or 2.4) * 1000, 2400)
            min_d = max(float(space.get("min_depth_m") or 2.4) * 1000, 2400)
            w_mm  = max(w_mm, min_w)
            d_mm  = max(d_mm, min_d)

            if cur_x + w_mm > canvas_w:
                cur_x  = 0
                cur_y += row_h + padding
                row_h  = 0.0
            x, y   = cur_x, cur_y
            cur_x += w_mm + padding
            row_h  = max(row_h, d_mm)

            placed.append({**space, "x": x, "y": y,
                           "w_mm": w_mm, "d_mm": d_mm})
        return placed

    def _compile_room(self, room: dict) -> list[DrawAction]:
        actions = []
        x, y   = room["x"], room["y"]
        w, d   = room["w_mm"], room["d_mm"]
        name   = room.get("name", "Room")
        rtype  = (room.get("type") or "room").lower()
        floor  = room.get("floor", 1)
        facing = room.get("facing", "south")
        wt     = WALL_THICKNESS["internal"]
        gid    = f"ROOM_{name.upper().replace(' ','_')}_F{floor}"

        actions.append(DrawAction(action_type=ActionType.START_GROUP,
                                   layer="A-WALL", group_id=gid,
                                   label_text=name))

        # 4 wall segments
        for (sx, sy, ex, ey) in [
            (x,   y,   x+w, y),
            (x+w, y,   x+w, y+d),
            (x+w, y+d, x,   y+d),
            (x,   y+d, x,   y),
        ]:
            actions.append(DrawAction(
                action_type=ActionType.DRAW_WALL, layer="A-WALL-INTR",
                group_id=gid,
                start=Point2D(x=sx, y=sy), end=Point2D(x=ex, y=ey),
                thickness_mm=wt, wall_type="internal", height_mm=3000,
            ))

        # Door
        dw = DOOR_WIDTHS.get(rtype, DOOR_WIDTHS["default"])
        actions.append(DrawAction(
            action_type=ActionType.DRAW_DOOR, layer="A-DOOR",
            group_id=gid,
            start=self._door_pos(x, y, w, d, facing, dw),
            door_width_mm=dw, door_swing="right", thickness_mm=wt,
        ))

        # Window (skip service spaces)
        if room.get("has_natural_light", True) and rtype not in (
                "toilet", "bathroom", "corridor", "store", "server_room"):
            win_w = min(w * 0.6, 1800)
            actions.append(DrawAction(
                action_type=ActionType.DRAW_WINDOW, layer="A-GLAZ",
                group_id=gid,
                start=self._window_pos(x, y, w, d, facing),
                window_width_mm=win_w, window_height_mm=1200,
                window_sill_mm=900, window_type="casement",
            ))

        # Room label
        area_sqm = room.get("area_sqm") or round(w * d / 1_000_000, 1)
        actions.append(DrawAction(
            action_type=ActionType.DRAW_ROOM_LABEL, layer="A-ANNO-TEXT",
            group_id=gid,
            center=Point2D(x=x + w/2, y=y + d/2),
            label_text=name, label_area_sqm=area_sqm, font_height_mm=250,
        ))

        # Area tag
        actions.append(DrawAction(
            action_type=ActionType.ADD_AREA_TAG, layer="A-AREA-IDEN",
            group_id=gid,
            center=Point2D(x=x + w/2, y=y + d/2 - 350),
            label_text=f"{area_sqm:.1f} m²", font_height_mm=180,
        ))

        # Hatch
        actions.append(DrawAction(
            action_type=ActionType.ADD_HATCH, layer="A-WALL-PATT",
            group_id=gid,
            hatch_pattern="ANSI37", hatch_scale=25.0, hatch_angle=45.0,
            hatch_boundary=[
                Point2D(x=x,   y=y),   Point2D(x=x+w, y=y),
                Point2D(x=x+w, y=y+d), Point2D(x=x,   y=y+d),
            ],
        ))

        actions.append(DrawAction(action_type=ActionType.END_GROUP,
                                   layer="A-WALL", group_id=gid))
        return actions

    def _door_pos(self, x, y, w, d, facing, dw) -> Point2D:
        mid_x = x + w/2 - dw/2
        mid_y = y + d/2 - dw/2
        return {"south": Point2D(x=mid_x, y=y),
                "north": Point2D(x=mid_x, y=y+d),
                "east":  Point2D(x=x+w,   y=mid_y),
                "west":  Point2D(x=x,     y=mid_y)}.get(
                    facing, Point2D(x=mid_x, y=y))

    def _window_pos(self, x, y, w, d, facing) -> Point2D:
        return {"south": Point2D(x=x+w/2, y=y),
                "north": Point2D(x=x+w/2, y=y+d),
                "east":  Point2D(x=x+w,   y=y+d/2),
                "west":  Point2D(x=x,     y=y+d/2)}.get(
                    facing, Point2D(x=x+w/2, y=y))


def compile_to_plan(generation: dict, request_id: str,
                     autocad_units: str = "mm") -> DrawActionPlan:
    compiler   = ActionCompiler()
    total_area = max(float(generation.get("total_area_sqm") or 100), 1.0)
    variations = []

    for i, v in enumerate(generation.get("variations", [])):
        try:
            vp = compiler.compile(v, i, total_area)
            # Pass zone graph through to C# GridLayoutEngine via VariationPlan.properties
            if v.get("zones"):
                vp.properties["zones_json"]            = json.dumps(v["zones"])
                vp.properties["organisation_strategy"] = v.get("organisation_strategy", "linear")
                vp.properties["structural_grid_m"]     = v.get("structural_grid_m", 4.0)
                vp.properties["entry_space"]           = v.get("entry_space", "")
            if generation.get("site_constraints"):
                vp.properties["site_constraints"] = json.dumps(generation["site_constraints"])
            variations.append(vp)
        except Exception as e:
            import structlog
            structlog.get_logger().error(
                "variation_compile_error", variation=i, error=str(e))

    return DrawActionPlan(
        request_id=request_id,
        api_version="v1",
        project_description=generation.get("project_type_understood", ""),
        project_category=generation.get("project_category", "unknown"),
        generated_at=datetime.now(timezone.utc).isoformat(),
        variations=variations,
        recommended_variation=int(generation.get("recommended_variation", 1)),
        global_warnings=generation.get("global_warnings", []),
        layer_standard="AIA",
    )
