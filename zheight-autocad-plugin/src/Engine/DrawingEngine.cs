// DrawingEngine.cs — corrected edition
// Bug fixes applied (see Phase3 layoutfix.md):
//   #7  EnsureStandardLayers() replaces missing DrawLayersFromActions()
//   #8  _siteConstraints field wired via SetSiteConstraints() — no null ref
//   #9  DrawDoorOnRoom: position computed in mm, scaled ONCE to drawing units
//   #10 DrawWindowOnRoom: same single-scale fix
//   #11 Multi-floor: each floor drawn offset above previous by plotDepth + gap
//   #12 Outer perimeter wall drawn on A-WALL-EXTR before room interiors

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;
using Newtonsoft.Json.Linq;
using zHeight.Plugin.Models;
using zHeight.Plugin.Solver;

namespace zHeight.Plugin.Engine
{
    public class DrawingEngine
    {
        private readonly Document _doc;
        private readonly Database _db;
        private readonly Editor   _ed;
        private readonly double   _s;   // mm-to-drawing-unit scale factor

        // Fix #8: instance field wired from ZHeightCommand via SetSiteConstraints()
        private SiteConstraints _siteConstraints = new SiteConstraints();

        private const double VariationGapMm     = 12000;
        private const double FLOOR_SEPARATION_MM = 2000; // gap between floor plans (fix #11)

        // Space types that are outdoor/site elements — drawn as open space, not rooms
        private static readonly HashSet<string> OutdoorTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "backyard","back_yard","front_yard","yard","garden","landscape",
                "outdoor","outdoor_space","patio","deck","courtyard_outdoor",
                "driveway","parking","open_space","greenspace","lawn"
            };

        public DrawingEngine(Document doc)
        {
            _doc = doc;
            _db  = doc.Database;
            _ed  = doc.Editor;
            _s   = GetMmScale(_db);

            _ed.WriteMessage($"\n[zHeight] Drawing unit scale: 1mm = {_s} drawing units");
        }

        // Fix #8: called by ZHeightCommand before ExecutePlan
        public void SetSiteConstraints(SiteConstraints site) =>
            _siteConstraints = site;

        public void ExecutePlan(DrawActionPlan plan)
        {
            _ed.WriteMessage(
                $"\n[zHeight] Executing: {plan.ProjectDescription} " +
                $"({plan.Variations.Count} variations)");

            var offsets = CalculateOffsets(plan.Variations);

            try
            {
                foreach (var variation in plan.Variations)
                {
                    _ed.WriteMessage(
                        $"\n[zHeight] Drawing V{variation.VariationId}: {variation.VariationName}");

                    var offset = offsets.GetValueOrDefault(variation.VariationId);
                    DrawVariation(variation, offset);
                }

                // Update extents so ZOOM E finds entities, then zoom
                _db.UpdateExt(false);
                _ed.WriteMessage("\n[zHeight] Done — zooming to layout...");
                _doc.SendStringToExecute("_.ZOOM _E \n_.REGEN\n", true, false, false);
            }
            catch (System.Exception ex)
            {
                _ed.WriteMessage($"\n[zHeight ERROR] Drawing failed: {ex.Message}");
                throw;
            }
        }

        private Dictionary<int, Point3d> CalculateOffsets(List<VariationPlan> variations)
        {
            var offsets = new Dictionary<int, Point3d>();
            double x = 0;
            foreach (var v in variations)
            {
                offsets[v.VariationId] = new Point3d(x, 0, 0);
                // Use plot width (known from site constraints) as the variation slot width.
                // Fallback: area-based estimate, never smaller than 12m.
                double estWidthMm = Math.Max(
                    _siteConstraints.PlotWidthMm,
                    Math.Sqrt(Math.Max(v.TotalAreaSqm, 50)) * 1200);
                x += (estWidthMm + VariationGapMm) * _s;
            }
            return offsets;
        }

        private void DrawVariation(VariationPlan variation, Point3d offset)
        {
            LayoutResult? layout = null;
            if (variation.Properties?.ContainsKey("zones_json") == true)
            {
                try
                {
                    // Override site constraints if backend sent lot dimensions
                    if (variation.Properties.TryGetValue("site_constraints", out var scRaw))
                    {
                        try
                        {
                            var sc = JObject.Parse(scRaw?.ToString() ?? "{}");
                            _siteConstraints = new SiteConstraints
                            {
                                PlotWidthMm  = sc["plot_width_mm"]?.Value<double>()  ?? _siteConstraints.PlotWidthMm,
                                PlotDepthMm  = sc["plot_depth_mm"]?.Value<double>()  ?? _siteConstraints.PlotDepthMm,
                                FrontSetback = sc["front_setback_mm"]?.Value<double>() ?? _siteConstraints.FrontSetback,
                                SideSetback  = sc["side_setback_mm"]?.Value<double>()  ?? _siteConstraints.SideSetback,
                                RearSetback  = sc["rear_setback_mm"]?.Value<double>()  ?? _siteConstraints.RearSetback,
                            };
                            _ed.WriteMessage(
                                $"\n[zHeight] Site: {_siteConstraints.PlotWidthMm/1000:F1}m × " +
                                $"{_siteConstraints.PlotDepthMm/1000:F1}m lot");
                        }
                        catch { /* use default constraints */ }
                    }

                    var zones = ParseZones(variation);
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
                        _siteConstraints.FrontSetback,
                        _siteConstraints.SideSetback,
                        _siteConstraints.RearSetback,
                        gridM * 1000);

                    foreach (var w in layout.Warnings)
                        _ed.WriteMessage($"\n[zHeight LAYOUT] {w}");

                    _ed.WriteMessage(
                        $"\n[zHeight] GridLayoutEngine: {layout.Rooms.Count} rooms placed, " +
                        $"building {layout.BuildingW/1000:F1}m × {layout.BuildingD/1000:F1}m, " +
                        $"lot {_siteConstraints.PlotWidthMm/1000:F1}m × {_siteConstraints.PlotDepthMm/1000:F1}m");
                }
                catch (Exception ex)
                {
                    _ed.WriteMessage($"\n[zHeight] Layout engine failed: {ex.Message} — fallback");
                    layout = null;
                }
            }

            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                var space = (BlockTableRecord)tr.GetObject(
                    _db.CurrentSpaceId, OpenMode.ForWrite);

                EnsureStandardLayers(tr);

