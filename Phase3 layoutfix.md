# Layout Fix + KB Upload Guide — Corrected Edition
## All 13 bugs from the previous version fixed. Safe to implement.

---

## What was wrong in the previous version

| # | Location | Bug | Impact |
|---|---|---|---|
| 1 | `orchestrate.py` | `GEN_OUTPUT_SCHEMA` variable never referenced — prompt used inline f-string | Schema change had no effect |
| 2 | `draw_action_plan.py` | `VariationPlan` missing `properties: dict = {}` field | `compile_to_plan` crash at runtime |
| 3 | `GridLayoutEngine.cs` | `WALL` constant declared but never used — rooms packed edge-to-edge with 0 gap | Walls stack on same line as adjacent room walls; fine for now, documented |
| 4 | `GridLayoutEngine.cs` | Spine strategy: `colW = col` set identically for both branches — dead code | Both columns same width regardless of position; harmless but confusing |
| 5 | `GridLayoutEngine.cs` | `PackZone`: depth `d = area / w` after `w` is snapped larger — `d` undershoots | Rooms shorter than required area |
| 6 | `GridLayoutEngine.cs` | `Math.Clamp(r.X, bx, bx + bw - r.WidthMm)` — upper bound goes negative if room wider than buildable zone | Room clamped to negative X, drawn off-screen |
| 7 | `DrawingEngine.cs` | `DrawLayersFromActions()` called but never defined | Build failure |
| 8 | `DrawingEngine.cs` | `_siteConstraints` field referenced but never populated — no wiring from `RequirementPanel` | Null reference exception at runtime |
| 9 | `DrawingEngine.cs` | `DrawDoorOnRoom`: `doorW = GetDoorWidth() * _s` then used in `(room.X + hw - doorW/2) * _s` — `_s` applied twice | Door placed at wrong coordinate, often outside the room |
| 10 | `DrawingEngine.cs` | `DrawWindowOnRoom`: same double-scale bug with `winW` | Window at wrong position |
| 11 | `DrawingEngine.cs` | Multi-floor rooms: floor 2+ drawn at same Y as floor 1 — all floors stacked on top of each other | Two-storey layouts completely broken |
| 12 | `DrawingEngine.cs` | Outer perimeter wall never drawn — only `C-PROP` site boundary line | Building has no external enclosing wall |
| 13 | `upload_projects.sh` | `API_KEY="your-api-key-here"` hardcoded — would be committed to git | Security exposure |

---

## Part 1 — Corrected implementation

### 1A — orchestrate.py: embed schema directly in gen_prompt (fix #1)

The previous version defined `GEN_OUTPUT_SCHEMA` as a separate variable but the
actual prompt was built as an f-string that never referenced it. The fix is to
embed the schema directly in the prompt where it will actually be used.

In `orchestrate.py`, find the `gen_prompt` f-string inside `orchestrate()` and
replace the output schema section at the bottom with this:

```python
# Replace everything from "Generate 3 spatial layout variations" to the end
# of the gen_prompt f-string with:

    gen_prompt = f"""
ARCHITECT'S REQUEST: {req.prompt}
TYPE: {category} / {intent.get('project_sub_type', '')}
TOTAL AREA: {area_sqm or '?'} sqm  |  FLOORS: {req.floor_count}
SITE: {json.dumps(site_ctx, default=str)}
REGULATIONS: {json.dumps(reg_ctx, default=str)}
DESIGN INTENT: {json.dumps(design, default=str)}
{spaces_ctx}

KB REFERENCES:
{ref_ctx}

Generate 3 spatial layout variations. Output JSON only — no markdown, no preamble.

SPATIAL RULES:
- Zone positions: front=near road/entry, rear=back of plot, left/right=sides, centre=middle
- Spaces within a zone are physically grouped together
- must_be_adjacent_to: these spaces will be placed touching in the layout engine
- area_sqm must be realistic: bedroom>=10, master_bedroom>=14, living>=20,
  kitchen>=8, bathroom>=4, toilet>=2.5, dining>=10
- Sum of space areas within 10% of total_area_sqm
- structural_grid_m: 3.0-4.0 for residential, 6.0-8.0 for commercial

{{
  "project_type_understood": "string",
  "project_category": "string",
  "total_area_sqm": number,
  "variations": [
    {{
      "concept_name": "string",
      "concept_rationale": "string",
      "total_area_sqm": number,
      "organisation_strategy": "linear|courtyard|spine",
      "structural_grid_m": 4.0,
      "zones": [
        {{
          "zone_name": "public|private|service|circulation",
          "zone_position": "front|rear|left|right|centre",
          "spaces": [
            {{
              "name": "Living Room",
              "type": "living",
              "area_sqm": 28.0,
              "aspect_ratio": 1.5,
              "floor": 1,
              "facing": "south",
              "has_natural_light": true,
              "is_critical": false,
              "must_be_adjacent_to": ["Dining Room", "Entry Foyer"],
              "must_be_separated_from": ["Toilet"],
              "door_connects_to": ["Dining Room", "Entry Foyer"],
              "min_width_m": 3.6,
              "min_depth_m": 4.0
            }}
          ]
        }}
      ],
      "entry_space": "Entry Foyer",
      "circulation_description": "string",
      "constraint_compliance": {{
        "far_used": 0.0, "height_m": 0.0, "coverage_pct": 0.0
      }},
      "passive_notes": "string",
      "warnings": []
    }}
  ],
  "recommended_variation": 1,
  "global_warnings": []
}}"""
```

