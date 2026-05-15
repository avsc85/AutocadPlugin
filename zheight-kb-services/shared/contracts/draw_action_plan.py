"""
DrawActionPlan — the single data contract between GCP backend and AutoCAD plugin.
All coordinates in millimetres. The C# plugin converts to drawing units.
"""
from __future__ import annotations
from enum import Enum
from typing import Any
from pydantic import BaseModel, Field


class ActionType(str, Enum):
    DRAW_WALL         = "DRAW_WALL"
    DRAW_DOOR         = "DRAW_DOOR"
    DRAW_WINDOW       = "DRAW_WINDOW"
    DRAW_COLUMN       = "DRAW_COLUMN"
    DRAW_STAIR        = "DRAW_STAIR"
    DRAW_ROOM_LABEL   = "DRAW_ROOM_LABEL"
    ADD_DIMENSION     = "ADD_DIMENSION"
    ADD_AREA_TAG      = "ADD_AREA_TAG"
    ADD_NORTH_ARROW   = "ADD_NORTH_ARROW"
    ADD_SCALE_BAR     = "ADD_SCALE_BAR"
    ADD_TITLE_BLOCK   = "ADD_TITLE_BLOCK"
    ADD_HATCH         = "ADD_HATCH"
    CREATE_LAYER      = "CREATE_LAYER"
    START_GROUP       = "START_GROUP"
    END_GROUP         = "END_GROUP"


class Point2D(BaseModel):
    x: float
    y: float


class DrawAction(BaseModel):
    action_type:      ActionType
    layer:            str
    group_id:         str | None = None
    start:            Point2D | None = None
    end:              Point2D | None = None
    vertices:         list[Point2D] = []
    center:           Point2D | None = None
    thickness_mm:     float | None = None
    height_mm:        float | None = None
    wall_type:        str | None = None
    door_width_mm:    float | None = None
    door_swing:       str = "right"
    swing_angle:      float = 90.0
    window_width_mm:  float | None = None
    window_height_mm: float | None = None
    window_sill_mm:   float | None = None
    window_type:      str | None = None
    column_width_mm:  float | None = None
    column_depth_mm:  float | None = None
    label_text:       str | None = None
    label_area_sqm:   float | None = None
    font_height_mm:   float = 300.0
    hatch_pattern:    str | None = None
    hatch_scale:      float = 1.0
    hatch_angle:      float = 0.0
    hatch_boundary:   list[Point2D] = []
    layer_color:      int | None = None
    layer_linetype:   str | None = None
    layer_lineweight: float | None = None
    properties:       dict[str, Any] = {}

    class Config:
        use_enum_values = True


class SpaceSummaryItem(BaseModel):
    name:      str
    type:      str
    area_sqm:  float
    floor:     int = 1
    facing:    str | None = None


class ConstraintReport(BaseModel):
    far_used:          float | None = None
    height_m:          float | None = None
    coverage_pct:      float | None = None
    all_rooms_reached: bool = True
    adjacency_met:     list[str] = []
    adjacency_failed:  list[str] = []


# ── F-01: Typed layout contract — replaces stringly-typed Properties dict ────────

class ZoneSpaceContract(BaseModel):
    """Typed contract for a single space/room within a zone."""
    name:              str = ""
    type:              str = ""
    area_sqm:          float | None = None
    floor:             int = 1
    has_natural_light: bool = True
    privacy_level:     str | None = None
    # VALIDATION-FIX: CHECK-F01 — typed as dict[str, list[str]] instead of opaque dict
    adjacency:         dict[str, list[str]] = {}
    aspect_ratio:      float | None = None
    min_width_m:       float | None = None
    min_depth_m:       float | None = None

    class Config:
        extra = "ignore"


class ZoneGroupContract(BaseModel):
    """Typed contract for a zone group (front / centre / rear / service)."""
    # VALIDATION-FIX: CHECK-F01 — added zone_id, open_plan, solar_wall
    zone_id:       str = ""
    zone_name:     str = ""
    zone_position: str = "front"
    open_plan:     bool = False
    solar_wall:    str = ""
    spaces:        list[ZoneSpaceContract] = []

    class Config:
        extra = "ignore"


class SiteConstraintsContract(BaseModel):
    plot_width_mm:    float | None = None
    plot_depth_mm:    float | None = None
    front_setback_mm: float | None = None
    side_setback_mm:  float | None = None
    rear_setback_mm:  float | None = None


class VariationPayload(BaseModel):
    """Typed layout payload — eliminates zones_json escaped string antipattern."""
    zones:                  list[ZoneGroupContract] = []
    organisation_strategy:  str = "residential"
    organisation_type:      str | None = None
    wing_orientation:       str = "living_left"
    garage_placement:       str = "rear"
    structural_grid_m:      float = 4.0
    entry_space:            str = ""
    site_constraints:       SiteConstraintsContract | None = None
    validation_warnings:    list[str] = []

    class Config:
        extra = "ignore"


class VariationPlan(BaseModel):
    variation_id:        int
    variation_name:      str
    concept_rationale:   str
    total_area_sqm:      float
    floor_count:         int = 1
    scale:               str = "1:100"
    units:               str = "mm"
    north_angle_deg:     float = 0.0
    actions:             list[DrawAction]
    space_summary:       list[SpaceSummaryItem] = []
    constraint_report:   ConstraintReport = Field(default_factory=ConstraintReport)
    passive_notes:       str = ""
    warnings:            list[str] = []
    layout:              VariationPayload = Field(default_factory=VariationPayload)
    properties:          dict = {}  # legacy — kept for backward compat; prefer layout

    class Config:
        use_enum_values = True


class DrawActionPlan(BaseModel):
    request_id:              str
    api_version:             str = "v1"
    project_description:     str
    project_category:        str
    generated_at:            str
    variations:              list[VariationPlan]
    recommended_variation:   int = 1
    reference_project_ids:   list[str] = []
    kb_scores:               list[float] = []
    global_warnings:         list[str] = []
    layer_standard:          str = "AIA"

    class Config:
        use_enum_values = True