                if (layout != null && layout.Rooms.Any())
                {
                    // ── Site plan: full lot boundary + setback zone ──────────────
                    try { DrawLotBoundary(layout, offset, space, tr); }
                    catch (Exception ex) { _ed.WriteMessage($"\n[zHeight] LotBoundary skipped: {ex.Message}"); }
                    try { DrawEntryWalkway(layout, offset, space, tr); }
                    catch (Exception ex) { _ed.WriteMessage($"\n[zHeight] Walkway skipped: {ex.Message}"); }

                    // ── Floor plans — one per floor, offset vertically ───────────
                    // Filter outdoor spaces — they are shown as open site area, not rooms
                    var indoorRooms = layout.Rooms
                        .Where(r => !OutdoorTypes.Contains(r.Type.ToLower()))
                        .ToList();

                    var byFloor = indoorRooms
                        .GroupBy(r => r.Floor)
                        .OrderBy(g => g.Key);

                    double totalSqm = variation.TotalAreaSqm > 0
                        ? variation.TotalAreaSqm
                        : indoorRooms.Sum(r => r.AreaSqm > 0 ? r.AreaSqm
                            : r.WidthMm * r.DepthMm / 1_000_000.0);

                    foreach (var floorGroup in byFloor)
                    {
                        int    floor     = floorGroup.Key;
                        double floorOffY = (floor - 1) *
                            (_siteConstraints.PlotDepthMm + FLOOR_SEPARATION_MM) * _s;
                        var floorOffset  = new Point3d(offset.X, offset.Y + floorOffY, 0);

                        DrawPerimeterWall(layout, floorOffset, space, tr);
                        try { DrawBuildingDimensions(layout, floorOffset, space, tr); }
                        catch (Exception ex) { _ed.WriteMessage($"\n[zHeight] BuildingDims skipped: {ex.Message}"); }
                        try { DrawFloorLabel(floor, totalSqm, floorOffset, space, tr); }
                        catch (Exception ex) { _ed.WriteMessage($"\n[zHeight] FloorLabel skipped: {ex.Message}"); }

                        var wallKeys = new HashSet<string>();
                        var wallSegs = new List<(double x1, double y1, double x2, double y2)>();
                        int roomsDrawn = 0;

                        foreach (var room in floorGroup)
                        {
                            try
                            {
                                bool isCorridor = room.Type.Equals(
                                    "corridor", StringComparison.OrdinalIgnoreCase);

                                DrawRoomHatch(room, floorOffset, space, tr);

                                AddWallSeg(wallKeys, wallSegs, room.X,     room.Y,   room.Right, room.Y);
                                AddWallSeg(wallKeys, wallSegs, room.Right, room.Y,   room.Right, room.Top);
                                AddWallSeg(wallKeys, wallSegs, room.Right, room.Top, room.X,     room.Top);
                                AddWallSeg(wallKeys, wallSegs, room.X,     room.Top, room.X,     room.Y);

                                if (!isCorridor)
                                    try { DrawDoorOnRoom(room, floorOffset, space, tr); }
                                    catch { /* door failure never blocks room */ }

                                string rt = (room.Type ?? "").ToLowerInvariant()
                                    .Replace('-','_').Replace(' ','_');
                                bool isEnclair = IsEnclaireType(rt);
                                bool noWin = isCorridor || isEnclair || new HashSet<string>
                                {
                                    "toilet","bathroom","bath","corridor","store",
                                    "server_room","utility","mechanical","laundry",
                                    "laundry_room","closet","wic","walk_in_closet",
                                    "powder_room","half_bath","primary_bath",
                                    "ensuite_bath","ensuite","master_bath",
                                    "mudroom","mud_room","pantry","storage","garage"
                                }.Contains(rt);

                                // Enclair: draw glass walls on all 4 sides instead of regular door+window
                                if (isEnclair)
                                    try { DrawEnclaireGlassWalls(room, floorOffset, space, tr); }
                                    catch { /* non-fatal */ }
                                else if (!isCorridor)
                                    { /* door already drawn above */ }

                                if (room.HasNaturalLight && !noWin)
                                    try { DrawWindowOnRoom(room, floorOffset, space, tr); }
                                    catch { /* window failure never blocks room */ }

                                try { DrawRoomLabel(room, floorOffset, space, tr); }
                                catch { /* label failure never blocks room */ }

                                roomsDrawn++;
                            }
                            catch (Exception ex)
                            {
                                _ed.WriteMessage(
                                    $"\n[zHeight WARN] Room '{room.Name}' skipped: {ex.Message}");
                            }
                        }

                        // Draw deduped wall segments
                        foreach (var (x1, y1, x2, y2) in wallSegs)
                        {
                            try { DrawWallSegment(x1, y1, x2, y2, "A-WALL-INTR", floorOffset, space, tr); }
                            catch { /* wall seg failure is non-fatal */ }
                        }

                        _ed.WriteMessage(
                            $"\n[zHeight] Floor {floor}: {roomsDrawn} rooms drawn, " +
                            $"{wallSegs.Count} wall segments");

                        // ── Suite-connecting doors + foyer→hallway door ──────────────
                        try { DrawInteriorConnectingDoors(floorGroup.ToList(), floorOffset, space, tr); }
                        catch (Exception ex) { _ed.WriteMessage($"\n[zHeight] ConnectingDoors skipped: {ex.Message}"); }

                        // ── Continuous dimension chains (room-by-room) ───────────────
                        try
                        {
                            double chainYmm = layout.BuildingY - 1200;
                            DrawHorizDimChain(floorGroup.ToList(), chainYmm, floorOffset, space, tr);
                            double chainXmm = layout.BuildingX + layout.BuildingW + 1200;
                            DrawVertDimChain(floorGroup.ToList(), chainXmm, floorOffset, space, tr);
                        }
                        catch (Exception ex) { _ed.WriteMessage($"\n[zHeight] DimChain skipped: {ex.Message}"); }
                    }
                }
                else
                {
                    foreach (var action in variation.Actions)
                    {
                        try { ExecuteAction(action, offset, space, tr); }
                        catch (Exception ex)
                        {
                            _ed.WriteMessage($"\n[zHeight WARN] {action.ActionType}: {ex.Message}");
                        }
                    }
                }