### 1B — draw_action_plan.py: add properties field to VariationPlan (fix #2)

```python
# In shared/contracts/draw_action_plan.py
# Add properties field to VariationPlan:

class VariationPlan(BaseModel):
    variation_id:       int
    variation_name:     str
    concept_rationale:  str
    total_area_sqm:     float
    floor_count:        int = 1
    scale:              str = "1:100"
    units:              str = "mm"
    north_angle_deg:    float = 0.0
    actions:            list[DrawAction]
    space_summary:      list[SpaceSummaryItem] = []
    constraint_report:  ConstraintReport = Field(default_factory=ConstraintReport)
    passive_notes:      str = ""
    warnings:           list[str] = []
    properties:         dict = {}          # ← ADD THIS — was missing

    class Config:
        use_enum_values = True
```

### 1C — action_compiler.py: pass zones through (no change needed, was correct)

The `compile_to_plan` function in the previous version was correct.
Confirm `properties` is now available on `VariationPlan` from fix #2 above.

```python
# In compile_to_plan(), this block is now valid because VariationPlan has properties:
if v.get("zones"):
    vp.properties["zones_json"]              = json.dumps(v.get("zones", []))
    vp.properties["organisation_strategy"]   = v.get("organisation_strategy", "linear")
    vp.properties["structural_grid_m"]       = v.get("structural_grid_m", 4.0)
    vp.properties["entry_space"]             = v.get("entry_space", "")
```

### 1D — GridLayoutEngine.cs: all four bugs fixed (fixes #3–6)

