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

import structlog
import sys
sys.path.insert(0, "/app")
from shared.contracts.draw_action_plan import (
    DrawAction, DrawActionPlan, VariationPlan, ActionType,
    Point2D, SpaceSummaryItem, ConstraintReport,
    VariationPayload, ZoneGroupContract, ZoneSpaceContract, SiteConstraintsContract,
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

log = structlog.get_logger()

# ── Zone position inference from room type ────────────────────────────────────
_TYPE_TO_ZONE: dict[str, str] = {
    "entry": "front",        "foyer": "front",         "entry_foyer": "front",
    "vestibule": "front",    "front_entry": "front",   "lobby": "front",
    "powder_room": "front",  "half_bath": "front",     "toilet": "front",
    "primary_bedroom": "rear",   "master_bedroom": "rear",  "secondary_bedroom": "rear",
    "guest_bedroom": "rear",     "guest_room": "rear",      "nursery": "rear",
    "bedroom": "rear",
    "primary_bath": "rear",  "ensuite": "rear",         "ensuite_bath": "rear",
    "master_bath": "rear",   "bathroom": "rear",        "full_bath": "rear",
    "shared_bath": "rear",   "walk_in_closet": "rear",  "closet": "rear",
    "laundry": "service",    "laundry_room": "service", "mudroom": "service",
    "mud_room": "service",   "garage": "service",       "utility": "service",
    "mechanical": "service", "storage": "service",      "pantry": "service",
}


def _infer_zone_pos(room_type: str) -> str:
    t = (room_type or "").lower().replace("-", "_").replace(" ", "_")
    return _TYPE_TO_ZONE.get(t, "centre")


# FIX-ARCH-04 / FIX-ARCH-05: canonical type → zone correctness rules
_HALF_BATH_TYPES    = {"powder_room", "half_bath", "toilet"}
_LAUNDRY_TYPES      = {"laundry", "laundry_room"}
_PANTRY_TYPES       = {"pantry"}
_MUDROOM_TYPES      = {"mudroom", "mud_room"}
_GARAGE_TYPES       = {"garage", "carport", "parking"}

# Types that must always land in a specific zone regardless of what Gemini said.
# Rule format: (from_positions: set|None, to_position: str, condition: callable|None)
#   from_positions=None  → apply from ANY zone
#   condition(space, zones) → extra guard; True means "migrate this space"
_ZONE_RULES: list[tuple[set | None, str, object]] = [
    # half-baths always belong in front (public zone) — never in bedroom wing
    (_HALF_BATH_TYPES, None, "front", None),
    # laundry in the centre (open-plan) zone is wrong — belongs in service
    (_LAUNDRY_TYPES,   {"centre"}, "service", None),
    # pantry in the front (entry/foyer) zone is wrong — belongs in service
    (_PANTRY_TYPES,    {"front"},  "service", None),
    # mudroom in rear without a nearby garage → service (utility/entry corridor)
    (_MUDROOM_TYPES,   {"rear"},   "service",
     lambda _s, zs: not any(
         (sp.get("type") or "").lower().replace("-", "_").replace(" ", "_")
         in _GARAGE_TYPES
         for z in zs for sp in (z.get("spaces") or [])
     )),
]


def _enforce_zone_classifications(zones: list) -> list:
    """Run a full type→zone correction pass, fixing Gemini misclassifications.

    Rules applied (in order):
      1. half-bath (powder_room/half_bath/toilet) in any non-front zone → front
      2. laundry in centre zone → service
      3. pantry in front zone → service
      4. mudroom in rear zone with no garage anywhere in plan → service
    """
    def _norm(t: str) -> str:
        return (t or "").lower().replace("-", "_").replace(" ", "_")

    # Collect migrations: list of (space_dict, target_zone_position)
    migrations: list[tuple[dict, str]] = []

    for rule_types, rule_from_positions, rule_to, rule_cond in _ZONE_RULES:
        for zone in zones:
            zpos = zone.get("zone_position", "")
            # Skip if rule_from_positions restricts source zone and this doesn't match
            if rule_from_positions is not None and zpos not in rule_from_positions:
                continue
            # Skip if already in the target zone
            if zpos == rule_to:
                continue

            remaining = []
            for s in (zone.get("spaces") or []):
                t = _norm(s.get("type", ""))
                if t in rule_types:
                    if rule_cond is None or rule_cond(s, zones):
                        migrations.append((s, rule_to))
                        continue
                remaining.append(s)
            zone["spaces"] = remaining

    if not migrations:
        return [z for z in zones if z.get("spaces")]

    # Ensure target zones exist and deposit migrated spaces
    zone_by_pos: dict[str, dict] = {z.get("zone_position", ""): z for z in zones}
    zones_list = list(zones)
    for space, target_pos in migrations:
        if target_pos not in zone_by_pos:
            new_zone = {"zone_name": target_pos, "zone_position": target_pos, "spaces": []}
            zones_list.append(new_zone)
            zone_by_pos[target_pos] = new_zone
        zone_by_pos[target_pos].setdefault("spaces", []).append(space)
        log.debug("zone_classification_corrected",
                  space=space.get("name"), type=space.get("type"),
                  to=target_pos)

    return [z for z in zones_list if z.get("spaces")]


def _normalize_to_zones(variation: dict) -> list | None:
    """Normalize any supported schema to the canonical zones list for GridLayoutEngine.

    Priority: zones → spaces → room_graph → area_program.
    Flat space lists are grouped by zone_position/type into zone objects.
    Returns None when no valid spatial data exists.
    """
    # 1. zones key — canonical, preferred
    raw_zones = variation.get("zones")
    if raw_zones and isinstance(raw_zones, list) and len(raw_zones) > 0:
        total_spaces = sum(len(z.get("spaces", [])) for z in raw_zones
                          if isinstance(z, dict))
        log.debug("zones_schema_detected", source="zones",
                  zone_count=len(raw_zones), space_count=total_spaces)
        return _enforce_zone_classifications(raw_zones)

    # 2. Flat space lists under alternate keys
    flat: list | None = None
    source = ""
    for key in ("spaces", "room_graph", "area_program"):
        candidate = variation.get(key)
        if candidate and isinstance(candidate, list) and len(candidate) > 0:
            flat = candidate
            source = key
            break

    if not flat:
        log.warning("zones_no_spatial_data", keys=list(variation.keys()))
        return None

    log.info("zones_normalizing_flat_spaces", source=source, space_count=len(flat))

    # Group flat spaces by zone_position
    zone_map: dict[str, list] = {}
    for s in flat:
        if not isinstance(s, dict):
            continue
        pos = (s.get("zone_position") or
               s.get("_zone_position") or
               _infer_zone_pos(s.get("type", "")))
        zone_map.setdefault(pos, []).append(s)

    if not zone_map:
        log.warning("zones_normalization_empty_after_grouping")
        return None

    zones = [
        {"zone_name": pos, "zone_position": pos, "spaces": rooms}
        for pos, rooms in zone_map.items()
        if rooms
    ]
    total_adj = sum(
        len(s.get("adjacency", [])) for z in zones for s in z["spaces"]
    )
    log.info("zones_normalization_complete", zone_count=len(zones),
             total_spaces=len(flat), adjacency_entries=total_adj)
    return _enforce_zone_classifications(zones)


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


# ── A-03: Room area validation table ─────────────────────────────────────────
# (min_sqm, max_sqm) per approved room type — prevents Gemini hallucinating
# impossible sizes (3 sqm living room, 80 sqm powder room, etc.)
ROOM_AREA_LIMITS: dict[str, tuple[float, float]] = {
    "entry":             (2.0,  14.0),
    "foyer":             (2.0,  14.0),
    "powder_room":       (1.5,   2.8),   # 3'×6' max — half-bath, not a full bath
    "half_bath":         (1.5,   2.8),
    "toilet":            (1.2,   2.8),
    "bathroom":          (3.5,   5.5),   # 5'×8'–6'×9' US shared bath
    "primary_bath":      (4.5,   7.5),   # 6'×8'–8'×10' primary en-suite
    "ensuite_bath":      (4.5,   7.5),
    "master_bath":       (4.5,   7.5),
    "secondary_bath":    (3.5,   5.0),   # same as shared_bath
    "shared_bath":       (3.5,   5.0),
    "full_bath":         (3.5,   5.5),
    "walk_in_closet":    (3.0,   6.0),   # WIC is a closet, not a room
    "closet":            (1.5,   8.0),
    "wic":               (3.0,  16.0),
    "dressing_room":     (4.0,  18.0),
    "primary_bedroom":   (14.0, 40.0),
    "master_bedroom":    (14.0, 40.0),
    "secondary_bedroom": (9.0,  28.0),
    "guest_bedroom":     (9.0,  28.0),
    "bedroom":           (9.0,  28.0),
    "home_office_bedroom":(9.0, 28.0),
    "kitchen":           (7.0,  35.0),
    "dining":            (8.0,  40.0),
    "living":            (15.0, 65.0),
    "great_room":        (20.0, 90.0),
    "family_room":       (15.0, 65.0),
    "open_plan":         (25.0, 110.0),
    "laundry":           (3.5,  14.0),
    "laundry_room":      (3.5,  14.0),
    "mudroom":           (3.0,  14.0),
    "garage":            (25.0, 80.0),
    "pantry":            (1.5,   8.0),
    "utility":           (3.0,  18.0),
    "mechanical":        (3.0,  18.0),
    "storage":           (2.0,  18.0),
    "enclair":           (10.0, 55.0),
    "sunroom":           (8.0,  45.0),
    "covered_porch":     (6.0,  40.0),
    "veranda":           (6.0,  40.0),
    "lanai":             (8.0,  50.0),
}


def _validate_area(room_type: str, area_sqm: float) -> tuple[float, list[str]]:
    """Clamp area to realistic bounds. Returns (clamped_area, warnings)."""
    t = (room_type or "").lower().replace("-", "_").replace(" ", "_")
    limits = ROOM_AREA_LIMITS.get(t)
    if limits is None:
        return area_sqm, []
    lo, hi = limits
    if area_sqm < lo:
        return lo, [f"{room_type}: {area_sqm:.1f}sqm < min {lo}sqm — clamped to {lo}"]
    if area_sqm > hi:
        return hi, [f"{room_type}: {area_sqm:.1f}sqm > max {hi}sqm — clamped to {hi}"]
    return area_sqm, []


def _validate_and_clamp_zones(variation: dict) -> list[str]:
    """Validate and clamp all room areas. Mutates variation zones in-place. Returns warnings."""
    all_warnings: list[str] = []
    for zone in (variation.get("zones") or []):
        for space in (zone.get("spaces") or []):
            area = space.get("area_sqm")
            rt = (space.get("type") or "").lower().replace("-", "_").replace(" ", "_")
            if not area or float(area) <= 0:
                limits = ROOM_AREA_LIMITS.get(rt)
                if limits:
                    default_area = round((limits[0] + limits[1]) / 2, 1)
                    space["area_sqm"] = default_area
                    all_warnings.append(
                        f"{space.get('name','?')} ({rt}): missing area — defaulting to {default_area}sqm")
            else:
                clamped, warns = _validate_area(space.get("type", ""), float(area))
                if warns:
                    space["area_sqm"] = clamped
                    all_warnings.extend(warns)
    return all_warnings


# VALIDATION-FIX: CHECK-F01 — normalise Gemini adjacency output to dict[str, list[str]]
def _normalise_adjacency(raw: object) -> dict[str, list[str]]:
    """Gemini may return adjacency as a dict-of-lists OR a bare list.
    Always return dict[str, list[str]] with canonical keys."""
    if not raw:
        return {}
    if isinstance(raw, dict):
        result: dict[str, list[str]] = {}
        for k, v in raw.items():
            result[k] = list(v) if isinstance(v, list) else ([str(v)] if v else [])
        return result
    if isinstance(raw, list):
        return {"connected_to": [str(x) for x in raw]}
    return {}


def compile_to_plan(generation: dict, request_id: str,
                     autocad_units: str = "mm") -> DrawActionPlan:
    compiler   = ActionCompiler()
    total_area = max(float(generation.get("total_area_sqm") or 100), 1.0)
    variations = []

    for i, v in enumerate(generation.get("variations", [])):
        try:
            # A-03: validate and clamp room areas before compilation
            area_warnings = _validate_and_clamp_zones(v)
            for w in area_warnings:
                log.warning("room_area_clamped", variation=i, detail=w)

            vp = compiler.compile(v, i, total_area)

            # Normalize to canonical zones and always forward to C# GridLayoutEngine
            zones = _normalize_to_zones(v)
            if zones:
                # F-01: populate typed Layout contract (primary path for C# plugin)
                # VALIDATION-FIX: CHECK-F01 — pass zone_id, open_plan, typed adjacency lists
                typed_zones = [
                    ZoneGroupContract(
                        zone_id=z.get("zone_id", ""),
                        zone_name=z.get("zone_name", ""),
                        zone_position=z.get("zone_position", "front"),
                        open_plan=bool(z.get("open_plan", False)),
                        solar_wall=z.get("solar_wall", ""),
                        spaces=[
                            ZoneSpaceContract(
                                name=s.get("name", ""),
                                type=s.get("type", ""),
                                area_sqm=s.get("area_sqm"),
                                floor=s.get("floor", 1),
                                has_natural_light=s.get("has_natural_light", True),
                                privacy_level=s.get("privacy_level"),
                                # Normalise adjacency: accept both dict-of-lists and bare list
                                adjacency=_normalise_adjacency(s.get("adjacency")),
                                aspect_ratio=s.get("aspect_ratio"),
                                min_width_m=s.get("min_width_m"),
                                min_depth_m=s.get("min_depth_m"),
                            )
                            for s in (z.get("spaces") or [])
                            if isinstance(s, dict)
                        ],
                    )
                    for z in zones
                    if isinstance(z, dict)
                ]
                sc_raw = generation.get("site_constraints") or {}
                site_constraints = SiteConstraintsContract(
                    plot_width_mm=sc_raw.get("plot_width_mm"),
                    plot_depth_mm=sc_raw.get("plot_depth_mm"),
                    front_setback_mm=sc_raw.get("front_setback_mm"),
                    side_setback_mm=sc_raw.get("side_setback_mm"),
                    rear_setback_mm=sc_raw.get("rear_setback_mm"),
                ) if sc_raw else None
                vp.layout = VariationPayload(
                    zones=typed_zones,
                    organisation_strategy=v.get("organisation_strategy", "residential"),
                    organisation_type=v.get("organisation_type"),
                    wing_orientation=v.get("wing_orientation", "living_left"),
                    garage_placement=v.get("garage_placement", "rear"),
                    structural_grid_m=v.get("structural_grid_m", 4.0),
                    entry_space=v.get("entry_space", ""),
                    site_constraints=site_constraints,
                    validation_warnings=area_warnings[:5],
                )
                # legacy properties — kept for backward compat with older plugin versions
                vp.properties["zones_json"]            = json.dumps(zones)
                vp.properties["organisation_strategy"] = v.get("organisation_strategy",
                                                                "residential")
                vp.properties["structural_grid_m"]     = v.get("structural_grid_m", 4.0)
                vp.properties["entry_space"]           = v.get("entry_space", "")
                vp.properties["wing_orientation"]      = v.get("wing_orientation",
                                                                "living_left")
                vp.properties["garage_placement"]      = v.get("garage_placement", "rear")
                log.info("zones_json_populated", variation=i, zone_count=len(zones))
                if area_warnings:
                    vp.warnings.extend(area_warnings[:5])  # cap warning list length
            else:
                log.warning("zones_json_missing_no_spatial_data", variation=i,
                            available_keys=list(v.keys()))

            if generation.get("site_constraints"):
                vp.properties["site_constraints"] = json.dumps(generation["site_constraints"])
            variations.append(vp)
        except Exception as e:
            log.error("variation_compile_error", variation=i, error=str(e))

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