                tr.Commit();
            }
            catch
            {
                tr.Abort();
                throw;
            }
        }

        // ── GridLayoutEngine room drawing ─────────────────────────────────────

        // Site plan: full lot boundary (dashed C-PROP) + front setback line + backyard label
        private void DrawLotBoundary(LayoutResult layout, Point3d offset,
                                      BlockTableRecord space, Transaction tr)
        {
            double pw = _siteConstraints.PlotWidthMm  * _s;
            double pd = _siteConstraints.PlotDepthMm  * _s;
            double fs = _siteConstraints.FrontSetback * _s;
            double ss = _siteConstraints.SideSetback  * _s;

            // Outer lot boundary
            var lot = new Polyline();
            lot.AddVertexAt(0, new Point2d(offset.X,      offset.Y),      0, 0, 0);
            lot.AddVertexAt(1, new Point2d(offset.X + pw, offset.Y),      0, 0, 0);
            lot.AddVertexAt(2, new Point2d(offset.X + pw, offset.Y + pd), 0, 0, 0);
            lot.AddVertexAt(3, new Point2d(offset.X,      offset.Y + pd), 0, 0, 0);
            lot.Closed        = true;
            lot.ConstantWidth = 0;
            lot.Layer         = "C-PROP";
            space.AppendEntity(lot);
            tr.AddNewlyCreatedDBObject(lot, true);

            double rs = _siteConstraints.RearSetback * _s;

            // Front setback dashed indicator line
            var fsLine = new Line(
                new Point3d(offset.X,      offset.Y + fs, 0),
                new Point3d(offset.X + pw, offset.Y + fs, 0));
            fsLine.Layer = "C-PROP";
            space.AppendEntity(fsLine);
            tr.AddNewlyCreatedDBObject(fsLine, true);

            // Rear setback line
            var rsLine = new Line(
                new Point3d(offset.X,      offset.Y + pd - rs, 0),
                new Point3d(offset.X + pw, offset.Y + pd - rs, 0));
            rsLine.Layer = "C-PROP";
            space.AppendEntity(rsLine);
            tr.AddNewlyCreatedDBObject(rsLine, true);

            // Left side setback line
            var lsLine = new Line(
                new Point3d(offset.X + ss, offset.Y,      0),
                new Point3d(offset.X + ss, offset.Y + pd, 0));
            lsLine.Layer = "C-PROP";
            space.AppendEntity(lsLine);
            tr.AddNewlyCreatedDBObject(lsLine, true);

            // Right side setback line
            var rsrLine = new Line(
                new Point3d(offset.X + pw - ss, offset.Y,      0),
                new Point3d(offset.X + pw - ss, offset.Y + pd, 0));
            rsrLine.Layer = "C-PROP";
            space.AppendEntity(rsrLine);
            tr.AddNewlyCreatedDBObject(rsrLine, true);

            // Buildable area rectangle (inner dashed boundary)
            var buildable = new Polyline();
            buildable.AddVertexAt(0, new Point2d(offset.X + ss,      offset.Y + fs),      0, 0, 0);
            buildable.AddVertexAt(1, new Point2d(offset.X + pw - ss, offset.Y + fs),      0, 0, 0);
            buildable.AddVertexAt(2, new Point2d(offset.X + pw - ss, offset.Y + pd - rs), 0, 0, 0);
            buildable.AddVertexAt(3, new Point2d(offset.X + ss,      offset.Y + pd - rs), 0, 0, 0);
            buildable.Closed = true;
            buildable.Layer  = "C-PROP";
            space.AppendEntity(buildable);
            tr.AddNewlyCreatedDBObject(buildable, true);

            // "STREET" label below lot boundary
            var streetLabel = new DBText();
            streetLabel.Position   = new Point3d(offset.X + pw / 2 - 1500 * _s,
                                                  offset.Y - 1500 * _s, 0);
            streetLabel.TextString = "STREET";
            streetLabel.Height     = 400 * _s;
            streetLabel.Layer      = "C-PROP";
            space.AppendEntity(streetLabel);
            tr.AddNewlyCreatedDBObject(streetLabel, true);

            // "MAXIMIZED BACKYARD" label in the open space above the building footprint
            double buildTopY = (layout.BuildingY + layout.BuildingD) * _s + offset.Y;
            double byardMid  = buildTopY + (offset.Y + pd - buildTopY) / 2;
            if (byardMid > buildTopY + 600 * _s)
            {
                double bh = 700 * _s;
                var byardLabel = new DBText();
                byardLabel.Position   = new Point3d(
                    offset.X + pw / 2 - 9 * bh * 0.5,  // approximate center for 18-char string
                    byardMid, 0);
                byardLabel.TextString = "MAXIMIZED BACKYARD";
                byardLabel.Height     = bh;
                byardLabel.Layer      = "C-PROP";
                space.AppendEntity(byardLabel);
                tr.AddNewlyCreatedDBObject(byardLabel, true);
            }

            // Lot depth annotation — right side with "126'-0"" style
            double lotGap  = 1200 * _s;
            double lotTick = 300 * _s;
            DrawDimLine(offset.X + pw + lotGap, offset.Y,
                        offset.X + pw + lotGap, offset.Y + pd,
                        lotTick, FtIn(_siteConstraints.PlotDepthMm), true, space, tr);
            // Lot width annotation — top of lot
            DrawDimLine(offset.X, offset.Y + pd + lotGap,
                        offset.X + pw, offset.Y + pd + lotGap,
                        lotTick, FtIn(_siteConstraints.PlotWidthMm), false, space, tr);
        }

        // Building perimeter wall — sized to actual building footprint (not full buildable area)
        private void DrawPerimeterWall(LayoutResult layout, Point3d offset,
                                        BlockTableRecord space, Transaction tr)
        {
            double bx = layout.BuildingX * _s + offset.X;
            double by = layout.BuildingY * _s + offset.Y;
            double bw = layout.BuildingW * _s;
            double bd = layout.BuildingD * _s;

            var pl = new Polyline();
            pl.AddVertexAt(0, new Point2d(bx,      by),      0, 0, 0);
            pl.AddVertexAt(1, new Point2d(bx + bw, by),      0, 0, 0);
            pl.AddVertexAt(2, new Point2d(bx + bw, by + bd), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(bx,      by + bd), 0, 0, 0);
            pl.Closed        = true;
            pl.ConstantWidth = 230 * _s;
            pl.Layer         = "A-WALL-EXTR";
            space.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
        }

        private void DrawFloorLabel(int floor, double totalAreaSqm, Point3d offset,
                                     BlockTableRecord space, Transaction tr)
        {
            // Position above the lot boundary (prominent title)
            double pw     = _siteConstraints.PlotWidthMm * _s;
            double pd     = _siteConstraints.PlotDepthMm * _s;
            double labelX = offset.X + pw / 2;
            double labelY = offset.Y + pd + 2400 * _s;

            // Format: "FLOOR PLAN – 1,200 SF"
            int sf = (int)Math.Round(totalAreaSqm * 10.7639);
            string sfStr = sf.ToString("N0"); // e.g. "1,200"
            string floorTitle = floor == 1
                ? $"FLOOR PLAN – {sfStr} SF"
                : $"FLOOR {floor} PLAN – {sfStr} SF";

            double th = 500 * _s;
            var titleTxt = new DBText();
            titleTxt.Position   = new Point3d(labelX - floorTitle.Length * th * 0.28, labelY, 0);
            titleTxt.TextString = floorTitle;
            titleTxt.Height     = th;
            titleTxt.Layer      = "A-ANNO-TTLB";
            space.AppendEntity(titleTxt);
            tr.AddNewlyCreatedDBObject(titleTxt, true);
        }

        private static void AddWallSeg(
            HashSet<string> keys, List<(double, double, double, double)> segs,
            double x1, double y1, double x2, double y2)
        {
            // Round to 10 mm to absorb floating-point jitter from grid snapping
            static long R(double v) => (long)Math.Round(v / 10.0) * 10;
            var (ax1, ay1, ax2, ay2) = (R(x1), R(y1), R(x2), R(y2));
            string key = ax1 < ax2 || (ax1 == ax2 && ay1 <= ay2)
                ? $"{ax1},{ay1}|{ax2},{ay2}"
                : $"{ax2},{ay2}|{ax1},{ay1}";
            if (keys.Add(key))
                segs.Add((x1, y1, x2, y2));
        }

        private void DrawWallSegment(double x1, double y1, double x2, double y2,
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

        // Fix #9: all door geometry calculated in mm, scaled ONCE at Point3d
        private void DrawDoorOnRoom(SpaceNode room, Point3d offset,
                                     BlockTableRecord space, Transaction tr)
        {
            double doorW_mm = GetDoorWidth(room.Type);
            double hw_mm    = room.WidthMm / 2;
            double hd_mm    = room.DepthMm / 2;

            double px_mm, py_mm;
            switch (room.Facing)
            {
                case "north":
                    px_mm = room.X + hw_mm - doorW_mm / 2;
                    py_mm = room.Top;
                    break;
                case "east":
                    px_mm = room.Right;
                    py_mm = room.Y + hd_mm - doorW_mm / 2;
                    break;
                case "west":
                    px_mm = room.X;
                    py_mm = room.Y + hd_mm - doorW_mm / 2;
                    break;
                default: // south
                    px_mm = room.X + hw_mm - doorW_mm / 2;
                    py_mm = room.Y;
                    break;
            }

            var    pos      = new Point3d(px_mm * _s + offset.X, py_mm * _s + offset.Y, 0);
            double doorW_du = doorW_mm * _s;

            // Door leaf and arc direction based on wall facing
            Line leaf; Arc arc;
            switch (room.Facing)
            {
                case "north":
                    leaf = new Line(pos, new Point3d(pos.X + doorW_du, pos.Y, 0));
                    arc  = new Arc(pos, doorW_du, 0, -Math.PI / 2);
                    break;
                case "east":
                    leaf = new Line(pos, new Point3d(pos.X, pos.Y + doorW_du, 0));
                    arc  = new Arc(pos, doorW_du, Math.PI / 2, Math.PI);
                    break;
                case "west":
                    leaf = new Line(pos, new Point3d(pos.X, pos.Y + doorW_du, 0));
                    arc  = new Arc(pos, doorW_du, 0, Math.PI / 2);
                    break;
                default: // south
                    leaf = new Line(pos, new Point3d(pos.X + doorW_du, pos.Y, 0));
                    arc  = new Arc(pos, doorW_du, 0, Math.PI / 2);
                    break;
            }
            leaf.Layer = "A-DOOR";
            space.AppendEntity(leaf);
            tr.AddNewlyCreatedDBObject(leaf, true);
            arc.Layer = "A-DOOR-SWNG";
            space.AppendEntity(arc);
            tr.AddNewlyCreatedDBObject(arc, true);
        }

        private void DrawWindowOnRoom(SpaceNode room, Point3d offset,
                                       BlockTableRecord space, Transaction tr)
        {
            double winW_mm = Math.Min(Math.Max(room.WidthMm, room.DepthMm) * 0.5, 1800);
            double cx_mm   = room.X + room.WidthMm / 2;
            double cy_mm   = room.Y + room.DepthMm / 2;
            bool   horiz   = room.Facing is "south" or "north";

            double wx_mm, wy_mm;
            switch (room.Facing)
            {
                case "north": wx_mm = cx_mm - winW_mm / 2; wy_mm = room.Top;   break;
                case "east":  winW_mm = Math.Min(room.DepthMm * 0.5, 1800);
                              wx_mm = room.Right;           wy_mm = cy_mm - winW_mm / 2; break;
                case "west":  winW_mm = Math.Min(room.DepthMm * 0.5, 1800);
                              wx_mm = room.X;               wy_mm = cy_mm - winW_mm / 2; break;
                default:      wx_mm = cx_mm - winW_mm / 2; wy_mm = room.Y;     break; // south
            }

            for (int i = 0; i < 3; i++)
            {
                double off_du = i * 50 * _s;
                Line line;
                if (horiz)
                {
                    // Horizontal wall — lines run along X axis
                    line = new Line(
                        new Point3d(wx_mm * _s + offset.X,              wy_mm * _s + offset.Y + off_du, 0),
                        new Point3d((wx_mm + winW_mm) * _s + offset.X,  wy_mm * _s + offset.Y + off_du, 0));
                }
                else
                {
                    // Vertical wall (east/west) — lines run along Y axis
                    line = new Line(
                        new Point3d(wx_mm * _s + offset.X + off_du, wy_mm * _s + offset.Y,              0),
                        new Point3d(wx_mm * _s + offset.X + off_du, (wy_mm + winW_mm) * _s + offset.Y,  0));
                }
                line.Layer = "A-GLAZ";
                space.AppendEntity(line);
                tr.AddNewlyCreatedDBObject(line, true);
            }
        }

        private void DrawRoomLabel(SpaceNode room, Point3d offset,
                                    BlockTableRecord space, Transaction tr)
        {
            double cx_du = (room.X + room.WidthMm / 2) * _s + offset.X;
            double cy_du = (room.Y + room.DepthMm / 2) * _s + offset.Y;
            double h     = 220 * _s;
            string dimStr = $"{FtIn(room.WidthMm)} x {FtIn(room.DepthMm)}";

            // Room name — centred by offsetting left by half estimated text width
            var name = new DBText();
            name.Position   = new Point3d(cx_du - room.Name.Length * h * 0.3, cy_du + h * 0.6, 0);
            name.TextString = room.Name.ToUpper();
            name.Height     = h;
            name.Layer      = "A-ANNO-TEXT";
            space.AppendEntity(name);
            tr.AddNewlyCreatedDBObject(name, true);

            // Dimension line below name
            var dims = new DBText();
            dims.Position   = new Point3d(cx_du - dimStr.Length * h * 0.25, cy_du - h * 0.6, 0);
            dims.TextString = dimStr;
            dims.Height     = h * 0.75;
            dims.Layer      = "A-ANNO-TEXT";
            space.AppendEntity(dims);
            tr.AddNewlyCreatedDBObject(dims, true);
        }

        // Convert mm to US architectural feet-inches (rounded to nearest 6 inches)
        private static string FtIn(double mm)
        {
            double totalIn = mm / 25.4;
            int ft = (int)(totalIn / 12);
            double rem = totalIn - ft * 12;
            int sixths = (int)Math.Round(rem / 6.0) * 6;
            if (sixths >= 12) { ft++; sixths = 0; }
            return sixths == 0 ? $"{ft}'-0\"" : $"{ft}'-{sixths}\"";
        }

        // Hatch fill — pattern chosen by room type for architectural readability
        private void DrawRoomHatch(SpaceNode room, Point3d offset,
                                    BlockTableRecord space, Transaction tr)
        {
            string rt = (room.Type ?? "").ToLowerInvariant().Replace('-','_').Replace(' ','_');

            // No hatch for open/circulation spaces and enclair (glass rooms) — leave white
            if (rt is "corridor" or "hallway" or "open_plan" or "living" or "dining"
                    or "great_room" or "family_room" or "living_room" or "dining_room") return;
            if (IsEnclaireType(rt)) return;

            // Choose pattern by room type
            string pattern;
            double scale;
            double angle;
            switch (rt)
            {
                case "bathroom" or "bath" or "primary_bath" or "ensuite_bath" or "ensuite"
                  or "master_bath" or "secondary_bath" or "shared_bath" or "full_bath"
                  or "powder_room" or "half_bath" or "toilet" or "kitchen":
                    pattern = "NET";    scale = 20.0 * Math.Max(_s, 1.0); angle = 0;           break;
                case "garage" or "utility" or "mechanical" or "laundry" or "laundry_room"
                  or "mudroom" or "mud_room" or "storage" or "pantry":
                    pattern = "ANSI37"; scale = 50.0 * Math.Max(_s, 1.0); angle = 0;           break;
                default: // bedrooms, entry, closets, etc.
                    pattern = "ANSI31"; scale = 75.0 * Math.Max(_s, 1.0); angle = Math.PI / 4; break;
            }

            try
            {
                // AutoCAD .NET hatch rule: add hatch to DB FIRST, then set pattern,
                // then add boundary, then AppendLoop — any other order risks eNotInDatabase.
                var hatch = new Hatch();
                space.AppendEntity(hatch);
                tr.AddNewlyCreatedDBObject(hatch, true);

                hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
                hatch.PatternScale = scale;
                hatch.PatternAngle = angle;
                hatch.Layer        = "A-WALL-PATT";
                hatch.Associative  = false; // non-associative is more reliable cross-version

                var boundary = new Polyline();
                boundary.AddVertexAt(0, new Point2d(room.X     * _s + offset.X, room.Y   * _s + offset.Y), 0, 0, 0);
                boundary.AddVertexAt(1, new Point2d(room.Right * _s + offset.X, room.Y   * _s + offset.Y), 0, 0, 0);
                boundary.AddVertexAt(2, new Point2d(room.Right * _s + offset.X, room.Top * _s + offset.Y), 0, 0, 0);
                boundary.AddVertexAt(3, new Point2d(room.X     * _s + offset.X, room.Top * _s + offset.Y), 0, 0, 0);
                boundary.Closed = true;
                boundary.Layer  = "A-WALL-PATT";
                space.AppendEntity(boundary);
                tr.AddNewlyCreatedDBObject(boundary, true);

                hatch.AppendLoop(HatchLoopTypes.Default,
                                 new ObjectIdCollection { boundary.ObjectId });
                hatch.EvaluateHatch(true);
            }
            catch (Exception ex)
            {
                // Hatch is optional visual fill — never let it crash the drawing
                _ed.WriteMessage($"\n[zHeight] Hatch skipped for {room.Name}: {ex.Message}");
            }
        }

        private static bool IsEnclaireType(string rt) =>
            rt is "enclair" or "covered_porch" or "sunroom" or "sun_room" or "lanai"
              or "veranda" or "covered_outdoor_room" or "conservatory"
              or "screened_porch" or "florida_room";

        // Enclair: draw all 4 walls as a glazing triple-line (A-GLAZ) to suggest glass/sliding walls
        private void DrawEnclaireGlassWalls(SpaceNode room, Point3d offset,
            BlockTableRecord space, Transaction tr)
        {
            double x1 = room.X     * _s + offset.X;
            double y1 = room.Y     * _s + offset.Y;
            double x2 = room.Right * _s + offset.X;
            double y2 = room.Top   * _s + offset.Y;

            // Three parallel lines on each of the 4 walls to suggest glazing
            for (int i = 0; i < 3; i++)
            {
                double offDu = i * 60 * _s;
                // Bottom wall
                var b = new Line(new Point3d(x1, y1 + offDu, 0), new Point3d(x2, y1 + offDu, 0));
                b.Layer = "A-GLAZ"; space.AppendEntity(b); tr.AddNewlyCreatedDBObject(b, true);
                // Top wall (rear — main glass wall facing backyard)
                var t = new Line(new Point3d(x1, y2 - offDu, 0), new Point3d(x2, y2 - offDu, 0));
                t.Layer = "A-GLAZ"; space.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
                // Left wall
                var l = new Line(new Point3d(x1 + offDu, y1, 0), new Point3d(x1 + offDu, y2, 0));
                l.Layer = "A-GLAZ"; space.AppendEntity(l); tr.AddNewlyCreatedDBObject(l, true);
                // Right wall
                var r = new Line(new Point3d(x2 - offDu, y1, 0), new Point3d(x2 - offDu, y2, 0));
                r.Layer = "A-GLAZ"; space.AppendEntity(r); tr.AddNewlyCreatedDBObject(r, true);
            }

            // Sliding door symbol: a double arrow line at the bottom of the enclair
            double sdW  = Math.Min(room.WidthMm * 0.5, 2400) * _s;
            double sdMx = (x1 + x2) / 2;
            var sd = new Line(new Point3d(sdMx - sdW / 2, y1, 0), new Point3d(sdMx + sdW / 2, y1, 0));
            sd.Layer = "A-DOOR"; space.AppendEntity(sd); tr.AddNewlyCreatedDBObject(sd, true);
        }

        // Interior connecting doors: bedroom↔bath suite connections + foyer↔hallway
        private void DrawInteriorConnectingDoors(IList<SpaceNode> rooms,
            Point3d offset, BlockTableRecord space, Transaction tr)
        {
            var bedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "primary_bedroom","master_bedroom","bedroom","secondary_bedroom","guest_bedroom" };
            var bathTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bathroom","bath","primary_bath","ensuite_bath","ensuite","master_bath",
              "secondary_bath","shared_bath","full_bath" };
            var entryTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "entry","foyer","entry_foyer","vestibule","front_entry" };

            const double tol = 60; // mm snap tolerance for shared-wall detection

            // ── 1. Bed ↔ Bath suite doors ─────────────────────────────────────
            var beds  = rooms.Where(r => bedTypes.Contains(r.Type)).ToList();
            var baths = rooms.Where(r => bathTypes.Contains(r.Type)).ToList();

            foreach (var bed in beds)
            {
                // Find a bath that shares a vertical wall with this bedroom
                var paired = baths.FirstOrDefault(b =>
                    // Bath immediately to the right of bed
                    (Math.Abs(b.X - bed.Right) < tol &&
                     b.Y < bed.Top - tol && b.Top > bed.Y + tol) ||
                    // Bath immediately to the left of bed
                    (Math.Abs(b.Right - bed.X) < tol &&
                     b.Y < bed.Top - tol && b.Top > bed.Y + tol));

                if (paired == null) continue;

                bool bathOnRight = Math.Abs(paired.X - bed.Right) < tol;
                double sharedX   = bathOnRight ? bed.Right : bed.X;
                // Place door on the shared wall, centred on the overlap Y range
                double overlapY1 = Math.Max(bed.Y, paired.Y);
                double overlapY2 = Math.Min(bed.Top, paired.Top);
                double doorW_mm  = 750;
                double midY      = (overlapY1 + overlapY2) / 2 - doorW_mm / 2;
                midY = Math.Max(midY, overlapY1);

                double sx_du = sharedX * _s + offset.X;
                double sy_du = midY    * _s + offset.Y;
                double dw_du = doorW_mm * _s;

                var leaf = new Line(
                    new Point3d(sx_du, sy_du,          0),
                    new Point3d(sx_du, sy_du + dw_du,  0));
                leaf.Layer = "A-DOOR";
                space.AppendEntity(leaf); tr.AddNewlyCreatedDBObject(leaf, true);

                // Arc swings into the bathroom
                double arcStart = bathOnRight ? 0 : Math.PI;
                var arc = new Arc(
                    new Point3d(sx_du, sy_du, 0), dw_du,
                    arcStart, arcStart + Math.PI / 2);
                arc.Layer = "A-DOOR-SWNG";
                space.AppendEntity(arc); tr.AddNewlyCreatedDBObject(arc, true);
            }

            // ── 2. Foyer → Hallway door (living wing LEFT → hallway CENTRE) ───
            var corridor = rooms.FirstOrDefault(r =>
                r.Type.Equals("corridor", StringComparison.OrdinalIgnoreCase));
            var foyer = rooms.FirstOrDefault(r => entryTypes.Contains(r.Type));
            if (corridor != null && foyer != null)
            {
                // After wing swap: living wing is LEFT, hallway is to its RIGHT.
                // Shared wall: foyer.Right ≈ corridor.X
                double sharedX = 0;
                bool found = false;
                if (Math.Abs(foyer.Right - corridor.X) < tol * 4)
                {
                    sharedX = corridor.X; found = true;
                }
                else if (Math.Abs(foyer.X - corridor.Right) < tol * 4)
                {
                    sharedX = corridor.Right; found = true;
                }

                if (found)
                {
                    double overlapY1 = Math.Max(foyer.Y, corridor.Y);
                    double overlapY2 = Math.Min(foyer.Top, corridor.Top);
                    double doorW_mm  = 900;
                    double midY      = (overlapY1 + overlapY2) / 2 - doorW_mm / 2;

                    double sx_du = sharedX  * _s + offset.X;
                    double sy_du = midY     * _s + offset.Y;
                    double dw_du = doorW_mm * _s;

                    var leaf = new Line(
                        new Point3d(sx_du, sy_du,         0),
                        new Point3d(sx_du, sy_du + dw_du, 0));
                    leaf.Layer = "A-DOOR";
                    space.AppendEntity(leaf); tr.AddNewlyCreatedDBObject(leaf, true);

                    // Arc swings toward the hallway (east direction)
                    var arc = new Arc(
                        new Point3d(sx_du, sy_du, 0), dw_du, 0, Math.PI / 2);
                    arc.Layer = "A-DOOR-SWNG";
                    space.AppendEntity(arc); tr.AddNewlyCreatedDBObject(arc, true);
                }
            }
        }

        // Continuous horizontal dimension chain — individual room widths across the building
        // Draws tick marks at every unique X boundary, labels each segment between ticks
        private void DrawHorizDimChain(IList<SpaceNode> rooms, double chainYmm,
                                        Point3d offset, BlockTableRecord space, Transaction tr)
        {
            var xVals = rooms.Select(r => r.X)
                             .Concat(rooms.Select(r => r.Right))
                             .Distinct()
                             .OrderBy(v => v)
                             .ToList();
            if (xVals.Count < 2) return;

            double chainY = chainYmm * _s + offset.Y;
            double tick   = 220 * _s;

            // Main chain line
            var dl = new Line(
                new Point3d(xVals[0] * _s + offset.X, chainY, 0),
                new Point3d(xVals[^1] * _s + offset.X, chainY, 0));
            dl.Layer = "A-ANNO-DIMS";
            space.AppendEntity(dl); tr.AddNewlyCreatedDBObject(dl, true);

            // Tick marks
            foreach (double xmm in xVals)
            {
                double xdu = xmm * _s + offset.X;
                var t = new Line(new Point3d(xdu, chainY - tick, 0),
                                  new Point3d(xdu, chainY + tick, 0));
                t.Layer = "A-ANNO-DIMS";
                space.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
            }

            // Segment labels
            for (int i = 0; i + 1 < xVals.Count; i++)
            {
                double seg = xVals[i + 1] - xVals[i];
                if (seg < 600) continue;
                string label = FtIn(seg);
                double midX  = (xVals[i] + xVals[i + 1]) / 2 * _s + offset.X;
                var txt = new DBText
                {
                    Position   = new Point3d(midX - label.Length * 50 * _s, chainY + 180 * _s, 0),
                    TextString = label,
                    Height     = 175 * _s,
                    Layer      = "A-ANNO-DIMS"
                };
                space.AppendEntity(txt); tr.AddNewlyCreatedDBObject(txt, true);
            }
        }

        // Continuous vertical dimension chain — individual room depths along the right side
        private void DrawVertDimChain(IList<SpaceNode> rooms, double chainXmm,
                                       Point3d offset, BlockTableRecord space, Transaction tr)
        {
            var yVals = rooms.Select(r => r.Y)
                             .Concat(rooms.Select(r => r.Top))
                             .Distinct()
                             .OrderBy(v => v)
                             .ToList();
            if (yVals.Count < 2) return;

            double chainX = chainXmm * _s + offset.X;
            double tick   = 220 * _s;

            var dl = new Line(
                new Point3d(chainX, yVals[0] * _s + offset.Y, 0),
                new Point3d(chainX, yVals[^1] * _s + offset.Y, 0));
            dl.Layer = "A-ANNO-DIMS";
            space.AppendEntity(dl); tr.AddNewlyCreatedDBObject(dl, true);

            foreach (double ymm in yVals)
            {
                double ydu = ymm * _s + offset.Y;
                var t = new Line(new Point3d(chainX - tick, ydu, 0),
                                  new Point3d(chainX + tick, ydu, 0));
                t.Layer = "A-ANNO-DIMS";
                space.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
            }

            for (int i = 0; i + 1 < yVals.Count; i++)
            {
                double seg = yVals[i + 1] - yVals[i];
                if (seg < 600) continue;
                string label = FtIn(seg);
                double midY  = (yVals[i] + yVals[i + 1]) / 2 * _s + offset.Y;
                var txt = new DBText
                {
                    Position   = new Point3d(chainX + 180 * _s, midY, 0),
                    Rotation   = Math.PI / 2,
                    TextString = label,
                    Height     = 175 * _s,
                    Layer      = "A-ANNO-DIMS"
                };
                space.AppendEntity(txt); tr.AddNewlyCreatedDBObject(txt, true);
            }
        }

        // Building footprint dimension annotations (W and D in feet-inches)
        private void DrawBuildingDimensions(LayoutResult layout, Point3d offset,
                                             BlockTableRecord space, Transaction tr)
        {
            double bx = layout.BuildingX * _s + offset.X;
            double by = layout.BuildingY * _s + offset.Y;
            double bw = layout.BuildingW * _s;
            double bd = layout.BuildingD * _s;
            double gap  = 900 * _s;
            double tick = 250 * _s;

            // Width — below the building
            DrawDimLine(bx, by - gap, bx + bw, by - gap, tick,
                        FtIn(layout.BuildingW), false, space, tr);
            // Depth — right of the building
            DrawDimLine(bx + bw + gap, by, bx + bw + gap, by + bd, tick,
                        FtIn(layout.BuildingD), true, space, tr);
        }

        private void DrawDimLine(double x1, double y1, double x2, double y2,
                                  double tick, string label, bool vertical,
                                  BlockTableRecord space, Transaction tr)
        {
            // Main line
            var dl = new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0));
            dl.Layer = "A-ANNO-DIMS";
            space.AppendEntity(dl); tr.AddNewlyCreatedDBObject(dl, true);

            // Tick marks
            if (vertical)
            {
                foreach (double py in new[] { y1, y2 })
                {
                    var t = new Line(new Point3d(x1 - tick, py, 0), new Point3d(x1 + tick, py, 0));
                    t.Layer = "A-ANNO-DIMS";
                    space.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
                }
                var txt = new DBText();
                txt.Position   = new Point3d(x1 + 200 * _s, (y1 + y2) / 2, 0);
                txt.Rotation   = Math.PI / 2;
                txt.TextString = label;
                txt.Height     = 200 * _s;
                txt.Layer      = "A-ANNO-DIMS";
                space.AppendEntity(txt); tr.AddNewlyCreatedDBObject(txt, true);
            }
            else
            {
                foreach (double px in new[] { x1, x2 })
                {
                    var t = new Line(new Point3d(px, y1 - tick, 0), new Point3d(px, y1 + tick, 0));
                    t.Layer = "A-ANNO-DIMS";
                    space.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
                }
                var txt = new DBText();
                txt.Position   = new Point3d((x1 + x2) / 2 - label.Length * 55 * _s, y1 + 180 * _s, 0);
                txt.TextString = label;
                txt.Height     = 200 * _s;
                txt.Layer      = "A-ANNO-DIMS";
                space.AppendEntity(txt); tr.AddNewlyCreatedDBObject(txt, true);
            }
        }

        // Entry walkway — paved path from building front to street
        private void DrawEntryWalkway(LayoutResult layout, Point3d offset,
                                       BlockTableRecord space, Transaction tr)
        {
            double bx     = layout.BuildingX * _s + offset.X;
            double by     = layout.BuildingY * _s + offset.Y;
            double bw     = layout.BuildingW * _s;
            double pathW  = 1800 * _s;
            double midX   = bx + bw / 2;
            double streetY = offset.Y;

            var path = new Polyline();
            path.AddVertexAt(0, new Point2d(midX - pathW / 2, streetY), 0, 0, 0);
            path.AddVertexAt(1, new Point2d(midX + pathW / 2, streetY), 0, 0, 0);
            path.AddVertexAt(2, new Point2d(midX + pathW / 2, by),      0, 0, 0);
            path.AddVertexAt(3, new Point2d(midX - pathW / 2, by),      0, 0, 0);
            path.Closed        = true;
            path.ConstantWidth = 0;
            path.Layer         = "C-PROP";
            space.AppendEntity(path);
            tr.AddNewlyCreatedDBObject(path, true);
        }

        private static double GetDoorWidth(string type)
        {
            string t = (type ?? "").ToLowerInvariant().Replace('-','_').Replace(' ','_');
            return t switch
            {
                "primary_bedroom" or "master_bedroom" or "bedroom"
                or "secondary_bedroom" or "guest_bedroom"   => 900,
                "primary_bath" or "ensuite_bath" or "ensuite"
                or "bathroom"  or "bath" or "shared_bath"   => 750,
                "powder_room"  or "half_bath" or "toilet"   => 750,
                "walk_in_closet" or "closet" or "wic"       => 750,
                "living" or "dining" or "open_plan"
                or "family_room" or "great_room"            => 1000,
                "entry" or "foyer" or "entry_foyer"
                or "vestibule"                              => 1200,
                "kitchen"                                   => 900,
                "laundry" or "laundry_room" or "mudroom"   => 900,
                "garage"                                    => 2400,
                "emergency" or "icu"                        => 1800,
                _                                           => 900,
            };
        }

        // Fix #7: create all AIA layers directly — no separate helper needed
        private void EnsureStandardLayers(Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(_db.LayerTableId, OpenMode.ForRead);

            var layers = new (string name, short color)[]
            {
                ("A-WALL-EXTR", 7), ("A-WALL-INTR", 7), ("A-WALL-PRTN", 8),
                ("A-DOOR",      4), ("A-DOOR-SWNG", 4), ("A-GLAZ",      4),
                ("A-ANNO-TEXT", 2), ("A-AREA-IDEN", 6), ("A-ANNO-SYMB", 2),
                ("A-ANNO-DIMS", 2), ("A-ANNO-TTLB", 7), ("A-WALL-PATT", 8),
                ("C-PROP",      1), ("S-COLS",      7),  ("ZH-AI-NOTES", 150),
            };

            foreach (var (name, color) in layers)
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

        // ── Zone parsing — reads from VariationPlan.Properties (fix #2) ───────

        private List<ZoneGroup> ParseZones(VariationPlan variation)
        {
            var zones = new List<ZoneGroup>();
            if (!variation.Properties.TryGetValue("zones_json", out var raw))
                return zones;

            try
            {
                var list = JArray.Parse(raw?.ToString() ?? "[]");

                foreach (var z in list)
                {
                    var zone = new ZoneGroup
                    {
                        ZoneName = z["zone_name"]?.ToString()     ?? "",
                        Position = z["zone_position"]?.ToString() ?? "front",
                    };

                    foreach (var s in z["spaces"] as JArray ?? new JArray())
                    {
                        zone.Spaces.Add(new SpaceNode
                        {
                            Name            = s["name"]?.ToString()               ?? "Room",
                            Type            = s["type"]?.ToString()               ?? "room",
                            AreaSqm         = s["area_sqm"]?.Value<double>()      ?? 15,
                            AspectRatio     = s["aspect_ratio"]?.Value<double>()  ?? 1.4,
                            Floor           = s["floor"]?.Value<int>()            ?? 1,
                            Facing          = s["facing"]?.ToString()             ?? "south",
                            HasNaturalLight = s["has_natural_light"]?.Value<bool>() ?? true,
                            MinWidthM       = s["min_width_m"]?.Value<double>()   ?? 2.4,
                            MinDepthM       = s["min_depth_m"]?.Value<double>()   ?? 2.4,
                            AdjacentTo      = s["must_be_adjacent_to"]?
                                              .ToObject<List<string>>() ?? new(),
                            DoorConnects    = s["door_connects_to"]?
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

        // ── Fallback action executor (used when no zones present) ─────────────

        private void ExecuteAction(DrawAction a, Point3d offset,
                                    BlockTableRecord space, Transaction tr)
        {
            switch (a.ActionType)
            {
                case ActionType.CREATE_LAYER:
                    EnsureLayer(a, tr);
                    break;

                case ActionType.DRAW_WALL:
                    if (a.Vertices?.Count >= 2)
                        DrawPolyline(a.Vertices, offset, a.Layer,
                                     (a.ThicknessMm ?? 150) * _s,
                                     a.Properties.ContainsKey("closed"), space, tr);
                    else if (a.Start != null && a.End != null)
                        DrawLine(a.Start, a.End, offset, a.Layer, space, tr);
                    break;

                case ActionType.DRAW_DOOR:
                    if (a.Start != null) DrawDoor(a, offset, space, tr);
                    break;

                case ActionType.DRAW_WINDOW:
                    if (a.Start != null) DrawWindow(a, offset, space, tr);
                    break;

                case ActionType.DRAW_COLUMN:
                    if (a.Center != null) DrawColumn(a, offset, space, tr);
                    break;

                case ActionType.DRAW_ROOM_LABEL:
                case ActionType.ADD_AREA_TAG:
                    if (a.Center != null && !string.IsNullOrEmpty(a.LabelText))
                        DrawMText(a, offset, space, tr);
                    break;

                case ActionType.ADD_DIMENSION:
                    if (a.Start != null && a.End != null)
                        DrawDimension(a, offset, space, tr);
                    break;

                case ActionType.ADD_HATCH:
                    if (a.HatchBoundary?.Count >= 3)
                        DrawHatch(a, offset, space, tr);
                    break;

                case ActionType.ADD_NORTH_ARROW:
                    if (a.Center != null) DrawNorthArrow(a, offset, space, tr);
                    break;

                case ActionType.ADD_TITLE_BLOCK:
                    if (a.Start != null && !string.IsNullOrEmpty(a.LabelText))
                        DrawMText(a, offset, space, tr);
                    break;

                case ActionType.START_GROUP:
                case ActionType.END_GROUP:
                    break;
            }
        }

        // ── Drawing primitives (fallback path) ───────────────────────────────

        private void DrawPolyline(List<Point2D> pts, Point3d offset, string layer,
                                   double cw, bool closed,
                                   BlockTableRecord space, Transaction tr)
        {
            var pl = new Polyline();
            for (int i = 0; i < pts.Count; i++)
                pl.AddVertexAt(i, To2d(pts[i], offset), 0, 0, 0);
            pl.Closed        = closed;
            pl.ConstantWidth = cw;
            pl.Layer         = layer;
            space.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
        }

        private void DrawLine(Point2D s, Point2D e, Point3d offset,
                               string layer, BlockTableRecord space, Transaction tr)
        {
            var line = new Line(To3d(s, offset), To3d(e, offset));
            line.Layer = layer;
            space.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);
        }

        private void DrawDoor(DrawAction a, Point3d offset,
                               BlockTableRecord space, Transaction tr)
        {
            var pos = To3d(a.Start!, offset);
            double w = (a.DoorWidthMm ?? 900) * _s;

            var leaf = new Line(pos, new Point3d(pos.X + w, pos.Y, 0));
            leaf.Layer = "A-DOOR";
            space.AppendEntity(leaf);
            tr.AddNewlyCreatedDBObject(leaf, true);

            double rad = a.SwingAngle * Math.PI / 180.0;
            var arc    = new Arc(pos, w, 0, rad);
            arc.Layer  = "A-DOOR-SWNG";
            space.AppendEntity(arc);
            tr.AddNewlyCreatedDBObject(arc, true);
        }

        private void DrawWindow(DrawAction a, Point3d offset,
                                 BlockTableRecord space, Transaction tr)
        {
            var pos = To3d(a.Start!, offset);
            double w = (a.WindowWidthMm ?? 1200) * _s;

            for (int i = 0; i < 3; i++)
            {
                double yOff = i * 50 * _s;
                var line = new Line(
                    new Point3d(pos.X, pos.Y + yOff, 0),
                    new Point3d(pos.X + w, pos.Y + yOff, 0));
                line.Layer = "A-GLAZ";
                space.AppendEntity(line);
                tr.AddNewlyCreatedDBObject(line, true);
            }
        }

        private void DrawColumn(DrawAction a, Point3d offset,
                                 BlockTableRecord space, Transaction tr)
        {
            var pos = To3d(a.Center!, offset);
            double cw = (a.ColumnWidthMm ?? 300) * _s / 2;
            double cd = (a.ColumnDepthMm ?? 300) * _s / 2;

            var solid = new Solid(
                new Point3d(pos.X - cw, pos.Y - cd, 0),
                new Point3d(pos.X + cw, pos.Y - cd, 0),
                new Point3d(pos.X - cw, pos.Y + cd, 0),
                new Point3d(pos.X + cw, pos.Y + cd, 0));
            solid.Layer = "S-COLS";
            space.AppendEntity(solid);
            tr.AddNewlyCreatedDBObject(solid, true);
        }

        private void DrawMText(DrawAction a, Point3d offset,
                                BlockTableRecord space, Transaction tr)
        {
            var pos = To3d(a.Center ?? a.Start!, offset);

            string content = a.LabelText!;
            if (a.LabelAreaSqm.HasValue)
                content += $"\\P{a.LabelAreaSqm:F1} m\\U+00B2";

            var mt = new MText();
            mt.Location   = pos;
            mt.TextHeight = a.FontHeightMm * _s;
            mt.Layer      = a.Layer;
            mt.Contents   = content;
            mt.Attachment = AttachmentPoint.MiddleCenter;
            space.AppendEntity(mt);
            tr.AddNewlyCreatedDBObject(mt, true);
        }

        private void DrawDimension(DrawAction a, Point3d offset,
                                    BlockTableRecord space, Transaction tr)
        {
            var p1  = To3d(a.Start!, offset);
            var p2  = To3d(a.End!,   offset);
            var mid = new Point3d((p1.X + p2.X) / 2, p1.Y - 600 * _s, 0);
            var dim = new RotatedDimension(0, p1, p2, mid, "", ObjectId.Null);
            dim.Layer = "A-ANNO-DIMS";
            space.AppendEntity(dim);
            tr.AddNewlyCreatedDBObject(dim, true);
        }

        private void DrawHatch(DrawAction a, Point3d offset,
                                BlockTableRecord space, Transaction tr)
        {
            var boundary = new Polyline();
            for (int i = 0; i < a.HatchBoundary.Count; i++)
                boundary.AddVertexAt(i, To2d(a.HatchBoundary[i], offset), 0, 0, 0);
            boundary.Closed = true;
            boundary.Layer  = a.Layer;
            space.AppendEntity(boundary);
            tr.AddNewlyCreatedDBObject(boundary, true);

            var hatch = new Hatch();
            hatch.SetHatchPattern(HatchPatternType.PreDefined, a.HatchPattern ?? "ANSI31");
            hatch.PatternScale = a.HatchScale;
            hatch.PatternAngle = a.HatchAngle * Math.PI / 180.0;
            hatch.Layer        = a.Layer;
            hatch.Associative  = true;
            space.AppendEntity(hatch);
            tr.AddNewlyCreatedDBObject(hatch, true);

            hatch.AppendLoop(HatchLoopTypes.Outermost,
                             new ObjectIdCollection { boundary.ObjectId });
            hatch.EvaluateHatch(true);
        }

        private void DrawNorthArrow(DrawAction a, Point3d offset,
                                     BlockTableRecord space, Transaction tr)
        {
            var pos = To3d(a.Center!, offset);
            double r = 300 * _s;

            var circle = new Circle(pos, Vector3d.ZAxis, r);
            circle.Layer = "A-ANNO-SYMB";
            space.AppendEntity(circle);
            tr.AddNewlyCreatedDBObject(circle, true);

            var arrow = new Line(pos, new Point3d(pos.X, pos.Y + r, 0));
            arrow.Layer = "A-ANNO-SYMB";
            space.AppendEntity(arrow);
            tr.AddNewlyCreatedDBObject(arrow, true);

            var nLabel = new DBText();
            nLabel.Position   = new Point3d(pos.X - 100 * _s, pos.Y + r + 80 * _s, 0);
            nLabel.TextString = "N";
            nLabel.Height     = 200 * _s;
            nLabel.Layer      = "A-ANNO-SYMB";
            space.AppendEntity(nLabel);
            tr.AddNewlyCreatedDBObject(nLabel, true);
        }

        private void EnsureLayer(DrawAction a, Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(_db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(a.Layer)) return;

            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = a.Layer };
            if (a.LayerColor.HasValue)
                ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci,
                    (short)a.LayerColor.Value);
            if (!string.IsNullOrEmpty(a.LayerLinetype))
            {
                var ltt = (LinetypeTable)tr.GetObject(
                    _db.LinetypeTableId, OpenMode.ForRead);
                if (ltt.Has(a.LayerLinetype))
                    ltr.LinetypeObjectId = ltt[a.LayerLinetype];
            }
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }

        // ── Coordinate helpers ────────────────────────────────────────────────

        private Point3d To3d(Point2D p, Point3d offset) =>
            new Point3d(p.X * _s + offset.X, p.Y * _s + offset.Y, 0);

        private Point2d To2d(Point2D p, Point3d offset) =>
            new Point2d(p.X * _s + offset.X, p.Y * _s + offset.Y);

        // ── Unit scale detection ──────────────────────────────────────────────

        public static double GetMmScale(Database db) => db.Insunits switch
        {
            UnitsValue.Millimeters => 1.0,
            UnitsValue.Centimeters => 0.1,
            UnitsValue.Decimeters  => 0.01,
            UnitsValue.Meters      => 0.001,
            UnitsValue.Kilometers  => 0.000001,
            UnitsValue.Inches      => 1.0 / 25.4,
            UnitsValue.Feet        => 1.0 / 304.8,
            UnitsValue.Yards       => 1.0 / 914.4,
            UnitsValue.Undefined   => 1.0,
            _                      => 1.0,
        };
    }
}