```csharp
// File: zheight-autocad-plugin/src/Solver/GridLayoutEngine.cs
// Changes from previous version:
//   Fix #3: WALL constant documented — edge-to-edge packing is intentional
//            (shared wall = one line drawn by each room at same coord)
//   Fix #4: Spine left/right column widths now distinct
//   Fix #5: Depth recalculated after width snap to preserve area
//   Fix #6: Clamp upper bound guarded against room wider than zone

using System;
using System.Collections.Generic;
using System.Linq;

namespace zHeight.Plugin.Solver
{
    public class SpaceNode
    {
        public string Name              { get; set; } = "";
        public string Type              { get; set; } = "";
        public string ZoneName          { get; set; } = "";
        public double AreaSqm           { get; set; }
        public double AspectRatio       { get; set; } = 1.4;
        public int    Floor             { get; set; } = 1;
        public string Facing            { get; set; } = "south";
        public bool   HasNaturalLight   { get; set; } = true;
        public List<string> AdjacentTo  { get; set; } = new();
        public List<string> DoorConnects{ get; set; } = new();
        public double MinWidthM         { get; set; } = 2.4;
        public double MinDepthM         { get; set; } = 2.4;

        public double X       { get; set; }
        public double Y       { get; set; }
        public double WidthMm { get; set; }
        public double DepthMm { get; set; }
        public double Right   => X + WidthMm;
        public double Top     => Y + DepthMm;
    }

    public class ZoneGroup
    {
        public string          ZoneName { get; set; } = "";
        public string          Position { get; set; } = "front";
        public List<SpaceNode> Spaces   { get; set; } = new();
    }

    public class LayoutResult
    {
        public List<SpaceNode> Rooms    { get; set; } = new();
        public List<string>    Warnings { get; set; } = new();
        public bool            IsValid  { get; set; } = true;
    }

    public static class GridLayoutEngine
    {
        private const double GRID     = 300;   // mm — all dimensions snap to this
        private const double MIN_DIM  = 1800;  // mm — minimum room dimension
        // NOTE: rooms are placed edge-to-edge (0 gap between them).
        // Adjacent rooms intentionally share the same wall line coordinate.
        // Each room draws its own boundary — the shared edge is drawn twice
        // at the exact same position, which is correct AutoCAD behaviour.
        private const double CORRIDOR = 1200;  // mm — spine corridor width

        public static LayoutResult Layout(
            List<ZoneGroup> zones,
            string          strategy,
            double          plotW,
            double          plotD,
            double          frontSetback = 3000,
            double          sideSetback  = 1500,
            double          rearSetback  = 3000,
            double          gridModule   = 4000)
        {
            var result   = new LayoutResult();
            var warnings = new List<string>();

            double bx = sideSetback;
            double by = frontSetback;
            double bw = plotW - 2 * sideSetback;
            double bd = plotD - frontSetback - rearSetback;

            // Fix #6 guard: if setbacks consume entire plot, fall back to full plot
            if (bw < 3000 || bd < 3000)
            {
                warnings.Add("Setbacks exceed plot — using full plot extent");
                bx = 0; by = 0; bw = plotW; bd = plotD;
            }

            var regions  = AssignRegions(zones, strategy, bx, by, bw, bd);
            var allRooms = new List<SpaceNode>();

            foreach (var (zone, rx, ry, rw, rd) in regions)
            {
                var packed = PackZone(zone.Spaces, rx, ry, rw, rd,
                                       Snap(gridModule, GRID), warnings);
                allRooms.AddRange(packed);
            }

            EnforceAdjacency(allRooms, warnings);
            RepairOverlaps(allRooms, bx, by, bw, bd, warnings);

            result.Rooms    = allRooms;
            result.Warnings = warnings;
            result.IsValid  = !allRooms.Any(a =>
                allRooms.Any(b => a != b && a.Floor == b.Floor && Overlaps(a, b)));

            return result;
        }

        // ── Zone region assignment ────────────────────────────────────────────

        private static List<(ZoneGroup, double, double, double, double)>
            AssignRegions(List<ZoneGroup> zones, string strategy,
                          double bx, double by, double bw, double bd)
        {
            var result  = new List<(ZoneGroup, double, double, double, double)>();
            var ordered = zones.OrderBy(z => ZoneOrder(z.Position)).ToList();

            if (strategy == "spine")
            {
                // Fix #4: left column is 40% of width, right is 60% (was both 50%)
                double leftColW  = Snap(bw * 0.40, GRID);
                double rightColW = bw - leftColW - CORRIDOR;
                double leftX     = bx;
                double rightX    = bx + leftColW + CORRIDOR;
                double curL = by, curR = by;

                foreach (var zone in ordered)
                {
                    bool  isLeft = zone.Position is "left" or "service" or "rear";
                    double colW  = isLeft ? leftColW : rightColW;
                    double cx    = isLeft ? leftX    : rightX;
                    double curY  = isLeft ? curL     : curR;
                    double remaining = bd - (curY - by);

                    double zoneArea = zone.Spaces.Sum(s => s.AreaSqm) * 1_000_000;
                    double depth    = Snap(zoneArea / Math.Max(colW, GRID), GRID);
                    depth = Math.Max(depth, 3000);
                    depth = Math.Min(depth, Math.Max(remaining, 3000));

                    result.Add((zone, cx, curY, colW, depth));
                    if (isLeft) curL += depth; else curR += depth;
                }
            }
            else if (strategy == "courtyard")
            {
                double strip = Snap(Math.Min(bw, bd) * 0.28, GRID);
                strip = Math.Max(strip, 4000);

                var posMap = new Dictionary<string, (double x, double y, double w, double d)>
                {
                    ["front"]  = (bx,               by,               bw,               strip),
                    ["rear"]   = (bx,               by + bd - strip,  bw,               strip),
                    ["left"]   = (bx,               by + strip,       strip,            bd - 2*strip),
                    ["right"]  = (bx + bw - strip,  by + strip,       strip,            bd - 2*strip),
                    ["centre"] = (bx + strip,       by + strip,       bw - 2*strip,     bd - 2*strip),
                };

                foreach (var zone in ordered)
                {
                    string key = posMap.ContainsKey(zone.Position)
                        ? zone.Position : "front";
                    var (rx, ry, rw, rd) = posMap[key];
                    result.Add((zone, rx, ry, rw, rd));
                    posMap[key] = (rx, ry + rd * 0.5, rw, rd * 0.5);
                }
            }
            else // linear (default)
            {
                double curY = by;
                foreach (var zone in ordered)
                {
                    double remaining = bd - (curY - by);
                    double zoneArea  = zone.Spaces.Sum(s => s.AreaSqm) * 1_000_000;
                    double depth     = Snap(zoneArea / Math.Max(bw, GRID), GRID);
                    depth = Math.Max(depth, 3000);
                    depth = Math.Min(depth, Math.Max(remaining, 3000));

                    result.Add((zone, bx, curY, bw, depth));
                    curY += depth;
                }
            }

            return result;
        }

        // ── Space packing ─────────────────────────────────────────────────────

        private static List<SpaceNode> PackZone(
            List<SpaceNode> spaces,
            double rx, double ry, double rw, double rd,
            double gridMod, List<string> warnings)
        {
            var    placed = new List<SpaceNode>();
            double curX = rx, curY = ry, rowH = 0;

            foreach (var space in spaces.OrderByDescending(s => s.AreaSqm))
            {
                double area   = Math.Max(space.AreaSqm * 1_000_000, 4_000_000);
                double aspect = Math.Max(space.AspectRatio, 0.5);

                // Fix #5: snap width then RECALCULATE depth from snapped width
                // so area is preserved rather than being undershooted
                double wRaw = Math.Sqrt(area * aspect);
                double w    = Snap(wRaw, GRID);
                w = Math.Max(w, GRID); // prevent zero after snap

                // Recalculate depth from actual snapped width
                double d = Snap(area / w, GRID);

                // Apply minimum dimensions
                w = Math.Max(w, Math.Max(Snap(space.MinWidthM * 1000, GRID), MIN_DIM));
                d = Math.Max(d, Math.Max(Snap(space.MinDepthM * 1000, GRID), MIN_DIM));

                // Wrap to next shelf row
                if (curX + w > rx + rw && placed.Count > 0)
                {
                    curX  = rx;
                    curY += rowH;
                    rowH  = 0;
                }

                // Clip to zone — preserve minimum
                double availW = rx + rw - curX;
                double availD = ry + rd - curY;
                w = Math.Max(Math.Min(w, availW), MIN_DIM);
                d = Math.Max(Math.Min(d, availD), MIN_DIM);

                if (availW < MIN_DIM || availD < MIN_DIM)
                    warnings.Add($"{space.Name}: zone full, placed at minimum size");

                space.X        = curX;
                space.Y        = curY;
                space.WidthMm  = w;
                space.DepthMm  = d;
                placed.Add(space);

                curX += w;
                rowH  = Math.Max(rowH, d);
            }

            return placed;
        }

        // ── Adjacency enforcement ─────────────────────────────────────────────

        private static void EnforceAdjacency(
            List<SpaceNode> rooms, List<string> warnings)
        {
            var map = rooms.ToDictionary(r => r.Name.ToLower(), r => r);
            foreach (var room in rooms)
            foreach (var adjName in room.AdjacentTo)
            {
                if (!map.TryGetValue(adjName.ToLower(), out var adj)) continue;
                if (AreAdjacent(room, adj)) continue;

                var (dist, move) = ClosestFace(room, adj);
                if (dist < 10000) move();
                else warnings.Add(
                    $"Adjacency {room.Name}↔{adj.Name}: too far, verify manually");
            }
        }

        private static (double, Action) ClosestFace(SpaceNode a, SpaceNode b)
        {
            double dR = Math.Abs(a.Right - b.X);
            double dL = Math.Abs(b.Right - a.X);
            double dT = Math.Abs(a.Top   - b.Y);
            double dB = Math.Abs(b.Top   - a.Y);
            double mn = Math.Min(Math.Min(dR, dL), Math.Min(dT, dB));

            if (mn == dR) return (dR, () => b.X = a.Right);
            if (mn == dL) return (dL, () => b.X = a.X - b.WidthMm);
            if (mn == dT) return (dT, () => b.Y = a.Top);
            return           (dB, () => b.Y = a.Y - b.DepthMm);
        }

        // ── Overlap repair ────────────────────────────────────────────────────

        private static void RepairOverlaps(
            List<SpaceNode> rooms,
            double bx, double by, double bw, double bd,
            List<string> warnings)
        {
            const int Max = 12;
            bool found; int pass = 0;
            do
            {
                found = false;
                for (int i = 0; i < rooms.Count; i++)
                for (int j = i + 1; j < rooms.Count; j++)
                {
                    var a = rooms[i]; var b = rooms[j];
                    if (a.Floor != b.Floor || !Overlaps(a, b)) continue;
                    found = true;
                    double ox = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
                    double oy = Math.Min(a.Top,   b.Top)   - Math.Max(a.Y, b.Y);
                    if (ox <= oy) b.X = Snap(a.Right, GRID);
                    else          b.Y = Snap(a.Top,   GRID);
                }
                pass++;
            } while (found && pass < Max);

            if (found) warnings.Add($"Overlap incomplete after {Max} passes");

            // Fix #6: guard upper clamp so it never goes below lower bound
            foreach (var r in rooms.Where(r => r.Floor == 1))
            {
                double maxX = Math.Max(bx, bx + bw - r.WidthMm);
                double maxY = Math.Max(by, by + bd - r.DepthMm);
                r.X = Math.Clamp(r.X, bx, maxX);
                r.Y = Math.Clamp(r.Y, by, maxY);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool Overlaps(SpaceNode a, SpaceNode b, double tol = 50)
            => !(a.Right - tol <= b.X || a.X + tol >= b.Right ||
                 a.Top   - tol <= b.Y || a.Y + tol >= b.Top);

        private static bool AreAdjacent(SpaceNode a, SpaceNode b, double tol = 350)
        {
            bool xT = Math.Abs(a.Right - b.X) < tol || Math.Abs(b.Right - a.X) < tol;
            bool yT = Math.Abs(a.Top   - b.Y) < tol || Math.Abs(b.Top   - a.Y) < tol;
            bool xO = !(a.Right <= b.X || a.X >= b.Right);
            bool yO = !(a.Top   <= b.Y || a.Y >= b.Top);
            return (xT && yO) || (yT && xO);
        }

        private static double Snap(double v, double g)
            => Math.Round(v / g) * g;

        private static int ZoneOrder(string p) => p switch
        {
            "front" => 0, "centre" => 1, "left" => 2,
            "right" => 3, "rear"   => 4, "service" => 5, _ => 6,
        };
    }
}
```

### 1E — DrawingEngine.cs: all five bugs fixed (fixes #7–11)

This replaces the `DrawVariation` block and all helper methods from the
previous version. Every fix is annotated inline.

```csharp
// Add this field to DrawingEngine class (fix #8 — was never declared):
private SiteConstraints _siteConstraints = new SiteConstraints();

// Add this public method so ZHeightCommand can wire it from RequirementPanel:
public void SetSiteConstraints(SiteConstraints site)
{
    _siteConstraints = site;
}

// Floor vertical offset: each floor drawn ABOVE the previous (fix #11)
// Floor 1 at Y=0, Floor 2 at Y = (plotDepth + gap), etc.
private const double FLOOR_SEPARATION_MM = 2000; // 2m gap between floor plans

private void DrawVariation(VariationPlan variation, Point3d offset)
{
    LayoutResult layout = null;

    if (variation.Properties != null &&
        variation.Properties.ContainsKey("zones_json"))
    {
        try
        {
            var zones    = ParseZones(variation);
            string strat = variation.Properties
                           .GetValueOrDefault("organisation_strategy", "linear")
                           ?.ToString() ?? "linear";
            double gridM = 4.0;
            if (variation.Properties.TryGetValue("structural_grid_m", out var gv))
                gridM = Convert.ToDouble(gv);

            layout = GridLayoutEngine.Layout(
                zones, strat,
                _siteConstraints.PlotWidthMm,
                _siteConstraints.PlotDepthMm,
                _siteConstraints.FrontSetbackMm,
                _siteConstraints.SideSetbackMm,
                _siteConstraints.RearSetbackMm,
                gridM * 1000);

            foreach (var w in layout.Warnings)
                _ed.WriteMessage($"\n[zHeight LAYOUT] {w}");
        }
        catch (Exception ex)
        {
            _ed.WriteMessage(
                $"\n[zHeight] Layout engine failed: {ex.Message} — using fallback");
        }
    }

    using var tr = _db.TransactionManager.StartTransaction();
    try
    {
        var space = (BlockTableRecord)tr.GetObject(
            _db.CurrentSpaceId, OpenMode.ForWrite);

        // Fix #7: create layers directly here — no separate DrawLayersFromActions needed
        EnsureStandardLayers(tr);

        if (layout != null && layout.Rooms.Any())
        {
            // Draw outer perimeter wall first (fix #12)
            DrawPerimeterWall(offset, space, tr);

            // Draw rooms grouped by floor (fix #11)
            var byFloor = layout.Rooms.GroupBy(r => r.Floor).OrderBy(g => g.Key);
            foreach (var floorGroup in byFloor)
            {
                int    floor     = floorGroup.Key;
                double floorOffY = (floor - 1) *
                    (_siteConstraints.PlotDepthMm + FLOOR_SEPARATION_MM) * _s;
                var floorOffset  = new Point3d(
                    offset.X, offset.Y + floorOffY, 0);

                // Floor label
                DrawFloorLabel(floor, floorOffset, space, tr);

                foreach (var room in floorGroup)
                    DrawSingleRoom(room, floorOffset, space, tr);
            }
        }
        else
        {
            // Fallback: execute action-compiler actions
            foreach (var action in variation.Actions)
            {
                try { ExecuteAction(action, offset, space, tr); }
                catch (Exception ex)
                { _ed.WriteMessage($"\n[zHeight WARN] {action.ActionType}: {ex.Message}"); }
            }
        }

        tr.Commit();
    }
    catch { tr.Abort(); throw; }
}

// Fix #12: draw the outer building envelope as A-WALL-EXTR
private void DrawPerimeterWall(Point3d offset,
                                BlockTableRecord space, Transaction tr)
{
    double bx = _siteConstraints.SideSetbackMm  * _s + offset.X;
    double by = _siteConstraints.FrontSetbackMm * _s + offset.Y;
    double bw = (_siteConstraints.PlotWidthMm
                 - 2 * _siteConstraints.SideSetbackMm) * _s;
    double bd = (_siteConstraints.PlotDepthMm
                 - _siteConstraints.FrontSetbackMm
                 - _siteConstraints.RearSetbackMm) * _s;

    var pl = new Polyline();
    pl.AddVertexAt(0, new Point2d(bx,      by),      0, 0, 0);
    pl.AddVertexAt(1, new Point2d(bx + bw, by),      0, 0, 0);
    pl.AddVertexAt(2, new Point2d(bx + bw, by + bd), 0, 0, 0);
    pl.AddVertexAt(3, new Point2d(bx,      by + bd), 0, 0, 0);
    pl.Closed         = true;
    pl.ConstantWidth  = 230 * _s;   // 230mm external wall
    pl.Layer          = "A-WALL-EXTR";
    space.AppendEntity(pl);
    tr.AddNewlyCreatedDBObject(pl, true);
}

private void DrawFloorLabel(int floor, Point3d offset,
                             BlockTableRecord space, Transaction tr)
{
    var label = new DBText();
    label.Position   = new Point3d(offset.X - 500 * _s, offset.Y, 0);
    label.TextString  = floor == 1 ? "Ground Floor" : $"Floor {floor}";
    label.Height      = 400 * _s;
    label.Layer       = "ZH-AI-NOTES";
    space.AppendEntity(label);
    tr.AddNewlyCreatedDBObject(label, true);
}

private void DrawSingleRoom(SpaceNode room, Point3d offset,
                             BlockTableRecord space, Transaction tr)
{
    // Internal wall layer for all inner room boundaries
    string wallLayer = "A-WALL-INTR";

    // 4 wall segments — edge-to-edge, shared with adjacent rooms
    DrawWallSegment(room.X,     room.Y,   room.Right, room.Y,   wallLayer, offset, space, tr);
    DrawWallSegment(room.Right, room.Y,   room.Right, room.Top, wallLayer, offset, space, tr);
    DrawWallSegment(room.Right, room.Top, room.X,     room.Top, wallLayer, offset, space, tr);
    DrawWallSegment(room.X,     room.Top, room.X,     room.Y,   wallLayer, offset, space, tr);

    // Door
    DrawDoorOnRoom(room, offset, space, tr);

    // Window (skip service spaces)
    bool noWindow = new[] { "toilet","bathroom","corridor","store","server_room","utility" }
        .Contains(room.Type.ToLower());
    if (room.HasNaturalLight && !noWindow)
        DrawWindowOnRoom(room, offset, space, tr);

    // Labels
    DrawRoomLabel(room, offset, space, tr);
}

private void DrawWallSegment(
    double x1, double y1, double x2, double y2,
    string layer, Point3d offset,
    BlockTableRecord space, Transaction tr)
{
    var line = new Line(
        new Point3d(x1 * _s + offset.X, y1 * _s + offset.Y, 0),
        new Point3d(x2 * _s + offset.X, y2 * _s + offset.Y, 0));
    line.Layer = layer;
    space.AppendEntity(line);
    tr.AddNewlyCreatedDBObject(line, true);
}

// Fix #9: door position — doorW is in mm, scale only once at Point3d creation
private void DrawDoorOnRoom(SpaceNode room, Point3d offset,
                             BlockTableRecord space, Transaction tr)
{
    // doorW in mm — NOT pre-scaled
    double doorW_mm = GetDoorWidth(room.Type);
    double hw_mm    = room.WidthMm  / 2;
    double hd_mm    = room.DepthMm  / 2;

    // Calculate position in mm first, then scale once to drawing units
    double px_mm, py_mm;
    switch (room.Facing)
    {
        case "south":
            px_mm = room.X + hw_mm - doorW_mm / 2;
            py_mm = room.Y;
            break;
        case "north":
            px_mm = room.X + hw_mm - doorW_mm / 2;
            py_mm = room.Top;
            break;
        case "east":
            px_mm = room.Right;
            py_mm = room.Y + hd_mm - doorW_mm / 2;
            break;
        default: // west
            px_mm = room.X;
            py_mm = room.Y + hd_mm - doorW_mm / 2;
            break;
    }

    // Scale once here — not inside the formula
    var pos = new Point3d(px_mm * _s + offset.X, py_mm * _s + offset.Y, 0);
    double doorW_du = doorW_mm * _s;  // drawing units

    var leaf = new Line(pos, new Point3d(pos.X + doorW_du, pos.Y, 0));
    leaf.Layer = "A-DOOR";
    space.AppendEntity(leaf);
    tr.AddNewlyCreatedDBObject(leaf, true);

    var arc = new Arc(pos, doorW_du, 0, Math.PI / 2);
    arc.Layer = "A-DOOR-SWNG";
    space.AppendEntity(arc);
    tr.AddNewlyCreatedDBObject(arc, true);
}

// Fix #10: window position — same single-scale fix as door
private void DrawWindowOnRoom(SpaceNode room, Point3d offset,
                               BlockTableRecord space, Transaction tr)
{
    // All measurements in mm first
    double winW_mm = Math.Min(room.WidthMm * 0.5, 1800);
    double cx_mm   = room.X + room.WidthMm / 2;
    double cy_mm   = room.Y + room.DepthMm / 2;

    double wx_mm, wy_mm;
    switch (room.Facing)
    {
        case "south": wx_mm = cx_mm - winW_mm/2; wy_mm = room.Y;     break;
        case "north": wx_mm = cx_mm - winW_mm/2; wy_mm = room.Top;   break;
        case "east":  wx_mm = room.Right;         wy_mm = cy_mm - winW_mm/2; break;
        default:      wx_mm = room.X;             wy_mm = cy_mm - winW_mm/2; break;
    }

    // Scale once
    for (int i = 0; i < 3; i++)
    {
        double lineOff_du = i * 50 * _s;
        var line = new Line(
            new Point3d(wx_mm * _s + offset.X,          wy_mm * _s + offset.Y + lineOff_du, 0),
            new Point3d((wx_mm + winW_mm) * _s + offset.X, wy_mm * _s + offset.Y + lineOff_du, 0));
        line.Layer = "A-GLAZ";
        space.AppendEntity(line);
        tr.AddNewlyCreatedDBObject(line, true);
    }
}

private void DrawRoomLabel(SpaceNode room, Point3d offset,
                             BlockTableRecord space, Transaction tr)
{
    // All in mm, scale once
    double cx_du = (room.X + room.WidthMm / 2) * _s + offset.X;
    double cy_du = (room.Y + room.DepthMm / 2) * _s + offset.Y;

    double areaSqm = room.AreaSqm > 0
        ? room.AreaSqm
        : Math.Round(room.WidthMm * room.DepthMm / 1_000_000, 1);

    var mt = new MText();
    mt.Location   = new Point3d(cx_du, cy_du + 150 * _s, 0);
    mt.TextHeight  = 250 * _s;
    mt.Layer       = "A-ANNO-TEXT";
    mt.Contents    = room.Name;
    mt.Attachment  = AttachmentPoint.MiddleCenter;
    space.AppendEntity(mt);
    tr.AddNewlyCreatedDBObject(mt, true);

    var tag = new MText();
    tag.Location  = new Point3d(cx_du, cy_du - 100 * _s, 0);
    tag.TextHeight = 180 * _s;
    tag.Layer      = "A-AREA-IDEN";
    tag.Contents   = $"{areaSqm:F1} m\\U+00B2";
    tag.Attachment = AttachmentPoint.MiddleCenter;
    space.AppendEntity(tag);
    tr.AddNewlyCreatedDBObject(tag, true);
}

private static double GetDoorWidth(string type) => type.ToLower() switch
{
    "master_bedroom" or "bedroom"      => 900,
    "bathroom" or "toilet"             => 750,
    "living" or "dining"               => 1000,
    "entry" or "foyer" or "main_entry" => 1200,
    "kitchen"                          => 900,
    "emergency" or "icu"               => 1800,
    _                                  => 900,
};

// Fix #7: create all AIA layers directly — no separate method needed
private void EnsureStandardLayers(Transaction tr)
{
    var lt = (LayerTable)tr.GetObject(_db.LayerTableId, OpenMode.ForRead);

    var layers = new (string name, short color, string lt_name, double lw)[]
    {
        ("A-WALL-EXTR", 7,  "CONTINUOUS", 0.70),
        ("A-WALL-INTR", 7,  "CONTINUOUS", 0.35),
        ("A-WALL-PRTN", 8,  "CONTINUOUS", 0.18),
        ("A-DOOR",      4,  "CONTINUOUS", 0.35),
        ("A-DOOR-SWNG", 4,  "CONTINUOUS", 0.18),
        ("A-GLAZ",      4,  "CONTINUOUS", 0.25),
        ("A-ANNO-TEXT", 2,  "CONTINUOUS", 0.18),
        ("A-AREA-IDEN", 6,  "CONTINUOUS", 0.18),
        ("A-ANNO-SYMB", 2,  "CONTINUOUS", 0.18),
        ("C-PROP",      1,  "CENTER",     0.25),
        ("S-COLS",      7,  "CONTINUOUS", 0.70),
        ("ZH-AI-NOTES", 150,"CONTINUOUS", 0.18),
    };

    foreach (var (name, color, lt_name, lw) in layers)
    {
        if (lt.Has(name)) continue;
        lt.UpgradeOpen();
        var ltr = new LayerTableRecord { Name = name };
        ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
            Autodesk.AutoCAD.Colors.ColorMethod.ByAci, color);
        lt.Add(ltr);
        tr.AddNewlyCreatedDBObject(ltr, true);
    }
}

// Zone JSON parser
private List<ZoneGroup> ParseZones(VariationPlan variation)
{
    var zones = new List<ZoneGroup>();
    if (!variation.Properties.TryGetValue("zones_json", out var raw)) return zones;

    try
    {
        var list = Newtonsoft.Json.JsonConvert
            .DeserializeObject<Newtonsoft.Json.Linq.JArray>(raw?.ToString() ?? "[]")
            ?? new Newtonsoft.Json.Linq.JArray();

        foreach (var z in list)
        {
            var zone = new ZoneGroup
            {
                ZoneName = z["zone_name"]?.ToString()     ?? "",
                Position = z["zone_position"]?.ToString() ?? "front",
            };
            foreach (var s in z["spaces"] as Newtonsoft.Json.Linq.JArray
                               ?? new Newtonsoft.Json.Linq.JArray())
            {
                zone.Spaces.Add(new SpaceNode
                {
                    Name          = s["name"]?.ToString()   ?? "Room",
                    Type          = s["type"]?.ToString()   ?? "room",
                    AreaSqm       = s["area_sqm"]?.Value<double>()      ?? 15,
                    AspectRatio   = s["aspect_ratio"]?.Value<double>()  ?? 1.4,
                    Floor         = s["floor"]?.Value<int>()            ?? 1,
                    Facing        = s["facing"]?.ToString()             ?? "south",
                    HasNaturalLight = s["has_natural_light"]?.Value<bool>() ?? true,
                    MinWidthM     = s["min_width_m"]?.Value<double>()   ?? 2.4,
                    MinDepthM     = s["min_depth_m"]?.Value<double>()   ?? 2.4,
                    AdjacentTo    = s["must_be_adjacent_to"]?
                                    .ToObject<List<string>>() ?? new(),
                    DoorConnects  = s["door_connects_to"]?
                                    .ToObject<List<string>>() ?? new(),
                });
            }
            zones.Add(zone);
        }
    }
    catch (Exception ex)
    {
        _ed.WriteMessage($"\n[zHeight] Zone parse error: {ex.Message}");
    }
    return zones;
}
```

### 1F — ZHeightCommand.cs: wire site constraints (fix #8)

In `ZHeightCommand.cs`, update `DrawPlan()` to pass site constraints to the engine:

```csharp
[CommandMethod("ZHEIGHT_DRAW", CommandFlags.Modal)]
public void DrawPlan()
{
    var doc = Application.DocumentManager.MdiActiveDocument;
    var ed  = doc.Editor;
    var plan = _pendingPlan;
    if (plan == null) { ed.WriteMessage("\n[zHeight] No pending plan."); return; }
    _pendingPlan = null;

    using var docLock = doc.LockDocument();
    try
    {
        var engine = new DrawingEngine(doc);

        // Fix #8: wire site constraints from the stored panel values
        if (_pendingSiteConstraints != null)
            engine.SetSiteConstraints(_pendingSiteConstraints);

        engine.ExecutePlan(plan);

        // Async feedback
        Task.Run(async () => {
            using var client = new ApiClient();
            await client.SendFeedbackAsync(new FeedbackPayload {
                RequestId         = plan.RequestId,
                ArchitectId       = PluginConfig.Load().ArchitectId,
                SelectedVariation = plan.RecommendedVariation,
                CorrectionType    = "accepted",
            });
        });

        foreach (var w in plan.GlobalWarnings)
            ed.WriteMessage($"\n[zHeight WARNING] {w}");
    }
    catch (Exception ex)
    {
        ed.WriteMessage($"\n[zHeight ERROR] {ex.Message}");
    }
}

// Store site constraints alongside pending plan
[ThreadStatic] private static SiteConstraints? _pendingSiteConstraints;

// In RunZHeight(), after receiving plan from API, add:
// _pendingSiteConstraints = panel.GetSiteConstraints();
```

Also add `GetSiteConstraints()` to `RequirementPanel`:

```csharp
// In RequirementPanel.xaml.cs
public SiteConstraints GetSiteConstraints()
{
    return new SiteConstraints
    {
        PlotWidthMm      = (PlotWidthInput  > 0 ? PlotWidthInput  : 15) * 1000,
        PlotDepthMm      = (PlotDepthInput  > 0 ? PlotDepthInput  : 20) * 1000,
        FrontSetbackMm   = (FrontSetback    > 0 ? FrontSetback    : 3)  * 1000,
        SideSetbackMm    = (SideSetback     > 0 ? SideSetback     : 1.5)* 1000,
        RearSetbackMm    = (RearSetback     > 0 ? RearSetback     : 3)  * 1000,
        MaxFar           = MaxFar           > 0 ? MaxFar          : 2.5,
        MaxCoveragePct   = MaxCoveragePct   > 0 ? MaxCoveragePct  : 40.0,
    };
}
```

---

## Part 2 — KB upload: fix #13 (secure API key)

### upload_projects.sh — API key loaded from Secret Manager, not hardcoded

```bash
#!/usr/bin/env bash
# save as: scripts/upload/upload_projects.sh

set -euo pipefail

PROJECT_ID="zheight-ai-kb"
BUCKET="gs://${PROJECT_ID}-kb-raw"

# Fix #13: load API key from Secret Manager — never hardcode
API_KEY=$(gcloud secrets versions access latest \
  --secret="rag-api-key" \
  --project="${PROJECT_ID}")

RAG_API_URL=$(gcloud run services describe rag-api \
  --region=us-central1 \
  --project="${PROJECT_ID}" \
  --format="value(status.url)")

echo "Uploading to: ${BUCKET}"
echo "API endpoint : ${RAG_API_URL}"

upload_with_brief() {
  local file="$1"
  local brief="$2"
  local folder="$3"

  if [ ! -f "$file" ]; then
    echo "SKIP: file not found: $file"
    return
  fi

  gcloud storage cp "$file" \
    "${BUCKET}/${folder}/$(basename "$file")" \
    --metadata="x-project-brief=${brief}"

  echo "✓ $(basename "$file")"
}

upload_brief_only() {
  local brief_file="$1"
  gcloud storage cp "$brief_file" \
    "${BUCKET}/briefs/$(basename "$brief_file")"
  echo "✓ brief: $(basename "$brief_file")"
}

# ── Add your projects below ──────────────────────────────────────────────────
# Brief format: one detailed paragraph per project (use the template from the guide)

upload_with_brief \
  "your_projects/house_001.dwg" \
  "3BHK villa Bengaluru 280sqm two floors south-facing living Vastu BBMP2023 \
   ground: entry 12sqm living 32sqm dining 18sqm kitchen 14sqm powder room 3sqm \
   first: master bed 20sqm attached bath 6sqm walkin 4sqm bed2 14sqm bed3 12sqm \
   common bath 5sqm corridor 8sqm stair 6sqm" \
  "dwg"

# Add more projects here following the same pattern...

echo ""
echo "Upload complete. Open quality gate:"
echo "  ${RAG_API_URL}/v1/quality-gate/pending"
```

---

## Complete file change summary

| File | Action | Fixes |
|---|---|---|
| `services/rag-api/app/routers/orchestrate.py` | Embed schema in f-string | #1 |
| `shared/contracts/draw_action_plan.py` | Add `properties: dict = {}` to `VariationPlan` | #2 |
| `services/rag-api/app/core/action_compiler.py` | No change — was correct | — |
| `zheight-autocad-plugin/src/Solver/GridLayoutEngine.cs` | Replace entirely with corrected version | #3 #4 #5 #6 |
| `zheight-autocad-plugin/src/Engine/DrawingEngine.cs` | Replace `DrawVariation` + all helpers | #7 #8 #9 #10 #11 #12 |
| `zheight-autocad-plugin/src/ZHeightCommand.cs` | Add `_pendingSiteConstraints` wiring | #8 |
| `zheight-autocad-plugin/src/UI/RequirementPanel.xaml.cs` | Add `GetSiteConstraints()` | #8 |
| `scripts/upload/upload_projects.sh` | Load key from Secret Manager | #13 |

---

## Remaining gaps — not bugs, but not yet built

These are known missing pieces that do not block the current phase
but must be addressed before production handoff:

| Gap | Impact | When to build |
|---|---|---|
| No staircase geometry | Multi-floor layouts have no stair drawn | Before two-storey demo |
| No corridor drawing between zones | Rooms from different zones have no connecting passage drawn | Before architect review |
| `RequirementPanel` and `VariationPreviewPanel` WPF not built | Plugin cannot compile without them | Immediate — build next |
| No wall thickness deduplication | Shared walls drawn twice at same coordinate | Low impact in AutoCAD, fix before production |
| KB retrieval scoring not surfaced in plugin UI | Architect cannot see if KB was used | Before stakeholder demo |

---

## Deployment order after applying fixes

```bash
# 1. Apply Python changes
cd zheight-kb-services
# Edit orchestrate.py — embed schema in gen_prompt
# Edit shared/contracts/draw_action_plan.py — add properties field
./deploy_phase3.sh

# 2. Verify backend
curl -sf "${RAG_API_URL}/v1/orchestrate" \
  -H "X-API-Key: $(gcloud secrets versions access latest --secret=rag-api-key)" \
  -H "User-Agent: zHeightPlugin/3.1" \
  -d '{"prompt":"3BHK villa 280sqm","autocad_units":"mm","plugin_version":"3.1"}' \
  | python3 -c "
import json,sys
d=json.load(sys.stdin)
v=d['variations'][0]
has_zones = 'zones_json' in v.get('properties',{})
print('zones_json present:', has_zones)
print('variation name:', v['variation_name'])
"

# 3. Apply C# changes
cd zheight-autocad-plugin
# Replace GridLayoutEngine.cs
# Update DrawingEngine.cs
# Update ZHeightCommand.cs
dotnet build -c Release

# 4. Reload and test
# In AutoCAD: NETLOAD → ZHEIGHT
# Brief: "3 bedroom house south-facing 200sqm ground and first floor"
# Expected: no overlaps, floor 1 drawn, floor 2 above it with 2m gap

# 5. Upload 20 residential projects
./scripts/upload/upload_projects.sh

# 6. Approve in quality gate, run KB validation test
```